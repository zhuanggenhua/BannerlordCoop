import assert from 'node:assert/strict';
import { writeFile } from 'node:fs/promises';
import { performance } from 'node:perf_hooks';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { options, run } from './p2p-mission-lifecycle.mjs';
import { assertActors, assertWriter, jsonOutput } from './p2p-udp-reliability.mjs';
import { clockOffset, mergeProbePages, summarizeMovement } from './map-responsiveness-evidence.mjs';

const INSTANCES = ['server', 'client1'];
export const PROFILE_CASES = [{ name: 'transparent', fault: {} },
    { name: 'one-way-50ms', fault: { delayEvery: 1, delayMs: 50 } }];

export async function exerciseMapMovement(context) {
    const { command, feature, initial, bothStates, setTime, proxy, config, emit, screenshot, consistentSnapshot } = context;
    assert.ok(proxy, 'Actual UDP proxy is required for measured network conditions');
    const read = async (instance, offset = 0) => jsonOutput(await command(instance,
        'coop.debug.movement_probe.read', [String(offset), '256']));
    async function calibrate(instance) {
        const samples = [];
        for (let n = 0; n < 5; n++) {
            const before = performance.now(), page = await read(instance), after = performance.now();
            samples.push({ before, after, clock: page.clock });
        }
        await emit({ phase: 'monotonic_clock_brackets', instance, samples }); return clockOffset(samples);
    }
    async function pages(instance) {
        const result = []; let offset = 0;
        for (let n = 0; n < 128; n++) {
            const page = await read(instance, offset); result.push(page);
            await emit({ phase: 'movement_probe_page', instance, page });
            if (!page.hasMore) return mergeProbePages(result);
            assert.ok(page.nextFromSeq > offset); offset = page.nextFromSeq;
        }
        throw new Error('Movement evidence page limit reached');
    }
    for (const profile of PROFILE_CASES) await feature('two-moving-players-' + profile.name, async () => {
        proxy.setFault(profile.fault);
        const label = profile.name + '-' + Date.now();
        for (const instance of INSTANCES) {
            const page = jsonOutput(await command(instance, 'coop.debug.movement_probe.begin', [label], true));
            assert.equal(page.armed, true); assert.ok(!page.fault);
        }
        const clocks = await Promise.all(INSTANCES.map(calibrate));
        await setTime('Play_1x');
        const clicks = [], beforeNetwork = structuredClone(proxy.stats);
        // These are observation windows between distinct user actions, not readiness waits.
        for (const [dx, dy] of [[6, 0], [0, 6], [-6, 0], [0, -6]]) {
            const outputs = await Promise.all(INSTANCES.map((instance, index) => command(instance,
                'coop.debug.ui.map_click_offset', [String(index ? -dx : dx), String(index ? -dy : dy)], true)));
            clicks.push({ utc: new Date().toISOString(), offsets: [dx, dy], outputs });
            await emit({ phase: 'paired_original_map_click', profile: profile.name, ...clicks.at(-1) });
            await delay(1500);
            if (clicks.length === 2) await Promise.all(INSTANCES.map(instance => screenshot(instance, profile.name + '-moving')));
        }
        for (const instance of INSTANCES) await command(instance, 'coop.debug.movement_probe.stop', [], true);
        const probes = await Promise.all(INSTANCES.map(pages));
        const analysis = summarizeMovement(probes, clocks, { input: 'input_enter', apply: 'apply_enter',
            simulation: 'model_frame', visual: 'visual_frame' });
        for (const probe of probes) {
            const localInputs = probe.rows.filter(row => row.kind === 'input_enter' && row.actorController === row.localController);
            assert.equal(localInputs.length, 4, 'Every actual local click must be observed exactly once');
        }
        assert.ok(analysis.observations.every(row => row.localSimulationMs !== null), 'Input did not produce local map motion');
        const states = await bothStates(); assertActors(states, initial); assertWriter(states, initial[0].controller, initial);
        const afterNetwork = structuredClone(proxy.stats);
        assert.ok(afterNetwork.sent.toHost > beforeNetwork.sent.toHost && afterNetwork.sent.toClient > beforeNetwork.sent.toClient);
        assert.equal(afterNetwork.errorCount, 0);
        if (profile.fault.delayMs) assert.ok(afterNetwork.delayed > beforeNetwork.delayed, 'No actual datagram delayed');
        const result = { label, profile, clocks, clicks, analysis, states, beforeNetwork, afterNetwork };
        await writeFile(config.record + '.' + profile.name + '.movement.json', JSON.stringify({ ...result, probes }, null, 2), { flag: 'wx' });
        proxy.setFault({});
        result.finalCriticalWorld = await consistentSnapshot('critical state after ' + profile.name);
        return result;
    });
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
    if (process.argv.includes('--help')) console.log('Two actual native map players; transparent and real 50ms one-way UDP delay. Use Mission scenario arguments and the exclusive game lock. No deployment on import.');
    else await run(options(process.argv.slice(2)), { name: 'p2p-map-responsiveness', featureCount: 3,
        scope: 'Two native players moving using original map clicks, local prediction responsiveness and remote observations; moving coordinates may differ. Near-zero experience requires measured evidence.',
        run: exerciseMapMovement });
}
