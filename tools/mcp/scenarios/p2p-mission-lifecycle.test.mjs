import assert from 'node:assert/strict';
import test from 'node:test';
import { execFileSync } from 'node:child_process';
import { assertAllAway, assertEngineProgress, assertMissionProgress, assertNativeState, assertTransportProgress,
    assertNoWorldProgress, assertSingleWriterProgress, nextEntryPhase } from './p2p-mission-lifecycle.mjs';

const initial = ['A', 'B'].map(controller => ({ controller, mainHero: 'hero-' + controller, mainParty: 'party-' + controller }));
const clone = value => structuredClone(value);
function states(isPaused = true, writer = 'B') {
    return initial.map(actor => ({ ...actor, roomHost: 'A', campaign: 'world', gameState: 'MissionState',
        lease: { roomId: 'room', authorityId: 'world', authorityControllerId: writer, epoch: 6, isPaused },
        frozen: false, yielded: false, localAuthority: !isPaused && actor.controller === writer,
        worldTicks: 20, hours: 24, mission: { time: 10, ended: false, ending: false, scene: 'tavern' } }));
}
const world = () => ({ campaign: 'world', partyIdentityHash: 'id', rosterCountsHash: 'counts', rosterXpHash: 'xp',
    volunteerHash: 'volunteers', heroGoldHash: 'gold', nextHandle: 100, ticks: 10, unregisteredParties: 0 });
const snapshot = () => ({ states: states(), worlds: [world(), world()], players: [[{ gold: 4 }], [{ gold: 4 }]],
    cycles: [{ instance: 'a', rows: [] }, { instance: 'b', rows: [] }] });

test('location open is a separate phase only after the exact encounter-established result', () => {
    assert.equal(nextEntryPhase("Started a local encounter with 'town_ES1'."), 'open-location');
    assert.equal(nextEntryPhase("Opening location 'Location_town_ES1_tavern'."), 'await-mission');
    for (const output of ['', 'Opening location eventually', 'Timed out', "Started a local encounter with 'town_ES2'."])
        assert.throws(() => nextEntryPhase(output), /do not retry/);
});

test('a non-null Mission pointer does not substitute for actual MissionState or MapState', () => {
    assertNativeState({ missionActive: true, activeState: 'TaleWorlds.MountAndBlade.MissionState' }, true);
    assertNativeState({ missionActive: false, activeState: 'TaleWorlds.CampaignSystem.GameState.MapState' }, false);
    assert.throws(() => assertNativeState({ missionActive: true, activeState: 'LoadingState' }, true));
    assert.throws(() => assertNativeState({ missionActive: false, activeState: 'MissionState' }, false));
});

test('native BMP frame evidence must progress in the same actual process', () => {
    const before = { pid: 12, complete: true, stable: true, headerValid: true, isBmp: true,
        sha256: 'a'.repeat(64), captureRequestEngineFrame: 30, observationEngineFrame: 32 };
    const after = { ...before, captureRequestEngineFrame: 34, observationEngineFrame: 36 };
    assertEngineProgress(before, after);
    assert.throws(() => assertEngineProgress(before, { ...after, pid: 13 }), /process changed/);
    assert.throws(() => assertEngineProgress(before, { ...after, captureRequestEngineFrame: 31 }), /frames continued/);
    assert.throws(() => assertEngineProgress(before, { ...after, sha256: '' }));
});

test('rendered frames alone cannot prove Mission logical simulation continues', () => {
    const before = states()[0], after = clone(before); after.mission.time++;
    assertMissionProgress(before, after);
    assert.throws(() => assertMissionProgress(before, clone(before)), /logical time did not advance/);
    assert.throws(() => assertMissionProgress(before, { ...after, mission: null }), /Missing native/);
    const ended = clone(after); ended.mission.ended = true;
    assert.throws(() => assertMissionProgress(before, ended));
});

const udpStats = () => ({ received: { toHost: 10, toClient: 20 }, sent: { toHost: 10, toClient: 20 },
    dropped: 0, delayed: 0, reordered: 0, heldTotal: 0, heldCurrentCount: 0, heldOverflow: 0, errorCount: 0, errors: [] });
test('all-away game transport needs real received and sent progress in both directions', () => {
    const before = udpStats(), after = udpStats();
    for (const kind of ['received', 'sent']) for (const direction of ['toHost', 'toClient']) after[kind][direction] += 2;
    assert.equal(assertTransportProgress(before, after).acceptance, 'PASS');
    for (const kind of ['received', 'sent']) for (const direction of ['toHost', 'toClient']) {
        const stalled = clone(after); stalled[kind][direction] = before[kind][direction];
        assert.throws(() => assertTransportProgress(before, stalled), /UDP did not progress/);
    }
});
test('zero UDP activity cannot be inferred from successful MCP calls or engine frames', () => {
    assert.throws(() => assertTransportProgress(udpStats(), udpStats()), /UDP did not progress/);
});
test('any observed packet fault invalidates the transparent transport observation', () => {
    const before = udpStats(), after = udpStats();
    for (const kind of ['received', 'sent']) for (const direction of ['toHost', 'toClient']) after[kind][direction]++;
    for (const key of ['dropped', 'delayed', 'reordered', 'heldTotal', 'heldCurrentCount', 'heldOverflow', 'errorCount']) {
        const faulty = clone(after); faulty[key]++;
        assert.throws(() => assertTransportProgress(before, faulty), /fault/);
    }
    const error = clone(after); error.errors.push('socket error');
    assert.throws(() => assertTransportProgress(before, error));
});

test('all-away requires shared valid epoch, automatic availability and zero writers', () => {
    assertAllAway(states(), initial);
    const double = states(); double[0].localAuthority = true;
    assert.throws(() => assertAllAway(double, initial));
    const wrongEpoch = states(); wrongEpoch[1].lease.epoch++;
    assert.throws(() => assertAllAway(wrongEpoch, initial));
    const yielded = states(); yielded[0].yielded = true;
    assert.throws(() => assertAllAway(yielded, initial), /override/);
    for (const key of ['roomId', 'authorityId', 'authorityControllerId']) {
        const missing = states(); missing.forEach(row => delete row.lease[key]);
        assert.throws(() => assertAllAway(missing, initial), /Missing paused lease/);
    }
});

test('all-away preserves exact world, tick, resources and dispatcher history', () => {
    const before = snapshot(), after = clone(before);
    after.states.forEach(row => row.mission.time++);
    assertNoWorldProgress(before, after, initial);
    for (const key of ['hours', 'worldTicks']) {
        const changed = clone(after); changed.states[0][key]++;
        assert.throws(() => assertNoWorldProgress(before, changed, initial), /world advanced/);
    }
    const newId = clone(after); newId.worlds.forEach(row => row.nextHandle++);
    assert.throws(() => assertNoWorldProgress(before, newId, initial), /snapshot changed/);
    const xp = clone(after); xp.worlds.forEach(row => row.rosterXpHash = 'changed');
    assert.throws(() => assertNoWorldProgress(before, xp, initial), /snapshot changed/);
    const events = clone(after); events.cycles[1].rows.push({ kind: 'DailyTick', count: 1 });
    assert.throws(() => assertNoWorldProgress(before, events, initial), /dispatcher ran/);
});

test('only confirmed map writer may advance world ticks across a Mission observation', () => {
    const before = states(false), after = clone(before);
    after[1].worldTicks++; after[1].hours++;
    assertSingleWriterProgress(before, after, 1, initial);
    const observer = clone(after); observer[0].worldTicks++;
    assert.throws(() => assertSingleWriterProgress(before, observer, 1, initial), /Observer advanced/);
    const stopped = clone(after); stopped[1].hours = before[1].hours;
    assert.throws(() => assertSingleWriterProgress(before, stopped, 1, initial), /clock did not progress/);
    const switched = clone(after); switched[0].lease.epoch++;
    assert.throws(() => assertSingleWriterProgress(before, switched, 1, initial));
});

test('help is offline and cannot load SDK or require coordinator game paths', () => {
    const output = execFileSync(process.execPath, ['tools/mcp/scenarios/p2p-mission-lifecycle.mjs', '--help'],
        { encoding: 'utf8', windowsHide: true });
    assert.match(output, /Coordinator only/); assert.match(output, /Import and --help are offline/);
});
