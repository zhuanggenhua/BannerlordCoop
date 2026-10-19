import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { appendFile, mkdir, readFile, realpath, stat, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { UdpFaultProxy } from './udp-fault-proxy.mjs';

export const HELP = `Usage: node tools/mcp/scenarios/p2p-udp-reliability.mjs
  --repo <coordinator checkout> --server <CoopMcpServer.exe> --config <profiles-proxy.json>
  --sdk-root <node_modules> --record <new evidence.jsonl>
  [--profile local] [--save P1B_DirectIP_Recruit] [--rounds 3]
  [--timeout-seconds 75] [--lost-writer]

Run under tools/coordination/with-game-lock.mjs, from the coordinator only.
The selected ListenAuthority profile must set clientProxyPort (for example24200).
One loopback UDP proxy binds that port and forwards to the native host port4200.
Default: three or more A/B/A rounds under real packet loss/delay/reordering,
then a committed-paused candidate disconnect, timeout and fresh-barrier reconnect.
--lost-writer: separate pre-final-ACK writer-loss safety experiment, explicitly
NOT lossless recovery. Rejoin must be refused with CampaignCheckpointUnavailable.
Every feature records whole logs, two BMP receipts and UDP stats; stop_run and
both process-tree cleanup receipts are mandatory. No deployment/private IPC.
Mutations are sent once. Read-only polls are bounded; uncertain mutations fail.
Import and --help do not load the SDK, bind a socket, connect or launch games.`;

const INSTANCES = ['server', 'client1'];
const WORLD_KEYS = ['partyIdentityHash', 'rosterCountsHash', 'rosterXpHash', 'volunteerHash', 'heroGoldHash', 'nextHandle', 'ticks'];
const PLAYER_KEYS = ['gold', 'troops', 'wounded', 'heroId', 'clanId', 'characterId', 'partyId', 'partyStringId',
    'heroStringId', 'settlementId', 'x', 'y', 'registeredPlayers', 'heroRegistrations', 'partyRegistrations', 'clanRegistrations'];
const CHECKPOINT_REASON = 'the campaign authority checkpoint is unavailable';
export const field = (object, name) => object[name] ?? object[name[0].toUpperCase() + name.slice(1)];
export const writer = state => field(state.lease, 'authorityControllerId');
export const epoch = state => field(state.lease, 'epoch');
export const paused = state => field(state.lease, 'isPaused');
export const jsonOutput = text => JSON.parse(text.includes('LIVE_TEST_JSON=') ? text.split('LIVE_TEST_JSON=').at(-1) : text);

export function options(argv) {
    const config = { profile: 'local', save: 'P1B_DirectIP_Recruit', rounds: 3, 'timeout-seconds': 75, 'lost-writer': false };
    const allowed = new Set(['repo', 'server', 'config', 'sdk-root', 'record', 'profile', 'save', 'rounds', 'timeout-seconds']);
    const seen = new Set();
    for (let i = 0; i < argv.length; i++) {
        const key = argv[i]?.replace(/^--/, '');
        assert.ok(argv[i]?.startsWith('--') && !seen.has(key), 'Invalid or duplicate option: ' + argv[i]);
        seen.add(key);
        if (key === 'lost-writer') { config[key] = true; continue; }
        assert.ok(allowed.has(key) && argv[i + 1] && !argv[i + 1].startsWith('--'), 'Invalid option: ' + argv[i]);
        config[key] = argv[++i];
    }
    for (const key of ['repo', 'server', 'config', 'sdk-root', 'record']) {
        assert.ok(config[key], 'Missing --' + key); config[key] = resolve(config[key]);
    }
    config.rounds = Number(config.rounds); config['timeout-seconds'] = Number(config['timeout-seconds']);
    assert.ok(Number.isInteger(config.rounds) && config.rounds >= 3 && config.rounds <= 20, 'rounds must be3..20');
    assert.ok(Number.isInteger(config['timeout-seconds']) && config['timeout-seconds'] >= 65 && config['timeout-seconds'] <= 180,
        'timeout-seconds must be65..180, longer than the real60s network timeout');
    return config;
}

export function proxyProfile(document, profileName) {
    const profile = document.profiles?.[profileName];
    assert.ok(profile && profile.listenAuthority === true, 'Selected profile must be ListenAuthority');
    assert.ok(Number.isInteger(profile.clientProxyPort) && profile.clientProxyPort >= 1 && profile.clientProxyPort <= 65535 &&
        profile.clientProxyPort !== 4200, 'clientProxyPort must be a separate valid loopback port');
    assert.ok(profile.serverPlatformId && profile.clientPlatformIds?.[0] && profile.serverPlatformId !== profile.clientPlatformIds[0]);
    return { port: profile.clientProxyPort, controllers: [profile.serverPlatformId, profile.clientPlatformIds[0]] };
}

export function assertActors(states, initial) {
    assert.equal(states.length, 2);
    for (let i = 0; i < 2; i++) {
        for (const key of ['controller', 'mainHero', 'mainParty']) assert.equal(states[i][key], initial[i][key], 'Native actor changed: ' + key);
        assert.equal(states[i].roomHost, initial[0].controller);
    }
}

export function assertWriter(states, controller, initial) {
    assertActors(states, initial);
    for (const value of states) {
        assert.equal(writer(value), controller); assert.equal(paused(value), false);
        assert.equal(value.frozen, false); assert.equal(value.localAuthority, value.controller === controller);
        assert.ok(Number.isInteger(epoch(value)) && epoch(value) > 0);
    }
    for (const key of ['epoch', 'authorityId', 'roomId']) {
        assert.notEqual(field(states[0].lease, key), undefined, 'Missing lease field ' + key);
        assert.equal(field(states[0].lease, key), field(states[1].lease, key), 'Lease mismatch: ' + key);
    }
    assert.equal(states.filter(value => value.localAuthority).length, 1, 'Exactly one writer required');
}

export function assertWorld(worlds) {
    assert.equal(worlds.length, 2);
    assert.equal(worlds[0].campaign, worlds[1].campaign);
    for (const world of worlds) assert.equal(world.unregisteredParties, 0);
    for (const key of WORLD_KEYS) {
        assert.notEqual(worlds[0][key], undefined, 'Missing world key ' + key);
        assert.equal(worlds[0][key], worlds[1][key], 'World mismatch: ' + key);
    }
}

export function assertPlayers(pair) {
    assert.equal(pair.length, 2);
    const maps = pair.map(rows => {
        assert.equal(rows.length, 2); const map = new Map(rows.map(row => [row.controllerId, row]));
        assert.equal(map.size, 2, 'Duplicate player controller'); return map;
    });
    assert.deepEqual([...maps[0].keys()].sort(), [...maps[1].keys()].sort());
    for (const [controller, left] of maps[0]) {
        const right = maps[1].get(controller);
        for (const key of PLAYER_KEYS) {
            assert.ok(Object.hasOwn(left, key) && Object.hasOwn(right, key), 'Missing player field ' + key);
            assert.equal(right[key], left[key], `${controller}/${key} mismatch`);
        }
        for (const row of [left, right]) {
            assert.equal(row.active, true); assert.equal(row.registeredPlayers, 2);
            for (const key of ['heroRegistrations', 'partyRegistrations', 'clanRegistrations']) assert.equal(row[key], 1);
        }
    }
}

export function assertFrozenHost(before, after, initial, expectedPaused) {
    assert.equal(after.controller, initial.controller); assert.equal(after.mainHero, initial.mainHero); assert.equal(after.mainParty, initial.mainParty);
    assert.equal(after.localAuthority, false, 'Host promoted without complete checkpoint');
    assert.equal(paused(after), expectedPaused); assert.equal(epoch(after), epoch(before));
    for (const key of ['authorityId', 'roomId']) assert.equal(field(after.lease,key),field(before.lease,key));
    assert.equal(writer(after), writer(before));
    assert.equal(after.hours, before.hours, 'Unresolved handoff advanced clock');
    assert.equal(after.worldTicks, before.worldTicks, 'Unresolved handoff advanced native Tick');
}

export function assertPacketEvidence(stats) {
    assert.ok(stats.dropped > 0 && stats.delayed > 0 && stats.reordered > 0, 'Actual packet faults were not all observed');
    assert.ok(stats.received.toHost > 0 && stats.received.toClient > 0 && stats.sent.toHost > 0 && stats.sent.toClient > 0);
    assert.deepEqual(stats.errors, []);
    assert.ok(stats.samples.some(row => row.action === 'drop') && stats.samples.some(row => row.action === 'delay'));
    for (const row of stats.samples) assert.match(row.sha256, /^[0-9a-f]{64}$/);
}

export function assertProcessEvidence(rows, expectedPids) {
    const games = rows.filter(row => row.Name?.toLowerCase() === 'bannerlord.exe');
    assert.equal(games.length, 2, 'Expected exactly two native Bannerlord processes');
    assert.deepEqual(games.map(row => row.ProcessId).sort((a,b) => a-b), [...expectedPids].sort((a,b) => a-b));
    assert.ok(!rows.some(row => /dedicated/i.test(row.Name)), 'Unexpected dedicated server process');
    for (const row of games) assert.ok(row.CommandLine && row.CreationDate, 'Incomplete OS process receipt');
}

export function logDiagnostics(text) {
    return { realTick: (text.match(/Failed to run ["']?Campaign\.RealTick/g) ?? []).length,
        duplicateIds: (text.match(/Duplicate id:|Generated duplicate id |Duplicate object handle:/gi) ?? []).length,
        aiBatch: (text.match(/(?:AI batch.*fail|Failed.*AI batch)/gi) ?? []).length,
        errorLines: text.split(/\r?\n/).filter(line => /\[(?:ERR|FTL)\]|\b(?:ERROR|FATAL)\b/.test(line)) };
}

export async function run(config) {
    assert.ok((await stat(join(config.repo, '.git'))).isDirectory(), 'Live runs require coordinator main checkout');
    assert.equal(await realpath(config.repo), await realpath(execFileSync('git', ['rev-parse', '--show-toplevel'],
        { cwd: config.repo, encoding: 'utf8', windowsHide: true }).trim()));
    const profile = proxyProfile(JSON.parse(await readFile(config.config, 'utf8')), config.profile);
    const sdk = join(config['sdk-root'], '@modelcontextprotocol/sdk/dist/esm/client');
    const [{ Client }, { StdioClientTransport }] = await Promise.all([
        import(pathToFileURL(join(sdk, 'index.js')).href), import(pathToFileURL(join(sdk, 'stdio.js')).href) ]);
    await mkdir(dirname(config.record), { recursive: true });
    await writeFile(config.record, '', { flag: 'wx' });
    const summaryPath = config.record + '.summary.json';
    await writeFile(summaryPath, '{}', { flag: 'wx' });
    const summary = { scenario: 'p2p-udp-reliability', startedUtc: new Date().toISOString(), config,
        codeHead: execFileSync('git', ['rev-parse', 'HEAD'], { cwd: config.repo, encoding: 'utf8', windowsHide: true }).trim(),
        features: [], screenshots: [], probes: [], failed: null, cleanupConfirmed: false, passed: false,
        losslessWriterRecovery: 'NOT_IMPLEMENTED', scope: 'Real loopback UDP faults, existing map authority protocol; no combat, native daily or cold-save acceptance.' };
    const proxy = new UdpFaultProxy({ listenPort: profile.port });
    const client = new Client({ name: 'codex-p2p-udp-reliability', version: '1.0.0' });
    const transport = new StdioClientTransport({ command: config.server, args: ['--config', config.config], cwd: config.repo,
        stderr: 'inherit', maxBufferSize: 16 * 1024 * 1024 });
    const logs = Object.fromEntries(INSTANCES.map(name => [name, { text: '', cursor: undefined, resets: 0 }]));
    let runId, initial, callId = 0, readDeadline;
    const emit = entry => appendFile(config.record, JSON.stringify({ utc: new Date().toISOString(), ...entry }) + '\n');
    const save = async () => { summary.udp = structuredClone(proxy.stats); await writeFile(summaryPath, JSON.stringify(summary, null, 2) + '\n'); };
    async function call(name, args, mutation = false) {
        assert.ok(!(readDeadline && mutation), 'Polling callbacks may not mutate');
        const timeout = readDeadline ? readDeadline - Date.now() : 360000;
        assert.ok(timeout > 0, 'Read deadline expired');
        const id = ++callId; await emit({ phase: 'request', callId: id, name, args, mutation });
        let raw;
        try { raw = await client.callTool({ name, arguments: args }, undefined, { timeout }); }
        catch (error) { await emit({ phase: 'transport_error', callId: id, name, error: error.stack, outcomeUncertain: mutation }); throw error; }
        const data = raw.structuredContent ?? JSON.parse(raw.content.find(item => item.type === 'text').text);
        if (name === 'start_run' && data.runId) { runId = data.runId; summary.runId = runId; }
        await emit({ phase: 'response', callId: id, name, args, data, raw });
        assert.ok(!raw.isError, `${name}: ${JSON.stringify(data)}`); return data;
    }
    async function command(instance, name, args = [], mutation = false) {
        const response = await call('execute_command', { run_id: runId, instance, name, arguments: args }, mutation);
        assert.equal(response.ok, true, JSON.stringify(response)); assert.equal(response.result.succeeded, true, JSON.stringify(response));
        return response.result.output;
    }
    const state = instance => command(instance, 'coop.debug.authority.state').then(jsonOutput);
    const both = () => Promise.all(INSTANCES.map(state));
    async function poll(label, read, timeout = 45000) {
        const deadline = Date.now() + timeout; let last;
        do {
            const previous = readDeadline;
            try { readDeadline = deadline; return await read(); }
            catch (error) { last = error; await emit({ phase: 'poll_pending', label, reason: error.message }); }
            finally { readDeadline = previous; }
            await delay(500);
        } while (Date.now() < deadline);
        throw new Error(label + ' did not converge: ' + last?.message, { cause: last });
    }
    const awaitWriter = controller => poll('writer ' + controller, async () => {
        const values = await both(); assertWriter(values, controller, initial); return values;
    });
    async function waitState(instance, status, timeout = 300) {
        const response = await call('wait_for_state', { run_id: runId, instance, state: status, timeout_seconds: timeout });
        assert.equal((response.result ?? response).reached, true, JSON.stringify(response));
    }
    async function snapshot(label) {
        return poll(label, async () => {
            const worlds = await Promise.all(INSTANCES.map(instance => command(instance, 'coop.debug.authority.world_state').then(jsonOutput)));
            assertWorld(worlds);
            const players = [];
            for (const instance of INSTANCES) {
                const rows = [];
                for (const actor of initial) rows.push(jsonOutput(await command(instance, 'coop.debug.players.party_state', [actor.controller])));
                players.push(rows);
            }
            assertPlayers(players); assertActors(await both(), initial); return { worlds, players };
        });
    }
    async function pause() {
        await command('server', 'coop.debug.set_time_mode', ['Pause', 'force-live-test'], true);
        return snapshot('exact paused critical state');
    }
    async function collectLogs() {
        summary.logs ??= {}; const failures = [];
        for (const instance of INSTANCES) {
            try {
            const log = logs[instance];
            for (let page = 0; page < 1024; page++) {
                const response = await call('read_logs', { run_id: runId, instance, cursor: log.cursor, max_bytes: 65536 });
                const chunk = response.result ?? response;
                assert.equal(typeof chunk.text, 'string'); assert.equal(typeof chunk.cursor, 'string');
                assert.equal(typeof chunk.hasMore, 'boolean'); assert.equal(typeof chunk.reset, 'boolean');
                if (chunk.reset) { log.resets++; log.text += '\n--- retained previous log; cursor reset ---\n'; }
                log.text += chunk.text; log.cursor = chunk.cursor;
                if (!chunk.hasMore) break;
                assert.ok(page < 1023, 'Incomplete log capture: page budget');
            }
            const path = config.record + '.' + instance + '.log'; await writeFile(path, log.text);
            const diagnostics = logDiagnostics(log.text);
            summary.logs[instance] = { path, sha256: createHash('sha256').update(log.text).digest('hex'), resets: log.resets, ...diagnostics };
            if (diagnostics.realTick + diagnostics.duplicateIds + diagnostics.aiBatch > 0 || diagnostics.errorLines.length > 0)
                failures.push({ instance, diagnostics });
            } catch (error) { failures.push({ instance, error: error.stack }); }
        }
        assert.deepEqual(failures, [], 'Incomplete logs or runtime error lines require investigation; no baseline blanket exemption');
    }
    async function screenshot(instance, label) {
        const requested = await call('screenshot', { run_id: runId, instance }, true);
        assert.equal(requested.ok, true); const id = requested.result.captureId; assert.ok(id);
        const capture = await poll('BMP ' + id, async () => {
            const response = await call('screenshot_status', { run_id: runId, instance, capture_id: id });
            assert.equal(response.ok, true); const value = response.result;
            assert.equal(value.complete, true); assert.ok(value.headerValid && value.isBmp && value.stable && value.sha256 && value.path);
            return value;
        }, 60000);
        summary.screenshots.push({ instance, label, ...capture });
    }
    async function feature(name, action) {
        const row = { name, startedUtc: new Date().toISOString(), status: 'RUNNING' }; summary.features.push(row); await save();
        try {
            row.evidence = await action(); await collectLogs();
            for (const instance of INSTANCES) await screenshot(instance, name);
            row.udp = structuredClone(proxy.stats); row.status = 'PASS';
        } catch (error) {
            row.status = 'FAIL'; row.error = error.stack;
            try { await collectLogs(); } catch (diagnostic) { row.logError = diagnostic.stack; }
            for (const instance of INSTANCES) { try { await screenshot(instance, name + '-failed'); } catch (diagnostic) { row.captureError = diagnostic.stack; } }
            throw error;
        } finally { row.completedUtc = new Date().toISOString(); await emit({ phase: 'feature_result', feature: row }); await save(); }
    }
    async function allocation(instance) {
        const created = jsonOutput(await command(instance, 'coop.debug.authority.allocation_probe', ['create'], true));
        const other = INSTANCES.find(value => value !== instance);
        const observed = await poll('allocated object replicated', async () => {
            const value = jsonOutput(await command(other, 'coop.debug.authority.allocation_probe', ['inspect', created.id]));
            assert.equal(value.handle, created.handle); assert.equal(value.id, created.id); return value;
        });
        const ordinal = Number(created.id.match(/(\d+)$/)?.[1]); assert.ok(Number.isInteger(ordinal));
        const prior = summary.probes.at(-1);
        if (prior) { assert.ok(created.handle > prior.handle); assert.ok(ordinal > prior.ordinal); }
        await command(instance, 'coop.debug.authority.allocation_probe', ['retire', created.id], true);
        await poll('object retired on both peers', async () => {
            for (const endpoint of INSTANCES) {
                const response = await call('execute_command', { run_id: runId, instance: endpoint,
                    name: 'coop.debug.authority.allocation_probe', arguments: ['inspect', created.id] });
                assert.equal(response.ok, true); assert.equal(response.result.succeeded, false); assert.equal(response.result.errorCode, 'probe_absent');
            }
        });
        const value = { instance, ...created, ordinal, observed }; summary.probes.push(value); return value;
    }
    async function soleTick(instance) {
        const other = INSTANCES.find(value => value !== instance), controller = initial[INSTANCES.indexOf(instance)].controller;
        await command('server', 'coop.debug.set_time_mode', ['Play_1x', 'force-live-test'], true);
        await awaitWriter(controller); const before = { writer: await state(instance), observer: await state(other) };
        const deadline = Date.now() + 5000;
        while (Date.now() < deadline) { await delay(500); assertWriter(await both(), controller, initial); }
        const after = { writer: await state(instance), observer: await state(other) };
        assert.equal(epoch(before.writer), epoch(after.writer));
        assert.ok(after.writer.worldTicks > before.writer.worldTicks && after.writer.hours > before.writer.hours);
        assert.equal(after.observer.worldTicks, before.observer.worldTicks);
        return { before, after, snapshot: await pause() };
    }
    async function holdThroughTimeout(expectedPaused) {
        const before = await state('server'), start = Date.now(), samples = [];
        assert.equal(before.localAuthority, false); assert.equal(paused(before), expectedPaused);
        while (Date.now() - start < config['timeout-seconds'] * 1000) {
            await delay(1000); const current = await state('server'); assertFrozenHost(before, current, initial[0], expectedPaused);
            const view = await call('get_run', { run_id: runId });
            samples.push({ utc: new Date().toISOString(), state: current, client: view.instances.find(value => value.name === 'client1') });
            if (samples.length % 5 === 0) { await collectLogs(); await emit({ phase: 'timeout_hold', sample: samples.at(-1), udp: proxy.stats }); }
        }
        const clientView = samples.at(-1).client;
        assert.equal(clientView.processAlive, true); assert.equal(clientView.status?.campaignLoaded, false, 'B did not leave Campaign after actual network timeout');
        assert.equal(clientView.status?.coopRunning, false, 'B still has active co-op session after timeout');
        return { elapsedMs: Date.now() - start, before, samples };
    }
    try {
        await proxy.start(); summary.proxy = { listenPort: proxy.port, targetPort: proxy.targetPort };
        await client.connect(transport);
        const started = await call('start_run', { profile: config.profile, client_count: 0, save_name: config.save }, true);
        assert.ok(runId); assert.equal(started.error, null); await waitState('server', 'readyForCampaignTests');
        const launched = await call('start_client', { run_id: runId, client_index: 1 }, true);
        assert.equal(launched.outcome, 'launched'); assert.equal(launched.instance, 'client1');
        await waitState('client1', 'readyToJoin', 180); const joined = await call('join_client', { run_id: runId, instance: 'client1' }, true);
        assert.equal(joined.ok, true); await waitState('client1', 'readyForCampaignTests');
        initial = await both(); assert.deepEqual(initial.map(value => value.controller), profile.controllers);
        for (const value of initial) assert.ok(value.mainHero && value.mainParty);
        await feature('two-process-exact-baseline', async () => {
            await awaitWriter(initial[0].controller); const snapshot = await pause();
            const view = await call('get_run', { run_id: runId }); assert.equal(view.instances.length, 2);
            const processes = INSTANCES.map(instance => view.instances.find(value => value.name === instance));
            assert.ok(processes.every(value => value.processAlive && value.identity.pid > 0));
            assert.notEqual(processes[0].identity.pid, processes[1].identity.pid);
            assert.equal(processes[0].status.buildVersion, processes[1].status.buildVersion);
            const assemblies = value => value.status.loadedAssemblies.map(row => [row.name, row.mvid]).sort();
            assert.deepEqual(assemblies(processes[0]), assemblies(processes[1]));
            const output = execFileSync('powershell.exe', ['-NoProfile', '-Command',
                "Get-CimInstance Win32_Process | Where-Object { $_.Name -like '*Bannerlord*' } | Select-Object Name,ProcessId,ParentProcessId,CreationDate,CommandLine | ConvertTo-Json -Depth 4"],
            { encoding: 'utf8', windowsHide: true });
            const parsed = JSON.parse(output), osProcesses = Array.isArray(parsed) ? parsed : [parsed];
            assertProcessEvidence(osProcesses, processes.map(value => value.identity.pid));
            return { snapshot, processes, osProcesses };
        });
        proxy.setFault({ dropEvery: 11, delayEvery: 5, delayMs: 250 });
        for (let round = 1; round <= config.rounds; round++) {
            await feature('loss-delay-reorder-round-' + round, async () => {
                const a = await awaitWriter(initial[0].controller), aProbe = await allocation('server');
                const aTick = await soleTick('server');
                await command('server', 'coop.debug.authority.availability', ['yield'], true);
                const b = await awaitWriter(initial[1].controller); assert.equal(epoch(b[0]), epoch(a[0]) + 2);
                const bProbe = await allocation('client1'), bTick = await soleTick('client1');
                await command('server', 'coop.debug.authority.availability', ['auto'], true);
                const returned = await awaitWriter(initial[0].controller); assert.equal(epoch(returned[0]), epoch(b[0]) + 2);
                return { a, b, returned, aProbe, bProbe, aTick, bTick, snapshot: await pause() };
            });
        }
        assertPacketEvidence(proxy.stats); proxy.setFault();
        if (config['lost-writer']) {
            await feature('lost-before-final-ack-safety-only', async () => {
                await command('server', 'coop.debug.authority.availability', ['yield'], true); await awaitWriter(initial[1].controller);
                await command('server', 'coop.debug.set_time_mode', ['Play_1x', 'force-live-test'], true);
                proxy.setFault({ blackout: true });
                await command('server', 'coop.debug.authority.availability', ['auto'], true);
                const held = await holdThroughTimeout(false); proxy.setFault();
                const beforeLength = logs.client1.text.length;
                await command('client1', 'coop.debug.connection.reconnect', [], true);
                const refusal = await poll('honest checkpoint-unavailable refusal', async () => {
                    await collectLogs(); const newText = logs.client1.text.slice(beforeLength);
                    assert.ok(newText.toLowerCase().includes(CHECKPOINT_REASON) || newText.includes('CampaignCheckpointUnavailable'),
                        'Missing explicit checkpoint-unavailable refusal, not inferred from generic disconnect');
                    const view = await call('get_run', { run_id: runId });
                    const b = view.instances.find(value => value.name === 'client1'); assert.equal(b.status?.campaignLoaded, false);
                    const host = await state('server'); assertFrozenHost(held.before, host, initial[0], false); return { host, client: b, newText };
                });
                return { held, refusal, verdict: 'SAFE_FENCE_AND_REFUSED_STALE_REJOIN', losslessRecovery: 'NOT_IMPLEMENTED' };
            });
        } else {
            await feature('committed-checkpoint-candidate-timeout-and-reconnect', async () => {
                await awaitWriter(initial[0].controller); await pause();
                proxy.setFault({ blackout: true });
                await command('server', 'coop.debug.authority.availability', ['yield'], true);
                const frozen = await poll('A committed a paused lease before candidate ACK', async () => {
                    const value = await state('server'); assert.equal(paused(value), true); assert.equal(value.localAuthority, false); return value;
                }, 10000);
                const held = await holdThroughTimeout(true);
                const barriers = () => [...logs.server.text.matchAll(/Sent campaign authority handoff barrier (\d+) for paused epoch (\d+)/g)]
                    .map(match => ({ sequence:Number(match[1]), epoch:Number(match[2]) })).filter(row => row.epoch === epoch(frozen));
                const oldBarriers = barriers(); assert.ok(oldBarriers.length > 0, 'No actual final barrier was sent before timeout');
                proxy.setFault();
                await command('client1', 'coop.debug.connection.reconnect', [], true);
                await waitState('client1', 'readyForCampaignTests');
                const recovered = await awaitWriter(initial[1].controller); assert.equal(epoch(recovered[0]), epoch(frozen) + 1);
                await collectLogs(); const freshBarriers = barriers();
                assert.ok(freshBarriers.some(row => row.sequence > Math.max(...oldBarriers.map(old => old.sequence))),
                    'Reconnected candidate did not receive a fresh final barrier sequence');
                const snapshot = await pause(), probe = await allocation('client1');
                await command('server', 'coop.debug.authority.availability', ['auto'], true);
                const returned = await awaitWriter(initial[0].controller); assert.equal(epoch(returned[0]), epoch(recovered[0]) + 2);
                return { frozen, held, oldBarriers, freshBarriers, recovered, snapshot, probe, returned, final: await pause() };
            });
        }
        assertPacketEvidence(proxy.stats);
    } catch (error) {
        summary.failed = { message: error.message, stack: error.stack }; await emit({ phase: 'scenario_failure', ...summary.failed });
        if (runId && !summary.features.some(row => row.status === 'FAIL')) {
            try { summary.failureRunView = await call('get_run', { run_id: runId }); } catch {}
            try { await collectLogs(); } catch (diagnostic) { summary.failureLogError = diagnostic.stack; }
            for (const instance of INSTANCES) {
                try { await screenshot(instance, 'startup-or-between-features-failure'); }
                catch (diagnostic) { await emit({ phase: 'failure_capture_error', instance, error: diagnostic.stack }); }
            }
        }
    } finally {
        if (runId) {
            try {
                const stopped = await call('stop_run', { run_id: runId }, true); summary.stopped = stopped;
                assert.equal(stopped.state, 'stopped'); assert.ok(stopped.instances.length > 0);
                if (initial) assert.equal(stopped.instances.length, 2);
                assert.ok(stopped.instances.every(row => row.cleanupComplete && !row.processAlive && !row.processTreeAlive)); summary.cleanupConfirmed = true;
            } catch (error) { summary.cleanupError = error.stack; }
            try { await collectLogs(); } catch (error) { summary.finalLogError = error.stack; }
        }
        try { await client.close(); } catch (error) { summary.transportCloseError = error.stack; }
        await proxy.close(); summary.completedUtc = new Date().toISOString();
        summary.passed = !summary.failed && !summary.cleanupError && !summary.finalLogError && !summary.transportCloseError && summary.cleanupConfirmed;
        await save(); console.log(JSON.stringify({ runId, passed: summary.passed, features: summary.features.map(row => [row.name, row.status]), summaryPath }));
        if (!summary.passed) process.exitCode = 1;
    }
    return summary;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
    if (process.argv.includes('--help')) console.log(HELP);
    else await run(options(process.argv.slice(2)));
}
