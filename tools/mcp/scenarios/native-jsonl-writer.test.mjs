import assert from 'node:assert/strict';
import test from 'node:test';
import { mkdtemp, readFile, realpath, rm } from 'node:fs/promises';
import { basename, dirname, join } from 'node:path';
import { tmpdir } from 'node:os';
import { createNativeJsonlWriter } from './native-jsonl-writer.mjs';

async function removeOwnedTemporaryDirectory(directory) {
    const absolute = await realpath(directory), parent = await realpath(tmpdir());
    assert.equal(dirname(absolute).toLowerCase(), parent.toLowerCase());
    assert.match(basename(absolute), /^coop-native-jsonl-/);
    await rm(absolute, { recursive: true });
}

test('concurrent large native pages retain invocation order and complete JSON records', async () => {
    const directory = await mkdtemp(join(tmpdir(), 'coop-native-jsonl-'));
    try {
        const file = join(directory, 'evidence.jsonl'), emit = createNativeJsonlWriter(file);
        const payload = 'x'.repeat(1024 * 1024);
        await Promise.all(Array.from({ length: 12 }, (_, seq) => emit({ seq, payload })));
        const rows = (await readFile(file, 'utf8')).trimEnd().split('\n').map(line => JSON.parse(line));
        assert.deepEqual(rows.map(row => row.seq), Array.from({ length: 12 }, (_, index) => index));
        assert.ok(rows.every(row => row.payload === payload));
    } finally { await removeOwnedTemporaryDirectory(directory); }
});

test('failed archive writes propagate and later records cannot masquerade as successful continuation', async () => {
    const directory = await mkdtemp(join(tmpdir(), 'coop-native-jsonl-'));
    try {
        const emit = createNativeJsonlWriter(directory);
        await assert.rejects(emit({ seq: 0 })); await assert.rejects(emit({ seq: 1 }));
    } finally { await removeOwnedTemporaryDirectory(directory); }
});
