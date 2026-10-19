import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { appendFile, mkdir, readFile, realpath, stat, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { assertActors, assertPlayers, assertProcessEvidence, assertProcessLogCompleteness, assertWorld, assertWriter,
    epoch, field, jsonOutput, logDiagnostics, paused, proxyProfile } from './p2p-udp-reliability.mjs';
import { allocationCheckpoint, assertAllocationCheckpointFloor, assertNoPollingMutation,
    assertSaleRegistrations, assertStopped, createReadDeadlineScope, OnceActions,
    options, pollReadOnly } from './p2p-prisoner-sale.mjs';
import { collectNativeLog } from './native-log-archive.mjs';
import { UdpFaultProxy } from './udp-fault-proxy.mjs';
import { assertFrozenMovementPair, assertFrozenNpcSamples } from './frozen-movement-evidence.mjs';

export { options };
export const HELP = `Usage: node tools/mcp/scenarios/p2p-mission-lifecycle.mjs
  --repo <coordinator checkout> --server <CoopMcpServer.exe> --config <profiles.json>
  --sdk-root <node_modules> --record <new evidence.jsonl>
  [--profile local] [--save P1B_DirectIP_Recruit]

Coordinator only, under tools/coordination/with-game-lock.mjs. Reuses original
location enter/leave and automatic Mission availability, without yield overrides.
Two real native processes: A Mission / B map writer; both Mission / paused world;
B returns / B resumes; A returns / A preferred writer. Real MapState and stable
BMP frame receipts are mandatory. Each mutation is dispatched once; an explicit
Started encounter response permits one separate location-open request.
B leaves its existing settlement once before A enters Mission and again after B
returns. Read-only native encounter/party/time receipts establish a free map player;
an occupied MapState alone is not a running-world precondition.
With clientProxyPort, the existing UDP proxy forwards without faults and measures
bidirectional traffic during all-away pause. Direct mode leaves that NOT_OBSERVED.
No deployment, forced time advance, Mission core rewrite, battle-result or
natural-day acceptance. Import and --help are offline.`;

const INSTANCES = ['server', 'client1'];
const LOCATION = 'Location_town_ES1_tavern';
const MAP_STATE = 'TaleWorlds.CampaignSystem.GameState.MapState';
const MISSION_STATE = 'TaleWorlds.MountAndBlade.MissionState';

const RUNTIME_ASSEMBLIES = ['Common', 'Coop', 'Coop.Core', 'Coop.Steam', 'GameInterface', 'Missions'];
const MVID = /^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i;

function runtimeRows(view) {
    assert.equal(view.error, null, 'Native run error');
    assert.equal(view.instances?.length, 2, 'Expected both current native process receipts');
    const rows = INSTANCES.map(name => view.instances.find(row => row.name === name));
    assert.ok(rows.every(Boolean), 'Missing or duplicate native instance');
    return rows;
}

function assemblyMetadata(status) {
    assert.ok(Array.isArray(status.loadedAssemblies), 'Missing native assembly metadata');
    const rows = status.loadedAssemblies.map(row => {
        assert.ok(typeof row.name === 'string' && row.name.length > 0, 'Missing assembly name');
        assert.match(row.mvid, MVID, 'Missing or invalid assembly MVID');
        return [row.name, row.mvid];
    }).sort();
    assert.equal(new Set(rows.map(row => row[0])).size, rows.length, 'Duplicate assembly metadata');
    for (const name of RUNTIME_ASSEMBLIES) assert.ok(rows.some(row => row[0] === name), 'Missing runtime assembly: ' + name);
    return rows;
}

export function runtimeMetadataExpectations(launchRun) {
    const rows = runtimeRows(launchRun);
    const identities = rows.map(row => {
        assert.ok(Number.isSafeInteger(row.identity?.pid) && row.identity.pid > 0);
        for (const key of ['startedUtc', 'role', 'platformId', 'runToken'])
            assert.ok(typeof row.identity[key] === 'string' && row.identity[key].length > 0, 'Missing launch identity: ' + key);
        assert.equal(row.identity.runToken, launchRun.runId);
        return structuredClone(row.identity);
    });
    assert.notEqual(identities[0].pid, identities[1].pid);
    const host = rows[0].status;
    assert.ok(host && typeof host.buildVersion === 'string' && host.buildVersion.length > 0, 'Missing launched host build anchor');
    const assemblies = assemblyMetadata(host);
    const assemblyMvid = launchRun.preflight?.bridge?.mvid;
    assert.match(assemblyMvid, MVID, 'Missing preflight bridge MVID');
    assert.equal(host.assemblyMvid, assemblyMvid);
    assert.equal(assemblies.find(row => row[0] === 'Coop')[1], assemblyMvid);
    return { runId: launchRun.runId, identities, buildVersion: host.buildVersion, assemblyMvid, assemblies };
}

export function assertRuntimeProcesses(view, expected) {
    assert.equal(view.runId, expected.runId, 'Native run changed');
    const rows = runtimeRows(view); let pending = false;
    for (let index = 0; index < rows.length; index++) {
        const row = rows[index], identity = expected.identities[index];
        assert.deepEqual(row.identity, identity, 'Native launch identity changed');
        assert.equal(row.processAlive, true, 'Native process is not alive');
        assert.ok(Object.hasOwn(row, 'error'), 'Missing native status error receipt');
        if (row.error !== null) {
            assert.ok(row.status == null && row.error.code === 'operation_cancelled' && row.error.outcomeUncertain === false,
                'Non-transient or uncertain native metadata error: ' + JSON.stringify(row.error));
        }
        if (row.status == null) { pending = true; continue; }
        const status = row.status;
        for (const [key, value] of Object.entries({ pid: identity.pid, processStartedUtc: identity.startedUtc,
            role: identity.role, platformId: identity.platformId, runToken: identity.runToken }))
            assert.equal(status[key], value, 'Native status identity mismatch: ' + key);
        // Validate every available field before permitting a retry for an incomplete peer.
        // A wrong build/MVID must not be hidden by a later favourable read.
        if (status.buildVersion == null) pending = true;
        else assert.equal(status.buildVersion, expected.buildVersion, 'Native build version changed');
        if (status.assemblyMvid == null) pending = true;
        else assert.equal(status.assemblyMvid, expected.assemblyMvid, 'Native bridge MVID changed');
        if (status.loadedAssemblies == null) pending = true;
        else assert.deepEqual(assemblyMetadata(status), expected.assemblies, 'Native assembly MVIDs changed');
    }
    if (pending) {
        const error = new Error('Current native runtime metadata is incomplete');
        error.code = 'ERR_NATIVE_METADATA_PENDING'; throw error;
    }
    return rows;
}

export async function readRuntimeProcesses({ read, expected, withDeadline = (_deadline, action) => action(),
    now = Date.now, sleep = delay, onPending = async () => {} }) {
    const result = await pollReadOnly('complete same-process native runtime metadata', deadline => withDeadline(deadline, async () => {
        try {
            const view = await read();
            assert.ok(now() < deadline, 'Native metadata read exceeded the original deadline');
            return { rows: assertRuntimeProcesses(view, expected) };
        } catch (error) {
            if (error?.code === 'ERR_NATIVE_METADATA_PENDING') throw error;
            // pollReadOnly retries ordinary assertions. Return fatal evidence and throw outside
            // that loop so identity, death, version and non-transient errors cannot be retried.
            return { fatal: error };
        }
    }), { timeoutMs: 30000, now, sleep, onPending });
    if (Object.hasOwn(result, 'fatal')) throw result.fatal;
    return result.rows;
}

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

export function parseNativePartyState(text) {
    const entries = [...text.matchAll(/(\w+)=([^\s]+)/g)].map(match => [match[1], match[2]]);
    assert.equal(new Set(entries.map(row => row[0])).size, entries.length, 'Duplicate native party-state field');
    const value = Object.fromEntries(entries);
    for (const key of ['party', 'mapEvent', 'settlement', 'encounter', 'playerSiege', 'besiegerCamp'])
        assert.ok(Object.hasOwn(value, key), 'Missing native party-state field: ' + key);
    for (const key of ['encounter', 'playerSiege']) {
        assert.ok(value[key] === 'True' || value[key] === 'False', 'Invalid native boolean: ' + key);
        value[key] = value[key] === 'True';
    }
    return value;
}

export function assertMapActor(observation, actor) {
    assert.equal(observation.instance, 'client1', 'B encounter receipt must come from its local process');
    assertNativeState(observation.status, false);
    assert.equal(observation.localParty.controllerId, actor.controller);
    assert.equal(observation.localParty.localControllerId, actor.controller, 'Remote encounter=false cannot prove local encounter closure');
    assert.equal(observation.localParty.mainHeroId, actor.mainHero);
    assert.equal(observation.localParty.mainPartyId, actor.mainParty);
    assert.equal(observation.localParty.partyStringId, actor.mainParty);
    assert.ok(typeof observation.localParty.partyId === 'string' && observation.localParty.partyId.length > 0, 'Missing registered local party id');
    assert.ok(Object.hasOwn(observation.localParty, 'settlementId'), 'Missing actual settlement receipt');
    assert.equal(observation.nativeParty.party, actor.mainParty);
    assert.equal(observation.nativeParty.mapEvent, 'none', 'Local player is still in a MapEvent');
    assert.equal(observation.nativeParty.playerSiege, false);
    assert.equal(observation.nativeParty.besiegerCamp, 'none');
    assert.equal(observation.partyReplicas.length, 2);
    for (const party of observation.partyReplicas) {
        assert.equal(party.controllerId, actor.controller); assert.equal(party.partyId, observation.localParty.partyId);
        assert.equal(party.heroStringId, actor.mainHero); assert.equal(party.partyStringId, actor.mainParty);
        assert.equal(party.active, true); assert.equal(party.mapEvent, 'none');
    }
}

export function assertFreeMapActor(observation, actor) {
    assertMapActor(observation, actor);
    assert.equal(observation.nativeParty.encounter, false, 'Local encounter has not closed');
    assert.equal(observation.nativeParty.settlement, 'none', 'Native player remains occupied in a settlement');
    assert.equal(observation.status.activeMenu, null, 'Native settlement/menu context remains active');
    for (const party of [observation.localParty, ...observation.partyReplicas])
        assert.equal(party.settlementId, null, 'Player replica still has a settlement');
}

export function assertTimeModes(modes, running = false) {
    assert.equal(modes.length, 2); assert.deepEqual(modes.map(row => row.instance), INSTANCES);
    for (const row of modes) assert.ok(['Pause', 'Play_1x', 'Play_2x'].includes(row.mode), 'Missing actual time-mode receipt');
    assert.equal(modes[0].mode, modes[1].mode, 'Selected shared time mode did not converge');
    if (running) assert.equal(modes[0].mode, 'Play_1x', 'Expected running time mode; do not force Play over an occupancy pause');
}

export async function prepareFreeMapActor({ read, leaveSettlement, poll, once, key, actor }) {
    const before = await read(); assertMapActor(before, actor);
    let leaveResult = null;
    if (before.localParty.settlementId !== null) leaveResult = await once.run(key, leaveSettlement);
    const after = await poll('confirmed B free map ' + key, async () => {
        const current = await read(); assertFreeMapActor(current, actor); assertTimeModes(current.timeModes); return current;
    });
    return { before, leaveResult, after };
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

export function assertTransportProgress(before, after) {
    const deltas = {};
    for (const value of [before, after]) {
        for (const key of ['dropped', 'delayed', 'reordered', 'heldTotal', 'heldCurrentCount', 'heldOverflow', 'errorCount'])
            assert.equal(value[key], 0, 'Transparent Mission network observation encountered a fault: ' + key);
        assert.deepEqual(value.errors, []);
    }
    for (const kind of ['received', 'sent']) {
        deltas[kind] = {};
        for (const direction of ['toHost', 'toClient']) {
            const from = before[kind]?.[direction], to = after[kind]?.[direction];
            assert.ok(Number.isSafeInteger(from) && from >= 0 && Number.isSafeInteger(to));
            assert.ok(to > from, `Game UDP did not progress: ${kind}/${direction}`);
            deltas[kind][direction] = to - from;
        }
    }
    return { acceptance: 'PASS', scope: 'Bidirectional game UDP transport liveness only; no battle-route or payload correctness claim', deltas };
}

export function assertAllAway(states, initial) {
    assertActors(states, initial);
    for (const state of states) {
        assert.equal(state.gameState, 'MissionState'); assert.equal(paused(state), true);
        assert.ok(field(state.lease, 'roomId') && field(state.lease, 'authorityId'), 'Missing paused lease identity');
        assert.notEqual(field(state.lease, 'authorityControllerId'), undefined, 'Missing paused lease controller field');
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
    assertFrozenNpcSamples(before.worlds); assertFrozenNpcSamples(after.worlds);
    assertFrozenMovementPair(before.worlds.map(world => world.movement));
    assertFrozenMovementPair(after.worlds.map(world => world.movement));
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
    const profiles = JSON.parse(await readFile(config.config, 'utf8')), profile = profiles.profiles?.[config.profile];
    assert.ok(profile?.listenAuthority, 'ListenAuthority profile required');
    assert.ok(profile.serverPlatformId && profile.clientPlatformIds?.[0]);
    assert.notEqual(profile.serverPlatformId, profile.clientPlatformIds[0]);
    const proxyOptions = profile.clientProxyPort == null ? null : proxyProfile(profiles, config.profile);
    const sdk = join(config['sdk-root'], '@modelcontextprotocol/sdk/dist/esm/client');
    const [{ Client }, { StdioClientTransport }] = await Promise.all([
        import(pathToFileURL(join(sdk, 'index.js')).href), import(pathToFileURL(join(sdk, 'stdio.js')).href),
    ]);
    await mkdir(dirname(config.record), { recursive: true });
    await writeFile(config.record, '', { flag: 'wx' });
    const summaryPath = config.record + '.summary.json';
    const summary = { scenario: 'p2p-mission-lifecycle', codeHead: git(['rev-parse', 'HEAD']), config,
        startedUtc: new Date().toISOString(), features: [], screenshots: [], logs: {}, runId: null,
        cleanupConfirmed: false, passed: false,
        transportAcceptance: 'NOT_OBSERVED',
        network: proxyOptions ? 'Direct IP through original transparent loopback UDP proxy; no injected packet faults' : 'Direct IP; transport liveness NOT_OBSERVED',
        scope: 'Native tavern Mission coordination and all-away pause/resume, not battle results or natural-day acceptance' };
    await writeFile(summaryPath, JSON.stringify(summary, null, 2), { flag: 'wx' });
    const proxy = proxyOptions ? new UdpFaultProxy({ listenPort: proxyOptions.port }) : null;
    const save = () => {
        if (proxy) summary.udp = structuredClone(proxy.stats);
        return writeFile(summaryPath, JSON.stringify(summary, null, 2) + '\n');
    };
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
            const worlds = await Promise.all(INSTANCES.map(instance => command(instance, 'coop.debug.authority.world_state', allAway ? ['positions'] : []).then(jsonOutput)));
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
    async function timeModes() {
        return Promise.all(INSTANCES.map(async instance => ({ instance,
            mode: await command(instance, 'coop.debug.get_time_mode') })));
    }
    async function runningModes(label) {
        return poll(label, async () => { const modes = await timeModes(); assertTimeModes(modes, true); return modes; });
    }
    async function readBMapActor() {
        const localParty = jsonOutput(await command('client1', 'coop.debug.players.party_state', [initial[1].controller]));
        const nativeParty = parseNativePartyState(await command('client1', 'coop.debug.siege.player_state', [localParty.partyId]));
        return { instance: 'client1', status: (await native('client1', false)).status, localParty, nativeParty,
            partyReplicas: await Promise.all(INSTANCES.map(instance => command(instance, 'coop.debug.players.party_state',
                [initial[1].controller]).then(jsonOutput))), timeModes: await timeModes() };
    }
    async function freeBMapActor(key) {
        return prepareFreeMapActor({ actor: initial[1], read: readBMapActor, poll, once, key,
            leaveSettlement: () => command('client1', 'coop.debug.siege.leave_settlement', [], true) });
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
        if (proxy) { await proxy.start(); summary.proxy = { listenPort: proxy.port, targetPort: proxy.targetPort }; }
        await client.connect(transport);
        const started = await call('start_run', { profile: config.profile, client_count: 0, save_name: config.save }, true);
        assert.ok(runId); assert.equal(started.error, null); await waitReady('server', 'readyForCampaignTests');
        const launched = await call('start_client', { run_id: runId, client_index: 1 }, true);
        assert.equal(launched.outcome, 'launched');
        const runtimeExpected = runtimeMetadataExpectations(launched.run);
        processPids = runtimeExpected.identities.map(identity => identity.pid);
        await waitReady('client1', 'readyToJoin');
        assert.equal((await call('join_client', { run_id: runId, instance: 'client1' }, true)).ok, true);
        await waitReady('client1', 'readyForCampaignTests'); initial = await bothStates();
        assert.deepEqual(initial.map(value => value.controller), [profile.serverPlatformId, profile.clientPlatformIds[0]]);
        const processes = await readRuntimeProcesses({ expected: runtimeExpected,
            read: () => call('get_run', { run_id: runId }),
            withDeadline: (deadline, read) => deadlines.run(deadline, read),
            onPending: error => emit({ phase: 'poll_pending', label: 'native runtime metadata', error: error.message }) });
        summary.processes = processes;
        const osProcesses = JSON.parse(execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-WindowStyle', 'Hidden', '-Command',
            "@(Get-CimInstance Win32_Process | Where-Object { $_.Name -match '^Bannerlord.*\\.exe$|DedicatedServer' } | Select-Object Name,ProcessId,CreationDate,CommandLine) | ConvertTo-Json -Compress -Depth 4"],
            { encoding: 'utf8', windowsHide: true }));
        assertProcessEvidence(osProcesses, processPids); summary.osProcesses = osProcesses;
        await feature('join-two-native-actors', async () => {
            await awaitWriter(0); const receipt = await consistentSnapshot('initial world');
            const freeMap = await freeBMapActor('B-initial-settlement-exit');
            const locations = await command('server', 'coop.debug.location.list');
            assert.ok([...locations.matchAll(/Id: '([^']+)'/g)].some(match => match[1] === LOCATION)); return { ...receipt, freeMap };
        });
        await feature('A-Mission-B-automatic-map-writer', async () => {
            const freeMap = await readBMapActor(); assertFreeMapActor(freeMap, initial[1]);
            await setTime('Play_1x'); const entry = await enter('server');
            const before = await awaitWriter(1); await native('client1', false);
            const modesBefore = await runningModes('running modes after A enters Mission');
            await delay(5000); const after = await bothStates(); assertSingleWriterProgress(before, after, 1, initial);
            const modesAfter = await timeModes(); assertTimeModes(modesAfter, true);
            const world = await consistentSnapshot('B writer critical world'); return { freeMap, entry, before, after, modesBefore, modesAfter, world };
        });
        await feature('both-Missions-exact-world-pause-native-frames-continue', async () => {
            await setTime('Play_1x'); const entry = await enter('client1');
            const before = await snapshot('all-away committed paused lease', true);
            const modesBefore = await timeModes(); assertTimeModes(modesBefore);
            const udpBefore = proxy ? { utc: new Date().toISOString(), stats: structuredClone(proxy.stats) } : null;
            const first = await Promise.all(INSTANCES.map(instance => screenshot(instance, 'all-away-before')));
            await delay(5000); const after = await snapshot('all-away unchanged world', true);
            assertNoWorldProgress(before, after, initial);
            const second = await Promise.all(INSTANCES.map(instance => screenshot(instance, 'all-away-after')));
            for (let index = 0; index < 2; index++) {
                assertEngineProgress(first[index], second[index]);
                assertMissionProgress(before.states[index], after.states[index]); await native(INSTANCES[index], true);
            }
            const udpAfter = proxy ? { utc: new Date().toISOString(), stats: structuredClone(proxy.stats) } : null;
            const modesAfter = await timeModes(); assertTimeModes(modesAfter);
            const transport = proxy ? { ...assertTransportProgress(udpBefore.stats, udpAfter.stats), before: udpBefore, after: udpAfter }
                : { acceptance: 'NOT_OBSERVED', scope: 'MCP and native frame receipts do not prove game P2P message exchange' };
            summary.transportAcceptance = transport.acceptance;
            return { entry, before, after, modesBefore, modesAfter, first, second,
                mcpControlAlive: 'Both game-thread commands and native frame receipts completed',
                p2pNetworkAcceptance: transport.acceptance, transport };
        });
        await feature('B-return-resumes-A-stays-in-Mission', async () => {
            await leave('client1'); await awaitWriter(1);
            const freeMap = await freeBMapActor('B-returned-settlement-exit');
            const modesBefore = await runningModes('automatic selected-speed restore after B leaves settlement');
            const before = await awaitWriter(1);
            await delay(5000); const after = await bothStates(); assertSingleWriterProgress(before, after, 1, initial);
            const modesAfter = await timeModes(); assertTimeModes(modesAfter, true);
            await native('server', true); return { freeMap, modesBefore, modesAfter, before, after, world: await consistentSnapshot('B resumed world') };
        });
        await feature('A-return-host-preference-final-A-B-A', async () => {
            const freeMap = await readBMapActor(); assertFreeMapActor(freeMap, initial[1]);
            await setTime('Play_1x'); await leave('server'); const before = await awaitWriter(0);
            const modesBefore = await runningModes('running modes after A returns');
            await delay(5000); const after = await bothStates(); assertSingleWriterProgress(before, after, 0, initial);
            const modesAfter = await timeModes(); assertTimeModes(modesAfter, true);
            for (const instance of INSTANCES) await native(instance, false);
            return { freeMap, modesBefore, modesAfter, before, after, world: await consistentSnapshot('final A world') };
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
        if (proxy) try { await proxy.close(); }
        catch (error) { summary.proxyCloseError = error.stack; process.exitCode = 1; }
        summary.completedUtc = new Date().toISOString();
        summary.passed = !summary.failed && !summary.cleanupError && !summary.logVerificationError &&
            !summary.closeError && !summary.proxyCloseError && summary.cleanupConfirmed && summary.features.length === 5;
        await save(); if (!summary.passed) process.exitCode = 1;
        console.log(JSON.stringify({ scenario: summary.scenario, runId, passed: summary.passed, summaryPath }));
    }
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
    if (process.argv.includes('--help')) console.log(HELP);
    else await run(options(process.argv.slice(2)));
}
