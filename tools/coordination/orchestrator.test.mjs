import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn, spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
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

async function heldVerification(board, directory, id) {
  const started = path.join(directory, 'verification-started.json');
  const release = path.join(directory, 'verification-release');
  const command = [process.execPath, '-e', `const fs=require('node:fs');fs.writeFileSync(${JSON.stringify(started)},JSON.stringify({pid:process.pid}));const timer=setInterval(()=>{if(fs.existsSync(${JSON.stringify(release)})){clearInterval(timer);console.log('held verification passed');process.exit(0)}},20);setTimeout(()=>process.exit(88),20000);`];
  const code = `import { Coordinator } from ${JSON.stringify(new URL('./orchestrator.mjs', import.meta.url).href)}; const result=new Coordinator(process.argv[1]).verify(process.argv[2],{command:${JSON.stringify(command)}});console.log(JSON.stringify(result));`;
  const child = spawn(process.execPath, ['--input-type=module', '-e', code, board.repo, id], { cwd: board.repo, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  let output = '';
  child.stdout.on('data', data => { output += data; });
  child.stderr.on('data', data => { output += data; });
  const exited = new Promise((resolve, reject) => { child.on('error', reject); child.on('close', (code, signal) => resolve({ code, signal, output })); });
  for (let attempt = 0; attempt < 500; attempt++) {
    if (fs.existsSync(started)) return { child, exited, release, commandPid: JSON.parse(fs.readFileSync(started, 'utf8')).pid };
    if (child.exitCode !== null) break;
    await new Promise(resolve => setTimeout(resolve, 20));
  }
  fs.writeFileSync(release, 'stop');
  child.kill();
  await exited;
  throw new Error(`verification child did not reach its actual command: ${output}`);
}

function cli(repo, ...args) {
  return spawnSync(process.execPath, [fileURLToPath(new URL('./orchestrator.mjs', import.meta.url)), '--root', repo, ...args], { encoding: 'utf8', windowsHide: true });
}

async function releaseVerification(held) {
  fs.writeFileSync(held.release, 'finish');
  if (held.child.exitCode === null) held.child.kill();
  await held.exited;
  for (let attempt = 0; attempt < 100; attempt++) {
    try { process.kill(held.commandPid, 0); }
    catch (error) { if (error.code === 'ESRCH') return; throw error; }
    await new Promise(resolve => setTimeout(resolve, 20));
  }
  process.kill(held.commandPid);
}

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

test('read-only workers share three slots and release them only after observed stop', t => {
  const { board } = fixture(t);
  for (const id of ['read-one', 'read-two', 'read-three', 'read-four']) board.create({ id, stream: 'review', readOnly: true, scope: [] });
  for (const id of ['read-one', 'read-two', 'read-three']) board.assign(id, { agentId: `/root/${id}`, adapter: id === 'read-two' ? 'cli' : 'native' });
  assert.throws(() => board.assign('read-four', { agentId: '/root/read-four' }), /three worker slots/);
  board.interrupt('read-one', { reason: 'Coordinator requested native agent interruption' });
  assert.throws(() => board.assign('read-four', { agentId: '/root/read-four' }), /three worker slots/);
  board.interrupt('read-one', { reason: 'Observed native agent idle after interrupt', agentStatus: 'idle' });
  assert.equal(board.assign('read-four', { agentId: '/root/read-four' }).status, 'assigned');
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

test('failed integrated verification recovers through a linked fix without reopening the original worker', t => {
  const { board, directory, repo } = fixture(t);
  const { task, handoff } = workerResult(board, directory);
  board.submit(task.id, handoff);
  approve(board, task.id);
  board.integrate(task.id);
  const command = [process.execPath, '-e', "require('node:assert/strict').equal(require('node:fs').readFileSync('source/authority/result.txt','utf8'),'repaired change\\n');console.log('repair verified');"];
  const failed = board.verify(task.id, { command });
  assert.equal(failed.status, 'needs_fix');
  assert.equal(failed.owner, 'coordinator');
  assert.match(failed.nextAction, /--followup-of implementation/);
  assert.throws(() => board.assign(task.id, { agentId: '/root/reopened' }), /coordinator-owned/);
  assert.throws(() => board.submit(task.id, handoff), /new follow-up task/);
  assert.throws(() => board.interrupt(task.id, { reason: 'invalid worker reopen' }), /no longer has a writing worker/);
  const originalHandoff = JSON.stringify(failed.handoff);
  assert.throws(() => board.create({ id: 'bad-fix', followupOf: task.id, scope: ['source/checkpoint'] }), /exceeds original/);
  const fix = board.create({ id: 'repair', followupOf: task.id });
  assert.equal(fix.baseCommit, git(repo, 'rev-parse', 'HEAD'));
  assert.equal(fix.followupOf, task.id);
  assert.equal(fix.independent, true);
  assert.deepEqual(board.status().tasks[task.id].followups, [fix.id]);
  board.assign(fix.id, { agentId: '/root/repair' });
  fs.writeFileSync(path.join(fix.worktree, handoff.changedFiles[0]), 'repaired change\n');
  git(fix.worktree, 'add', '.');
  git(fix.worktree, 'commit', '-m', 'Repair failed integration expectation');
  const result = spawnSync(command[0], command.slice(1), { cwd: fix.worktree, encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
  const log = path.join(directory, 'repair.log');
  fs.writeFileSync(log, result.stdout + result.stderr);
  board.submit(fix.id, { ...handoff, taskId: fix.id, baseCommit: fix.baseCommit, commit: git(fix.worktree, 'rev-parse', 'HEAD'), tests: [{ command: JSON.stringify(command), exitCode: result.status, log, sha256: createHash('sha256').update(fs.readFileSync(log)).digest('hex') }], evidence: [log] });
  approve(board, fix.id);
  board.integrate(fix.id);
  board.verify(fix.id, { command });
  board.close(fix.id);
  board.cleanup(fix.id);
  const recovered = board.verify(task.id, { command });
  assert.equal(recovered.status, 'verified');
  assert.equal(recovered.verificationHistory.length, 2);
  assert.equal(recovered.verificationHistory[0].exitCode, 1);
  assert.equal(recovered.verificationHistory[1].exitCode, 0);
  assert.equal(JSON.stringify(recovered.handoff), originalHandoff);
  assert.equal(git(task.worktree, 'rev-parse', 'HEAD'), handoff.commit);
  board.close(task.id);
  board.cleanup(task.id);
  assert.equal(board.status().tasks[task.id].status, 'closed');
  assert.equal(fs.existsSync(task.worktree), false);
  assert.equal(fs.existsSync(failed.verification.log), true);
});

test('long verification releases the state lock for other processes while blocking integration', async t => {
  const { board, directory, repo } = fixture(t);
  const { task, handoff } = workerResult(board, directory);
  board.submit(task.id, handoff);
  approve(board, task.id);
  board.integrate(task.id);
  const review = board.create({ id: 'parallel-review', stream: 'review', readOnly: true, scope: [] });
  board.assign(review.id, { agentId: '/root/parallel-review' });
  board.submit(review.id, { ...handoff, taskId: review.id, baseCommit: review.baseCommit, commit: review.baseCommit, changedFiles: [] });
  approve(board, review.id);
  const held = await heldVerification(board, directory, task.id);
  try {
    const status = cli(repo, 'status');
    assert.equal(status.status, 0, status.stderr);
    const observed = JSON.parse(status.stdout);
    assert.equal(observed.tasks[task.id].status, 'verifying');
    assert.equal(observed.verificationLease.ownerPid, held.child.pid);
    assert.equal(fs.existsSync(board.lockFile), false);
    const created = cli(repo, 'create', '--id', 'parallel-checkpoint', '--stream', 'checkpoint');
    assert.equal(created.status, 0, created.stderr);
    assert.equal(JSON.parse(created.stdout).status, 'created');
    assert.equal(cli(repo, 'assign', '--id', 'parallel-checkpoint', '--agent', '/root/parallel-checkpoint').status, 0);
    const blocked = cli(repo, 'integrate', '--id', review.id);
    assert.equal(blocked.status, 1);
    assert.match(blocked.stderr, /verification lease/);
    assert.throws(() => board.verify(task.id, { command: [process.execPath, '-e', 'process.exit(0)'] }), /verification lease/);
    assert.throws(() => board.recoverVerification({ reason: 'Cannot take a live owner lease' }), /still alive/);
    fs.writeFileSync(held.release, 'finish');
    const result = await held.exited;
    assert.equal(result.code, 0, result.output);
    assert.equal(board.status().tasks[task.id].status, 'verified');
    assert.equal(board.status().verificationLease, undefined);
  } finally {
    await releaseVerification(held);
  }
});

test('abandoned verification lease is recoverable only after its real owner process exits', async t => {
  const { board, directory } = fixture(t);
  const { task, handoff, command } = workerResult(board, directory);
  board.submit(task.id, handoff);
  approve(board, task.id);
  board.integrate(task.id);
  const held = await heldVerification(board, directory, task.id);
  try {
    assert.throws(() => board.recoverVerification({ reason: 'owner still alive' }), /still alive/);
    held.child.kill();
    await held.exited;
    await releaseVerification(held);
    const recovered = board.recoverVerification({ reason: 'Observed owner exit, released its controlled test process.' });
    assert.equal(recovered.status, 'needs_fix');
    assert.equal(recovered.verification.exitCode, null);
    assert.equal(recovered.verification.recovered, true);
    assert.equal(board.status().verificationLease, undefined);
    assert.equal(board.verify(task.id, { command }).status, 'verified');
    board.close(task.id);
    board.cleanup(task.id);
  } finally {
    await releaseVerification(held);
  }
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
