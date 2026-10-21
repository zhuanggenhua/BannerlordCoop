import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import { execFileSync, spawn } from 'node:child_process';
import { mkdir, readFile, readdir, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { performance } from 'node:perf_hooks';

export const QUICK_TEST_FILES = Object.freeze([
    'p2p-mission-lifecycle.test.mjs', 'p2p-map-authority-contracts.test.mjs',
    'frozen-movement-evidence.test.mjs', 'map-responsiveness-evidence.test.mjs',
    'native-log-archive.test.mjs', 'native-jsonl-writer.test.mjs',
].map(file => 'tools/mcp/scenarios/' + file));
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const git = (repo, ...args) => execFileSync('git', args, { cwd: repo, encoding: 'utf8', windowsHide: true }).trim();
function tapCounts(log) {
    const counts = Object.fromEntries([...log.toString('utf8').matchAll(/^# (tests|pass|fail|cancelled|skipped|todo) (\d+)\r?$/gm)]
        .map(match => [match[1], Number(match[2])]));
    const complete = ['tests', 'pass', 'fail', 'cancelled', 'skipped', 'todo'].every(key => Number.isSafeInteger(counts[key]));
    return { counts, success: complete && counts.tests > 0 && counts.pass === counts.tests &&
        counts.fail === 0 && counts.cancelled === 0 && counts.skipped === 0 && counts.todo === 0 };
}

async function inputs(repo) {
    const directory = 'tools/mcp/scenarios';
    async function walk(relative) {
        const files = [];
        for (const entry of await readdir(join(repo, relative), { withFileTypes: true })) {
            assert.ok(!entry.isSymbolicLink(), 'Scenario source links are not accepted by preflight');
            const file = relative + '/' + entry.name;
            if (entry.isDirectory()) files.push(...await walk(file));
            else if (entry.isFile() && entry.name.endsWith('.mjs'))
                files.push({ file, sha256: digest(await readFile(join(repo, file))) });
        }
        return files;
    }
    const files = (await walk(directory)).sort((a, b) => a.file.localeCompare(b.file, 'en'));
    return { kind: 'native-scenario-tool-smoke-v1', head: git(repo, 'rev-parse', 'HEAD'),
        nodeVersion: process.version, nodeExecutable: process.execPath, nodeOptions: process.env.NODE_OPTIONS ?? '', files,
        command: [process.execPath, '--test', '--test-reporter=tap', ...QUICK_TEST_FILES] };
}

async function execute(repo, command) {
    return new Promise((resolveRun, reject) => {
        const env = { ...process.env };
        for (const key of Object.keys(env)) if (key === 'NODE_OPTIONS' || key.startsWith('NODE_TEST_')) delete env[key];
        const child = spawn(command[0], command.slice(1), { cwd: repo, windowsHide: true, shell: false, env });
        const chunks = [];
        child.stdout.on('data', bytes => chunks.push(bytes));
        child.stderr.on('data', bytes => chunks.push(bytes));
        child.once('error', reject);
        child.once('close', (exitCode, signal) => resolveRun({ exitCode, signal, log: Buffer.concat(chunks) }));
    });
}

// This receipt proves only offline tool tests; managed/native acceptance is never cached here.
export async function runNativeScenarioPreflight(repo) {
    repo = resolve(repo);
    const started = performance.now(), startedUtc = new Date().toISOString();
    const input = await inputs(repo), key = digest(JSON.stringify(input));
    const cache = join(resolve(repo, git(repo, 'rev-parse', '--git-common-dir')), 'codex-coordination', 'preflight', key);
    await mkdir(cache, { recursive: true });
    const rejected = [];
    for (const entry of (await readdir(cache, { withFileTypes: true })).sort((a, b) => b.name.localeCompare(a.name))) {
        if (!entry.isDirectory() || !/^\d{13}-[0-9a-f-]{36}$/.test(entry.name)) continue;
        try {
            const original = join(cache, entry.name), receipt = JSON.parse(await readFile(join(original, 'result.json'), 'utf8'));
            assert.equal(receipt.kind, 'test');
            assert.equal(receipt.success, true); assert.equal(receipt.exitCode, 0); assert.equal(receipt.signal, null);
            assert.deepEqual(receipt.input, input); assert.equal(receipt.key, key);
            const log = await readFile(join(original, 'test.log'));
            assert.equal(digest(log), receipt.logSha256);
            const tap = tapCounts(log); assert.equal(tap.success, true); assert.deepEqual(receipt.counts, tap.counts);
            assert.deepEqual(await inputs(repo), input, 'Scenario inputs changed during cache verification');
            const attempt = join(cache, 'reuse-' + Date.now() + '-' + randomUUID()); await mkdir(attempt);
            const result = { kind: 'reuse', scope: input.kind, key, reused: true, reason: 'matching-head-node-command-source-and-verified-success-log',
                sourceAttempt: original, sourceExitCode: 0, counts: tap.counts, logSha256: receipt.logSha256,
                startedUtc, completedUtc: new Date().toISOString(), durationMs: performance.now() - started };
            await writeFile(join(attempt, 'result.json'), JSON.stringify(result, null, 2) + '\n', { flag: 'wx' });
            return { ...result, receipt: join(attempt, 'result.json') };
        } catch (error) { rejected.push({ attempt: entry.name, reason: error.message }); break; }
    }
    const attempt = join(cache, Date.now() + '-' + randomUUID()); await mkdir(attempt);
    let execution;
    try { execution = await execute(repo, input.command); }
    catch (error) { execution = { exitCode: null, signal: null, log: Buffer.from(error.stack ?? String(error)) }; }
    let unchanged = false, inputError;
    try { unchanged = JSON.stringify(await inputs(repo)) === JSON.stringify(input); }
    catch (error) { inputError = error.message; }
    const tap = tapCounts(execution.log);
    const success = execution.exitCode === 0 && execution.signal === null && unchanged && tap.success;
    await writeFile(join(attempt, 'test.log'), execution.log, { flag: 'wx' });
    const result = { kind: 'test', scope: input.kind, key, input, reused: false, success, exitCode: execution.exitCode, signal: execution.signal,
        reason: unchanged ? 'quick-tests-executed' : 'scenario-inputs-changed-during-tests', counts: tap.counts, inputError, rejected,
        logSha256: digest(execution.log), startedUtc, completedUtc: new Date().toISOString(), durationMs: performance.now() - started };
    const receipt = join(attempt, 'result.json');
    await writeFile(receipt, JSON.stringify(result, null, 2) + '\n', { flag: 'wx' });
    assert.equal(success, true, 'Native scenario preflight failed before SDK/MCP launch: ' + receipt);
    return { ...result, receipt };
}
