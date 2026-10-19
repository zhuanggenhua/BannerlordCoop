import assert from 'node:assert/strict';
import test from 'node:test';
import { assertFrozenMovementPair, assertFrozenNpcSamples } from './frozen-movement-evidence.mjs';

const row = (handle = 6042) => ({ handle, id: 'party_' + handle, x: 441.231049, y: 512.885559 });
const capture = () => ({ complete: true, ready: true, worldClockStopped: true,
    campaign: 'world', epoch: 6, ticks: 78699089256620, count: 2, rows: [row(), row(6050)] });

test('same-tick stationary per-peer samples do not excuse a cross-peer position difference', () => {
    const worlds = [0, 1].map(() => ({ campaign: 'world', ticks: 78699089256620, npcSample: [row()] }));
    worlds[1].npcSample[0].x = 433.513184;
    worlds[1].npcSample[0].y = 514.2225;
    assert.throws(() => assertFrozenNpcSamples(worlds), /Frozen NPC positions differ/);
});

test('a complete frozen capture compares all parties independent of local ordering', () => {
    const pair = [capture(), capture()]; pair[1].rows.reverse();
    assertFrozenMovementPair(pair);
});

test('an out-of-sample party position discrepancy fails the full capture', () => {
    const pair = [capture(), capture()];
    for (let index = 0; index < 30; index++) pair.forEach(value => value.rows.push(row(7000 + index)));
    pair.forEach(value => value.count = value.rows.length);
    pair[1].rows[31].x++;
    assert.throws(() => assertFrozenMovementPair(pair), /Frozen complete party positions differ/);
});

test('incomplete, unready or moving evidence cannot establish frozen equality', () => {
    for (const key of ['complete', 'ready', 'worldClockStopped']) {
        const pair = [capture(), capture()]; pair[1][key] = false;
        assert.throws(() => assertFrozenMovementPair(pair));
    }
});

test('missing pages, duplicate ids and unregistered parties fail before equality', () => {
    for (const change of [value => value.count++, value => value.rows[1].handle = value.rows[0].handle,
        value => value.rows[1].id = value.rows[0].id, value => value.rows[0].handle = 0,
        value => value.rows[0].x = NaN]) {
        const pair = [capture(), capture()]; change(pair[1]);
        assert.throws(() => assertFrozenMovementPair(pair));
    }
});

test('wrong campaign, epoch or tick is not treated as position convergence', () => {
    for (const key of ['campaign', 'epoch', 'ticks']) {
        const pair = [capture(), capture()]; pair[1][key] = key === 'campaign' ? 'other' : pair[1][key] + 1;
        assert.throws(() => assertFrozenMovementPair(pair), /Movement capture mismatch/);
    }
});

test('empty pairs cannot make a frozen campaign appear consistent', () => {
    assert.throws(() => assertFrozenNpcSamples([0, 1].map(() =>
        ({ campaign: 'world', ticks: 10, npcSample: [] }))), /Empty frozen/);
    assert.throws(() => assertFrozenMovementPair([0, 1].map(() =>
        ({ ...capture(), count: 0, rows: [] }))), /Empty movement/);
});

test('matching invalid sample coordinates, identities or handles remain invalid', () => {
    for (const change of [value => value.x = null, value => value.handle = 0, value => value.id = '']) {
        const value = row(); change(value);
        assert.throws(() => assertFrozenNpcSamples([0, 1].map(() =>
            ({ campaign: 'world', ticks: 10, npcSample: [value] }))));
    }
    assert.throws(() => assertFrozenNpcSamples([0, 1].map(() =>
        ({ campaign: 'world', ticks: 10, npcSample: [row(), row()] }))));
});
