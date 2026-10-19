import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { appendFile, mkdir, readFile, realpath, stat, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { assertActors, assertPlayers, assertProcessEvidence, assertProcessLogCompleteness, assertWorld, assertWriter,
    epoch, field, jsonOutput, logDiagnostics, paused, writer } from './p2p-udp-reliability.mjs';
import { allocationCheckpoint, assertAllocationCheckpointFloor, assertNoPollingMutation,
    assertSaleRegistrations, assertStopped, createReadDeadlineScope, OnceActions,
    options, pollReadOnly } from './p2p-prisoner-sale.mjs';
import { collectNativeLog } from './native-log-archive.mjs';

export { options };
export const HELP = `Usage: node tools/mcp/scenarios/p2p-mission-lifecycle.mjs
  --repo <coordinator checkout> --server <CoopMcpServer.exe> --config <direct profiles.json>
  --sdk-root <node_modules> --record <new evidence.jsonl>
  [--profile local] [--save P1B_DirectIP_Recruit]

Coordinator only, under tools/coordination/with-game-lock.mjs. Reuses original
location enter/leave and automatic Mission availability, without yield overrides.
Two real native processes: A Mission / B map writer; both Mission / paused world;
B returns / B resumes; A returns / A preferred writer. Real MapState and stable
BMP frame receipts are mandatory. Each mutation is dispatched once; an explicit
Started encounter response permits one separate location-open request.
No deployment, forced time advance, Mission core rewrite, battle-result or
natural-day acceptance. Import and --help are offline.`;

const INSTANCES = ['server', 'client1'];
const LOCATION = 'Location_town_ES1_tavern';
const MAP_STATE = 'TaleWorlds.CampaignSystem.GameState.MapState';
const MISSION_STATE = 'TaleWorlds.MountAndBlade.MissionState';

export function nextEntryPhase(output) {
    if (output === "Started a local encounter with 'town_ES1'.") return 'open-location';
    if (output === `Opening location '${LOCATION}'.`) return 'await-mission';
    throw new Error('Uncertain native location entry; do not retry: ' + output);
}

export function assertNativeState(status, mission) {
    assert.ok(status, 'Missing native state receipt');
    assert.equal(status.missionActive, mission);
    assert.equal(status.activeState, mission ? MISSION_STATE : MAP_STATE,
        'Mission pointer alone does not establish active native lifecycle state');
}

export function assertEngineProgress(before, after) {
    assert.equal(before.pid, after.pid, 'Native process changed during Mission observation');
    assert.ok(Number.isSafeInteger(before.pid) && before.pid > 0);
    for (const capture of [before, after]) {
        assert.ok(capture.complete && capture.stable && capture.headerValid && capture.isBmp);
        assert.match(capture.sha256, /^[a-fA-F0-9]{64}$/);
        assert.ok(Number.isSafeInteger(capture.captureRequestEngineFrame));
        assert.ok(Number.isSafeInteger(capture.observationEngineFrame));
        assert.ok(capture.observationEngineFrame >= capture.captureRequestEngineFrame);
    }
    assert.ok(after.captureRequestEngineFrame > before.observationEngineFrame,
        'Two successful commands do not prove native engine frames continued');
}

export function assertMissionProgress(before, after) {
    for (const state of [before, after]) {
        assert.equal(state.gameState, 'MissionState');
        assert.ok(state.mission && Number.isFinite(state.mission.time), 'Missing native Mission logical time');
        assert.equal(state.mission.ended, false); assert.equal(state.mission.ending, false);
        assert.ok(state.mission.scene); assert.equal(state.controller, before.controller);
        assert.equal(state.mission.scene, before.mission.scene);
    }
    assert.ok(after.mission.time > before.mission.time, 'Mission logical time did not advance');
}

export function assertAllAway(states, initial) {
    assertActors(states, initial);
    for (const state of states) {
        assert.equal(state.gameState, 'MissionState'); assert.equal(paused(state), true);
        assert.equal(state.localAuthority, false); assert.equal(state.frozen, false);
        assert.equal(state.yielded, false, 'Availability override masks automatic Mission coordination');
    }
    for (const key of ['roomId', 'authorityId', 'authorityControllerId', 'epoch'])
        assert.equal(field(states[0].lease, key), field(states[1].lease, key));
    assert.ok(Number.isSafeInteger(epoch(states[0])) && epoch(states[0]) > 0);
}

export function assertNoWorldProgress(before, after, initial) {
    assertAllAway(before.states, initial); assertAllAway(after.states, initial);
    assertWorld(before.worlds); assertWorld(after.worlds);
    for (let index = 0; index < 2; index++) {
        assert.deepEqual(after.states[index].lease, before.states[index].lease);
        for (const key of ['hours', 'worldTicks'])
            assert.equal(after.states[index][key], before.states[index][key], 'All-away world advanced: ' + key);
        // Exact same-peer hashes/counters are expected only while no writer exists.
        assert.deepEqual(after.worlds[index], before.worlds[index], 'All-away world snapshot changed');
        assert.deepEqual(after.cycles[index], before.cycles[index], 'Cycle dispatcher ran while all players were away');
    }
    assert.deepEqual(after.players, before.players, 'All-away player resources or identities changed');
}

export function assertSingleWriterProgress(before, after, writerIndex, initial) {
    const controller = initial[writerIndex].controller;
    assertWriter(before, controller, initial); assertWriter(after, controller, initial);
    for (let index = 0; index < 2; index++) {
        assert.equal(epoch(after[index]), epoch(before[index]), 'Unexpected authority transition inside observation');
        assert.equal(after[index].yielded, false);
        if (index === writerIndex) {
            assert.ok(after[index].worldTicks > before[index].worldTicks, 'Writer native world Tick did not progress');
            assert.ok(after[index].hours > before[index].hours, 'Writer world clock did not progress');
        } else assert.equal(after[index].worldTicks, before[index].worldTicks, 'Observer advanced authoritative world Tick');
    }
}

export async function run(config) {
    assert.ok((await stat(join(config.repo, '.git'))).isDirectory(), 'Use coordinator main checkout');
    const git = args => execFileSync('git', args, { cwd: config.repo, encoding: 'utf8', windowsHide: true }).trim();
    assert.equal(await realpath(config.repo), await realpath(git(['rev-parse', '--show-toplevel'])));
    const profile = JSON.parse(await readFile(config.config, 'utf8')).profiles?.[config.profile];
    assert.ok(profile?.listenAuthority && !profile.clientProxyPort, 'Direct-IP ListenAuthority profile required');
    const sdk = join(config['sdk-root'], '@modelcontextprotocol/sdk/dist/esm/client');
    const [{ Client }, { StdioClientTransport }] = await Promise.all([
        import(pathToFileURL(join(sdk, 'index.js')).href), import(pathToFileURL(join(sdk, 'stdio.js')).href),
    ]);
    await mkdir(dirname(config.record), { recursive: true });
    await writeFile(config.record, '', { flag: 'wx' });
    const summaryPath = config.record + '.summary.json';
    const summary = { scenario: 'p2p-mission-lifecycle', codeHead: git(['rev-parse', 'HEAD']), config,
        startedUtc: new Date().toISOString(), features: [], screenshots: [], logs: {}, runId: null,
        cleanupConfirmed: false, passed: false, network: 'Direct IP; no injected packet faults',
        scope: 'Native tavern Mission coordination and all-away pause/resume, not battle results or natural-day acceptance' };
    await writeFile(summaryPath, JSON.stringify(summary, null, 2), { flag: 'wx' });
    const save = () => writeFile(summaryPath, JSON.stringify(summary, null, 2) + '\n');
    const emit = entry => appendFile(config.record, JSON.stringify({ utc: new Date().toISOString(), ...entry }) + '\n');
    const client = new Client({ name: 'codex-p2p-mission-lifecycle', version: '1.0.0' });
    const transport = new StdioClientTransport({ command: config.server, args: ['--config', config.config],
        cwd: config.repo, stderr: 'inherit', maxBufferSize: 16 * 1024 * 1024 });
    const deadlines = createReadDeadlineScope(), once = new OnceActions();
    const logs = INSTANCES.map(() => ({ text: '', cursor: undefined }));
    let runId, initial, processPids, callId = 0, allocationFloor;
    async function call(name, args, mutation = false) {
        const deadline = deadlines.currentDeadline(); assertNoPollingMutation(deadline, mutation);
        const timeout = deadline === undefined ? 360000 : deadline - Date.now();
        assert.ok(timeout > 0, 'Read-only convergence deadline expired');
        const id = ++callId; await emit({ phase: 'request', callId: id, name, args, mutation });
        let raw;
        try { raw = await client.callTool({ name, arguments: args }, undefined, { timeout }); }
        catch (error) {
            await emit({ phase: 'transport_error', callId: id, name, error: error.stack, outcomeUncertain: mutation }); throw error;
        }
        const data = raw.structuredContent ?? JSON.parse(raw.content.find(item => item.type === 'text').text);
        if (name === 'start_run' && data.runId) { runId = data.runId; summary.runId = runId; }
        await emit({ phase: 'response', callId: id, name, args, data, raw });
        assert.ok(!raw.isError, `${name}: ${JSON.stringify(data)}`);
        return name === 'read_logs' ? { result: data.result ?? data, qaCallId: id } : data;
    }
    async function command(instance, name, args = [], mutation = false) {
        const data = await call('execute_command', { run_id: runId, instance, name, arguments: args }, mutation);
        assert.equal(data.ok, true); assert.equal(data.result?.succeeded, true, `${name}: ${JSON.stringify(data)}`);
        return data.result.output;
    }
    const state = instance => command(instance, 'coop.debug.authority.state').then(jsonOutput);
    const bothStates = () => Promise.all(INSTANCES.map(state));
    async function poll(label, read, timeoutMs = 30000) {
        return pollReadOnly(label, deadline => deadlines.run(deadline, read), { timeoutMs,
            onPending: error => emit({ phase: 'poll_pending', label, error: error.message }) });
    }
    async function waitReady(instance, stateName) {
        const data = await call('wait_for_state', { run_id: runId, instance, state: stateName, timeout_seconds: 300 });
        assert.equal((data.result ?? data).reached, true);
    }
    async function native(instance, mission) {
        const view = await call('get_run', { run_id: runId });
        const row = view.instances?.find(value => value.name === instance);
        assert.equal(row?.identity.pid, processPids[INSTANCES.indexOf(instance)]);
        assert.equal(row.processAlive, true); assertNativeState(row.status, mission); return row;
    }
    async function screenshot(instance, label) {
        const requested = await call('screenshot', { run_id: runId, instance }, true);
        assert.equal(requested.ok, true); const captureId = requested.result?.captureId; assert.ok(captureId);
        const capture = await poll('complete native BMP ' + captureId, async () => {
            const data = await call('screenshot_status', { run_id: runId, instance, capture_id: captureId });
            assert.equal(data.ok, true); const result = data.result;
            assert.ok(result?.complete && result.stable && result.headerValid && result.isBmp && result.sha256); return result;
        }, 60000);
        const receipt = { instance, label, pid: processPids[INSTANCES.indexOf(instance)], ...capture };
        summary.screenshots.push(receipt); return receipt;
    }
    async function cycles(instance) {
        let offset = 0, first; const rows = [];
        for (let page = 0; page < 1024; page++) {
            const data = jsonOutput(await command(instance, 'coop.debug.authority.cycles', [String(offset)]));
            if (!first) first = data;
            assert.equal(data.instance, first.instance); assert.equal(data.offset, offset); assert.equal(data.overflowed, false);
            assert.equal(data.processId, processPids[INSTANCES.indexOf(instance)]);
            rows.push(...data.rows); offset = data.nextOffset;
            if (!data.hasMore) { assert.equal(rows.length, data.totalRows); return { instance: data.instance, rows }; }
        }
        throw new Error('Incomplete cycle evidence pages');
    }
    async function snapshot(label, allAway = false) {
        return poll(label, async () => {
            const states = await bothStates(); assertActors(states, initial);
            if (allAway) assertAllAway(states, initial);
            const worlds = await Promise.all(INSTANCES.map(instance => command(instance, 'coop.debug.authority.world_state').then(jsonOutput)));
            assertWorld(worlds); assertAllocationCheckpointFloor(worlds, allocationFloor);
            const players = await Promise.all(INSTANCES.map(instance => Promise.all(initial.map(actor =>
                command(instance, 'coop.debug.players.party_state', [actor.controller]).then(jsonOutput)))));
            assertSaleRegistrations(players, initial); assertPlayers(players);
            return { states, worlds, players, cycles: await Promise.all(INSTANCES.map(cycles)) };
        });
    }
    async function awaitWriter(index) {
        return poll('automatic writer ' + initial[index].controller, async () => {
            const states = await bothStates(); assertWriter(states, initial[index].controller, initial);
            assert.ok(states.every(row => row.yielded === false)); return states;
        });
    }
    async function setTime(mode) {
        await command('server', 'coop.debug.set_time_mode', [mode, 'force-live-test'], true);
    }
    async function consistentSnapshot(label) {
        await setTime('Pause'); const receipt = await snapshot(label);
        const index = receipt.states.findIndex(value => value.localAuthority); assert.ok(index >= 0);
        allocationFloor = allocationCheckpoint(receipt.worlds[index]); return receipt;
    }
    async function collectLogs(archived = false) {
        for (let index = 0; index < 2; index++) {
            const instance = INSTANCES[index], log = logs[index];
            await collectNativeLog(log, async cursor => {
                return call('read_logs', { run_id: runId, instance, cursor, max_bytes: 65536 });
            }, { archived, source: { runId, instance, rawRecord: config.record } });
            const path = config.record + '.' + instance + '.log'; await writeFile(path, log.text);
            const critical = logDiagnostics(log.text);
            summary.logs[instance] = { path, sha256: createHash('sha256').update(log.text).digest('hex'),
                collections: log.collections, critical };
            assertProcessLogCompleteness(log.text, processPids?.[index]);
            assert.equal(critical.realTick + critical.duplicateIds + critical.aiBatch + critical.errorLines.length, 0,
                'Native lifecycle exception or critical log: ' + JSON.stringify(critical));
        }
    }
    async function feature(name, action) {
        const row = { name, status: 'RUNNING', startedUtc: new Date().toISOString() };
        summary.features.push(row); await save();
        try {
            row.evidence = await action(); await collectLogs();
            for (const instance of INSTANCES) await screenshot(instance, name);
            row.status = 'PASS';
        } catch (error) { row.status = 'FAIL'; row.error = error.stack; throw error; }
        finally { row.completedUtc = new Date().toISOString(); await emit({ phase: 'feature_result', feature: row }); await save(); }
    }
    async function enter(instance) {
        await native(instance, false);
        const output = await once.run(instance + '-encounter', () => command(instance, 'coop.debug.location.enter', [LOCATION], true));
        if (nextEntryPhase(output) === 'open-location') {
            await native(instance, false);
            const opened = await once.run(instance + '-open', () => command(instance, 'coop.debug.location.enter', [LOCATION], true));
            assert.equal(nextEntryPhase(opened), 'await-mission');
        }
        await waitReady(instance, 'readyForMissionTests'); await poll('actual MissionState', () => native(instance, true));
        const first = await screenshot(instance, 'mission-entered');
        const logicalBefore = await state(instance);
        const logicalAfter = await poll('Mission logical time advances before exit eligibility', async () => {
            const current = await state(instance); assertMissionProgress(logicalBefore, current); return current;
        });
        const second = await screenshot(instance, 'mission-running');
        assertEngineProgress(first, second); await native(instance, true);
        return { output, first, second, logicalBefore, logicalAfter };
    }
    async function leave(instance) {
        await native(instance, true);
        await once.run(instance + '-leave', () => command(instance, 'coop.debug.location.leave', [], true));
        return poll('actual MapState after Mission exit', () => native(instance, false), 60000);
    }
    try {
        await client.connect(transport);
        const started = await call('start_run', { profile: config.profile, client_count: 0, save_name: config.save }, true);
        assert.ok(runId); assert.equal(started.error, null); await waitReady('server', 'readyForCampaignTests');
        const launched = await call('start_client', { run_id: runId, client_index: 1 }, true);
        assert.equal(launched.outcome, 'launched'); await waitReady('client1', 'readyToJoin');
        assert.equal((await call('join_client', { run_id: runId, instance: 'client1' }, true)).ok, true);
        await waitReady('client1', 'readyForCampaignTests'); initial = await bothStates();
        assert.deepEqual(initial.map(value => value.controller), [profile.serverPlatformId, profile.clientPlatformIds[0]]);
        const view = await call('get_run', { run_id: runId }); assert.equal(view.instances.length, 2);
        const processes = INSTANCES.map(name => view.instances.find(value => value.name === name));
        processPids = processes.map(row => row.identity.pid); assert.notEqual(...processPids);
        assert.ok(processes.every(row => row.processAlive));
        const assemblies = row => row.status.loadedAssemblies.map(value => [value.name, value.mvid]).sort();
        assert.deepEqual(assemblies(processes[0]), assemblies(processes[1]));
        assert.equal(processes[0].status.buildVersion, processes[1].status.buildVersion);
        summary.processes = processes;
        const osProcesses = JSON.parse(execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-WindowStyle', 'Hidden', '-Command',
            "@(Get-CimInstance Win32_Process | Where-Object { $_.Name -match '^Bannerlord.*\\.exe$|DedicatedServer' } | Select-Object Name,ProcessId,CreationDate,CommandLine) | ConvertTo-Json -Compress -Depth 4"],
            { encoding: 'utf8', windowsHide: true }));
        assertProcessEvidence(osProcesses, processPids); summary.osProcesses = osProcesses;
        await feature('join-two-native-actors', async () => {
            await awaitWriter(0); const receipt = await consistentSnapshot('initial world');
            const locations = await command('server', 'coop.debug.location.list');
            assert.ok([...locations.matchAll(/Id: '([^']+)'/g)].some(match => match[1] === LOCATION)); return receipt;
        });
        await feature('A-Mission-B-automatic-map-writer', async () => {
            await setTime('Play_1x'); const entry = await enter('server');
            const before = await awaitWriter(1); await native('client1', false);
            await delay(5000); const after = await bothStates(); assertSingleWriterProgress(before, after, 1, initial);
            const world = await consistentSnapshot('B writer critical world'); return { entry, before, after, world };
        });
        await feature('both-Missions-exact-world-pause-native-frames-continue', async () => {
            await setTime('Play_1x'); const entry = await enter('client1');
            const before = await snapshot('all-away committed paused lease', true);
            const first = await Promise.all(INSTANCES.map(instance => screenshot(instance, 'all-away-before')));
            await delay(5000); const after = await snapshot('all-away unchanged world', true);
            assertNoWorldProgress(before, after, initial);
            const second = await Promise.all(INSTANCES.map(instance => screenshot(instance, 'all-away-after')));
            for (let index = 0; index < 2; index++) {
                assertEngineProgress(first[index], second[index]);
                assertMissionProgress(before.states[index], after.states[index]); await native(INSTANCES[index], true);
            }
            return { entry, before, after, first, second, networkAlive: 'Both game-thread commands and native frame receipts completed' };
        });
        await feature('B-return-resumes-A-stays-in-Mission', async () => {
            await leave('client1'); const before = await awaitWriter(1); await setTime('Play_1x');
            await delay(5000); const after = await bothStates(); assertSingleWriterProgress(before, after, 1, initial);
            await native('server', true); return { before, after, world: await consistentSnapshot('B resumed world') };
        });
        await feature('A-return-host-preference-final-A-B-A', async () => {
            await setTime('Play_1x'); await leave('server'); const before = await awaitWriter(0);
            await delay(5000); const after = await bothStates(); assertSingleWriterProgress(before, after, 0, initial);
            for (const instance of INSTANCES) await native(instance, false);
            return { before, after, world: await consistentSnapshot('final A world') };
        });
    } catch (error) {
        summary.failed = { message: error.message, stack: error.stack }; process.exitCode = 1;
        await emit({ phase: 'scenario_failure', ...summary.failed });
        if (runId) {
            for (const instance of INSTANCES) {
                try { await state(instance); } catch (diagnostic) { await emit({ phase: 'diagnostic_error', instance, error: diagnostic.stack }); }
                if (processPids) try { await screenshot(instance, 'failure'); }
                catch (diagnostic) { await emit({ phase: 'capture_error', instance, error: diagnostic.stack }); }
            }
            try { await collectLogs(); } catch (diagnostic) { await emit({ phase: 'log_error', error: diagnostic.stack }); }
        }
    } finally {
        if (runId) {
            try {
                const stopped = await call('stop_run', { run_id: runId }, true); summary.stopped = stopped;
                assertStopped(stopped, initial ? 2 : undefined); summary.cleanupConfirmed = true;
            } catch (error) { summary.cleanupError = error.stack; process.exitCode = 1; }
            if (summary.cleanupConfirmed) try { await collectLogs(true); }
            catch (error) { summary.logVerificationError = error.stack; process.exitCode = 1; }
        }
        try { await client.close(); } catch (error) { summary.closeError = error.stack; process.exitCode = 1; }
        summary.completedUtc = new Date().toISOString();
        summary.passed = !summary.failed && !summary.cleanupError && !summary.logVerificationError &&
            !summary.closeError && summary.cleanupConfirmed && summary.features.length === 5;
        await save(); if (!summary.passed) process.exitCode = 1;
        console.log(JSON.stringify({ scenario: summary.scenario, runId, passed: summary.passed, summaryPath }));
    }
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
    if (process.argv.includes('--help')) console.log(HELP);
    else await run(options(process.argv.slice(2)));
}
