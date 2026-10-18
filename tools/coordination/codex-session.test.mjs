import assert from 'node:assert/strict';
import { resolve } from 'node:path';
import { test } from 'node:test';
import { buildExecArgs, parseCodexEvents } from './codex-session.mjs';

const sessionId = '01a1496b-7401-7433-af46-a4bb355d675b';
const baseOptions = { cwd: resolve('.'), outputDir: resolve('session-output'), model: 'verified-test-model', verifiedParentModel: 'verified-test-model' };

test('builds separate cwd, verified model, JSONL and schema arguments without a shell prompt', () => {
    const args = buildExecArgs(baseOptions);
    assert.equal(args[0], 'exec');
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
