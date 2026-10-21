import assert from 'node:assert/strict';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { exerciseMapMovement } from './p2p-map-responsiveness.mjs';
import { options, run, assertSingleWriterProgress } from './p2p-mission-lifecycle.mjs';
import { assertActors, assertPlayers, assertWorld, assertWriter, epoch, jsonOutput } from './p2p-udp-reliability.mjs';
import { assertFrozenMovementPair, assertFrozenNpcSamples } from './frozen-movement-evidence.mjs';
import { assertMovingPlayersCritical } from './map-responsiveness-evidence.mjs';

const instances = ['server', 'client1'];

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
export async function exerciseMapAuthorityContracts(context) {
    const { command, feature, initial, bothStates, poll, setTime, emit } = context;
    await exerciseMapMovement(context);
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
            await setTime('Play_1x');
            const afterProgress = await poll(name + '-single-writer-progress', async () => {
                const after = await bothStates();
                assertSingleWriterProgress(beforeProgress, after, writerIndex, initial, [mode === 'yield', false]);
                return after;
            });
            const afterCritical = await capturePredictedMapState(context, name + '-critical-after-progress');
            return { beforeTransfer, committed, finalBaseline, beforeProgress, afterProgress, afterCritical,
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
