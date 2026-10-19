import assert from 'node:assert/strict';
import test from 'node:test';
import { collectNativeLog } from './native-log-archive.mjs';

function reader(chunks) {
    let index = 0;
    return async () => { assert.ok(index < chunks.length, 'unexpected extra read'); return chunks[index++]; };
}
const chunk = (text, cursor, reset = false, hasMore = false) => ({ text, cursor, reset, hasMore });
const state = (text = '', cursor = undefined) => ({ text, cursor });

test('single-page copied archive preserves identical emissions and appends new tail once', async () => {
    const log = state('same\nsame\n', 'live');
    await collectNativeLog(log, reader([chunk('same\nsame\nsame\n', 'archive', true)]), { archived: true });
    assert.equal(log.text, 'same\nsame\nsame\n');
    assert.equal(log.collections[0].copiedPrefixBytes, 10);
});

test('multi-page archive reconciles repeated application lines on later pages', async () => {
    const log = state('header\napplication\napplication\n', 'live');
    await collectNativeLog(log, reader([chunk('header\n', 'a1', true, true),
        chunk('application\n', 'a2', false, true), chunk('application\ntail\n', 'a3')]), { archived: true });
    assert.equal(log.text, 'header\napplication\napplication\ntail\n');
    assert.equal(log.collections[0].pages[1].requestedCursor, 'a1');
    assert.equal(log.cursor, 'a3');
});

test('Unicode UTF-8 byte lengths differ from character counts across page boundaries', async () => {
    const text = '甲🧭\r\n乙\r\n', log = state();
    await collectNativeLog(log, reader([chunk('甲', 'l1', false, true), chunk('🧭\r\n乙\r\n', 'l2')]));
    await collectNativeLog(log, reader([chunk('甲🧭', 'a1', true, true), chunk('\r\n乙\r\n新尾', 'a2')]), { archived: true });
    assert.equal(log.text, text + '新尾');
    assert.equal(log.collections[1].copiedPrefixBytes, Buffer.byteLength(text));
    assert.equal(log.collections[1].pages[0].bytes, 7);
});

test('same-source repeated identical records remain present', async () => {
    const log = state();
    await collectNativeLog(log, reader([chunk('same\nsame\n', 'l1')]));
    await collectNativeLog(log, reader([chunk('same\n', 'l2')]));
    assert.equal(log.text, 'same\nsame\nsame\n');
});

test('wrong prefix fails without changing accepted text or cursor and retains page audit', async () => {
    const log = state('old\nold\n', 'live');
    await assert.rejects(collectNativeLog(log, reader([chunk('old\n', 'a1', true, true), chunk('different\n', 'a2')]),
        { archived: true }), /exact collected live byte prefix/);
    assert.equal(log.text, 'old\nold\n'); assert.equal(log.cursor, 'live');
    assert.equal(log.collections[0].status, 'FAILED'); assert.equal(log.collections[0].pages.length, 2);
    assert.match(log.collections[0].pages[1].sha256, /^[a-f0-9]{64}$/);
});

test('incomplete or shorter archive and unexpected live resets fail closed', async () => {
    await assert.rejects(collectNativeLog(state('long', 'l'), reader([chunk('lo', 'a', true)]), { archived: true }), /byte prefix/);
    await assert.rejects(collectNativeLog(state(), reader([chunk('live', 'a', true)])), /Live log reset/);
    const log = state('old', 'l');
    await assert.rejects(collectNativeLog(log, reader([chunk('old', 'a', true, true)]), { archived: true, pageBudget: 1 }), /page budget/);
    assert.equal(log.text, 'old'); assert.equal(log.cursor, 'l');
});

test('source changes mid-pagination fail rather than stitching unrelated logs', async () => {
    await assert.rejects(collectNativeLog(state('old', 'l'), reader([chunk('old', 'a', true, true),
        chunk('other', 'b', true)]), { archived: true }), /changed during/);
});

test('concurrent independent sources retain separate cursors, bytes and receipts', async () => {
    const a = state('A\n', 'liveA'), b = state('B\n', 'liveB');
    await Promise.all([collectNativeLog(a, reader([chunk('A\n', 'a1', true, true), chunk('A\n', 'a2')]), { archived: true }),
        collectNativeLog(b, reader([chunk('B\nB\n', 'b1', true)]), { archived: true })]);
    assert.equal(a.text, 'A\nA\n'); assert.equal(b.text, 'B\nB\n');
    assert.equal(a.collections[0].pages[0].requestedCursor, 'liveA');
    assert.equal(b.collections[0].pages[0].requestedCursor, 'liveB');
});


test('same-length wrong archive prefix is not accepted by length alone', async () => {
    await assert.rejects(collectNativeLog(state('aa', 'live'), reader([chunk('bb', 'a', true)]),
        { archived: true }), /byte prefix/);
});

test('receipts associate original raw call IDs with cursor and content hashes', async () => {
    const log = state('', 'live');
    await collectNativeLog(log, async cursor => {
        assert.equal(cursor, 'live');
        return { qaCallId: 42, result: chunk('ERR repeated\nERR repeated\n', 'next') };
    }, { source: { runId: 'run', instance: 'client1', rawRecord: 'raw.jsonl' } });
    const receipt = log.collections[0];
    assert.equal(receipt.pages[0].callId, 42); assert.equal(receipt.source.rawRecord, 'raw.jsonl');
    assert.match(receipt.pages[0].requestedCursorSha256, /^[a-f0-9]{64}$/);
    assert.match(receipt.pages[0].cursorSha256, /^[a-f0-9]{64}$/);
    assert.equal(log.text, 'ERR repeated\nERR repeated\n');
});
