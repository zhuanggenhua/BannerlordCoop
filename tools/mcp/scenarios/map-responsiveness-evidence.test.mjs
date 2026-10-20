import assert from 'node:assert/strict';
import test from 'node:test';
import { execFileSync } from 'node:child_process';
import { assertMovingPlayersCritical, clockOffset, latencyBounds, mergeProbePages, summarizeMovement } from './map-responsiveness-evidence.mjs';

test('moving player comparison allows position offsets and rejects resource or registration divergence', () => {
    const players = ['A', 'B'].map(controllerId => ({ controllerId, gold: 10, troops: 5, wounded: 0, heroId: controllerId,
        clanId: controllerId, characterId: controllerId, partyId: controllerId, partyStringId: controllerId,
        heroStringId: controllerId, settlementId: null, registeredPlayers: 2, heroRegistrations: 1,
        partyRegistrations: 1, clanRegistrations: 1, active: true, x: 1, y: 2 }));
    const pair = [players, structuredClone(players)]; pair[1][0].x += 3;
    assertMovingPlayersCritical(pair);
    for (const edit of [r => r.gold++, r => r.troops++, r => r.heroId = 'switched', r => r.partyRegistrations++,
        r => delete r.clanId, r => r.x = NaN]) {
        const bad = structuredClone(pair); edit(bad[1][0]); assert.throws(() => assertMovingPlayersCritical(bad));
    }
});

const clock = (pid = 1, ticks = 1000) => ({ pid, frequency: 1000, basis: 'Stopwatch.GetTimestamp', stopwatchTicks: ticks });
const samples = () => [0, 1, 2].map(n => ({ before: 100 + n, after: 102 + n, clock: clock(1, 1101 + n) }));
test('clock uncertainty is bounded by actual requests, not equated with network response latency', () => {
    const result = clockOffset(samples()); assert.equal(result.lower, 999); assert.equal(result.upper, 1001);
    assert.equal(result.uncertaintyMs, 2);
});
test('inconsistent brackets and clock identities cannot produce synthetic timing', () => {
    for (const edit of [s => s[2].clock.pid++, s => s[2].clock.frequency++,
        s => s[2].clock.stopwatchTicks += 100, s => s[2].after = s[2].before - 1]) {
        const value = samples(); edit(value); assert.throws(() => clockOffset(value));
    }
});
test('remote delay reports an interval including both process clock uncertainties', () => {
    const source = { pid: 1, stopwatchTicks: 2000 }, destination = { pid: 2, stopwatchTicks: 3200 };
    assert.deepEqual(latencyBounds(source, destination, { pid: 1, frequency: 1000, lower: 999, upper: 1001 },
        { pid: 2, frequency: 1000, lower: 1997, upper: 2003 }), { lowerMs: 196, upperMs: 204 });
});
const page = () => ({ schemaVersion: 1, label: 'run', capacity: 8192, firstSeq: 0, nextSeq: 2,
    nextFromSeq: 2, hasMore: false, armed: false, complete: true, overflow: false, droppedRows: 0,
    fault: null, clock: clock(), rows: [{ seq: 0 }, { seq: 1 }] });
test('pagination merges contiguous immutable stopped pages', () => {
    const a = page(), b = page(); a.rows.pop(); a.nextFromSeq = 1; a.hasMore = true; b.rows.shift();
    assert.equal(mergeProbePages([a, b]).rows.length, 2);
});
test('missing repeated incomplete overflow or changed pages fail without dropping evidence', () => {
    for (const edit of [p => p.rows[1].seq = 0, p => p.rows.pop(), p => p.armed = true,
        p => p.complete = false, p => p.overflow = true, p => p.fault = 'native fault', p => p.hasMore = true]) {
        const p = page(); edit(p); assert.throws(() => mergeProbePages([p]));
    }
});
function probes() {
    const row = (pid, kind, t, extra = {}) => ({ kind, epoch: 5, partyId: 'party-A', actorController: 'A',
        x: 0, y: 0, visualX: 0, visualY: 0, requestedX: 4, requestedY: 5,
        clock: { ...clock(pid, t), engineFrame: t }, ...extra });
    return [{ clock: clock(1), rows: [row(1, 'visual', 9), row(1, 'input', 10), row(1, 'model', 11, { x: 0.2 }),
        row(1, 'visual', 12, { visualX: 0.2 })] },
    { clock: clock(2), rows: [row(2, 'visual', 19), row(2, 'apply', 20), row(2, 'visual', 21, { visualX: 0.2 }),
        row(2, 'input', 30, { partyId: 'party-B', actorController: 'B' }),
        row(2, 'model', 31, { partyId: 'party-B', x: 0.2 })] }];
}
const clocks = [1, 2].map(pid => ({ pid, frequency: 1000, lower: 0, upper: 0, uncertaintyMs: 0 }));
const kinds = { input: 'input', apply: 'apply', simulation: 'model', visual: 'visual' };
test('local motion uses same-process timestamps even with delayed remote application', () => {
    const result = summarizeMovement(probes(), clocks, kinds), a = result.observations[0];
    assert.equal(a.localSimulationMs, 1); assert.equal(a.localVisualStateMs, 2);
    assert.deepEqual(a.remoteApply, { lowerMs: 10, upperMs: 10 });
    assert.deepEqual(a.remoteVisualState, { lowerMs: 11, upperMs: 11 });
    assert.equal(result.nearZeroAcceptance, 'NOT_ESTABLISHED');
});
test('replica moving coordinates may differ without weakening other timing requirements', () => {
    const p = probes(); p[1].rows.forEach(row => { row.x += 10; row.y += 2; });
    assert.equal(summarizeMovement(p, clocks, kinds).observations[0].localSimulationMs, 1);
});
test('repeated target correlation remains ambiguous and never picks a convenient arrival', () => {
    const p = probes(); p[1].rows.push({ ...p[1].rows[1] });
    const a = summarizeMovement(p, clocks, kinds).observations[0];
    assert.equal(a.correlation, 'AMBIGUOUS'); assert.equal(a.remoteApply, 'NOT_OBSERVED');
});
test('missing motion remains unobserved rather than zero milliseconds', () => {
    const p = probes(); p[0].rows = p[0].rows.filter(row => !['model', 'visual'].includes(row.kind));
    const a = summarizeMovement(p, clocks, kinds).observations[0];
    assert.equal(a.localSimulationMs, null); assert.equal(a.localVisualStateMs, null);
});
test('authority changes invalidate a stable-writer responsiveness measurement', () => {
    const p = probes(); p[1].rows[2].epoch++; assert.throws(() => summarizeMovement(p, clocks, kinds));
});
test('responsiveness scenario help is offline and documents actual network delay', () => {
    const output = execFileSync(process.execPath, ['tools/mcp/scenarios/p2p-map-responsiveness.mjs', '--help'], { encoding: 'utf8' });
    assert.match(output, /50ms one-way UDP delay/);
});
