import assert from 'node:assert/strict';

export function clockOffset(samples) {
    assert.ok(samples.length >= 3, 'At least three clock brackets required');
    let lower = -Infinity, upper = Infinity;
    const { pid, frequency, basis } = samples[0].clock;
    assert.ok(Number.isSafeInteger(pid) && pid > 0 && frequency > 0 && basis);
    for (const { before, after, clock } of samples) {
        assert.equal(clock.pid, pid); assert.equal(clock.frequency, frequency); assert.equal(clock.basis, basis);
        assert.ok(Number.isFinite(before) && after >= before && Number.isFinite(clock.stopwatchTicks));
        const nativeMs = clock.stopwatchTicks * 1000 / frequency;
        lower = Math.max(lower, nativeMs - after); upper = Math.min(upper, nativeMs - before);
    }
    assert.ok(lower <= upper, 'Inconsistent native/root monotonic clock brackets');
    return { pid, frequency, basis, lower, upper, uncertaintyMs: upper - lower };
}

export function mergeProbePages(pages) {
    assert.ok(pages.length > 0);
    const first = pages[0], rows = []; let next = 0;
    for (const page of pages) {
        for (const key of ['schemaVersion', 'label', 'capacity', 'firstSeq', 'nextSeq']) assert.equal(page[key], first[key]);
        assert.equal(page.armed, false); assert.equal(page.complete, true);
        assert.equal(page.overflow, false); assert.equal(page.droppedRows, 0); assert.ok(!page.fault);
        assert.equal(page.clock.pid, first.clock.pid);
        for (const row of page.rows) { assert.equal(row.seq, next++, 'Missing or repeated probe row'); rows.push(row); }
        assert.equal(page.nextFromSeq, next);
    }
    assert.equal(first.firstSeq, 0); assert.equal(next, first.nextSeq);
    assert.equal(pages.at(-1).hasMore, false);
    return { ...first, rows };
}

export function latencyBounds(source, destination, sourceClock, destinationClock) {
    assert.equal(source.pid, sourceClock.pid); assert.equal(destination.pid, destinationClock.pid);
    const delta = destination.stopwatchTicks * 1000 / destinationClock.frequency -
        source.stopwatchTicks * 1000 / sourceClock.frequency;
    return { lowerMs: delta - destinationClock.upper + sourceClock.lower,
        upperMs: delta - destinationClock.lower + sourceClock.upper };
}

const targetMatches = (left, right) => Number.isFinite(left.targetX) && Number.isFinite(left.targetY) &&
    Math.abs(left.targetX - right.targetX) < 0.001 && Math.abs(left.targetY - right.targetY) < 0.001;

export function summarizeMovement(probes, clocks, kinds) {
    assert.equal(probes.length, 2); assert.equal(clocks.length, 2);
    const epochs = new Set(probes.flatMap(probe => probe.rows.map(row => row.epoch)));
    assert.equal(epochs.size, 1, 'Authority changed inside a responsiveness measurement');
    const observations = [];
    for (let index = 0; index < 2; index++) {
        const normalize = row => ({ ...row, ...row.clock,
            targetX: row.requestedX ?? row.targetX, targetY: row.requestedY ?? row.targetY });
        const probe = { ...probes[index], rows: probes[index].rows.map(normalize) };
        const other = { ...probes[1 - index], rows: probes[1 - index].rows.map(normalize) }, clock = clocks[index];
        const inputs = probe.rows.filter(row => row.kind === kinds.input);
        assert.ok(inputs.length > 0, 'No original map-click input observed');
        for (let n = 0; n < inputs.length; n++) {
            const input = inputs[n], end = inputs[n + 1]?.stopwatchTicks ?? Infinity;
            const local = probe.rows.filter(row => row.partyId === input.partyId && row.stopwatchTicks >= input.stopwatchTicks &&
                row.stopwatchTicks < end);
            const previousVisual = probe.rows.filter(row => row.kind === kinds.visual && row.partyId === input.partyId &&
                row.stopwatchTicks < input.stopwatchTicks).at(-1);
            const first = kind => local.find(row => row.kind === kind && (kind === kinds.visual ?
                previousVisual && Math.hypot(row.visualX - previousVisual.visualX, row.visualY - previousVisual.visualY) > 0.00001 :
                Math.hypot(row.x - input.x, row.y - input.y) > 0.00001));
            const simulation = first(kinds.simulation), visual = first(kinds.visual);
            const matches = other.rows.filter(row => row.kind === kinds.apply && row.partyId === input.partyId && targetMatches(input, row));
            const ambiguous = inputs.filter(row => row.partyId === input.partyId && targetMatches(row, input)).length > 1;
            const remote = !ambiguous && matches.length === 1 ? matches[0] : null;
            const priorRemoteVisual = remote && other.rows.filter(row => row.kind === kinds.visual && row.partyId === input.partyId &&
                row.stopwatchTicks < remote.stopwatchTicks).at(-1);
            const remoteVisual = remote && priorRemoteVisual && other.rows.find(row => row.kind === kinds.visual && row.partyId === input.partyId &&
                row.stopwatchTicks >= remote.stopwatchTicks && Math.hypot(row.visualX - priorRemoteVisual.visualX,
                    row.visualY - priorRemoteVisual.visualY) > 0.00001);
            observations.push({ pid: probe.clock.pid, actor: input.actorController, partyId: input.partyId, inputSeq: input.seq,
                target: { x: input.targetX, y: input.targetY },
                localSimulationMs: simulation ? (simulation.stopwatchTicks - input.stopwatchTicks) * 1000 / clock.frequency : null,
                localVisualStateMs: visual ? (visual.stopwatchTicks - input.stopwatchTicks) * 1000 / clock.frequency : null,
                localSimulationFrames: simulation ? simulation.engineFrame - input.engineFrame : null,
                localVisualFrames: visual ? visual.engineFrame - input.engineFrame : null,
                remoteApply: remote ? latencyBounds(input, remote, clock, clocks[1 - index]) : 'NOT_OBSERVED',
                remoteVisualState: remoteVisual ? latencyBounds(input, remoteVisual, clock, clocks[1 - index]) : 'NOT_OBSERVED',
                correlation: ambiguous || matches.length > 1 ? 'AMBIGUOUS' : remote ? 'actor-and-unique-target' : 'NOT_OBSERVED',
                interpretation: n === 0 ? 'First motion after initial map input' :
                    'Already moving: first subsequent motion is not proof of completed turn',
            });
        }
    }
    return { observations, crossProcessClockUncertaintyMs: clocks.map(row => row.uncertaintyMs),
        scope: 'Native input, model and visual-state timing; no pixel scanout timing or physical mouse latency',
        nearZeroAcceptance: 'NOT_ESTABLISHED', stopAcceptance: 'NOT_OBSERVED',
        movingPositionEquality: 'Not required; prediction and temporary replica offsets allowed' };
}
