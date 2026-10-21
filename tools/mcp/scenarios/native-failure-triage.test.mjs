import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { createWriteStream } from 'node:fs';
import { appendFile, mkdtemp, mkdir, readFile, writeFile, stat } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { resolve, join } from 'node:path';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { fileURLToPath } from 'node:url';
import { triageNativeFailure } from './native-failure-triage.mjs';

const sha = text => createHash('sha256').update(text).digest('hex');
const cli = fileURLToPath(new URL('./native-failure-triage.mjs', import.meta.url));
const head = 'e'.repeat(40), runId = 'actual-run-not-filename';
const actor = { name: 'client1', identity: { pid: 42, platformId: 'B', runToken: runId },
    status: { pid: 42, runToken: runId, buildVersion: '0.1.7+' + head, assemblyMvid: 'actual-mvid', loadedAssemblies: [{ name: 'Coop', mvid: 'actual-mvid' }] } };
async function fixture(log = '[42 11:00:00 INF Test] ready\r\n', extra = {}) {
    const repo = await mkdtemp(join(tmpdir(), 'native-triage-'));
    const record = join(repo, 'unrelated-name.jsonl'), logPath = record + '.client1.log';
    const stopped = { runId, state: 'stopped', instances: [{ ...actor, status: null, processAlive: false, processTreeAlive: false, cleanupComplete: true }] };
    const rows = [{ phase: 'response', name: 'start_run', data: { runId, instances: [actor] } },
        { phase: 'response', name: 'stop_run', data: stopped }];
    await writeFile(record, rows.map(x => JSON.stringify(x)).join('\n') + '\n');
    await writeFile(logPath, log);
    const summary = { scenario: 'synthetic', codeHead: head, runId, passed: false, cleanupConfirmed: true,
        completedUtc: '2026-10-21T00:00:00Z', processes: [actor], stopped,
        features: [{ name: 'one', status: 'PASS' }, { name: 'two', status: 'FAIL', error: 'original failure' }],
        failed: { message: 'original failure' }, errors: ['additional original error'],
        logs: { client1: { path: logPath, sha256: sha(log) } }, screenshots: [], ...extra };
    await writeFile(record + '.summary.json', JSON.stringify(summary));
    return { repo, record, logPath, summary, out: join(repo, 'new-report') };
}

test('strict original level semantics, first entry context and stack stop at next header', async () => {
    const log = '\uFEFF[(42) 11:00:00 DBG AutoRegistry] Environment.GetStackTrace(Exception e, Boolean x)\r\n' +
        '   at DEBUG.Exception.NoError()\r\n' +
        '[(42) 11:00:01 INF Authority] epoch5 lease applied\r\n' +
        '[(42) 11:00:02 ERR Real] original first error\r\n' +
        'System.NullReferenceException: actual\r\n   at Real.First()\r\n' +
        '[(42) 11:00:03 DBG Other] debug stack\r\n   at Debug.Second()\r\n' +
        '[(42) 11:00:04 FTL Fatal] second real error\r\n' +
        '[(42) 11:00:05 WRN AI] Skipping mobile-party AI x next 10 visits after 3 failures\r\n';
    const f = await fixture(log), report = await triageNativeFailure(f), result = report.logs.client1;
    assert.equal(result.errorEntries, 2);
    assert.equal(result.firstError.line, 4);
    assert.deepEqual(result.firstError.continuation.map(x => x.line), [5, 6]);
    assert.ok(!result.firstError.continuation.some(x => x.text.includes('Debug.Second')));
    assert.equal(result.firstError.before.length, 3);
    assert.equal(result.firstError.latestPriorAuthority[0].line, 3);
    assert.equal(result.quarantineWarnings.count, 1);
    assert.equal(report.native.passed, false);
    assert.equal(report.rootCause, 'NOT_INFERRED');
    assert.deepEqual(report.native.features.map(x => x.status), ['PASS', 'FAIL']);
    assert.match(report.native.errors.errors.text, /additional original error/);
});

test('all 1020 error entries scanned; first contiguous headers have no borrowed stack', async () => {
    const f = await fixture('[42 00:00:00 ERR Class] same\n'.repeat(1020));
    const r = await triageNativeFailure(f);
    assert.equal(r.logs.client1.errorEntries, 1020);
    assert.equal(r.logs.client1.groupedErrors[0].count, 1020);
    assert.equal(r.logs.client1.uniqueSignatures, 1);
    assert.deepEqual(r.logs.client1.firstError.continuation, []);
    assert.equal(r.logs.client1.firstError.after.length, 24);
});

test('repo relative evidence, real provenance, stop receipts and original source bytes persist', async () => {
    const f = await fixture();
    const shot = join(f.repo, 'image.bmp');
    await writeFile(shot, Buffer.from([0x42, 0x4d, 0, 255]));
    f.summary.screenshots = [{ path: 'image.bmp', sha256: sha(await readFile(shot)) }];
    await writeFile(f.record + '.summary.json', JSON.stringify(f.summary));
    const paths = [f.record, f.record + '.summary.json', f.logPath, shot];
    const originals = await Promise.all(paths.map(p => readFile(p)));
    const r = await triageNativeFailure({ ...f, record: 'unrelated-name.jsonl' });
    assert.equal(r.completeness, 'COMPLETE');
    assert.equal(r.native.runId, runId);
    assert.equal(r.native.codeHead, head);
    assert.equal(r.raw.starts[0].instances[0].identity.pid, 42);
    assert.equal(r.raw.actors[0].build.assemblyMvid, 'actual-mvid');
    assert.equal(r.raw.stops[0].instances[0].cleanupComplete, true);
    assert.equal(r.native.stop.instances[0].processAlive, false);
    for (let i = 0; i < paths.length; i++) {
        assert.deepEqual(await readFile(paths[i]), originals[i]);
        const source = r.sources.find(x => x.path === resolve(paths[i]));
        assert.equal(source.sha256, sha(originals[i]));
        assert.equal(source.reverifiedSha256, source.sha256);
        assert.equal(source.unchanged, true);
    }
});

test('preexisting output and attempts to use an input path refuse without replacement', async () => {
    const f = await fixture();
    await mkdir(f.out);
    await writeFile(join(f.out, 'report.json'), 'do not replace');
    await assert.rejects(triageNativeFailure(f), /already exists/);
    assert.equal(await readFile(join(f.out, 'report.json'), 'utf8'), 'do not replace');
    await assert.rejects(triageNativeFailure({ ...f, out: f.record }), /already exists/);
    await assert.rejects(triageNativeFailure({ ...f, out: f.repo }), /already exists/);
});

test('missing screenshot/log and original summary diagnostics visibly mark incomplete', async () => {
    const f = await fixture();
    f.summary.logs.absent = { path: 'missing.log' };
    f.summary.logs.client1.critical = { errorLines: ['recorded but absent'] };
    f.summary.screenshots.push({ path: 'missing.bmp' });
    f.summary.logVerificationError = 'original verification failure';
    await writeFile(f.record + '.summary.json', JSON.stringify(f.summary));
    const r = await triageNativeFailure(f);
    assert.equal(r.completeness, 'INCOMPLETE');
    assert.equal(r.sources.filter(x => x.missing).length, 2);
    assert.ok(r.issues.some(x => x.code === 'summary-error-count-mismatch'));
    assert.equal(r.native.errors.logVerificationError.text, 'original verification failure');
});

test('summary versus raw PID/build/run and cleanup conflicts are visible without correcting original fields', async () => {
    const f = await fixture();
    f.summary.runId = 'wrong-summary-run';
    f.summary.processes = [{ ...actor, identity: { pid: 99 }, status: { buildVersion: 'wrong-version', assemblyMvid: 'wrong-mvid' } }];
    await writeFile(f.record + '.summary.json', JSON.stringify(f.summary));
    const raw = JSON.parse((await readFile(f.record, 'utf8')).trim().split('\n')[0]);
    await writeFile(f.record, JSON.stringify(raw) + '\n');
    const r = await triageNativeFailure(f);
    assert.equal(r.native.runId, 'wrong-summary-run');
    assert.equal(r.native.actors[0].identity.pid, 99);
    assert.equal(r.native.cleanupConfirmed, true);
    for (const code of ['run-id-not-in-raw', 'summary-raw-actor-mismatch', 'cleanup-receipt-disagreement'])
        assert.ok(r.issues.some(x => x.code === code), code);
    assert.equal(r.completeness, 'INCOMPLETE');
});

test('malformed JSONL and partial tail are explicit; EOF error continuation retained', async () => {
    const f = await fixture('[42 00:00:00 ERR Class] first\n   at EOF.Stack()');
    await writeFile(f.record, '{bad}\n{"phase":"response","data":{"runId":"actual-run-not-filename"}}');
    const r = await triageNativeFailure(f);
    assert.equal(r.raw.malformedRecords, 1);
    assert.equal(r.raw.partialFinalLine, true);
    assert.equal(r.logs.client1.firstError.continuation[0].text, '   at EOF.Stack()');
    assert.ok(r.issues.some(x => x.code === 'partial-log-tail'));
    assert.equal(r.completeness, 'INCOMPLETE');
});

test('malformed summary and valid JSON with invalid record shape yield an explicit incomplete artifact', async () => {
    const f = await fixture();
    await writeFile(f.record + '.summary.json', 'null');
    await writeFile(f.record, 'null\n42\n[]\n');
    const r = await triageNativeFailure(f);
    assert.equal(r.completeness, 'INCOMPLETE');
    assert.equal(r.native.runId, null);
    assert.equal(r.raw.malformedRecords, 3);
    assert.ok(r.issues.some(x => x.code === 'malformed-summary'));
    assert.ok(r.issues.some(x => x.code === 'invalid-jsonl-record-shape'));
});

test('UTF8 multibyte split at stream boundary and BOM CRLF retain original SHA and line number', async () => {
    const prefix = '[42 00:00:00 DBG X] ';
    const first = prefix + 'x'.repeat(65535 - Buffer.byteLength(prefix)) + '中\r\n';
    const f = await fixture(first + '[42 00:00:01 ERR 中文] 首错\r\n');
    const r = await triageNativeFailure(f);
    assert.equal(r.logs.client1.firstError.line, 2);
    assert.match(r.logs.client1.firstError.text, /首错/);
    assert.equal(r.sources.find(x => x.path === f.logPath).sha256, sha(first + '[42 00:00:01 ERR 中文] 首错\r\n'));
    assert.equal(r.logs.client1.errorEntries, 1);
});

test('capped line/issue/group retention is explicit rather than silently claiming completeness', async () => {
    const f = await fixture('[42 00:00:00 ERR X] ' + 'x'.repeat(70000) + '\n' +
        Array.from({ length: 150 }, (_, i) => `[42 00:00:01 ERR X] distinct ${i}\n`).join(''));
    await writeFile(f.record, '{bad}\n'.repeat(300));
    const r = await triageNativeFailure(f);
    assert.equal(r.issues.length, 256);
    assert.ok(r.omittedIssueDetails > 0);
    assert.equal(r.logs.client1.errorEntries, 151);
    assert.equal(r.logs.client1.groupedErrors.length, 128);
    assert.equal(r.logs.client1.signaturesComplete, false);
    assert.ok(r.logs.client1.firstError.truncated);
});

test('CLI streams a generated >96MiB log under a 64MiB V8 heap cap, original FAIL stays FAIL', async () => {
    const f = await fixture();
    const stream = createWriteStream(f.logPath);
    const line = '[42 00:00:00 DBG AutoRegistry] Environment.GetStackTrace(Exception e) ' + 'x'.repeat(950) + '\n';
    const block = line.repeat(1024);
    for (let i = 0; i < 100; i++) if (!stream.write(block)) await once(stream, 'drain');
    stream.end('[42 00:00:01 ERR End] actual end\n');
    await once(stream, 'finish');
    delete f.summary.logs.client1.sha256;
    await writeFile(f.record + '.summary.json', JSON.stringify(f.summary));
    const child = spawn(process.execPath, ['--max-old-space-size=64', cli, '--repo', f.repo, '--record', 'unrelated-name.jsonl', '--out', f.out], { windowsHide: true });
    let output = '', error = '';
    child.stdout.on('data', x => output += x); child.stderr.on('data', x => error += x);
    const [exit] = await once(child, 'exit');
    assert.equal(exit, 0, error);
    const receipt = JSON.parse(output);
    assert.equal(receipt.nativePassed, false);
    assert.equal(receipt.errors.client1, 1);
    const r = JSON.parse(await readFile(join(f.out, 'report.json'), 'utf8'));
    assert.equal(r.logs.client1.firstError.line, 102401);
    assert.ok((await stat(f.logPath)).size > 96 * 1024 * 1024);
    assert.ok(r.performance.sampledHeapHighWaterBytes < 64 * 1024 * 1024);

    // A completed source must stay stable throughout scanning, including the final rehash.
    let appending = true, writes = 0;
    const append = (async () => {
        while (appending) {
            await appendFile(f.logPath, '[42 00:00:02 DBG Append] still running\n');
            writes++;
            await new Promise(done => setTimeout(done, 2));
        }
    })();
    let changed;
    try { changed = await triageNativeFailure({ ...f, out: join(f.repo, 'changing-source-report') }); }
    finally { appending = false; await append; }
    assert.ok(writes > 1);
    assert.equal(changed.completeness, 'INCOMPLETE');
    assert.ok(changed.issues.some(x => x.code === 'source-changing' || x.code === 'source-changed-during-analysis'));
    assert.equal(changed.sources.find(x => x.path === f.logPath).unchanged, false);
});
