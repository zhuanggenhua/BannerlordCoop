import assert from 'node:assert/strict';

export function assertFrozenNpcSamples(worlds) {
    assert.equal(worlds.length, 2);
    assert.equal(worlds[0].campaign, worlds[1].campaign);
    assert.equal(worlds[0].ticks, worlds[1].ticks, 'Frozen NPC samples need the same world tick');
    const samples = worlds.map(world => {
        assert.ok(Array.isArray(world.npcSample), 'Missing frozen NPC position sample');
        assert.ok(world.npcSample.length > 0, 'Empty frozen NPC position sample');
        assert.equal(new Set(world.npcSample.map(row => row.handle)).size, world.npcSample.length);
        assert.equal(new Set(world.npcSample.map(row => row.id)).size, world.npcSample.length);
        for (const row of world.npcSample) {
            assert.ok(Number.isSafeInteger(row.handle) && row.handle > 0);
            assert.ok(typeof row.id === 'string' && row.id.length > 0);
            assert.ok(Number.isFinite(row.x) && Number.isFinite(row.y));
        }
        return world.npcSample.map(row => ({ handle: row.handle, id: row.id, x: row.x, y: row.y }))
            .sort((left, right) => left.handle - right.handle);
    });
    assert.deepEqual(samples[1], samples[0], 'Frozen NPC positions differ between peers');
}

export function assertFrozenMovementPair(captures) {
    assert.equal(captures.length, 2);
    for (const capture of captures) {
        assert.equal(capture.complete, true, 'Incomplete movement evidence');
        assert.equal(capture.ready, true, 'Object graph is not ready');
        assert.equal(capture.worldClockStopped, true, 'Movement comparison needs a stopped world');
        assert.ok(typeof capture.campaign === 'string' && capture.campaign.length > 0);
        assert.ok(Number.isSafeInteger(capture.epoch) && capture.epoch > 0);
        assert.ok(Number.isSafeInteger(capture.ticks) && capture.ticks > 0);
        assert.ok(Number.isSafeInteger(capture.count) && capture.count > 0, 'Empty movement capture');
        assert.ok(Array.isArray(capture.rows), 'Missing movement rows');
        assert.equal(capture.rows.length, capture.count, 'Movement page coverage is incomplete');
        assert.equal(new Set(capture.rows.map(row => row.handle)).size, capture.count, 'Duplicate movement handle');
        assert.equal(new Set(capture.rows.map(row => row.id)).size, capture.count, 'Duplicate movement identity');
        for (const row of capture.rows) {
            assert.ok(Number.isSafeInteger(row.handle) && row.handle > 0, 'Unregistered movement party');
            assert.ok(typeof row.id === 'string' && row.id.length > 0);
            assert.ok(Number.isFinite(row.x) && Number.isFinite(row.y), 'Invalid native party coordinate');
        }
    }
    for (const key of ['campaign', 'epoch', 'ticks', 'count'])
        assert.equal(captures[0][key], captures[1][key], 'Movement capture mismatch: ' + key);
    const rows = captures.map(capture => capture.rows.map(row =>
        ({ handle: row.handle, id: row.id, x: row.x, y: row.y })).sort((left, right) => left.handle - right.handle));
    assert.deepEqual(rows[1], rows[0], 'Frozen complete party positions differ between peers');
}
