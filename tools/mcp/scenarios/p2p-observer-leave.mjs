import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { appendFile, mkdir, readFile, realpath, stat, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { assertActors, assertPlayers, assertProcessEvidence, assertWorld, assertWriter, epoch, field,
    jsonOutput, logDiagnostics, writer, paused as leasePaused } from './p2p-udp-reliability.mjs';

export const HELP = `Usage: node tools/mcp/scenarios/p2p-observer-leave.mjs
  --repo <coordinator checkout> --server <CoopMcpServer.exe> --config <profiles.json>
  --sdk-root <node_modules> --record <new evidence.jsonl>
  [--profile local] [--save P1B_DirectIP_Recruit]
Run under with-game-lock.mjs from the coordinator only. Native Danustica tavern
preparation runs only when the preparing player is the confirmed writer.
Then B observer exits through A writer, followed by A observer through B writer.
Every feature records full logs, two BMP receipts and strict state comparisons.
No teleport, private IPC, loopback client, replenishment, or uncertain mutation retry.
This accepts trusted QA preparation and observer exit, not ordinary travel/entry,
Mission gameplay, natural daily scheduling, cold saves, or actual packet faults.
--help/import are offline and do not import SDK, bind sockets or launch games.`;
const INSTANCES = ['server', 'client1'];
const TOWN = { id: 'town_ES1', location: 'Location_town_ES1_tavern' };
const STABLE_PLAYER = ['gold', 'troops', 'wounded', 'heroId', 'clanId', 'characterId', 'partyId',
    'partyStringId', 'heroStringId', 'registeredPlayers', 'heroRegistrations', 'partyRegistrations', 'clanRegistrations'];
const STABLE_WORLD = ['partyIdentityHash', 'rosterCountsHash', 'rosterXpHash', 'volunteerHash', 'heroGoldHash', 'ticks'];

export function options(argv) {
    const config = { profile: 'local', save: 'P1B_DirectIP_Recruit' }, seen = new Set();
    const allowed = new Set(['repo', 'server', 'config', 'sdk-root', 'record', 'profile', 'save']);
    for (let i = 0; i < argv.length; i += 2) {
        const key = argv[i]?.replace(/^--/, '');
        assert.ok(argv[i]?.startsWith('--') && allowed.has(key) && !seen.has(key) && argv[i + 1] &&
            !argv[i + 1].startsWith('--'), 'Invalid or duplicate option: ' + argv[i]);
        seen.add(key); config[key] = argv[i + 1];
    }
    for (const key of ['repo', 'server', 'config', 'sdk-root', 'record']) {
        assert.ok(config[key], 'Missing --' + key); config[key] = resolve(config[key]);
    }
    return config;
}
export function assertNativeMap(view, instance) {
    const status = view.instances?.find(row => row.name === instance)?.status;
    assert.ok(status, 'Missing native status for ' + instance);
    assert.equal(status.missionActive, false);
    assert.equal(status.activeState, 'TaleWorlds.CampaignSystem.GameState.MapState');
    return status;
}
export function assertTown(inspections, controller, town) {
    assert.equal(inspections.length, 2);
    for (const value of inspections) {
        const matches = value.players.filter(row => row.controller === controller);
        assert.equal(matches.length, 1); assert.equal(matches[0].currentSettlement, town);
    }
}
export function assertUiClosed(evidence, movement, status) {
    for (const key of ['menuView', 'pendingMenuView', 'encounterOverlay'])
        assert.match(evidence, new RegExp('(?:^| )' + key + '=False(?: |$)'));
    assert.match(movement, /(?:^|\|)settlement=none(?:\||$)/);
    assert.match(movement, /(?:^|\|)encounter=none(?:\||$)/);
    assert.ok(Object.hasOwn(status, 'activeMenu'), 'Missing native active-menu observation');
    assert.equal(status.activeMenu, null); assert.equal(status.missionActive, false);
    assert.equal(status.activeState, 'TaleWorlds.CampaignSystem.GameState.MapState');
}
export function assertNoWorldEffect(before, after) {
    assertPlayers(before.players); assertPlayers(after.players); assertWorld(before.worlds); assertWorld(after.worlds);
    for (let i = 0; i < 2; i++) {
        const rows = new Map(before.players[i].map(row => [row.controllerId, row]));
        for (const row of after.players[i]) for (const key of [...STABLE_PLAYER, 'settlementId', 'x', 'y'])
            assert.equal(row[key], rows.get(row.controllerId)[key], row.controllerId + '/' + key);
        for (const key of [...STABLE_WORLD, 'nextHandle']) assert.equal(after.worlds[i][key], before.worlds[i][key]);
    }
}
export function assertLeaveTransition(before, after, controller) {
    assertPlayers(before.players); assertPlayers(after.players); assertWorld(before.worlds); assertWorld(after.worlds);
    for (let viewpoint = 0; viewpoint < 2; viewpoint++) {
        const previous = new Map(before.players[viewpoint].map(row => [row.controllerId, row]));
        for (const row of after.players[viewpoint]) {
            const prior = previous.get(row.controllerId); assert.ok(prior);
            for (const key of STABLE_PLAYER) assert.equal(row[key], prior[key], row.controllerId + '/' + key);
            if (row.controllerId === controller) {
                assert.equal(prior.settlementId, TOWN.id); assert.equal(row.settlementId, null);
            } else for (const key of ['settlementId', 'x', 'y']) assert.equal(row[key], prior[key], 'Other player changed ' + key);
        }
        for (const key of STABLE_WORLD) assert.equal(after.worlds[viewpoint][key], before.worlds[viewpoint][key], 'Leave changed world ' + key);
        assert.ok(after.worlds[viewpoint].nextHandle >= before.worlds[viewpoint].nextHandle);
    }
}
export function leaveApplications(text) {
    return [...text.matchAll(/Settlement leave applied controller=("[^"]*"|\S+) epoch=(\d+) sequence=(\d+) party=(\d+)/g)]
        .map(match => ({ controller: match[1].startsWith('"') ? JSON.parse(match[1]) : match[1],
            epoch: Number(match[2]), sequence: Number(match[3]), party: Number(match[4]) }));
}
export class OnceActions {
    #dispatched = new Set();
    async run(key, action) {
        assert.ok(!this.#dispatched.has(key), 'Mutation already dispatched: ' + key);
        this.#dispatched.add(key);
        return action();
    }
}
export function assertStopped(stopped, expectedCount) {
    assert.equal(stopped.state, 'stopped'); assert.ok(stopped.instances?.length > 0);
    if (expectedCount !== undefined) assert.equal(stopped.instances.length, expectedCount);
    assert.ok(stopped.instances.every(row => row.cleanupComplete === true && row.processAlive === false && row.processTreeAlive === false));
}

export async function run(config) {
    assert.ok((await stat(join(config.repo, '.git'))).isDirectory(), 'Live scenario requires coordinator main checkout');
    assert.equal(await realpath(execFileSync('git', ['rev-parse', '--show-toplevel'],
        { cwd: config.repo, encoding: 'utf8', windowsHide: true }).trim()), await realpath(config.repo));
    for (const path of [config.server, config.config]) assert.ok((await stat(path)).isFile(), `Missing file: ${path}`);
    const profile = JSON.parse(await readFile(config.config, 'utf8')).profiles?.[config.profile];
    assert.ok(profile?.listenAuthority && profile.serverPlatformId && profile.clientPlatformIds?.[0]);
    assert.notEqual(profile.serverPlatformId, profile.clientPlatformIds[0]);
    assert.ok(!profile.clientProxyPort, 'Use the original direct-IP profile without a proxy');
    const sdk = join(config['sdk-root'], '@modelcontextprotocol/sdk/dist/esm/client');
    const [{ Client }, { StdioClientTransport }] = await Promise.all([
        import(pathToFileURL(join(sdk, 'index.js')).href), import(pathToFileURL(join(sdk, 'stdio.js')).href),
    ]);
    await mkdir(dirname(config.record), { recursive: true });
    const summaryPath = config.record + '.summary.json';
    await writeFile(config.record, '', { flag: 'wx' });
    await writeFile(summaryPath, '{}', { flag: 'wx' });
    const summary = { scenario: 'p2p-observer-leave', startedUtc: new Date().toISOString(),
        codeHead: execFileSync('git', ['rev-parse', 'HEAD'], { cwd: config.repo, encoding: 'utf8', windowsHide: true }).trim(),
        config, runId: null, features: [], screenshots: [], operations: [],
        failed: null, cleanupConfirmed: false, passed: false,
        scope: 'Trusted QA native writer-only Danustica preparation and both observer exits; not ordinary observer travel/entry, packet faults, natural daily cycles, cold saves, or battle.' };
    let runId, callId = 0, initial, readDeadline;
    const once = new OnceActions();
    const client = new Client({ name: 'codex-p2p-observer-leave', version: '1.0.0' });
    const transport = new StdioClientTransport({ command: config.server, args: ['--config', config.config],
        cwd: config.repo, stderr: 'inherit', maxBufferSize: 16 * 1024 * 1024 });
    const logState = Object.fromEntries(INSTANCES.map(instance => [instance, { text: '', cursor: undefined }]));
    const emit = entry => appendFile(config.record, JSON.stringify({ utc: new Date().toISOString(), ...entry }) + '\n');
    const saveSummary = () => writeFile(summaryPath, JSON.stringify(summary, null, 2) + '\n');

    async function call(name, args, mutation = false) {
        assert.ok(!(readDeadline && mutation), 'Mutations cannot run in a polling callback');
        const remaining = readDeadline ? readDeadline - Date.now() : 360000;
        assert.ok(remaining > 0, 'Read-only convergence deadline expired');
        const id = ++callId;
        await emit({ phase: 'request', callId: id, name, args, mutation });
        let raw;
        try { raw = await client.callTool({ name, arguments: args }, undefined, { timeout: remaining }); }
        catch (error) {
            await emit({ phase: 'transport_error', callId: id, name, error: error.stack,
                outcomeUncertain: mutation, noMutationRetry: mutation });
            throw error;
        }
        let data;
        try { data = raw.structuredContent ?? JSON.parse(raw.content.find(item => item.type === 'text').text); }
        catch (error) { await emit({ phase: 'decode_error', callId: id, name, raw }); throw error; }
        if (name === 'start_run' && data.runId) { runId = data.runId; summary.runId = runId; }
        await emit({ phase: 'response', callId: id, name, args, data, raw });
        assert.ok(!raw.isError, `${name} MCP error: ${JSON.stringify(data)}`);
        console.log(JSON.stringify({ tool: name, instance: args.instance, callId: id, runId,
            status: data.state ?? data.ok ?? data.outcome ?? data.result?.reached }));
        return data;
    }
    async function result(instance, name, args = [], mutation = false) {
        const response = await call('execute_command', { run_id: runId, instance, name, arguments: args }, mutation);
        assert.equal(response.ok, true, JSON.stringify(response));
        assert.ok(response.result, 'Missing command result');
        return response.result;
    }
    async function command(instance, name, args = [], mutation = false) {
        const response = await result(instance, name, args, mutation);
        assert.equal(response.succeeded, true, JSON.stringify(response));
        return response.output;
    }
    const state = instance => command(instance, 'coop.debug.authority.state').then(jsonOutput);
    const world = instance => command(instance, 'coop.debug.authority.world_state').then(jsonOutput);
    const inspect = instance => command(instance, 'coop.debug.recruitment.inspect').then(jsonOutput);
    const playerState = (instance, controller) => command(instance, 'coop.debug.players.party_state', [controller]).then(jsonOutput);
    const bothStates = () => Promise.all(INSTANCES.map(state));
    async function poll(label, readAndAssert, timeout = 30000) {
        const deadline = Date.now() + timeout;
        let lastError;
        do {
            const previousDeadline = readDeadline;
            try { readDeadline = deadline; return await readAndAssert(); }
            catch (error) {
                // Poll only read/assert callbacks. Mutating commands never run in this retry loop.
                lastError = error;
                await emit({ phase: 'poll_pending', label, reason: error.message });
            }
            finally { readDeadline = previousDeadline; }
            await delay(500);
        } while (Date.now() < deadline);
        throw new Error(`${label} did not converge: ${lastError?.message}`, { cause: lastError });
    }
    async function awaitWriter(controller) {
        return poll('confirmed writer ' + controller, async () => {
            const states = await bothStates();
            for (const value of states) {
                assert.equal(writer(value), controller); assert.equal(leasePaused(value), false);
                assert.equal(value.frozen, false); assert.equal(value.localAuthority, value.controller === controller);
            }
            assert.equal(epoch(states[0]), epoch(states[1]));
            if (initial) assertWriter(states, controller, initial);
            return states;
        });
    }
    async function registrations() {
        const pair = [];
        for (const instance of INSTANCES) {
            const entries = [];
            for (const actor of initial) {
                const entry = await playerState(instance, actor.controller);
                assert.equal(entry.controllerId, actor.controller);
                assert.equal(entry.active, true); assert.equal(entry.partyRegistrations, 1);
                assert.equal(entry.heroRegistrations, 1); assert.equal(entry.clanRegistrations, 1);
                assert.equal(entry.registeredPlayers, 2);
                if (instance === 'server') assert.equal(entry.mode, 'ListenAuthority');
                assert.equal(entry.heroStringId, actor.mainHero); assert.equal(entry.partyStringId, actor.mainParty);
                entries.push(entry);
            }
            pair.push(entries);
        }
        assertPlayers(pair);
        assertActors(await bothStates(), initial);
        return pair;
    }
    async function stableWorld(label) {
        return poll(label, async () => {
            const worlds = await Promise.all(INSTANCES.map(world));
            assertWorld(worlds); await registrations();
            return worlds;
        });
    }
    async function readSnapshot() {
        const worlds = await Promise.all(INSTANCES.map(world)); assertWorld(worlds);
        return { worlds, players: await registrations(), states: await bothStates() };
    }
    const snapshot = label => poll(label, readSnapshot);
    async function pause() {
        await command('server', 'coop.debug.set_time_mode', ['Pause', 'force-live-test'], true);
        return stableWorld('stopped critical world');
    }
    async function waitState(instance, value, seconds = 300) {
        const response = await call('wait_for_state', { run_id: runId, instance, state: value, timeout_seconds: seconds });
        assert.equal((response.result ?? response).reached, true, JSON.stringify(response));
    }
    async function screenshot(instance, label) {
        const requested = await call('screenshot', { run_id: runId, instance }, true);
        assert.equal(requested.ok, true, JSON.stringify(requested));
        const id = requested.result?.captureId;
        assert.ok(id, 'Missing screenshot captureId; request will not be retried');
        const capture = await poll('BMP capture ' + id, async () => {
            const response = await call('screenshot_status', { run_id: runId, instance, capture_id: id });
            assert.equal(response.ok, true); assert.equal(response.result.complete, true);
            assert.ok(response.result.isBmp && response.result.headerValid && response.result.stable);
            assert.ok(response.result.path && response.result.sha256);
            return response.result;
        }, 60000);
        summary.screenshots.push({ instance, label, ...capture });
    }
    async function collectLogs(archived = false) {
        const errors = [];
        for (const instance of INSTANCES) {
            try {
            const log = logState[instance];
            for (let page = 0; page < 1024; page++) {
                const response = await call('read_logs', { run_id: runId, instance, cursor: log.cursor, max_bytes: 65536 });
                const chunk = response.result ?? response;
                assert.equal(typeof chunk.text, 'string'); assert.equal(typeof chunk.cursor, 'string');
                assert.equal(typeof chunk.reset, 'boolean'); assert.equal(typeof chunk.hasMore, 'boolean');
                if (chunk.reset) {
                    assert.ok(archived, 'Live log replaced or truncated; whole-run evidence is incomplete');
                    log.text += '\n--- retained prior log before archive reset ---\n';
                }
                log.text += chunk.text; log.cursor = chunk.cursor;
                if (!chunk.hasMore) break;
                assert.ok(page < 1023, 'Log page budget exhausted; evidence incomplete');
            }
            const path = config.record + '.' + instance + '.log';
            await writeFile(path, log.text);
            const critical = logDiagnostics(log.text);
            summary.logs ??= {};
            summary.logs[instance] = { path, sha256: createHash('sha256').update(log.text).digest('hex'), critical };
            assert.equal(critical.realTick + critical.duplicateIds + critical.aiBatch + critical.errorLines.length, 0, `${instance} critical errors: ${JSON.stringify(critical)}`);
            } catch (error) {
                errors.push(`${instance}: ${error.message}`);
                await emit({ phase: 'log_evidence_error', instance, error: error.stack });
            }
        }
        assert.deepEqual(errors, [], 'Log capture/critical error verification failed');
    }
    async function feature(name, action) {
        const row = { name, startedUtc: new Date().toISOString(), status: 'RUNNING' };
        summary.features.push(row); await saveSummary();
        try {
            row.evidence = await action(); await collectLogs();
            if (row.evidence?.kind === 'observer-leave') {
                const value = row.evidence;
                value.nativeApplyLogs = leaveApplications(logState[value.writerInstance].text)
                    .filter(item => item.controller === value.controller && item.epoch === value.epoch);
                assert.equal(value.nativeApplyLogs.length, 1, 'Observer request must execute exactly once on the writer');
            }
            for (const instance of INSTANCES) await screenshot(instance, name);
            row.status = 'PASS';
        } catch (error) {
            row.status = 'FAIL'; row.error = error.stack;
            try { await collectLogs(); } catch (diagnostic) { row.logError = diagnostic.stack; }
            for (const instance of INSTANCES) { try { await screenshot(instance, name + '-failed'); } catch (diagnostic) { row.captureError = diagnostic.stack; } }
            throw error;
        } finally {
            row.completedUtc = new Date().toISOString(); await emit({ phase: 'feature_result', feature: row }); await saveSummary();
        }
    }
    async function tickForFiveSeconds(instance) {
        const npcBefore = (await world(instance)).npcSample;
        assert.ok(npcBefore.length > 0, 'No native NPC sample');
        const other = instance === 'server' ? 'client1' : 'server';
        await command('server', 'coop.debug.set_time_mode', ['Play_1x', 'force-live-test'], true);
        await awaitWriter(initial[INSTANCES.indexOf(instance)].controller);
        const before = { writer: await state(instance), observer: await state(other) };
        await delay(5000);
        const after = { writer: await state(instance), observer: await state(other) };
        assert.equal(epoch(after.writer), epoch(before.writer));
        assert.ok(after.writer.worldTicks > before.writer.worldTicks, 'Writer native Campaign.Tick did not advance');
        assert.ok(after.writer.hours > before.writer.hours, 'Writer world time did not advance');
        assert.equal(after.observer.worldTicks, before.observer.worldTicks, 'Observer advanced Campaign.Tick');
        const worlds = await pause();
        const npcAfter = worlds[INSTANCES.indexOf(instance)].npcSample;
        const previousNpc = new Map(npcBefore.map(row => [row.id, row]));
        const movedNpcs = npcAfter.filter(row => previousNpc.has(row.id) &&
            (previousNpc.get(row.id).x !== row.x || previousNpc.get(row.id).y !== row.y)).map(row => row.id);
        return { before, after, worlds, npcBefore, npcAfter, movedNpcs,
            naturalDailyOrFullAiScheduleAccepted: false };
    }
    async function settlementReplicas(instance, settlement, label) {
        const controller = initial[INSTANCES.indexOf(instance)].controller;
        return poll(label, async () => {
            const values = await Promise.all(INSTANCES.map(inspect));
            assertTown(values, controller, settlement);
            assert.equal(values[INSTANCES.indexOf(instance)].settlement, settlement);
            await registrations();
            return values;
        });
    }
    async function ensureSettlement(instance, selected) {
        const { id: townId, location } = selected;
        await awaitWriter(initial[INSTANCES.indexOf(instance)].controller);
        const current = await inspect(instance);
        if (current.settlement === townId) return { alreadyInside: true, townId,
            replicas: await settlementReplicas(instance, townId, 'existing native settlement ' + instance) };
        assert.equal(current.settlement, null, 'Previous native settlement must be left before town preparation');
        const firstEntry = await command(instance, 'coop.debug.location.enter', [location], true);
        let view = await call('get_run', { run_id: runId });
        let mission = firstEntry.startsWith('Opening location') ||
            view.instances.find(value => value.name === instance)?.status?.missionActive;
        if (!mission && (await inspect(instance)).settlement !== townId) {
            // First call may initialize only PlayerEncounter; a second call opens the observed location.
            const secondEntry = await command(instance, 'coop.debug.location.enter', [location], true);
            view = await call('get_run', { run_id: runId });
            mission = secondEntry.startsWith('Opening location') ||
                view.instances.find(value => value.name === instance)?.status?.missionActive;
        }
        if (mission) {
            await waitState(instance, 'readyForMissionTests');
            await command(instance, 'coop.debug.location.leave', [], true);
            // Campaign readiness also remains true inside MissionState; observe native return explicitly.
            await poll('native MapState after location leave ' + instance, async () =>
                assertNativeMap(await call('get_run', { run_id: runId }), instance));
        }
        return { townId, replicas: await settlementReplicas(instance, townId, 'native settlement ' + instance) };
    }
    async function leaveObserver(instance, label) {
        const controller = initial[INSTANCES.indexOf(instance)].controller;
        const expectedWriter = initial[instance === 'server' ? 1 : 0].controller;
        await awaitWriter(expectedWriter); await pause();
        const before = await snapshot(label + ' before');
        await settlementReplicas(instance, TOWN.id, label + ' starts inside Danustica');
        const submitted = await once.run(label, () => command(instance, 'coop.debug.siege.leave_settlement', [], true));
        assert.ok(submitted.includes('Requested'), 'Observer command executed a local native consequence instead of requesting');
        const after = await poll(label + ' authoritative exit', async () => {
            assertWriter(await bothStates(), expectedWriter, initial);
            const receipt = await readSnapshot(); assertLeaveTransition(before, receipt, controller);
            const ui = { evidence: await command(instance, 'coop.debug.ui.evidence_map_state'),
                movement: await command(instance, 'coop.debug.ui.map_movement_state') };
            ui.status = assertNativeMap(await call('get_run', { run_id: runId }), instance);
            assertUiClosed(ui.evidence, ui.movement, ui.status);
            return { ...receipt, ui };
        });
        const repeated = await once.run(label + ' outside-repeat', () => result(instance, 'coop.debug.siege.leave_settlement', [], true));
        assert.equal(repeated.succeeded, false); assert.ok(repeated.output.includes('not in a settlement'));
        const unchanged = await snapshot(label + ' repeat unchanged');
        assertNoWorldEffect(after, unchanged);
        const operation = { kind: 'observer-leave', label, instance, controller, expectedWriter,
            writerInstance: instance === 'server' ? 'client1' : 'server', epoch: epoch(before.states[0]),
            submitted, repeated, before, after, unchanged };
        summary.operations.push(operation); return operation;
    }

    try {
        await client.connect(transport);
        const started = await call('start_run', { profile: config.profile, client_count: 0, save_name: config.save }, true);
        assert.ok(runId); assert.equal(started.error, null);
        await waitState('server', 'readyForCampaignTests');
        await delay(1000); // A is loaded before a second game process is launched.
        const launched = await call('start_client', { run_id: runId, client_index: 1 }, true);
        assert.equal(launched.outcome, 'launched'); assert.equal(launched.instance, 'client1');
        assert.equal(launched.run.error, null);
        await waitState('client1', 'readyToJoin', 180);
        const joined = await call('join_client', { run_id: runId, instance: 'client1' }, true);
        assert.equal(joined.ok, true); await waitState('client1', 'readyForCampaignTests');
        initial = await bothStates(); assert.notEqual(initial[0].controller, initial[1].controller);
        assert.deepEqual(initial.map(value => value.controller), [profile.serverPlatformId, profile.clientPlatformIds[0]]);
        for (const value of initial) assert.ok(value.mainHero && value.mainParty);
        for (const instance of INSTANCES) {
            const catalog = JSON.stringify(await call('list_commands', { run_id: runId, instance }));
            for (const name of ['coop.debug.authority.state', 'coop.debug.authority.world_state',
                'coop.debug.authority.availability', 'coop.debug.recruitment.inspect', 'coop.debug.players.party_state',
                'coop.debug.location.list', 'coop.debug.location.enter', 'coop.debug.location.leave',
                'coop.debug.siege.leave_settlement', 'coop.debug.ui.evidence_map_state', 'coop.debug.ui.map_movement_state'])
                assert.ok(catalog.includes(name), instance + ' lacks ' + name);
        }
        await feature('join-single-native-actors', async () => {
            await awaitWriter(initial[0].controller);
            const actors = await poll('active unique player registrations', registrations);
            const view = await call('get_run', { run_id: runId });
            assert.equal(view.instances.length, 2, 'Expected exactly two owned player processes');
            const [a, b] = INSTANCES.map(name => view.instances.find(value => value.name === name));
            assert.ok(a?.processAlive && b?.processAlive);
            assert.ok(a.identity.pid > 0 && b.identity.pid > 0 && a.identity.pid !== b.identity.pid);
            assert.equal(a.status.buildVersion, b.status.buildVersion);
            const assemblies = instance => instance.status.loadedAssemblies.map(value => [value.name, value.mvid])
                .sort((left, right) => left[0].localeCompare(right[0]));
            assert.deepEqual(assemblies(a), assemblies(b), 'Player game processes loaded different mod assemblies');
            const rows = JSON.parse(execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-WindowStyle', 'Hidden', '-Command',
                "@(Get-CimInstance Win32_Process | Where-Object { $_.Name -match '^Bannerlord.*\\.exe$|DedicatedServer' } | Select-Object Name,ProcessId,CreationDate,CommandLine) | ConvertTo-Json -Compress -Depth 4"],
                { encoding: 'utf8', windowsHide: true }));
            assertProcessEvidence(rows, [a.identity.pid, b.identity.pid]);
            await writeFile(config.record + '.processes.json', JSON.stringify(rows, null, 2));
            return { actors, processes: view.instances, osProcesses: rows };
        });
        await feature('native-Danustica-writer-only-preparation', async () => {
            await pause();
            const locations = await command('server', 'coop.debug.location.list');
            assert.ok([...locations.matchAll(/Id: '([^']+)'/g)].some(match => match[1] === TOWN.location), 'Native Danustica tavern was not observed');
            await awaitWriter(initial[0].controller);
            const a = await ensureSettlement('server', TOWN);
            await command('server', 'coop.debug.authority.availability', ['auto'], true);
            await awaitWriter(initial[0].controller); await pause();
            await command('server', 'coop.debug.authority.availability', ['yield'], true);
            await awaitWriter(initial[1].controller); await pause();
            const b = await ensureSettlement('client1', TOWN);
            await command('server', 'coop.debug.authority.availability', ['auto'], true);
            await awaitWriter(initial[0].controller); await pause();
            await settlementReplicas('server', TOWN.id, 'A native Danustica');
            await settlementReplicas('client1', TOWN.id, 'B native Danustica');
            return { a, b, snapshot: await snapshot('prepared players'), trustedQaLifecyclePreparation: true };
        });
        await feature('B-observer-exits-through-A-writer', () => leaveObserver('client1', 'B-observer-exit'));
        await feature('A-to-B-final-checkpoint', async () => {
            await command('server', 'coop.debug.authority.availability', ['yield'], true);
            const states = await awaitWriter(initial[1].controller); return { states, worlds: await pause() };
        });
        await feature('A-observer-exits-through-B-writer', () => leaveObserver('server', 'A-observer-exit'));
        await feature('B-sole-writer-time-and-NPC-sample', () => tickForFiveSeconds('client1'));
        await feature('B-to-A-final-checkpoint', async () => {
            await command('server', 'coop.debug.authority.availability', ['auto'], true);
            const states = await awaitWriter(initial[0].controller); return { states, worlds: await pause() };
        });
        await feature('A-sole-writer-time-and-NPC-sample', () => tickForFiveSeconds('server'));
        await feature('final-strict-world-and-actors', async () => {
            await pause(); const receipt = await snapshot('final strict replicas');
            for (const actor of initial) await settlementReplicas(INSTANCES[initial.indexOf(actor)], null, 'final actors outside');
            return receipt;
        });
    } catch (error) {
        summary.failed = { message: error.message, stack: error.stack };
        await emit({ phase: 'scenario_failure', ...summary.failed }); process.exitCode = 1;
        if (runId) {
            try { await call('get_run', { run_id: runId }); } catch {}
            for (const instance of INSTANCES) { try { await state(instance); } catch {} }
            try { await collectLogs(); } catch (diagnostic) { await emit({ phase: 'diagnostic_failure', error: diagnostic.stack }); }
        }
    } finally {
        if (runId) {
            try {
                const stopped = await call('stop_run', { run_id: runId }, true); summary.stopped = stopped;
                assertStopped(stopped, initial ? 2 : undefined);
                summary.cleanupConfirmed = true;
            } catch (error) {
                summary.cleanupError = { message: error.message, stack: error.stack }; process.exitCode = 1;
            }
            if (summary.cleanupConfirmed) {
                try { await collectLogs(true); }
                catch (error) { summary.logVerificationError = error.message; process.exitCode = 1; }
            }
        }
        try { await client.close(); }
        catch (error) { summary.transportCloseError = error.message; process.exitCode = 1; }
        summary.completedUtc = new Date().toISOString();
        summary.passed = !summary.failed && !summary.cleanupError && !summary.logVerificationError &&
            !summary.transportCloseError && summary.cleanupConfirmed;
        if (!summary.passed) process.exitCode = 1;
        await saveSummary();
        console.log(JSON.stringify({ scenario: summary.scenario, runId, passed: summary.passed,
            cleanupConfirmed: summary.cleanupConfirmed, features: summary.features.length, summaryPath }));
    }
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
    if (process.argv.includes('--help')) console.log(HELP);
    else await run(options(process.argv.slice(2)));
}
