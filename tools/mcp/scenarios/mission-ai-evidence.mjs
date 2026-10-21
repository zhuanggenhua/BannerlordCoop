import assert from 'node:assert/strict';
import { assertActors, field, jsonOutput } from './p2p-udp-reliability.mjs';
import { assertAiSimulationEvidence } from './p2p-world-lifecycle.mjs';

export function assertMissionAiObservation(receipt, expected, initial, processPids) {
    assert.equal(expected.length, 2);
    assert.equal(processPids.length, 2);
    assert.equal(receipt.cycles.length, 2);
    assert.deepEqual(expected[0].lease, expected[1].lease);
    const paused = field(expected[0].lease, 'isPaused');
    assert.equal(typeof paused, 'boolean');
    for (const states of [receipt.before, receipt.after]) {
        assertActors(states, initial);
        assert.equal(states.filter(s => s.localAuthority).length, paused ? 0 : 1);
        states.forEach((state, index) => {
            assert.deepEqual(state.lease, expected[index].lease, 'Lease changed during native AI observation');
            assert.equal(state.frozen, false);
            assert.equal(state.yielded, false, 'Debug availability cannot prove automatic Mission handoff');
            assert.equal(state.localAuthority, !paused && state.controller === field(state.lease, 'authorityControllerId'));
            const cycles = receipt.cycles[index];
            assert.equal(cycles.campaign, state.campaign);
            assert.equal(cycles.overflowed, false);
            assert.ok(Number.isSafeInteger(cycles.processId) && cycles.processId > 0);
            assert.equal(cycles.processId, processPids[index], 'AI evidence came from another native game process');
            assert.ok(typeof cycles.instance === 'string' && cycles.instance.length > 0);
            assertAiSimulationEvidence(cycles, { allowZeroObserved: true });
            for (const key of ['Campaign.Tick.completed', 'Campaign.RealTick.completed', 'Campaign.LateAITickAux.completed'])
                assert.ok(Number.isSafeInteger(cycles.simulation[key]) && cycles.simulation[key] >= 0,
                    'Native Campaign execution counter unavailable: ' + key);
        });
    }
    assert.notEqual(receipt.cycles[0].processId, receipt.cycles[1].processId);
    assert.notEqual(receipt.cycles[0].instance, receipt.cycles[1].instance);
}

export async function captureMissionAiObservation({ bothStates, command, emit, initial, processPids }, expected, label) {
    const before = await bothStates();
    const cycles = await Promise.all(['server', 'client1'].map(instance =>
        command(instance, 'coop.debug.authority.cycles', ['0', 'cycles']).then(jsonOutput)));
    const after = await bothStates();
    const receipt = { before, cycles, after };
    await emit({ phase: 'mission_ai_observation', label, receipt });
    assertMissionAiObservation(receipt, expected, initial, processPids);
    return receipt;
}

export function assertMissionAiWindow(before, after, writerIndex) {
    assert.ok([-1, 0, 1].includes(writerIndex));
    const deltas = [];
    for (let i = 0; i < 2; i++) {
        assert.deepEqual(after.before[i].lease, before.after[i].lease, 'Lease changed within native AI window');
        for (const key of ['campaign', 'instance', 'processId'])
            assert.equal(after.cycles[i][key], before.cycles[i][key]);
        const start = assertAiSimulationEvidence(before.cycles[i], { allowZeroObserved: true });
        const end = assertAiSimulationEvidence(after.cycles[i], { allowZeroObserved: i !== writerIndex });
        const delta = {};
        for (const key of new Set([...Object.keys(start), ...Object.keys(end)])) {
            const a = start[key] ?? 0, b = end[key] ?? 0;
            assert.ok(Number.isSafeInteger(a) && a >= 0 && Number.isSafeInteger(b) && b >= a,
                'Invalid or regressing native counter: ' + key);
            delta[key] = b - a;
            if (key.endsWith('.failed') || /\.(unknown|unqualified|unexpectedWorld)\.completed$/.test(key))
                assert.equal(delta[key], 0, 'Failed or unsafe native execution: ' + key);
        }
        if (i === writerIndex) {
            assert.ok(delta['MobilePartyAi.Tick.writerNpc.completed'] > 0, 'New writer did not execute native NPC AI');
            assert.ok(delta['Campaign.Tick.completed'] > 0, 'New writer did not execute native Campaign Tick');
        } else {
            for (const key of ['MobilePartyAi.Tick.writerNpc.completed', 'MobilePartyAi.Tick.writerPlayer.completed',
                'Campaign.Tick.completed', 'Campaign.RealTick.completed', 'Campaign.LateAITickAux.completed'])
                assert.equal(delta[key] ?? 0, 0, 'Observer/paused node executed authority simulation: ' + key);
            if (writerIndex === -1) assert.equal(delta['MobilePartyAi.Tick.completed'], 0, 'Paused world ran native party AI');
        }
        deltas.push(delta);
    }
    return { writerIndex, deltas, coverage: 'Actual native execution in stable Mission lease window; not unique NPC or natural 24h coverage' };
}
