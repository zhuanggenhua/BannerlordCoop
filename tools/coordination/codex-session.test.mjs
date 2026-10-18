import assert from 'node:assert/strict';
import { resolve } from 'node:path';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync, spawn } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { test } from 'node:test';
import { setTimeout as delay } from 'node:timers/promises';
import { buildExecArgs, parseCodexEvents, runCodexSession } from './codex-session.mjs';

const sessionId = '01a1496b-7401-7433-af46-a4bb355d675b';
const baseOptions = { cwd: resolve('.'), outputDir: resolve('session-output'), model: 'verified-test-model', verifiedParentModel: 'verified-test-model' };

test('builds separate cwd, verified model, JSONL and schema arguments without a shell prompt', () => {
    const args = buildExecArgs(baseOptions);
    assert.deepEqual(args.slice(0, 3), ['--ask-for-approval', 'never', 'exec']);
    assert.equal(args[args.indexOf('--sandbox') + 1], 'read-only');
    assert.ok(!args.includes('--add-dir'));
    assert.equal(args[args.indexOf('--cd') + 1], baseOptions.cwd);
    assert.equal(args[args.indexOf('--model') + 1], baseOptions.verifiedParentModel);
    assert.ok(args.includes('--json'));
    assert.ok(args.includes('--output-schema'));
    assert.equal(args.at(-1), '-');
});

test('resume always names an exact session and never selects the last session', () => {
    const args = buildExecArgs({ ...baseOptions, resumeSessionId: sessionId });
    assert.deepEqual(args.slice(-3), ['resume', sessionId, '-']);
    assert.ok(!args.includes('--last'));
    assert.throws(() => buildExecArgs({ ...baseOptions, resumeSessionId: '--last' }), /exact session UUID/);
});

test('rejects missing and mismatched model evidence before execution', () => {
    assert.throws(() => buildExecArgs({ ...baseOptions, verifiedParentModel: undefined }), /verified parent model/);
    assert.throws(() => buildExecArgs({ ...baseOptions, model: 'other-model' }), /exactly match/);
    assert.throws(() => buildExecArgs({ ...baseOptions, cwd: 'relative' }), /absolute path/);
});

test('parses thread identity and completion but retains failures even after completion', () => {
    const events = [
        { type: 'thread.started', thread_id: sessionId },
        { type: 'turn.failed', error: { message: 'failed deliberately' } },
        { type: 'turn.completed' },
        { type: 'error', message: 'transport failure' },
    ];
    const result = parseCodexEvents(events.map(JSON.stringify).join('\n'));
    assert.equal(result.threadId, sessionId);
    assert.equal(result.completed, true);
    assert.equal(result.failed, true);
    assert.equal(result.errors.length, 2);
});

test('rejects malformed or conflicting protocol events', () => {
    const result = parseCodexEvents(`{"type":"thread.started","thread_id":"one"}\n{"type":"thread.started","thread_id":"two"}\nnot json\n{}\n`);
    assert.equal(result.errors.length, 3);
    assert.equal(result.completed, false);
});

const repository = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const commonDirectory = execFileSync('git', ['rev-parse', '--path-format=absolute', '--git-common-dir'], { cwd: repository, encoding: 'utf8', windowsHide: true }).trim();
const testDirectory = join(commonDirectory, 'codex-coordination', 'session-tests');
mkdirSync(testDirectory, { recursive: true });
const fixtureDirectory = mkdtempSync(join(testDirectory, 'transport-only-'));
const fakePath = join(fixtureDirectory, 'fake-codex.mjs');
writeFileSync(fakePath, `
import { writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { spawn } from 'node:child_process';
const mode = process.argv[2];
const args = process.argv.slice(3);
const outputPath = args[args.indexOf('--output-last-message') + 1];
const directory = dirname(outputPath);
const resumeIndex = args.indexOf('resume');
const threadId = mode === 'wrong-thread' ? '01a1496b-7401-7433-af46-a4bb355d675c' : resumeIndex < 0 ? '${sessionId}' : args[resumeIndex + 1];
const emit = value => process.stdout.write(JSON.stringify(value) + '\\n');
let prompt = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', data => prompt += data);
process.stdin.on('end', () => {
    writeFileSync(join(directory, 'fake-received.json'), JSON.stringify({ cwd: process.cwd(), args, prompt }));
    emit({ type: 'thread.started', thread_id: threadId });
    if (mode === 'hang') {
        const child = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], { stdio: 'ignore', windowsHide: true });
        writeFileSync(join(directory, 'descendant.pid'), String(child.pid));
        setInterval(() => {}, 1000);
        return;
    }
    const handoff = {
        taskId: 'transport-fixture', baseCommit: '1'.repeat(40), commit: '2'.repeat(40),
        summary: 'Only fake CLI transport was tested.', changedFiles: ['fixture-only'],
        tests: [{ command: 'fake', exitCode: 0, log: 'fixture.log', sha256: 'a'.repeat(64) }],
        evidence: [], risks: ['Not real model inference.'],
    };
    if (mode === 'missing-field') delete handoff.tests;
    if (mode === 'bad-nested') handoff.tests[0].sha256 = 'invalid';
    writeFileSync(outputPath, mode === 'bad-json' ? '{"taskId":' : JSON.stringify(handoff));
    emit({ type: 'item.completed', item: { type: 'agent_message', text: 'final message alone proves nothing' } });
    if (mode === 'failed') emit({ type: 'turn.failed', error: { message: 'deliberate turn failure' } });
    if (mode === 'error') emit({ type: 'error', message: 'deliberate protocol error' });
    if (mode === 'malformed') process.stdout.write('{"type":');
    else if (mode !== 'exit-only') emit({ type: 'turn.completed' });
    if (mode === 'nonzero') process.exitCode = 7;
    if (mode === 'orphan-inherit' || mode === 'orphan-ignore') {
        const child = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], {
            stdio: mode === 'orphan-inherit' ? ['ignore', process.stdout, process.stderr] : 'ignore', windowsHide: true, detached: true,
        });
        writeFileSync(join(directory, 'descendant.pid'), String(child.pid));
        child.unref();
        setTimeout(() => process.exit(0), 30);
    }
});
`, 'utf8');

function fixtureOptions(mode, overrides = {}) {
    return {
        ...baseOptions, cwd: repository,
        outputDir: join(fixtureDirectory, `attempt-${crypto.randomUUID()}`),
        prompt: 'test prompt with spaces, $(), `backticks`, and 中文',
        executable: process.execPath, executableArgs: [fakePath, mode], timeoutMs: 5000,
        ...overrides,
    };
}

function loadJson(path) { return JSON.parse(readFileSync(path, 'utf8')); }

test('write tasks explicitly grant the matching Git common directory and never its source parent', () => {
    const args = buildExecArgs({ ...baseOptions, cwd: repository, writeTask: true, gitCommonDir: commonDirectory });
    assert.equal(args[args.indexOf('--sandbox') + 1], 'workspace-write');
    assert.equal(args[args.indexOf('--add-dir') + 1], commonDirectory);
    assert.equal(args.filter(arg => arg === '--add-dir').length, 1);
    assert.ok(!args.includes(dirname(commonDirectory)));
    assert.ok(!args.includes('--dangerously-bypass-approvals-and-sandbox'));
});

test('rejects missing or unrelated write grants before a worker can spawn', async () => {
    for (const invalid of [
        { writeTask: true }, { writeTask: true, gitCommonDir: 'relative' },
        { writeTask: true, gitCommonDir: dirname(commonDirectory) },
        { gitCommonDir: commonDirectory }, { writeTask: 'true' },
    ]) {
        const options = fixtureOptions('completed', invalid);
        await assert.rejects(runCodexSession(options), /gitCommonDir|writeTask/);
        assert.equal(existsSync(options.outputDir), false);
    }
});

test('persists the requested write sandbox and noninteractive approval policy in a fake transport receipt', async () => {
    const result = await runCodexSession(fixtureOptions('completed', { writeTask: true, gitCommonDir: commonDirectory }));
    assert.equal(result.state, 'completed');
    assert.equal(result.writeTask, true);
    assert.equal(result.sandbox, 'workspace-write');
    assert.equal(result.gitCommonDir, commonDirectory);
    assert.equal(result.approvalPolicy, 'never');
});

test('spawns a CLI in the assigned cwd, sends stdin, and persists a reviewable completed receipt', async () => {
    const options = fixtureOptions('completed');
    const result = await runCodexSession(options);
    assert.equal(result.state, 'completed');
    assert.equal(result.threadId, sessionId);
    assert.equal(result.model, options.verifiedParentModel);
    assert.ok(Number.isInteger(result.pid));
    assert.equal(result.requiresCoordinatorReview, true);
    assert.equal(result.handoff.taskId, 'transport-fixture');
    assert.deepEqual(loadJson(result.paths.receipt), result);
    const received = loadJson(join(options.outputDir, 'fake-received.json'));
    assert.equal(received.cwd, repository);
    assert.equal(received.prompt, options.prompt);
    assert.ok(!received.args.includes(options.prompt));
    assert.match(readFileSync(result.paths.events, 'utf8'), /turn.completed/);
});

test('uses an exact resume identity and can read the prompt from a file', async () => {
    const promptFile = join(fixtureDirectory, 'prompt.txt');
    writeFileSync(promptFile, 'resume this exact session, no model call is made by the fixture', 'utf8');
    const options = fixtureOptions('completed', { prompt: undefined, promptFile, resumeSessionId: sessionId });
    const result = await runCodexSession(options);
    assert.equal(result.state, 'completed');
    const received = loadJson(join(options.outputDir, 'fake-received.json'));
    assert.deepEqual(received.args.slice(-3), ['resume', sessionId, '-']);
    assert.equal(received.prompt, readFileSync(promptFile, 'utf8'));
});

test('rejects missing or mismatched parent model before creating an attempt or spawning', async () => {
    for (const mismatch of [{ verifiedParentModel: undefined }, { model: 'different-model' }]) {
        const options = fixtureOptions('completed', mismatch);
        await assert.rejects(runCodexSession(options), /verified parent model|exactly match/);
        assert.equal(existsSync(options.outputDir), false);
    }
    const relativePrompt = fixtureOptions('completed', { prompt: undefined, promptFile: 'relative-prompt.txt' });
    await assert.rejects(runCodexSession(relativePrompt), /absolute path/);
    assert.equal(existsSync(relativePrompt.outputDir), false);
});

for (const [mode, expectedError] of [
    ['exit-only', /No turn.completed/], ['failed', /turn.failed/], ['error', /deliberate protocol error/],
    ['malformed', /Invalid JSON/], ['bad-json', /Invalid final handoff JSON/],
    ['missing-field', /tests is required/], ['bad-nested', /sha256 does not match/],
    ['nonzero', /exit code was 7/], ['wrong-thread', /identity did not match/],
]) {
    test(`does not accept ${mode} as a successful handoff`, async () => {
        const result = await runCodexSession(fixtureOptions(mode, mode === 'wrong-thread' ? { resumeSessionId: sessionId } : {}));
        assert.equal(result.state, 'failed');
        assert.match(result.errors.join('\n'), expectedError);
        assert.equal(loadJson(result.paths.receipt).state, 'failed');
    });
}

async function waitForPath(path) {
    for (let retry = 0; retry < 200 && !existsSync(path); retry++) await delay(10);
    assert.equal(existsSync(path), true, `Expected fixture output ${path}`);
}

async function assertStopped(pid) {
    for (let retry = 0; retry < 100; retry++) {
        try { process.kill(pid, 0); }
        catch (error) { if (error.code === 'ESRCH') return; throw error; }
        await delay(10);
    }
    assert.fail(`Owned process ${pid} is still running.`);
}

test('timeout terminates the spawned CLI and its descendant and records a terminal timeout', async () => {
    const options = fixtureOptions('hang', { timeoutMs: 700 });
    const result = await runCodexSession(options);
    assert.equal(result.state, 'timeout');
    await assertStopped(result.pid);
    const descendant = Number(readFileSync(join(options.outputDir, 'descendant.pid'), 'utf8'));
    await assertStopped(descendant);
    assert.equal(loadJson(result.paths.receipt).state, 'timeout');
});

test('AbortSignal cancels only the owned process tree and persists its known thread ID', async () => {
    const controller = new AbortController();
    const options = fixtureOptions('hang', { signal: controller.signal });
    const unrelated = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], { stdio: 'ignore', windowsHide: true });
    try {
        const resultPromise = runCodexSession(options);
        await waitForPath(join(options.outputDir, 'descendant.pid'));
        const running = loadJson(join(options.outputDir, 'receipt.json'));
        assert.equal(running.state, 'running');
        assert.equal(running.threadId, sessionId);
        controller.abort('test cancellation');
        const result = await resultPromise;
        assert.equal(result.state, 'aborted');
        assert.equal(result.threadId, sessionId);
        await assertStopped(result.pid);
        await assertStopped(Number(readFileSync(join(options.outputDir, 'descendant.pid'), 'utf8')));
        assert.equal(process.kill(unrelated.pid, 0), true);
    } finally {
        controller.abort();
        unrelated.kill('SIGKILL');
        await assertStopped(unrelated.pid);
    }
});

test('pre-cancelled attempts do not spawn', async () => {
    const controller = new AbortController();
    controller.abort();
    const result = await runCodexSession(fixtureOptions('completed', { signal: controller.signal }));
    assert.equal(result.state, 'aborted');
    assert.equal(result.pid, null);
});

for (const mode of ['orphan-inherit', 'orphan-ignore']) {
    test(`bounds closure and cleans verified descendants when the CLI exits first: ${mode}`, async () => {
        const options = fixtureOptions(mode, { timeoutMs: 2000 });
        const start = Date.now();
        let descendant;
        try {
            const result = await runCodexSession(options);
            descendant = Number(readFileSync(join(options.outputDir, 'descendant.pid'), 'utf8'));
            assert.equal(result.exitCode, 0);
            assert.equal(result.state, 'completed', JSON.stringify(result.errors));
            assert.equal(result.cleanup.complete, true);
            assert.ok(result.cleanup.identities.some(identity => identity.pid === descendant));
            assert.ok(Date.now() - start < 12_000);
            await assertStopped(result.pid);
            await assertStopped(descendant);
        } finally {
            if (descendant) {
                try { process.kill(descendant, 'SIGKILL'); }
                catch (error) { if (error.code !== 'ESRCH') throw error; }
            }
        }
    });
}

test('unverifiable descendants remain cleanup_required without hanging or claiming completion', { skip: process.platform !== 'win32' }, async () => {
    const previousSystemRoot = process.env.SystemRoot;
    const options = fixtureOptions('orphan-inherit', { timeoutMs: 2000 });
    let descendant;
    const start = Date.now();
    try {
        // Fail only this Node process's inspector lookup. No global configuration changes.
        process.env.SystemRoot = fixtureDirectory;
        const result = await runCodexSession(options);
        descendant = Number(readFileSync(join(options.outputDir, 'descendant.pid'), 'utf8'));
        assert.equal(result.state, 'cleanup_required');
        assert.equal(result.retryable, false);
        assert.equal(result.cleanup.complete, false);
        assert.equal(result.exitCode, 0);
        assert.ok(Date.now() - start < 5000);
        assert.equal(process.kill(descendant, 0), true, 'Unknown descendants must not be killed by guessing their identity.');
        assert.equal(loadJson(result.paths.receipt).state, 'cleanup_required');
    } finally {
        if (previousSystemRoot === undefined) delete process.env.SystemRoot;
        else process.env.SystemRoot = previousSystemRoot;
        if (descendant) {
            try { process.kill(descendant, 'SIGKILL'); }
            catch (error) { if (error.code !== 'ESRCH') throw error; }
            await assertStopped(descendant);
        }
    }
});

test('parallel attempts retain independent cwd, output and receipts', async () => {
    const directories = ['worker one', 'worker two'].map(name => join(fixtureDirectory, name));
    directories.forEach(path => mkdirSync(path));
    const options = directories.map(cwd => fixtureOptions('completed', { cwd }));
    const results = await Promise.all(options.map(runCodexSession));
    assert.deepEqual(results.map(result => result.state), ['completed', 'completed']);
    assert.notEqual(results[0].pid, results[1].pid);
    for (let index = 0; index < results.length; index++) {
        assert.equal(loadJson(join(options[index].outputDir, 'fake-received.json')).cwd, directories[index]);
        assert.equal(loadJson(results[index].paths.receipt).cwd, directories[index]);
    }
});

test('spawn failure is terminal and the same attempt cannot overwrite prior evidence', async () => {
    const options = fixtureOptions('completed', { executable: join(fixtureDirectory, 'does-not-exist') });
    const result = await runCodexSession(options);
    assert.equal(result.state, 'failed');
    assert.match(result.errors.join('\n'), /Spawn failed/);
    await assert.rejects(runCodexSession(options), { code: 'EEXIST' });
});
