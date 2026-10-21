import assert from 'node:assert/strict';
import test from 'node:test';
import { execFileSync } from 'node:child_process';
import { assertAllAway, assertEngineProgress, assertMissionProgress, assertNativeState, assertTransportProgress,
    assertNoWorldProgress, assertSingleWriterProgress, assertFreeMapActor, assertMapActor, assertTimeModes,
    nextEntryPhase, parseNativePartyState, prepareFreeMapActor, runtimeMetadataExpectations,
    assertRuntimeProcesses, readRuntimeProcesses } from './p2p-mission-lifecycle.mjs';
import { OnceActions, createReadDeadlineScope, assertNoPollingMutation } from './p2p-prisoner-sale.mjs';

const initial = ['A', 'B'].map(controller => ({ controller, mainHero: 'hero-' + controller, mainParty: 'party-' + controller }));
const clone = value => structuredClone(value);
function states(isPaused = true, writer = 'B') {
    return initial.map(actor => ({ ...actor, roomHost: 'A', campaign: 'world', gameState: 'MissionState',
        lease: { roomId: 'room', authorityId: 'world', authorityControllerId: writer, epoch: 6, isPaused },
        frozen: false, yielded: false, localAuthority: !isPaused && actor.controller === writer,
        worldTicks: 20, hours: 24, mission: { time: 10, ended: false, ending: false, scene: 'tavern' } }));
}
const world = () => ({ campaign: 'world', partyIdentityHash: 'id', rosterCountsHash: 'counts', rosterXpHash: 'xp',
    volunteerHash: 'volunteers', heroGoldHash: 'gold', nextHandle: 100, ticks: 10, unregisteredParties: 0,
    npcSample: [{ handle: 1, id: 'npc', x: 441, y: 512 }],
    movement: { complete: true, ready: true, worldClockStopped: true, campaign: 'world', epoch: 6, ticks: 10,
        count: 1, rows: [{ handle: 1, id: 'npc', x: 441, y: 512 }] } });
const snapshot = () => ({ states: states(), worlds: [world(), world()], players: [[{ gold: 4 }], [{ gold: 4 }]],
    cycles: [{ instance: 'a', rows: [] }, { instance: 'b', rows: [] }] });

function freeMapObservation() {
    const actor = initial[1], party = { controllerId: actor.controller, localControllerId: actor.controller,
        mainHeroId: actor.mainHero, mainPartyId: actor.mainParty, partyStringId: actor.mainParty,
        heroStringId: actor.mainHero, partyId: 'MobileParty_' + actor.mainParty, mapEvent: 'none',
        settlementId: null, active: true };
    return { instance: 'client1', localParty: party, partyReplicas: [clone(party), clone(party)],
        nativeParty: parseNativePartyState('party=party-B mapEvent=none siegeAssault=False side=None besiegerCamp=none army=none settlement=none heroHitPoints=100 playerSiege=False encounter=False'),
        status: { missionActive: false, activeState: 'TaleWorlds.CampaignSystem.GameState.MapState', activeMenu: null },
        timeModes: [{ instance: 'server', mode: 'Pause' }, { instance: 'client1', mode: 'Pause' }] };
}

test('native party parser requires actual local encounter evidence and rejects malformed receipts', () => {
    assert.equal(freeMapObservation().nativeParty.encounter, false);
    for (const text of ['party=party-B', 'party=party-B party=other',
        'party=party-B mapEvent=none settlement=none playerSiege=False besiegerCamp=none encounter=unknown'])
        assert.throws(() => parseNativePartyState(text));
});

test('MapState with a town or local encounter is not a free campaign player', () => {
    const good = freeMapObservation(); assertFreeMapActor(good, initial[1]);
    for (const change of [r => r.localParty.settlementId = 'town_ES1',
        r => r.partyReplicas[0].settlementId = 'town_ES1', r => r.nativeParty.settlement = 'town_ES1',
        r => r.nativeParty.encounter = true, r => r.status.activeMenu = 'town',
        r => r.nativeParty.mapEvent = 'battle', r => r.partyReplicas[0].mapEvent = 'battle',
        r => r.status.missionActive = true, r => r.instance = 'server',
        r => r.localParty.localControllerId = 'A', r => r.nativeParty.party = 'party-A',
        r => delete r.localParty.partyId, r => delete r.localParty.settlementId]) {
        const bad = clone(good); change(bad); assert.throws(() => assertFreeMapActor(bad, initial[1]));
    }
});

test('a valid native settlement observation permits one original exit and waits for real free-map replicas', async () => {
    const occupied = freeMapObservation(); occupied.localParty.settlementId = 'town_ES1';
    occupied.nativeParty.settlement = 'town_ES1'; occupied.nativeParty.encounter = true; occupied.status.activeMenu = 'town';
    occupied.partyReplicas.forEach(row => row.settlementId = 'town_ES1');
    assertMapActor(occupied, initial[1]);
    let reads = 0, leaves = 0;
    const proof = await prepareFreeMapActor({ actor: initial[1], key: 'initial-exit', once: new OnceActions(),
        read: async () => reads++ === 0 ? occupied : freeMapObservation(),
        leaveSettlement: async () => { leaves++; return 'Requested native settlement leave'; },
        poll: async (_label, read) => read() });
    assert.equal(leaves, 1); assert.equal(reads, 2); assert.equal(proof.before.nativeParty.encounter, true);
    assertFreeMapActor(proof.after, initial[1]);
});

test('already free B does not receive an unnecessary settlement exit', async () => {
    let leaves = 0;
    const proof = await prepareFreeMapActor({ actor: initial[1], key: 'already-free', once: new OnceActions(),
        read: async () => freeMapObservation(), leaveSettlement: async () => { leaves++; }, poll: async (_label, read) => read() });
    assert.equal(leaves, 0); assert.equal(proof.leaveResult, null);
});

test('uncertain or duplicate settlement exits are never retried to manufacture readiness', async () => {
    const occupied = freeMapObservation(); occupied.localParty.settlementId = 'town_ES1';
    const once = new OnceActions(); let leaves = 0;
    const config = { actor: initial[1], key: 'one-exit', once, read: async () => occupied,
        leaveSettlement: async () => { leaves++; throw new Error('uncertain native result'); }, poll: async () => assert.fail('no poll after uncertain mutation') };
    await assert.rejects(prepareFreeMapActor(config), /uncertain native result/);
    await assert.rejects(prepareFreeMapActor(config), /already dispatched/);
    assert.equal(leaves, 1);
});

test('running-time check needs actual matching receipts and cannot accept an occupancy pause', () => {
    const modes = freeMapObservation().timeModes; assertTimeModes(modes);
    assert.throws(() => assertTimeModes(modes, true), /do not force Play/);
    assert.throws(() => assertTimeModes([{ instance: 'server', mode: 'Play_1x' }, { instance: 'client1', mode: 'Pause' }]), /did not converge/);
    assert.throws(() => assertTimeModes([{ instance: 'server' }, { instance: 'client1' }]), /actual time-mode/);
    assertTimeModes(modes.map(row => ({ ...row, mode: 'Play_1x' })), true);
});

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

test('Mission progress rejects availability overrides without an explicit map-only expectation', () => {
    const before = states(false), after = clone(before);
    after[1].worldTicks++; after[1].hours++;
    for (const sample of ['before', 'after']) for (const index of [0, 1]) {
        const observedBefore = clone(before), observedAfter = clone(after);
        (sample === 'before' ? observedBefore : observedAfter)[index].yielded = true;
        assert.throws(() => assertSingleWriterProgress(observedBefore, observedAfter, 1, initial));
    }
});

test('explicit map yield and auto observations preserve their exact per-node overrides', () => {
    for (const [writer, writerIndex, expectedYield] of [['B', 1, [true, false]], ['A', 0, [false, false]]]) {
        const before = states(false, writer), after = clone(before);
        for (let index = 0; index < 2; index++)
            before[index].yielded = after[index].yielded = expectedYield[index];
        after[writerIndex].worldTicks++; after[writerIndex].hours++;
        assertSingleWriterProgress(before, after, writerIndex, initial, expectedYield);
        for (const sample of ['before', 'after']) for (const index of [0, 1]) {
            const observedBefore = clone(before), observedAfter = clone(after);
            (sample === 'before' ? observedBefore : observedAfter)[index].yielded = !expectedYield[index];
            assert.throws(() => assertSingleWriterProgress(observedBefore, observedAfter, writerIndex, initial, expectedYield));
        }
    }
});

test('progress expectations require exactly two booleans and never coerce observed flags', () => {
    const before = states(false), after = clone(before);
    after[1].worldTicks++; after[1].hours++;
    for (const expected of [null, true, [], [false], [false, false, false], [0, false], [false, 'false'],
        [false, undefined], [, false], [false, new Boolean(false)],
        { 0: false, 1: false, length: 2 }])
        assert.throws(() => assertSingleWriterProgress(before, after, 1, initial, expected));
    for (const flag of [undefined, null, 0, 1, 'false', 'true']) {
        const changed = clone(after); changed[0].yielded = flag;
        assert.throws(() => assertSingleWriterProgress(before, changed, 1, initial, [false, false]));
    }
});

test('a map yield override cannot hide stopped simulation, observer ticks or changed identities', () => {
    const before = states(false), after = clone(before);
    before[0].yielded = after[0].yielded = true;
    after[1].worldTicks++; after[1].hours++;
    assertSingleWriterProgress(before, after, 1, initial, [true, false]);
    const corruptions = [
        changed => changed[0].worldTicks++,
        changed => changed[1].worldTicks = before[1].worldTicks,
        changed => changed[1].hours = before[1].hours,
        changed => changed.forEach(state => state.lease.epoch++),
        changed => changed[1].localAuthority = false,
        changed => changed[0].mainHero = 'wrong-hero',
        changed => changed[1].mainParty = 'wrong-party',
    ];
    for (const corrupt of corruptions) {
        const changed = clone(after); corrupt(changed);
        assert.throws(() => assertSingleWriterProgress(before, changed, 1, initial, [true, false]));
    }
});

test('help is offline and cannot load SDK or require coordinator game paths', () => {
    const output = execFileSync(process.execPath, ['tools/mcp/scenarios/p2p-mission-lifecycle.mjs', '--help'],
        { encoding: 'utf8', windowsHide: true });
    assert.match(output, /Coordinator only/); assert.match(output, /Import and --help are offline/);
});

function runtimeView() {
    const names = ['Common', 'Coop', 'Coop.Core', 'Coop.Steam', 'GameInterface', 'Missions'];
    const mvid = number => '00000000-0000-4000-8000-' + String(number).padStart(12, '0');
    const instances = ['server', 'client1'].map((name, index) => {
        const identity = { pid: 100 + index, startedUtc: '2026-10-19T14:00:00Z',
            role: index ? 'client' : 'server', platformId: index ? 'B' : 'A', runToken: 'run' };
        return { name, identity, processAlive: true, error: null, status: { pid: identity.pid,
            processStartedUtc: identity.startedUtc, role: identity.role, platformId: identity.platformId,
            runToken: identity.runToken, buildVersion: '0.1.7+runtime-sha', assemblyMvid: mvid(2),
            loadedAssemblies: names.map((name, index) => ({ name, mvid: mvid(index + 1) })) } };
    });
    return { runId: 'run', error: null, instances, preflight: { bridge: { mvid: mvid(2) } } };
}

function metadataPoll(read) {
    let clock = 0; const scope = createReadDeadlineScope(), deadlines = [];
    return { deadlines, run: expected => readRuntimeProcesses({ expected,
        read: () => { deadlines.push(scope.currentDeadline()); return read(); },
        withDeadline: (deadline, action) => scope.run(deadline, action),
        now: () => clock, sleep: async milliseconds => { clock += milliseconds; } }) };
}

test('cancelled or missing current metadata is retried within one readonly deadline until a wholly fresh pair exists', async () => {
    const complete = runtimeView(), expected = runtimeMetadataExpectations(complete);
    const missing = clone(complete); missing.instances[0].status = null;
    const cancelled = clone(complete); cancelled.instances[1].status = null;
    cancelled.instances[1].error = { code: 'operation_cancelled', outcomeUncertain: false, message: 'The operation was canceled.' };
    const responses = [missing, cancelled, complete]; let reads = 0;
    const poll = metadataPoll(() => responses[reads++]);
    const result = await poll.run(expected);
    assert.equal(reads, 3); assert.equal(result[0], complete.instances[0]); assert.equal(result[1], complete.instances[1]);
    assert.deepEqual(poll.deadlines, [30000, 30000, 30000]);
    assert.equal(missing.instances[0].status, null); assert.equal(cancelled.instances[1].status, null);
    assert.equal(cancelled.instances[1].error.code, 'operation_cancelled', 'Original failed read must stay intact');
});

test('missing metadata fields require a new complete same-read receipt rather than inferred defaults', async () => {
    const complete = runtimeView(), expected = runtimeMetadataExpectations(complete);
    for (const key of ['buildVersion', 'assemblyMvid', 'loadedAssemblies']) {
        const missing = clone(complete); delete missing.instances[1].status[key]; let reads = 0;
        const result = await metadataPoll(() => ++reads === 1 ? missing : complete).run(expected);
        assert.equal(reads, 2); assert.equal(result[1], complete.instances[1]);
    }
});

test('complete metadata from different reads cannot be combined into a successful pair', async () => {
    const complete = runtimeView(), expected = runtimeMetadataExpectations(complete);
    const onlyA = clone(complete); onlyA.instances[1].status = null;
    const onlyB = clone(complete); onlyB.instances[0].status = null;
    let reads = 0; const responses = [onlyA, onlyB, complete];
    const result = await metadataPoll(() => responses[reads++]).run(expected);
    assert.equal(reads, 3); assert.equal(result[0], complete.instances[0]);
});

test('PID, starttime, run token, death and hard metadata errors stop immediately rather than waiting for a favourable read', async () => {
    const good = runtimeView(), expected = runtimeMetadataExpectations(good);
    const changes = [
        view => view.instances[0].identity.pid++, view => view.instances[0].identity.startedUtc = 'later',
        view => view.instances[0].identity.runToken = 'different', view => view.instances[0].processAlive = false,
        view => view.instances[0].status.pid++, view => view.instances[0].status.processStartedUtc = 'later',
        view => view.instances[0].status.runToken = 'different', view => view.runId = 'other',
        view => view.error = { code: 'run_failed' }, view => view.instances.pop(),
        view => view.instances[0].error = { code: 'transport_failed', outcomeUncertain: false },
        view => delete view.instances[0].error,
        view => { view.instances[0].status = null; view.instances[0].error = { code: 'response_identity_mismatch', outcomeUncertain: false }; },
        view => { view.instances[0].status = null; view.instances[0].error = { code: 'operation_cancelled', outcomeUncertain: true }; },
        view => view.instances[0].error = { code: 'operation_cancelled', outcomeUncertain: false },
    ];
    for (const change of changes) {
        const bad = clone(good); change(bad); let reads = 0;
        await assert.rejects(metadataPoll(() => ++reads === 1 ? bad : good).run(expected));
        assert.equal(reads, 1, 'Unsafe receipt was retried: ' + change);
    }
});

test('wrong build or assembly MVID is fatal even when another peer is missing and the next read would match', async () => {
    const good = runtimeView(), expected = runtimeMetadataExpectations(good);
    const changes = [
        status => status.buildVersion = 'different', status => status.buildVersion = '',
        status => status.assemblyMvid = '00000000-0000-4000-8000-000000000099',
        status => status.loadedAssemblies[0].mvid = '00000000-0000-4000-8000-000000000099',
        status => status.loadedAssemblies[0].mvid = '', status => status.loadedAssemblies = [],
        status => status.loadedAssemblies.push(clone(status.loadedAssemblies[0])),
        status => status.loadedAssemblies[0].name = '', status => status.loadedAssemblies.pop(),
    ];
    for (const change of changes) {
        const bad = clone(good); bad.instances[1].status = null; change(bad.instances[0].status);
        let reads = 0;
        await assert.rejects(metadataPoll(() => ++reads === 1 ? bad : good).run(expected));
        assert.equal(reads, 1);
    }
});

test('launch pinning requires known two identities, distinct PIDs and host metadata matching actual preflight bridge', () => {
    const good = runtimeView(); runtimeMetadataExpectations(good);
    for (const change of [v => v.instances[1].identity.pid = v.instances[0].identity.pid,
        v => delete v.instances[1].identity.startedUtc, v => delete v.instances[0].status.buildVersion,
        v => v.preflight.bridge.mvid = '00000000-0000-4000-8000-000000000099',
        v => v.instances[0].status.loadedAssemblies = [], v => v.instances[0].status = null]) {
        const bad = clone(good); change(bad); assert.throws(() => runtimeMetadataExpectations(bad));
    }
});

test('persistent missing metadata expires at the original 30s deadline without resetting its budget', async () => {
    const good = runtimeView(), expected = runtimeMetadataExpectations(good), missing = clone(good);
    missing.instances[1].status = null; let reads = 0;
    const poll = metadataPoll(() => { reads++; return missing; });
    await assert.rejects(poll.run(expected), /did not converge/);
    assert.equal(reads, 60); assert.equal(new Set(poll.deadlines).size, 1); assert.equal(poll.deadlines[0], 30000);
});

test('a read finishing after the original deadline cannot pass even with complete correct metadata', async () => {
    const good = runtimeView(); let clock = 0, reads = 0;
    await assert.rejects(readRuntimeProcesses({ expected: runtimeMetadataExpectations(good),
        read: async () => { reads++; clock = 30001; return good; }, now: () => clock,
        sleep: async () => assert.fail('Late response must not be retried') }), /original deadline/);
    assert.equal(reads, 1);
});

test('runtime metadata polling preserves the existing mutation guard and never retries transport exceptions', async () => {
    const good = runtimeView(), expected = runtimeMetadataExpectations(good), scope = createReadDeadlineScope();
    let calls = 0;
    await assert.rejects(readRuntimeProcesses({ expected,
        withDeadline: (deadline, action) => scope.run(deadline, action),
        read: async () => { calls++; assertNoPollingMutation(scope.currentDeadline(), true); } }), /Mutations cannot run/);
    assert.equal(calls, 1); calls = 0;
    await assert.rejects(readRuntimeProcesses({ expected,
        read: async () => { calls++; throw new Error('Unclassified transport failure'); } }), /Unclassified transport failure/);
    assert.equal(calls, 1);
});

test('even non-Error transport rejections are terminal rather than turning into a retryable catch failure', async () => {
    const good = runtimeView(), expected = runtimeMetadataExpectations(good);
    for (const rejection of [null, undefined, false, 'transport-rejected']) {
        let reads = 0;
        const result = readRuntimeProcesses({ expected, read: async () => { if (++reads === 1) throw rejection; return good; } });
        await assert.rejects(result); assert.equal(reads, 1);
    }
});
