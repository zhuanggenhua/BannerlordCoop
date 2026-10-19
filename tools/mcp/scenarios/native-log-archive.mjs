import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';

const digest = text => createHash('sha256').update(text, 'utf8').digest('hex');

// read_logs remains the source of bytes and cursors; only reconcile its archived copy.
export async function collectNativeLog(log, read, { archived = false, pageBudget = 1024, source = null } = {}) {
    log.collections ??= [];
    const receipt = { source, archived, startedUtc: new Date().toISOString(), previousSha256: digest(log.text),
        previousBytes: Buffer.byteLength(log.text), pages: [], status: 'READING' };
    log.collections.push(receipt);
    let cursor = log.cursor, collected = '', reset = false;
    try {
        for (let page = 0; page < pageBudget; page++) {
            const requestedCursor = cursor;
            const response = await read(cursor);
            const chunk = response.result ?? response;
            assert.equal(typeof chunk.text, 'string'); assert.equal(typeof chunk.cursor, 'string');
            assert.equal(typeof chunk.reset, 'boolean'); assert.equal(typeof chunk.hasMore, 'boolean');
            receipt.pages.push({ requestedCursor: requestedCursor ?? null, cursor: chunk.cursor,
                callId: response.qaCallId ?? null, requestedCursorSha256: requestedCursor === undefined ? null : digest(requestedCursor),
                cursorSha256: digest(chunk.cursor), reset: chunk.reset, hasMore: chunk.hasMore, bytes: Buffer.byteLength(chunk.text),
                sha256: digest(chunk.text), receivedUtc: new Date().toISOString() });
            if (chunk.reset) {
                assert.ok(archived, 'Live log reset; complete native application evidence is unavailable');
                assert.equal(page, 0, 'Log source changed during paginated capture');
                reset = true;
            }
            collected += chunk.text;
            cursor = chunk.cursor;
            if (!chunk.hasMore) {
                if (reset) {
                    const previous = Buffer.from(log.text, 'utf8'), archive = Buffer.from(collected, 'utf8');
                    assert.ok(archive.length >= previous.length && archive.subarray(0, previous.length).equals(previous),
                        'Archived log does not preserve the exact collected live byte prefix');
                    receipt.copiedPrefixBytes = previous.length;
                }
                const nextText = reset ? collected : log.text + collected;
                receipt.resultBytes = Buffer.byteLength(nextText);
                receipt.resultSha256 = digest(nextText);
                receipt.completedUtc = new Date().toISOString(); receipt.status = 'COMPLETE';
                log.text = nextText; log.cursor = cursor;
                return receipt;
            }
        }
        throw new Error('Incomplete log capture: page budget exhausted');
    } catch (error) {
        receipt.completedUtc = new Date().toISOString(); receipt.status = 'FAILED'; receipt.error = error.stack;
        throw error;
    }
}
