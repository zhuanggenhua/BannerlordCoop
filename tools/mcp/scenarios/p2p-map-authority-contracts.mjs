import assert from 'node:assert/strict';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { exerciseMapMovement } from './p2p-map-responsiveness.mjs';
import { options, run, assertSingleWriterProgress } from './p2p-mission-lifecycle.mjs';
import { assertActors, assertPlayers, assertWorld, assertWriter, epoch, jsonOutput } from './p2p-udp-reliability.mjs';
import { assertFrozenMovementPair, assertFrozenNpcSamples } from './frozen-movement-evidence.mjs';
import { assertMovingPlayersCritical } from './map-responsiveness-evidence.mjs';
import { assertAiSimulationEvidence } from './p2p-world-lifecycle.mjs';

const instances = ['server', 'client1'];

export function assertMapAiObservation(evidence, committed, initial, campaign) {
    const writer = committed.find(state => state.localAuthority)?.controller;
    for (const states of [evidence.statesBefore, evidence.statesAfter]) {
        assertWriter(states, writer, initial);
        states.forEach((state, index) => assert.deepEqual(state.lease, committed[index].lease,
            'Authority lease changed during AI counter observation'));
    }
    assert.equal(evidence.cycles.length, 2);
    for (const receipt of evidence.cycles) {
        assert.equal(receipt.campaign, campaign, 'AI counter campaign differs from frozen world');
        assert.equal(receipt.overflowed, false, 'AI counter evidence overflowed');
        assert.ok(typeof receipt.instance === 'string' && receipt.instance.length > 0);
        assert.ok(Number.isSafeInteger(receipt.processId) && receipt.processId > 0);
        assertAiSimulationEvidence(receipt, { allowZeroObserved: true });
    }
    assert.notEqual(evidence.cycles[0].instance, evidence.cycles[1].instance);
    assert.notEqual(evidence.cycles[0].processId, evidence.cycles[1].processId);
}

export function assertMapAiProgress(before, after, writerIndex, activePartyCount) {
    assert.ok(writerIndex === 0 || writerIndex === 1);
    assert.ok(Number.isSafeInteger(activePartyCount) && activePartyCount > 0 &&
        Number.isSafeInteger(activePartyCount * 2), 'Invalid complete active-party count');
    const tickBudget = activePartyCount * 2;
    const completed = [];
    for (let index = 0; index < 2; index++) {
        const startReceipt = before.cycles[index], endReceipt = after.cycles[index];
        for (const key of ['campaign', 'instance', 'processId'])
            assert.equal(endReceipt[key], startReceipt[key], 'AI counter identity changed: ' + key);
        const start = assertAiSimulationEvidence(startReceipt, { allowZeroObserved: true });
        const end = assertAiSimulationEvidence(endReceipt, { allowZeroObserved: index !== writerIndex });
        for (const key of new Set([...Object.keys(start), ...Object.keys(end)])) {
            const oldCount = start[key] ?? 0, newCount = end[key] ?? 0;
            assert.ok(Number.isSafeInteger(oldCount) && oldCount >= 0 && Number.isSafeInteger(newCount) && newCount >= 0,
                'Invalid simulation counter: ' + key);
            assert.ok(newCount >= oldCount, 'Simulation counter moved backwards: ' + key);
            const unsafe = /\.(unknown|unqualified|unexpectedWorld)\.(completed|failed)$/.test(key);
            if (key.endsWith('.failed') || unsafe)
                assert.equal(newCount, oldCount, 'New failed or unsafe AI execution: ' + key);
        }
        const delta = kind => end['MobilePartyAi.Tick.' + kind + '.completed'] - start['MobilePartyAi.Tick.' + kind + '.completed'];
        if (index === writerIndex) assert.ok(delta('writerNpc') >= tickBudget,
            `Writer NPC execution-call coverage ${delta('writerNpc')} is below ${tickBudget}`);
        else for (const kind of ['writerNpc', 'writerPlayer'])
            assert.equal(delta(kind), 0, 'Observer advanced authoritative AI: ' + kind);
        completed.push(delta('writerNpc'));
    }
    return { activePartyCount, tickBudget, writerNpcCompletedDeltas: completed,
        coverage: 'Execution-call volume only; not unique NPC, natural-cycle or 24h coverage' };
}

async function captureMapAiObservation(context, name, committed, campaign) {
    const statesBefore = await context.bothStates();
    const cycles = await Promise.all(instances.map(instance => context.command(instance,
        'coop.debug.authority.cycles', ['0', 'cycles']).then(jsonOutput)));
    const statesAfter = await context.bothStates();
    const evidence = { statesBefore, cycles, statesAfter };
    await context.emit({ phase: 'map_ai_execution_observation', name, ...evidence });
    assertMapAiObservation(evidence, committed, context.initial, campaign);
    return evidence;
}

export async function capturePredictedMapState(context, label) {
    const { command, initial, bothStates, poll, setTime } = context;
    await setTime('Pause');
    return poll(label, async () => {
        const states = await bothStates();
        assertActors(states, initial);
        assertWriter(states, states.find(state => state.localAuthority)?.controller, initial);
        const worlds = await Promise.all(instances.map(instance => command(instance,
            'coop.debug.authority.world_state').then(jsonOutput)));
        assertWorld(worlds);
        const players = await Promise.all(instances.map(instance => Promise.all(initial.map(actor =>
            command(instance, 'coop.debug.players.party_state', [actor.controller]).then(jsonOutput)))));
        assertMovingPlayersCritical(players, initial);
        return { states, worlds, players,
            positionPolicy: 'Prediction offsets retained; exact final checkpoint comparison is required after handoff' };
    });
}

// Reuses original input/prediction probes, native snapshot comparison and immutable log archive.
// Yield is solely a map-only readiness injection; this scenario does not accept Mission transitions.
export async function exerciseMapAuthorityContracts(context, movementStage = exerciseMapMovement) {
    const { command, feature, initial, bothStates, poll, setTime, emit } = context;
    await movementStage(context);
    for (const [mode, writerIndex, name] of [['yield', 1, 'A-to-B-map-handoff'], ['auto', 0, 'B-to-A-map-handoff']]) {
        await feature(name, async () => {
            const beforeTransfer = await capturePredictedMapState(context, name + '-critical-before-barrier');
            const oldEpoch = epoch(beforeTransfer.states[0]);
            await command('server', 'coop.debug.authority.availability', [mode], true);
            const committed = await poll(name + '-confirmed-writer', async () => {
                const states = await bothStates(); assertActors(states, initial);
                assertWriter(states, initial[writerIndex].controller, initial);
                assert.ok(epoch(states[0]) > oldEpoch, 'Handoff did not commit a new epoch');
                return states;
            });
            const finalBaseline = await poll(name + '-complete-frozen-movement', async () => {
                const worlds = await Promise.all(instances.map(instance => command(instance,
                    'coop.debug.authority.world_state', ['positions']).then(jsonOutput)));
                assertWorld(worlds);
                assertFrozenNpcSamples(worlds);
                assertFrozenMovementPair(worlds.map(world => world.movement));
                const players = await Promise.all(instances.map(instance => Promise.all(initial.map(actor =>
                    command(instance, 'coop.debug.players.party_state', [actor.controller]).then(jsonOutput)))));
                assertPlayers(players);
                return worlds;
            });
            await emit({ phase: 'final_barrier_exact_movement', name, committed, worlds: finalBaseline });
            const beforeProgress = await bothStates();
            const activePartyCount = finalBaseline[0].movement.count;
            const beforeAi = await captureMapAiObservation(context, name + '-before-play', committed, finalBaseline[0].campaign);
            await setTime('Play_1x');
            let afterAi, aiCoverage;
            const afterProgress = await poll(name + '-single-writer-progress', async () => {
                afterAi = await captureMapAiObservation(context, name + '-progress', committed, finalBaseline[0].campaign);
                const after = afterAi.statesAfter;
                assertSingleWriterProgress(beforeProgress, after, writerIndex, initial, [mode === 'yield', false]);
                aiCoverage = assertMapAiProgress(beforeAi, afterAi, writerIndex, activePartyCount);
                return after;
            });
            const afterCritical = await capturePredictedMapState(context, name + '-critical-after-progress');
            return { beforeTransfer, committed, finalBaseline, beforeProgress, afterProgress, beforeAi, afterAi, aiCoverage, afterCritical,
                scope: 'Real map simulation and final-barrier exact positions; debug readiness yield, not Mission acceptance' };
        });
    }
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
    if (process.argv.includes('--help')) console.log('Original two-player map clicks, transparent/50ms UDP profiles, then map-only A-B-A with complete exact final position baselines. Uses p2p-mission-lifecycle arguments and exclusive game lock. No Mission/battle/performance acceptance.');
    else await run(options(process.argv.slice(2)), { name: 'p2p-map-authority-contracts', featureCount: 5,
        scope: 'MODIFY stages 1-2: original two-player input/prediction and A-B-A map authority, final exact baselines, actor/resources/IDs and single world Tick writer. No Mission or natural-day acceptance.',
        run: exerciseMapAuthorityContracts });
}
