import { createReadStream } from 'node:fs';
import { mkdir, readFile, realpath, stat, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { dirname, isAbsolute, relative, resolve, sep } from 'node:path';
import { pathToFileURL } from 'node:url';
import { performance } from 'node:perf_hooks';
import { logDiagnostics } from './p2p-udp-reliability.mjs';

export const HELP = 'Usage: node native-failure-triage.mjs --repo <evidence repo> --record <JSONL> --out <new directory>';
const LIMITS = { summaryBytes: 16 * 1024 * 1024, recordLineBytes: 16 * 1024 * 1024,
    logLineBytes: 64 * 1024, textBytes: 4096, groups: 128, before: 8, after: 24, timeline: 12 };
const unknown = value => value === undefined ? null : value;
const short = value => {
    const text = typeof value === 'string' ? value : JSON.stringify(value);
    if (text === undefined) return null;
    const bytes = Buffer.from(text);
    return { text: bytes.subarray(0, LIMITS.textBytes).toString('utf8'), bytes: bytes.length,
        truncated: bytes.length > LIMITS.textBytes };
};
const header = line => line.match(/^\s*\[([^\]\r\n]*)\]/)?.[1];
const entry = (line, text) => ({ line, ...short(text) });
const stamp = s => ({ size: s.size, mtimeMs: s.mtimeMs, ctimeMs: s.ctimeMs, ino: s.ino });

function boundedIssues() {
    const list = [];
    list.omitted = 0;
    list.push = (...items) => {
        for (const item of items) {
            if (list.length < 256) Array.prototype.push.call(list, item);
            else list.omitted++;
        }
        return list.length;
    };
    return list;
}

async function hashFile(path) {
    const before = stamp(await stat(path));
    const hash = createHash('sha256');
    let bytes = 0;
    for await (const chunk of createReadStream(path)) { hash.update(chunk); bytes += chunk.length; }
    const after = stamp(await stat(path));
    return { path, sha256: hash.digest('hex'), bytes, stat: after,
        stable: JSON.stringify(before) === JSON.stringify(after) && bytes === after.size };
}

// Keep at most one capped line. Hashing elsewhere always covers the original bytes.
async function scanLines(path, maxBytes, visit, sampleHeap) {
    let parts = [], length = 0, oversized = false, number = 0, tail = false;
    const emit = async () => {
        number++;
        let line = Buffer.concat(parts, length).toString('utf8');
        if (line.endsWith('\r')) line = line.slice(0, -1);
        if (number === 1) line = line.replace(/^\uFEFF/, '');
        await visit(line, number, oversized);
        parts = []; length = 0; oversized = false;
    };
    for await (const chunk of createReadStream(path)) {
        sampleHeap();
        let start = 0;
        for (let i = 0; i <= chunk.length; i++) {
            if (i !== chunk.length && chunk[i] !== 10) continue;
            const fragment = chunk.subarray(start, i);
            const room = maxBytes - length;
            if (fragment.length > room) oversized = true;
            if (room > 0) {
                const kept = fragment.subarray(0, room);
                parts.push(kept); length += kept.length;
            }
            if (i < chunk.length) { await emit(); tail = false; }
            else if (fragment.length) tail = true;
            start = i + 1;
        }
    }
    if (tail || length || oversized) await emit();
    return { lines: number, partialFinalLine: tail };
}

async function scanLog(path, issues, sampleHeap) {
    const result = { errorEntries: 0, uniqueSignatures: 0, groupedErrors: [], ungroupedEntries: 0,
        realTick: 0, duplicateIds: 0, aiBatch: 0, firstError: null,
        quarantineWarnings: { count: 0, examples: [] } };
    const groups = new Map(), before = [], prior = [];
    let firstOpen = false;
    const scanned = await scanLines(path, LIMITS.logLineBytes, (text, line, oversized) => {
        if (oversized) issues.push({ code: 'oversized-log-line', path, line });
        const row = entry(line, text), prefix = header(text);
        const diagnostic = logDiagnostics(text);
        for (const key of ['realTick', 'duplicateIds', 'aiBatch']) result[key] += diagnostic[key];
        if (result.firstError && result.firstError.after.length < LIMITS.after) result.firstError.after.push(row);
        if (firstOpen) {
            if (prefix !== undefined) firstOpen = false;
            else if (result.firstError.continuation.length < LIMITS.after) result.firstError.continuation.push(row);
            else result.firstError.continuationTruncated = true;
        }
        if (diagnostic.errorLines.length) {
            result.errorEntries++;
            const signature = text.replace(/^\s*\[[^\]]*\]\s*/, '');
            const key = createHash('sha256').update(signature).digest('hex');
            if (groups.has(key)) groups.get(key).count++;
            else if (groups.size < LIMITS.groups) groups.set(key, { signature: short(signature), signatureSha256: key, firstHeader: row,
                count: 1, firstLine: line, lastLine: line });
            else result.ungroupedEntries++;
            if (groups.has(key)) groups.get(key).lastLine = line;
            if (!result.firstError) {
                result.firstError = { ...row, before: [...before], after: [], continuation: [],
                    continuationTruncated: false, latestPriorAuthority: [...prior] };
                firstOpen = true;
            }
        }
        if (prefix !== undefined && /(?:^|\s)(?:WRN|WARN|WARNING)(?:\s|$)/.test(prefix) &&
            /Skipping mobile-party AI|AI.*quarantin/i.test(text)) {
            result.quarantineWarnings.count++;
            if (result.quarantineWarnings.examples.length < 8) result.quarantineWarnings.examples.push(row);
        }
        if (prefix !== undefined && /handoff|barrier|lease|epoch|navigation|movement.*(?:baseline|checkpoint|install|capture)/i.test(text)) {
            prior.push(row); if (prior.length > LIMITS.timeline) prior.shift();
        }
        before.push(row); if (before.length > LIMITS.before) before.shift();
    }, sampleHeap);
    result.groupedErrors = [...groups.values()].sort((a, b) => b.count - a.count);
    result.uniqueSignatures = groups.size;
    result.signaturesComplete = result.ungroupedEntries === 0;
    Object.assign(result, scanned);
    if (scanned.partialFinalLine) issues.push({ code: 'partial-log-tail', path, line: scanned.lines });
    return result;
}

function actorReceipt(actor) {
    return { name: unknown(actor.name), identity: actor.identity ?? null, processAlive: unknown(actor.processAlive),
        processTreeAlive: unknown(actor.processTreeAlive), cleanupComplete: unknown(actor.cleanupComplete),
        error: short(actor.error), build: actor.status ? {
            pid: unknown(actor.status.pid), runToken: unknown(actor.status.runToken),
            buildVersion: unknown(actor.status.buildVersion), assemblyMvid: unknown(actor.status.assemblyMvid),
            processStartedUtc: unknown(actor.status.processStartedUtc), loadedAssemblies: actor.status.loadedAssemblies ?? null
        } : null };
}

async function scanRaw(path, issues, sampleHeap) {
    const result = { records: 0, malformedRecords: 0, runIds: [], starts: [], stops: [], actors: [], firstUtc: null, lastUtc: null };
    const ids = new Set();
    const actors = new Map();
    const scanned = await scanLines(path, LIMITS.recordLineBytes, (text, line, oversized) => {
        if (oversized) { result.malformedRecords++; issues.push({ code: 'oversized-jsonl-line', path, line }); return; }
        if (!text.trim()) return;
        let row;
        try { row = JSON.parse(text); }
        catch { result.malformedRecords++; issues.push({ code: 'malformed-jsonl', path, line }); return; }
        if (!row || typeof row !== 'object' || Array.isArray(row)) {
            result.malformedRecords++; issues.push({ code: 'invalid-jsonl-record-shape', path, line }); return;
        }
        result.records++;
        result.firstUtc ??= row.utc ?? null; result.lastUtc = row.utc ?? result.lastUtc;
        if (row.phase !== 'response') return;
        const data = row.data;
        if (data?.runId && ids.size < 32) ids.add(data.runId);
        const instances = Array.isArray(data?.instances) ? data.instances : (data?.result?.instance ? [data.result.instance] : []);
        for (const instance of instances) {
            if (instance?.status && (actors.size < 32 || actors.has(instance.name)))
                actors.set(instance.name, { line, utc: row.utc ?? null, ...actorReceipt(instance) });
        }
        if (['start_run', 'stop_run'].includes(row.name)) {
            const target = row.name === 'start_run' ? result.starts : result.stops;
            if (target.length < 32) target.push({ line, utc: row.utc ?? null, runId: data?.runId ?? null,
                state: data?.state ?? null, error: short(data?.error), instances: instances.filter(x => x && typeof x === 'object').map(actorReceipt) });
        }
    }, sampleHeap);
    Object.assign(result, scanned, { runIds: [...ids], actors: [...actors.values()] });
    if (scanned.partialFinalLine) issues.push({ code: 'partial-jsonl-tail', path, line: scanned.lines });
    return result;
}

function markdown(report) {
    const lines = ['# Native evidence triage', '', `Diagnostic completeness: ${report.completeness}.`,
        `Recorded native verdict: ${JSON.stringify(report.native.passed)} (unchanged; this tool does not accept a native scenario).`,
        `Run: ${report.native.runId ?? 'unknown'}; runtime HEAD: ${report.native.codeHead ?? 'unknown'}.`,
        `Cleanup confirmed: ${JSON.stringify(report.native.cleanupConfirmed)}.`, '', '## Recorded features', ''];
    for (const feature of report.native.features) lines.push(`- ${feature.name}: ${feature.status}`);
    lines.push('', '## Original summary errors', '', '```json', JSON.stringify(report.native.errors, null, 2), '```', '', '## Actor errors', '');
    for (const [actor, log] of Object.entries(report.logs)) {
        lines.push(`### ${actor}`, '', `Entries: ${log.errorEntries}; retained signatures: ${log.uniqueSignatures}; quarantine warnings: ${log.quarantineWarnings.count}.`);
        if (log.firstError) {
            lines.push('', 'First error and its own continuation:', '', '```text',
                ...[log.firstError, ...log.firstError.continuation].map(x => `${x.line}: ${x.text}`), '```', '',
                'Latest prior authority/navigation headers:', '', '```text',
                ...log.firstError.latestPriorAuthority.map(x => `${x.line}: ${x.text}`), '```');
        }
        lines.push('');
    }
    lines.push('## Integrity and provenance', '', 'See report.json for exact paths, all SHA256 receipts, PID/build/stop receipts, bounded context and grouped errors.', '',
        '```json', JSON.stringify(report.issues, null, 2), '```', '', `Elapsed ${report.performance.elapsedMs} ms; sampled heap high-water ${report.performance.sampledHeapHighWaterBytes} bytes.`, '');
    return lines.join('\n');
}

export async function triageNativeFailure({ repo, record, out }) {
    if (!repo || !record || !out) throw new Error(HELP);
    const started = performance.now();
    let heap = process.memoryUsage().heapUsed;
    const sampleHeap = () => { heap = Math.max(heap, process.memoryUsage().heapUsed); };
    repo = await realpath(resolve(repo));
    record = resolve(repo, record); out = resolve(out);
    const summaryPath = record + '.summary.json';
    const parent = await realpath(dirname(out));
    out = resolve(parent, out.split(sep).at(-1));
    const sources = new Map(), issues = boundedIssues(), logs = {};
    async function source(path, kind, expectedSha256) {
        path = resolve(repo, path);
        const rel = relative(out, path);
        if (rel === '' || (!rel.startsWith('..' + sep) && rel !== '..' && !isAbsolute(rel)))
            throw new Error('Output contains an input source');
        try {
            const receipt = await hashFile(path);
            const value = { ...receipt, kind, expectedSha256: expectedSha256 ?? null };
            sources.set(path, value);
            if (!receipt.stable) issues.push({ code: 'source-changing', path });
            if (expectedSha256 && expectedSha256 !== receipt.sha256) issues.push({ code: 'recorded-sha-mismatch', path });
            return value;
        } catch (error) {
            issues.push({ code: 'missing-or-unreadable-source', path, message: error.message });
            sources.set(path, { path, kind, missing: true, expectedSha256: expectedSha256 ?? null });
            return null;
        }
    }
    // Refuse pre-existing outputs before reading large sources, and never replace them.
    try { await stat(out); throw new Error('Output directory already exists'); }
    catch (error) { if (error.code !== 'ENOENT') throw error; }
    let summary = {};
    const summaryReceipt = await source(summaryPath, 'summary');
    if (summaryReceipt) {
        if (summaryReceipt.bytes > LIMITS.summaryBytes) issues.push({ code: 'oversized-summary', path: summaryPath });
        else {
            try {
                summary = JSON.parse((await readFile(summaryPath, 'utf8')).replace(/^\uFEFF/, ''));
                if (!summary || typeof summary !== 'object' || Array.isArray(summary)) throw new Error('Expected summary object');
                for (const key of ['processes', 'screenshots', 'features']) {
                    if (summary[key] !== undefined && !Array.isArray(summary[key])) throw new Error('Expected array: ' + key);
                }
                if (summary.logs !== undefined && (!summary.logs || typeof summary.logs !== 'object' || Array.isArray(summary.logs)))
                    throw new Error('Expected actor log map');
            }
            catch (error) { summary = {}; issues.push({ code: 'malformed-summary', path: summaryPath, message: error.message }); }
        }
    }
    const rawReceipt = await source(record, 'raw-jsonl');
    const raw = rawReceipt ? await scanRaw(record, issues, sampleHeap) : null;
    const actorLogs = Object.keys(summary.logs ?? {}).length ? summary.logs :
        Object.fromEntries((summary.processes ?? []).map(actor => [actor.name, { path: record + '.' + actor.name + '.log' }]));
    for (const [actor, metadata] of Object.entries(actorLogs)) {
        if (!metadata || typeof metadata !== 'object') { issues.push({ code: 'invalid-actor-log-metadata', actor }); continue; }
        const path = resolve(repo, metadata.path ?? record + '.' + actor + '.log');
        if (await source(path, 'actor-log', metadata.sha256)) {
            logs[actor] = await scanLog(path, issues, sampleHeap);
            const recorded = metadata.critical?.errorLines;
            logs[actor].summaryRecordedErrorEntries = Array.isArray(recorded) ? recorded.length : null;
            if (recorded && recorded.length !== logs[actor].errorEntries) issues.push({ code: 'summary-error-count-mismatch', actor,
                recorded: recorded.length, scanned: logs[actor].errorEntries });
        }
    }
    for (const screenshot of summary.screenshots ?? []) {
        if (screenshot.path) await source(screenshot.path, 'screenshot', screenshot.sha256);
        else issues.push({ code: 'screenshot-path-missing', label: screenshot.label ?? null });
    }
    for (const receipt of sources.values()) {
        if (receipt.missing) continue;
        try {
            const after = await hashFile(receipt.path);
            receipt.reverifiedSha256 = after.sha256;
            receipt.unchanged = receipt.sha256 === after.sha256 && JSON.stringify(receipt.stat) === JSON.stringify(after.stat) && after.stable;
            if (!receipt.unchanged) issues.push({ code: 'source-changed-during-analysis', path: receipt.path });
        } catch (error) { receipt.unchanged = false; issues.push({ code: 'source-lost-during-analysis', path: receipt.path, message: error.message }); }
    }
    if (!summary.completedUtc) issues.push({ code: 'summary-not-completed' });
    if (raw && summary.runId && !raw.runIds.includes(summary.runId)) issues.push({ code: 'run-id-not-in-raw', runId: summary.runId });
    for (const actor of summary.processes ?? []) {
        const observed = raw?.actors.find(x => x.name === actor.name);
        if (observed && (observed.identity?.pid !== actor.identity?.pid ||
            observed.build?.buildVersion !== actor.status?.buildVersion || observed.build?.assemblyMvid !== actor.status?.assemblyMvid))
            issues.push({ code: 'summary-raw-actor-mismatch', actor: actor.name });
    }
    if (summary.cleanupConfirmed === true && !(raw?.stops.length && raw.stops.at(-1).instances.length &&
        raw.stops.at(-1).instances.every(x => x.cleanupComplete === true && x.processAlive === false)))
        issues.push({ code: 'cleanup-receipt-disagreement' });
    if (!Object.keys(logs).length) issues.push({ code: 'no-actor-logs' });
    const report = { schemaVersion: 1, generatedUtc: new Date().toISOString(), diagnosticOnly: true,
        completeness: issues.length ? 'INCOMPLETE' : 'COMPLETE', rootCause: 'NOT_INFERRED',
        input: { repo, record, summary: summaryPath }, limits: LIMITS,
        native: { scenario: summary.scenario ?? null, codeHead: summary.codeHead ?? null, runId: summary.runId ?? null,
            passed: unknown(summary.passed), cleanupConfirmed: unknown(summary.cleanupConfirmed), startedUtc: summary.startedUtc ?? null,
            completedUtc: summary.completedUtc ?? null, features: (summary.features ?? []).map(f => ({ name: f.name, status: f.status,
                startedUtc: f.startedUtc ?? null, completedUtc: f.completedUtc ?? null, error: short(f.error) })),
            errors: Object.fromEntries(Object.entries(summary).filter(([k]) => k === 'errors' || k === 'failed' || /error$/i.test(k)).map(([k, v]) => [k, short(v)])),
            actors: (summary.processes ?? []).map(actorReceipt), osProcesses: summary.osProcesses ?? null,
            stop: summary.stopped ? { runId: summary.stopped.runId ?? null, state: summary.stopped.state ?? null,
                error: short(summary.stopped.error), instances: (summary.stopped.instances ?? []).map(actorReceipt) } : null },
        raw, logs, sources: [...sources.values()], issues, omittedIssueDetails: issues.omitted,
        performance: { elapsedMs: Math.round(performance.now() - started), sampledHeapHighWaterBytes: heap } };
    await mkdir(out);
    await writeFile(resolve(out, 'report.json'), JSON.stringify(report, null, 2) + '\n', { flag: 'wx' });
    await writeFile(resolve(out, 'report.md'), markdown(report), { flag: 'wx' });
    return report;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
    const args = {};
    try {
        for (let i = 2; i < process.argv.length; i += 2) {
            const key = process.argv[i];
            if (!['--repo', '--record', '--out'].includes(key) || !process.argv[i + 1] || args[key.slice(2)]) throw new Error(HELP);
            args[key.slice(2)] = process.argv[i + 1];
        }
        const report = await triageNativeFailure(args);
        console.log(JSON.stringify({ completeness: report.completeness, nativePassed: report.native.passed,
            runId: report.native.runId, errors: Object.fromEntries(Object.entries(report.logs).map(([k, v]) => [k, v.errorEntries])),
            performance: report.performance, out: resolve(args.out) }));
        process.exitCode = report.completeness === 'COMPLETE' ? 0 : 2;
    } catch (error) { console.error(error.message); process.exitCode = 1; }
}
