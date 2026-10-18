import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
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
  fs.unlinkSync(board.lockFile);
  assert.equal(board.status().clean, true);
});

test('worker checkout cannot mutate the coordinator registry', t => {
  const { board } = fixture(t);
  const task = board.create({ id: 'worker', stream: 'authority' });
  const worker = new Coordinator(task.worktree);
  assert.throws(() => worker.create({ id: 'nested', stream: 'checkpoint' }), /registered coordinator/);
});
