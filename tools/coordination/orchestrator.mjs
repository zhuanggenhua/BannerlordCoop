#!/usr/bin/env node
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const digest = data => crypto.createHash('sha256').update(data).digest('hex');
const json = file => JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, ''));
const same = (a, b) => JSON.stringify([...a].sort()) === JSON.stringify([...b].sort());
const writing = task => !task.readOnly && ['created', 'assigned', 'needs_fix'].includes(task.status);
const canonical = value => fs.realpathSync.native(path.resolve(value));
const within = (root, value) => {
  const relative = path.relative(root, value);
  return relative !== '' && relative !== '..' && !relative.startsWith(`..${path.sep}`) && !path.isAbsolute(relative);
};
function run(executable, args, cwd) {
  const result = spawnSync(executable, args, { cwd, encoding: 'utf8', windowsHide: true, maxBuffer: 32 * 1024 * 1024 });
  if (result.error || result.status !== 0) throw new Error(`${executable} ${args.join(' ')}: ${result.error?.message ?? result.stderr ?? result.stdout}`);
  return result.stdout.trim();
}
function scopes(values) {
  if (!Array.isArray(values)) throw new Error('scope must be an array');
  return values.map(value => {
    if (typeof value !== 'string' || !value || value.includes('\\') || path.posix.isAbsolute(value) || /^[a-z]:/i.test(value) || value.split('/').some(part => part === '..' || part === '.git')) throw new Error('scope must contain repository-relative paths');
    return value.replace(/\/+$/, '');
  });
}
const covers = (scope, file) => file === scope || file.startsWith(`${scope}/`);
const overlaps = (a, b) => a.some(left => b.some(right => covers(left, right) || covers(right, left)));
function requireText(value, name) {
  if (typeof value !== 'string' || !value.trim()) throw new Error(`${name} is required`);
  return value;
}

export class Coordinator {
  constructor(repo) {
    this.repo = canonical(run('git', ['rev-parse', '--show-toplevel'], path.resolve(repo)));
    this.common = canonical(run('git', ['rev-parse', '--path-format=absolute', '--git-common-dir'], this.repo));
    this.directory = path.join(this.common, 'codex-coordination');
    this.stateFile = path.join(this.directory, 'state.json');
    this.lockFile = path.join(this.directory, 'state.lock');
  }

  git(args, cwd = this.repo) { return run('git', args, cwd); }
  clean(cwd) {
    if (this.git(['status', '--porcelain', '--untracked-files=all'], cwd)) throw new Error(`working tree is not clean: ${cwd}`);
    for (const marker of ['MERGE_HEAD', 'CHERRY_PICK_HEAD', 'REBASE_HEAD']) {
      if (fs.existsSync(this.git(['rev-parse', '--path-format=absolute', '--git-path', marker], cwd))) throw new Error(`unfinished Git operation: ${marker}`);
    }
  }

  transaction(action) {
    fs.mkdirSync(this.directory, { recursive: true });
    let descriptor;
    try { descriptor = fs.openSync(this.lockFile, 'wx'); }
    catch (error) { if (error.code === 'EEXIST') throw new Error('coordination lock held; inspect state.lock owner before recovering'); throw error; }
    try {
      fs.writeFileSync(descriptor, JSON.stringify({ pid: process.pid, started: new Date().toISOString() }));
      const state = fs.existsSync(this.stateFile) ? json(this.stateFile) : null;
      const result = action(state);
      if (result.state) {
        const temporary = `${this.stateFile}.${process.pid}.tmp`;
        fs.writeFileSync(temporary, `${JSON.stringify(result.state, null, 2)}\n`);
        fs.renameSync(temporary, this.stateFile);
      }
      return result.value;
    } finally { fs.closeSync(descriptor); fs.unlinkSync(this.lockFile); }
  }

  initialized(state) {
    if (!state) throw new Error('run init first');
    if (state.version !== 1) throw new Error('unsupported coordination state');
    return state;
  }

  coordinator(state) {
    this.initialized(state);
    if (this.repo !== state.coordinatorRoot || this.git(['branch', '--show-current']) !== state.coordinatorBranch) throw new Error('mutation requires the registered coordinator checkout and branch');
  }

  init() {
    return this.transaction(existing => {
      if (existing) { this.coordinator(existing); return { value: existing }; }
      this.clean(this.repo);
      const branch = this.git(['branch', '--show-current']);
      if (!branch.startsWith('codex/')) throw new Error('coordinator must use a codex/ branch');
      const configFile = path.join(this.repo, 'tools', 'coordination', 'p2p-workstreams.json');
      const config = json(configFile);
      if (config.modelPolicy !== 'inherit-parent' || config.maxWorkers !== 3 || config.workstreams.length !== 7) throw new Error('expected seven workstreams, three workers, inherited model');
      const head = this.git(['rev-parse', 'HEAD']);
      this.git(['merge-base', '--is-ancestor', config.acceptedBaseline.commit, head]);
      const accepted = [config.acceptedBaseline.report, ...config.acceptedBaseline.evidence].map(file => {
        this.git(['cat-file', '-e', `${config.acceptedBaseline.commit}:${file}`]);
        const content = fs.readFileSync(path.join(this.repo, file));
        if (!content.length) throw new Error(`empty baseline evidence: ${file}`);
        return { file, sha256: digest(content) };
      });
      const root = this.git(['worktree', 'list', '--porcelain']).split('\n').find(line => line.startsWith('worktree ')).slice(9);
      const managedRoot = path.join(path.dirname(root), `${path.basename(root)}-worktrees`);
      fs.mkdirSync(managedRoot, { recursive: true });
      const state = { version: 1, coordinatorRoot: this.repo, coordinatorBranch: branch, initialHead: head, managedRoot: canonical(managedRoot), modelPolicy: 'inherit-parent', maxWorkers: 3, acceptedBaseline: { ...config.acceptedBaseline, files: accepted }, workstreams: config.workstreams, tasks: {} };
      return { state, value: state };
    });
  }

  status() {
    return this.transaction(state => {
      this.initialized(state);
      return { value: { ...state, observedHead: this.git(['rev-parse', 'HEAD']), clean: !this.git(['status', '--porcelain']) } };
    });
  }

  next() {
    return this.transaction(state => {
      this.initialized(state);
      const finished = stream => Object.values(state.tasks).some(task => task.stream === stream && task.status === 'closed');
      return { value: state.workstreams.map(stream => ({ ...stream, modelPolicy: state.modelPolicy, ready: stream.dependencies.every(finished) && !Object.values(state.tasks).some(task => task.stream === stream.id && task.status !== 'closed'), blockedBy: stream.dependencies.filter(dependency => !finished(dependency)) })) };
    });
  }

  create({ id, stream, scope, readOnly = false }) {
    return this.transaction(state => {
      this.coordinator(state);
      this.clean(this.repo);
      if (!/^[a-z0-9][a-z0-9-]{0,63}$/.test(id ?? '')) throw new Error('id must be a short lowercase slug');
      if (state.tasks[id]) throw new Error('task id already registered');
      const definition = state.workstreams.find(item => item.id === stream);
      if (!definition) throw new Error('unknown workstream');
      const taskScope = scopes(scope ?? definition.scope);
      if (!readOnly && !taskScope.length) throw new Error('writing task requires scope');
      if (taskScope.some(value => !definition.scope.some(allowed => covers(allowed.replace(/\/+$/, ''), value)))) throw new Error('scope exceeds workstream');
      const blocked = definition.dependencies.filter(dependency => !Object.values(state.tasks).some(task => task.stream === dependency && task.status === 'closed'));
      if (blocked.length) throw new Error(`dependencies incomplete: ${blocked.join(', ')}`);
      const active = Object.values(state.tasks).filter(writing);
      if (!readOnly && active.length >= state.maxWorkers) throw new Error('three writing workers already reserved');
      if (!readOnly && active.some(task => overlaps(task.scope, taskScope))) throw new Error('scope overlaps an active writer');
      const worktree = path.join(state.managedRoot, id);
      const branch = `codex/agent-${id}`;
      if (!within(state.managedRoot, worktree) || fs.existsSync(worktree)) throw new Error('worktree directory already exists or is unsafe');
      if (this.git(['branch', '--list', branch])) throw new Error('branch already exists');
      const baseCommit = this.git(['rev-parse', 'HEAD']);
      const task = { id, stream, scope: taskScope, readOnly, worktree, branch, baseCommit, status: 'created', modelPolicy: 'inherit-parent', createdAt: new Date().toISOString(), registeredBy: 'codex-coordination' };
      this.git(['worktree', 'add', '-b', branch, worktree, baseCommit]);
      try {
        for (const relative of ['mb2', 'source/packages']) {
          const source = path.join(state.coordinatorRoot, relative);
          const target = path.join(worktree, relative);
          if (process.platform === 'win32' && fs.existsSync(source) && !fs.existsSync(target)) fs.symlinkSync(canonical(source), target, 'junction');
        }
      } catch (error) { task.setupWarning = error.message; }
      state.tasks[id] = task;
      return { state, value: task };
    });
  }

  task(state, id) {
    this.coordinator(state);
    const task = state.tasks[id];
    if (!task) throw new Error('unknown task');
    return task;
  }

  assign(id, { agentId, adapter = 'native' }) {
    return this.transaction(state => {
      const task = this.task(state, id);
      requireText(agentId, 'agentId');
      if (!['native', 'cli'].includes(adapter)) throw new Error('adapter must be native or cli');
      if (!['created', 'needs_fix', 'interrupted'].includes(task.status)) throw new Error('task is not assignable');
      const others = Object.values(state.tasks).filter(other => other.id !== id && writing(other));
      if (!task.readOnly && (others.length >= state.maxWorkers || others.some(other => overlaps(other.scope, task.scope) || other.agentId === agentId || other.worktree === task.worktree))) throw new Error('worker, directory or scope already reserved');
      if (canonical(task.worktree) !== task.worktree) throw new Error('worktree resolves outside registered directory');
      task.agentId = agentId;
      task.adapter = adapter;
      task.status = 'assigned';
      task.prompt = `Task ${id}: ${state.workstreams.find(item => item.id === task.stream).next}. Work only in ${task.worktree}, branch ${task.branch}, base ${task.baseCommit}. Scope: ${task.scope.join(', ')}. Model: inherit parent. No deployment, games, push or PR. Compile with -p:ModName=. Commit verified work; submit handoff.schema.json with actual tests, exit codes and log hashes. Native agent calls are performed by the root chat, not this Node process.`;
      return { state, value: task };
    });
  }
}

const help = `Usage: node tools/coordination/orchestrator.mjs --root <repo> <command> [options]
Commands: init | status | next | create | assign
create --id slug --stream authority [--scope path] [--read-only]
assign --id slug --agent native-agent-id [--adapter native|cli]
The root chat invokes native agents; this CLI registers their exclusive worktrees.
All persistent state is in the Git common directory. Model policy: inherit-parent.`;

export function main(args = process.argv.slice(2)) {
  const options = { scope: [] };
  const positional = [];
  for (let index = 0; index < args.length; index++) {
    const value = args[index];
    if (value === '--help' || value === '-h') { process.stdout.write(`${help}\n`); return; }
    if (value === '--read-only') { options.readOnly = true; continue; }
    if (value.startsWith('--')) {
      const next = args[++index];
      if (next === undefined) throw new Error(`missing value for ${value}`);
      if (value === '--scope') options.scope.push(next); else options[value.slice(2)] = next;
    } else positional.push(value);
  }
  const board = new Coordinator(options.root ?? process.cwd());
  const command = positional[0];
  let result;
  if (['init', 'status', 'next'].includes(command)) result = board[command]();
  else if (command === 'create') result = board.create({ id: options.id, stream: options.stream, scope: options.scope.length ? options.scope : undefined, readOnly: options.readOnly });
  else if (command === 'assign') result = board.assign(options.id, { agentId: options.agent, adapter: options.adapter });
  else throw new Error(`unknown command; ${help}`);
  process.stdout.write(`${JSON.stringify(result, null, 2)}\n`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { main(); } catch (error) { process.stderr.write(`${error.message}\n`); process.exitCode = 1; }
}
