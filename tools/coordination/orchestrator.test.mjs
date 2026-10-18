import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { Coordinator } from './orchestrator.mjs';

function git(cwd, ...args) {
  const result = spawnSync('git', args, { cwd, encoding: 'utf8', windowsHide: true });
  assert.equal(result.status, 0, result.stderr);
  return result.stdout.trim();
}
function fixture(t) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'coop-board-'));
  const repo = path.join(directory, 'repo');
  fs.mkdirSync(repo);
  t.after(() => fs.rmSync(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 }));
  git(repo, 'init', '-b', 'codex/coordinator');
  git(repo, 'config', 'user.name', 'Coordination Test');
  git(repo, 'config', 'user.email', 'coordination@example.invalid');
  git(repo, 'config', 'core.autocrlf', 'false');
  fs.writeFileSync(path.join(repo, 'accepted.txt'), 'accepted\n');
  fs.mkdirSync(path.join(repo, 'source'));
  fs.writeFileSync(path.join(repo, 'source', 'base.txt'), 'base\n');
  git(repo, 'add', '.');
  git(repo, 'commit', '-m', 'Accepted baseline');
  const baseline = git(repo, 'rev-parse', 'HEAD');
  const streams = ['authority', 'checkpoint', 'ai', 'pause', 'e2e', 'fault', 'review'].map(id => ({ id, next: `Next ${id}`, accepted: ['already done'], scope: [`source/${id}`], dependencies: id === 'e2e' ? ['authority'] : [] }));
  const config = { maxWorkers: 3, modelPolicy: 'inherit-parent', acceptedBaseline: { commit: baseline, report: 'accepted.txt', evidence: ['accepted.txt'] }, workstreams: streams };
  fs.mkdirSync(path.join(repo, 'tools', 'coordination'), { recursive: true });
  fs.writeFileSync(path.join(repo, 'tools', 'coordination', 'p2p-workstreams.json'), JSON.stringify(config));
  git(repo, 'add', '.');
  git(repo, 'commit', '-m', 'Configure task board');
  const board = new Coordinator(repo);
  board.init();
  return { repo, board, directory };
}

function workerResult(board, directory, id = 'implementation') {
  const task = board.create({ id, stream: 'authority' });
  board.assign(id, { agentId: `/root/${id}` });
  const file = 'source/authority/result.txt';
  fs.mkdirSync(path.dirname(path.join(task.worktree, file)), { recursive: true });
  fs.writeFileSync(path.join(task.worktree, file), 'verified change\n');
  git(task.worktree, 'add', '.');
  git(task.worktree, 'commit', '-m', 'Implement verified change');
  const command = [process.execPath, '-e', "require('node:assert/strict').equal(require('node:fs').readFileSync('source/authority/result.txt','utf8'),'verified change\\n'); console.log('passed actual file check');"];
  const result = spawnSync(command[0], command.slice(1), { cwd: task.worktree, encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
  const log = path.join(directory, `${id}.log`);
  fs.writeFileSync(log, result.stdout + result.stderr);
  const handoff = { taskId: id, baseCommit: task.baseCommit, commit: git(task.worktree, 'rev-parse', 'HEAD'), summary: 'Verified file change', changedFiles: [file], tests: [{ command: JSON.stringify(command), exitCode: result.status, log, sha256: createHash('sha256').update(fs.readFileSync(log)).digest('hex') }], evidence: [log], risks: [] };
  return { task, handoff, command };
}

const approve = (board, id) => board.review(id, { reviewerId: '/root/independent-review', decision: 'pass', notes: 'Compared committed diff, scope and test receipt.' });

test('init records the accepted baseline without creating repeat tasks', t => {
  const { board } = fixture(t);
  const state = board.status();
  assert.equal(state.clean, true);
  assert.equal(state.modelPolicy, 'inherit-parent');
  assert.equal(Object.keys(state.tasks).length, 0);
  assert.equal(state.acceptedBaseline.files.length, 2);
  assert.equal(board.next().find(item => item.id === 'e2e').ready, false);
});

test('real task worktrees are unique and scopes cannot overlap', t => {
  const { board } = fixture(t);
  const task = board.create({ id: 'first', stream: 'authority' });
  assert.equal(git(task.worktree, 'branch', '--show-current'), 'codex/agent-first');
  assert.equal(git(task.worktree, 'rev-parse', 'HEAD'), task.baseCommit);
  assert.throws(() => board.create({ id: 'first', stream: 'checkpoint' }), /already registered/);
  assert.throws(() => board.create({ id: 'overlap', stream: 'authority', scope: ['source/authority/child'] }), /overlaps/);
  assert.throws(() => board.create({ id: 'outside', stream: 'authority', scope: ['source/checkpoint'] }), /exceeds/);
  fs.mkdirSync(path.join(board.status().managedRoot, 'external'));
  assert.throws(() => board.create({ id: 'external', stream: 'checkpoint' }), /already exists/);
});

test('writer budget, dependencies and native agent ownership are enforced', t => {
  const { board } = fixture(t);
  for (const stream of ['authority', 'checkpoint', 'ai']) board.create({ id: stream, stream });
  board.assign('authority', { agentId: '/root/one' });
  assert.throws(() => board.assign('checkpoint', { agentId: '/root/one' }), /already reserved/);
  assert.throws(() => board.create({ id: 'fourth', stream: 'pause' }), /three writing workers/);
  assert.throws(() => board.create({ id: 'early-e2e', stream: 'e2e' }), /dependencies incomplete/);
  assert.equal(board.status().tasks.authority.adapter, 'native');
  assert.match(board.status().tasks.authority.prompt, /inherit parent/);
});

test('cross-process lock refuses concurrent state mutation', t => {
  const { board } = fixture(t);
  fs.writeFileSync(board.lockFile, JSON.stringify({ pid: process.pid }));
  assert.throws(() => board.status(), /lock held/);
  const moduleUrl = new URL('./orchestrator.mjs', import.meta.url).href;
  const child = spawnSync(process.execPath, ['--input-type=module', '-e', `import { Coordinator } from ${JSON.stringify(moduleUrl)}; new Coordinator(process.argv[1]).status();`, board.repo], { encoding: 'utf8' });
  assert.equal(child.status, 1);
  assert.match(child.stderr, /lock held/);
  fs.unlinkSync(board.lockFile);
  assert.equal(board.status().clean, true);
});

test('worker checkout cannot mutate the coordinator registry', t => {
  const { board } = fixture(t);
  const task = board.create({ id: 'worker', stream: 'authority' });
  const worker = new Coordinator(task.worktree);
  assert.throws(() => worker.create({ id: 'nested', stream: 'checkpoint' }), /registered coordinator/);
});

test('handoff rejects incorrect files, bad hashes, failures and wrong commit', t => {
  const { board, directory } = fixture(t);
  const { handoff } = workerResult(board, directory);
  assert.throws(() => board.submit(handoff.taskId, { ...handoff, changedFiles: ['source/authority/missing.txt'] }), /actual Git diff/);
  assert.throws(() => board.submit(handoff.taskId, { ...handoff, commit: handoff.baseCommit }), /worker HEAD/);
  assert.throws(() => board.submit(handoff.taskId, { ...handoff, tests: [{ ...handoff.tests[0], sha256: '0'.repeat(64) }] }), /SHA256 mismatch/);
  assert.throws(() => board.submit(handoff.taskId, { ...handoff, tests: [{ ...handoff.tests[0], exitCode: 1 }] }), /test failed/);
  assert.throws(() => board.submit(handoff.taskId, { ...handoff, baseCommit: 'a'.repeat(40) }), /mismatch/);
  assert.equal(board.status().tasks[handoff.taskId].status, 'assigned');
});

test('review and actual post-integration verification gate closure and cleanup', t => {
  const { board, directory, repo } = fixture(t);
  const { task, handoff, command } = workerResult(board, directory);
  assert.throws(() => board.integrate(task.id), /passed review/);
  board.submit(task.id, handoff);
  assert.throws(() => board.review(task.id, { reviewerId: `/root/${task.id}`, decision: 'pass', notes: 'self review' }), /independent/);
  approve(board, task.id);
  assert.throws(() => board.close(task.id), /post-integration/);
  assert.throws(() => board.cleanup(task.id), /integrated, verified/);
  const integrated = board.integrate(task.id);
  assert.equal(integrated.status, 'integrated');
  assert.equal(fs.readFileSync(path.join(repo, handoff.changedFiles[0]), 'utf8'), 'verified change\n');
  assert.throws(() => board.verify(task.id, { command: true }), /command array/);
  const verified = board.verify(task.id, { command });
  assert.equal(verified.status, 'verified');
  assert.match(fs.readFileSync(verified.verification.log, 'utf8'), /passed actual file check/);
  board.close(task.id);
  assert.equal(board.next().find(item => item.id === 'e2e').ready, true);
  assert.equal(board.next().find(item => item.id === 'authority').ready, false);
  assert.equal(board.next().find(item => item.id === 'authority').completed, true);
  assert.throws(() => board.create({ id: 'repeat', stream: 'authority' }), /already completed/);
  const cleaned = board.cleanup(task.id);
  assert.ok(cleaned.cleanedAt);
  assert.equal(fs.existsSync(task.worktree), false);
  assert.equal(git(repo, 'branch', '--list', task.branch), '');
  assert.equal(fs.existsSync(cleaned.handoff.tests[0].log), true);
  assert.equal(board.cleanup(task.id).cleanedAt, cleaned.cleanedAt);
});

test('retained handoff logs cannot be changed after submission', t => {
  const { board, directory } = fixture(t);
  const { task, handoff } = workerResult(board, directory);
  const submitted = board.submit(task.id, handoff);
  fs.appendFileSync(submitted.handoff.tests[0].log, 'tampered');
  assert.throws(() => approve(board, task.id), /SHA256 mismatch/);
  assert.equal(board.status().tasks[task.id].status, 'submitted');
});

test('cherry-pick conflict is aborted and assigned needs_fix without false integration', t => {
  const { board, directory, repo } = fixture(t);
  const { task, handoff } = workerResult(board, directory);
  fs.mkdirSync(path.join(repo, 'source', 'authority'));
  fs.writeFileSync(path.join(repo, handoff.changedFiles[0]), 'different coordinator change\n');
  git(repo, 'add', '.');
  git(repo, 'commit', '-m', 'Conflicting independently integrated change');
  const head = git(repo, 'rev-parse', 'HEAD');
  board.submit(task.id, handoff);
  approve(board, task.id);
  const failed = board.integrate(task.id);
  assert.equal(failed.status, 'needs_fix');
  assert.equal(failed.integration, undefined);
  assert.match(failed.integrationFailure.message, /CONFLICT/);
  assert.equal(git(repo, 'rev-parse', 'HEAD'), head);
  assert.equal(git(repo, 'status', '--porcelain'), '');
  assert.throws(() => board.close(task.id), /post-integration/);
  assert.equal(fs.existsSync(task.worktree), true);
});

test('failed coordinator test records its actual nonzero exit and prevents completion', t => {
  const { board, directory } = fixture(t);
  const { task, handoff } = workerResult(board, directory);
  board.submit(task.id, handoff);
  approve(board, task.id);
  board.integrate(task.id);
  assert.throws(() => board.verify(task.id, { command: ['dotnet', 'test', 'source/Coop.sln'] }), /disable deployment/);
  const failed = board.verify(task.id, { command: [process.execPath, '-e', "console.error('real failure');process.exit(7)"] });
  assert.equal(failed.status, 'needs_fix');
  assert.equal(failed.verification.exitCode, 7);
  assert.match(fs.readFileSync(failed.verification.log, 'utf8'), /real failure/);
  assert.throws(() => board.close(task.id), /post-integration/);
});

test('interrupt retains writer reservation until the root reports actual agent stop', t => {
  const { board } = fixture(t);
  board.create({ id: 'first', stream: 'authority' });
  board.assign('first', { agentId: '/root/first' });
  const request = board.interrupt('first', { reason: 'worker no longer progressing' });
  assert.equal(request.needsAgentInterrupt, true);
  assert.throws(() => board.create({ id: 'second', stream: 'authority' }), /overlaps/);
  board.interrupt('first', { reason: 'native interrupt_agent observed interrupted', agentStatus: 'interrupted' });
  assert.equal(board.status().tasks.first.status, 'interrupted');
  board.create({ id: 'second', stream: 'authority' });
  assert.throws(() => board.assign('first', { agentId: '/root/retry' }), /already reserved/);
});

test('cleanup refuses dirty or changed managed worktrees and preserves foreign branches', t => {
  const { board, directory, repo } = fixture(t);
  const { task, handoff, command } = workerResult(board, directory);
  git(repo, 'branch', 'codex/old-experiment');
  board.submit(task.id, handoff);
  approve(board, task.id);
  board.integrate(task.id);
  board.verify(task.id, { command });
  board.close(task.id);
  fs.writeFileSync(path.join(task.worktree, 'uncommitted.txt'), 'keep me');
  assert.throws(() => board.cleanup(task.id), /not clean/);
  assert.ok(git(repo, 'branch', '--list', 'codex/old-experiment'));
  assert.equal(fs.existsSync(task.worktree), true);
});

test('read-only review with no commit diff is verified and closed without empty commits', t => {
  const { board, directory, repo } = fixture(t);
  const task = board.create({ id: 'readonly-review', stream: 'review', readOnly: true, scope: [] });
  board.assign(task.id, { agentId: '/root/readonly-review' });
  const log = path.join(directory, 'review.log');
  fs.writeFileSync(log, 'Inspected baseline and task evidence; no code changes needed.\n');
  const handoff = { taskId: task.id, baseCommit: task.baseCommit, commit: task.baseCommit, summary: 'Read-only review', changedFiles: [], tests: [{ command: 'read-only baseline review', exitCode: 0, log, sha256: createHash('sha256').update(fs.readFileSync(log)).digest('hex') }], evidence: [log], risks: [] };
  board.submit(task.id, handoff);
  approve(board, task.id);
  const previous = git(repo, 'rev-parse', 'HEAD');
  assert.equal(board.integrate(task.id).integration.commits.length, 0);
  assert.equal(git(repo, 'rev-parse', 'HEAD'), previous);
  board.verify(task.id, { command: [process.execPath, '-e', "require('node:assert/strict').ok(require('node:fs').existsSync('accepted.txt'));console.log('baseline remains present')"] });
  board.close(task.id);
  board.cleanup(task.id);
  assert.equal(git(repo, 'rev-parse', 'HEAD'), previous);
});

test('only explicit coordinator authorization registers an independent infrastructure scope', t => {
  const { board } = fixture(t);
  assert.throws(() => board.create({ id: 'infra', stream: 'e2e', scope: ['tools/coordination'] }), /scopeAuthorization/);
  const task = board.create({ id: 'infra', stream: 'e2e', scope: ['tools/coordination'], scopeAuthorization: 'Coordinator: infrastructure self-check is independent of gameplay dependencies.', objective: 'Exercise board lifecycle' });
  assert.deepEqual(task.scope, ['tools/coordination']);
  assert.match(task.scopeAuthorization, /Coordinator/);
  assert.equal(task.independent, true);
  assert.match(board.assign(task.id, { agentId: '/root/infra' }).prompt, /Exercise board lifecycle/);
});

test('independent infrastructure completion does not satisfy gameplay dependencies', t => {
  const { board, directory } = fixture(t);
  const task = board.create({ id: 'infra-readonly', stream: 'authority', readOnly: true, scope: [], scopeAuthorization: 'Infrastructure capability inspection only.' });
  board.assign(task.id, { agentId: '/root/infra-readonly' });
  const log = path.join(directory, 'infra-readonly.log');
  fs.writeFileSync(log, 'Infrastructure self-check only\n');
  board.submit(task.id, { taskId: task.id, baseCommit: task.baseCommit, commit: task.baseCommit, summary: 'Independent infrastructure receipt', changedFiles: [], tests: [{ command: 'infrastructure inspection', exitCode: 0, log, sha256: createHash('sha256').update(fs.readFileSync(log)).digest('hex') }], evidence: [], risks: [] });
  approve(board, task.id);
  board.integrate(task.id);
  board.verify(task.id, { command: [process.execPath, '-e', "console.log('independent infrastructure verification')"] });
  board.close(task.id);
  board.cleanup(task.id);
  assert.equal(board.next().find(item => item.id === 'authority').completed, false);
  assert.equal(board.next().find(item => item.id === 'authority').ready, true);
  assert.equal(board.next().find(item => item.id === 'e2e').ready, false);
});
