import assert from 'node:assert/strict';
import test from 'node:test';
import { assertMissionAiObservation, assertMissionAiWindow, captureMissionAiObservation } from './mission-ai-evidence.mjs';

const initial = ['a', 'b'].map(controller => ({ controller, mainHero: 'h-' + controller, mainParty: 'p-' + controller }));
const classes = ['writerNpc', 'writerPlayer', 'observerLocalNavigation', 'receiveApply', 'unqualified', 'unknown'];
function fixture(paused = false) {
    const states = initial.map(actor => ({ ...actor, campaign: 'campaign', roomHost: 'a', frozen: false, yielded: false,
        lease: { roomId: 'room', authorityId: 'lease', authorityControllerId: 'b', epoch: 5, isPaused: paused },
        localAuthority: !paused && actor.controller === 'b' }));
    const cycles = initial.map((_, i) => {
        const simulation = { 'Campaign.Tick.completed': 0, 'Campaign.RealTick.completed': 0, 'Campaign.LateAITickAux.completed': 0 };
        for (const outcome of ['completed', 'failed', 'skipped']) {
            simulation['MobilePartyAi.Tick.' + outcome] = 0;
            for (const kind of [...classes, 'unexpectedWorld']) simulation['MobilePartyAi.Tick.' + kind + '.' + outcome] = 0;
        }
        simulation['MobilePartyAi.Tick.observed'] = simulation['MobilePartyAi.Tick.beforeOriginal.failed'] = 0;
        return { campaign: 'campaign', instance: 'visit-' + i, processId: 100 + i, overflowed: false, simulation,
            aiSimulation: { schemaVersion: 1, classes, firstEntries: [] } };
    });
    return { before: structuredClone(states), after: structuredClone(states), cycles };
}
function advance(receipt, index, kind = 'writerNpc', outcome = 'completed') {
    const c = receipt.cycles[index].simulation;
    for (const key of ['observed', outcome, kind + '.' + outcome]) c['MobilePartyAi.Tick.' + key]++;
    if (kind === 'writerNpc' && outcome === 'completed') c['Campaign.Tick.completed']++;
}

test('Mission writer executes native NPC AI while observer executes no authority simulation', () => {
    const before = fixture(), after = structuredClone(before); advance(after, 1);
    assertMissionAiObservation(before, before.before, initial, [100, 101]);
    assertMissionAiObservation(after, before.before, initial, [100, 101]);
    assert.equal(assertMissionAiWindow(before, after, 1).deltas[1]['MobilePartyAi.Tick.writerNpc.completed'], 1);
});
test('authority identity alone cannot pass a Mission AI window', () => {
    const before = fixture(); assert.throws(() => assertMissionAiWindow(before, structuredClone(before), 1));
    const after = structuredClone(before); advance(after, 1); after.cycles[1].simulation['Campaign.Tick.completed'] = 0;
    assert.throws(() => assertMissionAiWindow(before, after, 1));
});
test('observer authoritative AI or Campaign Tick fails even while elected writer advances', () => {
    for (const corrupt of [r => advance(r, 0), r => r.cycles[0].simulation['Campaign.Tick.completed']++]) {
        const before = fixture(), after = structuredClone(before); advance(after, 1); corrupt(after);
        assert.throws(() => assertMissionAiWindow(before, after, 1));
    }
});
test('all-away native world pause rejects any completed party AI or late AI task', () => {
    const before = fixture(true); assert.equal(assertMissionAiWindow(before, structuredClone(before), -1).writerIndex, -1);
    for (const corrupt of [r => advance(r, 0, 'observerLocalNavigation'), r => r.cycles[1].simulation['Campaign.LateAITickAux.completed']++]) {
        const after = structuredClone(before); corrupt(after);
        assert.throws(() => assertMissionAiWindow(before, after, -1));
    }
});
test('new failed or unqualified native AI execution is never accepted', () => {
    for (const [kind, outcome] of [['writerNpc', 'failed'], ['unknown', 'completed'], ['unqualified', 'completed']]) {
        const before = fixture(), after = structuredClone(before); advance(after, 1); advance(after, 1, kind, outcome);
        assert.throws(() => assertMissionAiWindow(before, after, 1));
    }
});
test('raw evidence is emitted before rejecting a lease transition during counter collection', async () => {
    const data = fixture(), events = []; let reads = 0;
    const context = { initial, processPids: [100, 101], emit: async x => events.push(x), bothStates: async () => {
        const value = structuredClone(data.before); if (reads++) value[1].lease.epoch++; return value;
    }, command: async instance => JSON.stringify(data.cycles[instance === 'server' ? 0 : 1]) };
    await assert.rejects(() => captureMissionAiObservation(context, data.before, 'entry'));
    assert.equal(events.length, 1); assert.equal(events[0].receipt.after[1].lease.epoch, 6);
});
test('debug yield, duplicate process identity and counter regression are rejected', () => {
    for (const corrupt of [r => r.after[0].yielded = true, r => r.cycles[1].processId = r.cycles[0].processId]) {
        const before = fixture(); corrupt(before); assert.throws(() => assertMissionAiObservation(before, fixture().before, initial, [100, 101]));
    }
    const before = fixture(), after = structuredClone(before); advance(before, 1);
    assert.throws(() => assertMissionAiWindow(before, after, 1));
});

test('missing native Campaign execution counters and unrelated game PIDs fail observation', () => {
    for (const corrupt of [r => delete r.cycles[0].simulation['Campaign.Tick.completed'], r => r.cycles[0].processId = 999]) {
        const receipt = fixture(); corrupt(receipt);
        assert.throws(() => assertMissionAiObservation(receipt, fixture().before, initial, [100, 101]));
    }
});
