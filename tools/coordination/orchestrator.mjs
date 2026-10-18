#!/usr/bin/env node
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const digest = data => crypto.createHash('sha256').update(data).digest('hex');
const json = file => JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, ''));
const same = (a, b) => JSON.stringify([...a].sort()) === JSON.stringify([...b].sort());
const writing = task => !task.readOnly && !task.integration && ['created', 'assigned', 'needs_fix', 'interrupt_requested'].includes(task.status);
const occupiesSlot = task => task.agentId && ['assigned', 'interrupt_requested'].includes(task.status);
const canonical = value => fs.realpathSync.native(path.resolve(value));
const within = (root, value) => {
  const relative = path.relative(root, value);
  return relative !== '' && relative !== '..' && !relative.startsWith(`..${path.sep}`) && !path.isAbsolute(relative);
};
function run(executable, args, cwd) {
  const result = spawnSync(executable, args, { cwd, encoding: 'utf8', windowsHide: true, maxBuffer: 32 * 1024 * 1024 });
  if (result.error || result.status !== 0) throw new Error(`${executable} ${args.join(' ')}: ${result.error?.message ?? `${result.stdout}\n${result.stderr}`}`);
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

  finished(state, stream) {
    const definition = state.workstreams.find(item => item.id === stream);
    return Object.values(state.tasks).some(task => task.stream === stream && task.queueItem === definition?.next && !task.independent && task.status === 'closed');
  }

  init() {
    return this.transaction(existing => {
      if (existing) {
        this.coordinator(existing);
        this.clean(this.repo);
        const observedHead = this.git(['rev-parse', 'HEAD']);
        this.git(['merge-base', '--is-ancestor', existing.acceptedBaseline.commit, observedHead]);
        return { value: { ...existing, observedHead } };
      }
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
      return { value: state.workstreams.map(stream => ({ ...stream, modelPolicy: state.modelPolicy, completed: this.finished(state, stream.id), ready: !this.finished(state, stream.id) && stream.dependencies.every(dependency => this.finished(state, dependency)) && !Object.values(state.tasks).some(task => task.stream === stream.id && !task.independent && task.status !== 'closed'), blockedBy: stream.dependencies.filter(dependency => !this.finished(state, dependency)) })) };
    });
  }

  create({ id, stream, scope, readOnly = false, scopeAuthorization, objective, followupOf }) {
    return this.transaction(state => {
      this.coordinator(state);
      this.clean(this.repo);
      if (!/^[a-z0-9][a-z0-9-]{0,63}$/.test(id ?? '')) throw new Error('id must be a short lowercase slug');
      if (state.tasks[id]) throw new Error('task id already registered');
      const parent = followupOf ? state.tasks[followupOf] : null;
      if (followupOf && (!parent?.integration || parent.status !== 'needs_fix')) throw new Error('follow-up requires an integrated task awaiting repair');
      stream ??= parent?.stream;
      scope ??= parent?.scope;
      scopeAuthorization ??= parent?.scopeAuthorization;
      objective ??= parent ? `Repair failed integration verification for ${parent.id}` : undefined;
      const definition = state.workstreams.find(item => item.id === stream);
      if (!definition) throw new Error('unknown workstream');
      if (objective !== undefined) requireText(objective, 'objective');
      if (objective && objective !== definition.next && !scopeAuthorization?.trim() && !parent) throw new Error('independent objective requires scopeAuthorization');
      if (this.finished(state, stream) && !scopeAuthorization?.trim()) throw new Error('current backlog item already completed; register an independent task explicitly');
      const taskScope = scopes(scope ?? definition.scope);
      if (parent && taskScope.some(value => !parent.scope.some(allowed => covers(allowed, value)))) throw new Error('follow-up scope exceeds original task scope');
      if (!readOnly && !taskScope.length) throw new Error('writing task requires scope');
      if (taskScope.some(value => !definition.scope.some(allowed => covers(allowed.replace(/\/+$/, ''), value))) && !scopeAuthorization?.trim()) throw new Error('scope exceeds workstream; coordinator scopeAuthorization required');
      const blocked = definition.dependencies.filter(dependency => !this.finished(state, dependency));
      if (!readOnly && blocked.length && !scopeAuthorization?.trim() && !parent) throw new Error(`dependencies incomplete: ${blocked.join(', ')}`);
      const active = Object.values(state.tasks).filter(writing);
      if (!readOnly && active.length >= state.maxWorkers) throw new Error('three writing workers already reserved');
      if (!readOnly && active.some(task => overlaps(task.scope, taskScope))) throw new Error('scope overlaps an active writer');
      const worktree = path.join(state.managedRoot, id);
      const branch = `codex/agent-${id}`;
      if (!within(state.managedRoot, worktree) || fs.existsSync(worktree)) throw new Error('worktree directory already exists or is unsafe');
      if (this.git(['branch', '--list', branch])) throw new Error('branch already exists');
      const baseCommit = this.git(['rev-parse', 'HEAD']);
      const task = { id, stream, objective: objective ?? definition.next, queueItem: definition.next, independent: Boolean(scopeAuthorization?.trim()) || Boolean(parent), followupOf, scope: taskScope, readOnly, scopeAuthorization, worktree, branch, baseCommit, status: 'created', owner: 'coordinator', modelPolicy: 'inherit-parent', createdAt: new Date().toISOString(), registeredBy: 'codex-coordination' };
      this.git(['worktree', 'add', '-b', branch, worktree, baseCommit]);
      try {
        for (const relative of ['mb2', 'source/packages']) {
          const source = path.join(state.coordinatorRoot, relative);
          const target = path.join(worktree, relative);
          if (process.platform === 'win32' && fs.existsSync(source) && !fs.existsSync(target)) fs.symlinkSync(canonical(source), target, 'junction');
        }
      } catch (error) { task.setupWarning = error.message; }
      state.tasks[id] = task;
      if (parent) {
        parent.followups = [...(parent.followups ?? []), id];
        parent.nextAction = `Review, integrate and verify follow-up ${id}; then rerun verify --id ${parent.id} on the clean coordinator HEAD.`;
      }
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
      if (task.integration) throw new Error('integrated task remains coordinator-owned; create --followup-of for worker changes');
      if (!['created', 'needs_fix', 'interrupted'].includes(task.status)) throw new Error('task is not assignable');
      const occupied = Object.values(state.tasks).filter(other => other.id !== id && occupiesSlot(other));
      if (occupied.length >= state.maxWorkers) throw new Error('three worker slots already occupied');
      if (occupied.some(other => other.agentId === agentId)) throw new Error('worker agent already reserved');
      const others = Object.values(state.tasks).filter(other => other.id !== id && writing(other));
      if (!task.readOnly && (others.length >= state.maxWorkers || others.some(other => overlaps(other.scope, task.scope) || other.agentId === agentId || other.worktree === task.worktree))) throw new Error('worker, directory or scope already reserved');
      if (canonical(task.worktree) !== task.worktree) throw new Error('worktree resolves outside registered directory');
      task.agentId = agentId;
      task.owner = agentId;
      task.adapter = adapter;
      task.status = 'assigned';
      task.prompt = `Task ${id}: ${task.objective}. Work only in ${task.worktree}, branch ${task.branch}, base ${task.baseCommit}. Scope: ${task.scope.join(', ')}. Model: inherit parent. No deployment, games, push or PR. Compile with -p:ModName=. Commit verified work; submit handoff.schema.json with actual tests, exit codes and log hashes. Native agent calls are performed by the root chat, not this Node process.`;
      return { state, value: task };
    });
  }

  validateHandoff(task, handoff) {
    if (handoff.taskId !== task.id || handoff.baseCommit !== task.baseCommit || !/^[0-9a-f]{40}$/.test(handoff.commit ?? '')) throw new Error('handoff task/base/commit mismatch');
    requireText(handoff.summary, 'summary');
    if (!Array.isArray(handoff.evidence) || !Array.isArray(handoff.risks) || handoff.risks.some(value => typeof value !== 'string')) throw new Error('evidence and risks arrays required');
    this.clean(task.worktree);
    if (canonical(task.worktree) !== task.worktree || this.git(['branch', '--show-current'], task.worktree) !== task.branch) throw new Error('registered worktree/branch changed');
    if (this.git(['rev-parse', 'HEAD'], task.worktree) !== handoff.commit) throw new Error('handoff commit is not worker HEAD');
    this.git(['merge-base', '--is-ancestor', task.baseCommit, handoff.commit]);
    if (this.git(['rev-list', '--merges', `${task.baseCommit}..${handoff.commit}`])) throw new Error('merge commits require a separate integration plan');
    const changedFiles = scopes(handoff.changedFiles);
    const actual = this.git(['diff', '--name-only', '--no-renames', task.baseCommit, handoff.commit]).split('\n').filter(Boolean);
    if (new Set(changedFiles).size !== changedFiles.length || !same(changedFiles, actual)) throw new Error('changedFiles differs from actual Git diff');
    if ((task.readOnly && actual.length) || actual.some(file => !task.scope.some(scope => covers(scope, file)))) throw new Error('changed files exceed task scope');
    if (!task.readOnly && !actual.length) throw new Error('writing task has no committed changes');
    if (!Array.isArray(handoff.tests) || !handoff.tests.length) throw new Error('at least one test receipt required');
    for (const receipt of handoff.tests) {
      requireText(receipt.command, 'test command');
      if (receipt.exitCode !== 0 || !/^[0-9a-f]{64}$/i.test(receipt.sha256 ?? '')) throw new Error('test failed or invalid SHA256');
      const log = path.resolve(task.worktree, requireText(receipt.log, 'test log'));
      if (!fs.statSync(log).isFile() || digest(fs.readFileSync(log)) !== receipt.sha256.toLowerCase()) throw new Error('test log SHA256 mismatch');
    }
    for (const file of handoff.evidence) {
      if (!fs.statSync(path.resolve(task.worktree, requireText(file, 'evidence file'))).isFile()) throw new Error('evidence must be a file');
    }
    return structuredClone(handoff);
  }

  submit(id, handoff) {
    return this.transaction(state => {
      const task = this.task(state, id);
      if (!['assigned', 'needs_fix'].includes(task.status)) throw new Error('task is not accepting a handoff');
      if (task.integration) throw new Error('integrated task requires a new follow-up task for changes');
      const verified = this.validateHandoff(task, handoff);
      const retained = path.join(this.directory, 'evidence', id, verified.commit);
      fs.mkdirSync(retained, { recursive: true });
      verified.tests = verified.tests.map((receipt, index) => {
        const log = path.join(retained, `test-${index}.log`);
        fs.copyFileSync(path.resolve(task.worktree, receipt.log), log);
        return { ...receipt, log };
      });
      verified.evidence = verified.evidence.map((file, index) => {
        const target = path.join(retained, `evidence-${index}${path.extname(file)}`);
        fs.copyFileSync(path.resolve(task.worktree, file), target);
        return target;
      });
      task.handoff = verified;
      task.status = 'submitted';
      fs.writeFileSync(path.join(retained, 'handoff.json'), `${JSON.stringify(verified, null, 2)}\n`);
      return { state, value: task };
    });
  }

  review(id, { reviewerId, decision, notes }) {
    return this.transaction(state => {
      const task = this.task(state, id);
      if (task.status !== 'submitted') throw new Error('review requires a submitted handoff');
      requireText(reviewerId, 'reviewerId');
      requireText(notes, 'review notes');
      if (reviewerId === task.agentId || !['pass', 'needs_fix'].includes(decision)) throw new Error('independent reviewer and explicit decision required');
      this.validateHandoff(task, task.handoff);
      task.review = { reviewerId, decision, notes, at: new Date().toISOString() };
      task.status = decision === 'pass' ? 'reviewed' : 'needs_fix';
      return { state, value: task };
    });
  }

  integrate(id) {
    return this.transaction(state => {
      const task = this.task(state, id);
      if (state.verificationLease) throw new Error(`verification lease ${state.verificationLease.token} blocks coordinator HEAD changes`);
      if (task.status !== 'reviewed') throw new Error('integration requires a passed review');
      this.clean(this.repo);
      this.validateHandoff(task, task.handoff);
      const previousHead = this.git(['rev-parse', 'HEAD']);
      this.git(['merge-base', '--is-ancestor', task.baseCommit, previousHead]);
      const commits = this.git(['rev-list', '--reverse', `${task.baseCommit}..${task.handoff.commit}`]).split('\n').filter(Boolean);
      try {
        if (commits.length) this.git(['cherry-pick', ...commits]);
        task.integration = { previousHead, head: this.git(['rev-parse', 'HEAD']), commits, at: new Date().toISOString() };
        task.status = 'integrated';
        task.owner = 'coordinator';
        task.nextAction = `Run actual coordinator verification with verify --id ${id}.`;
      } catch (error) {
        let abortError;
        try { this.git(['cherry-pick', '--abort']); } catch (abort) { abortError = abort.message; }
        task.status = 'needs_fix';
        task.integrationFailure = { message: error.message, abortError, previousHead, observedHead: this.git(['rev-parse', 'HEAD']) };
      }
      return { state, value: task };
    });
  }

  verify(id, { command, timeoutMs = 600000 }) {
    const lease = this.transaction(state => {
      const task = this.task(state, id);
      if (state.verificationLease) throw new Error(`verification lease ${state.verificationLease.token} already active`);
      if (!task.integration || !['integrated', 'verified', 'needs_fix'].includes(task.status)) throw new Error('verification requires integration');
      this.clean(this.repo);
      if (!Array.isArray(command) || !command.length || command.some(value => typeof value !== 'string' || !value)) throw new Error('actual test command array required');
      if (!Number.isInteger(timeoutMs) || timeoutMs < 1 || timeoutMs > 3600000) throw new Error('timeout must be 1..3600000 ms');
      const executable = path.basename(command[0]).toLowerCase().replace(/\.exe$/, '');
      const compiles = executable === 'msbuild' || (executable === 'dotnet' && ['build', 'test', 'msbuild'].includes(command[1]));
      if (compiles && !command.some(value => /^[-/]p:ModName=$/i.test(value))) throw new Error('build verification requires -p:ModName= to disable deployment');
      const head = this.git(['rev-parse', 'HEAD']);
      this.git(['merge-base', '--is-ancestor', task.integration.head, head]);
      const token = crypto.randomUUID();
      const log = path.join(this.directory, 'evidence', id, `integration-${token}.log`);
      fs.mkdirSync(path.dirname(log), { recursive: true });
      const prepared = { token, taskId: id, head, ownerPid: process.pid, command, timeoutMs, log, startedAt: new Date().toISOString() };
      state.verificationLease = prepared;
      task.status = 'verifying';
      task.owner = 'coordinator';
      task.nextAction = `Verification owned by PID ${process.pid}; independent worker tasks may continue. Recover only after confirming the owner has exited.`;
      return { state, value: prepared };
    });
    const result = spawnSync(command[0], command.slice(1), { cwd: this.repo, encoding: 'utf8', windowsHide: true, timeout: timeoutMs, maxBuffer: 32 * 1024 * 1024 });
    const content = `$ ${JSON.stringify(command)}\nHEAD ${lease.head}\n${result.stdout ?? ''}${result.stderr ?? ''}${result.error?.stack ?? ''}\nexitCode=${result.status}\n`;
    fs.writeFileSync(lease.log, content);
    const finish = () => this.transaction(state => {
      const task = this.task(state, id);
      if (state.verificationLease?.token !== lease.token || task.status !== 'verifying') throw new Error('verification lease changed; result cannot be accepted');
      const observedHead = this.git(['rev-parse', 'HEAD']);
      this.recordVerification(task, { head: lease.head, observedHead, token: lease.token, command, exitCode: result.status, log: lease.log, sha256: digest(content), at: new Date().toISOString() });
      task.status = result.status === 0 && !result.error && observedHead === lease.head ? 'verified' : 'needs_fix';
      try { this.clean(this.repo); } catch (error) { task.status = 'needs_fix'; task.verification.dirtyError = error.message; }
      task.owner = 'coordinator';
      task.nextAction = task.status === 'verified' ? `Close task ${id}, then clean its verified worktree.` : `Create a repair task with create --followup-of ${id}; integrate its reviewed fix, then rerun verify --id ${id} on the clean coordinator HEAD.`;
      delete state.verificationLease;
      return { state, value: task };
    });
    for (let attempt = 0; ; attempt++) {
      try { return finish(); }
      catch (error) {
        if (!error.message.startsWith('coordination lock held') || attempt >= 40) throw error;
        Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 50);
      }
    }
  }

  recordVerification(task, receipt) {
    task.verificationHistory ??= [];
    if (task.verification && !task.verificationHistory.some(previous => previous.log === task.verification.log)) task.verificationHistory.push(task.verification);
    task.verification = receipt;
    task.verificationHistory.push(receipt);
  }

  recoverVerification({ reason }) {
    return this.transaction(state => {
      this.coordinator(state);
      const lease = state.verificationLease;
      if (!lease) throw new Error('no verification lease to recover');
      requireText(reason, 'recovery reason');
      let dead = false;
      try { process.kill(lease.ownerPid, 0); }
      catch (error) { if (error.code === 'ESRCH') dead = true; else throw new Error('cannot confirm verification owner has exited'); }
      if (!dead) throw new Error('verification owner is still alive; do not steal its lease');
      const task = this.task(state, lease.taskId);
      fs.appendFileSync(lease.log, `\nOwner PID ${lease.ownerPid} exited; verification outcome unknown. Recovery: ${reason}\n`);
      this.recordVerification(task, { head: lease.head, command: lease.command, exitCode: null, log: lease.log, sha256: digest(fs.readFileSync(lease.log)), recovered: true, ownerPid: lease.ownerPid, at: new Date().toISOString() });
      task.status = 'needs_fix';
      task.owner = 'coordinator';
      task.nextAction = `Owner confirmed exited. Rerun verify --id ${task.id} on clean HEAD; no passing outcome was inferred.`;
      delete state.verificationLease;
      return { state, value: task };
    });
  }

  close(id) {
    return this.transaction(state => {
      const task = this.task(state, id);
      this.clean(this.repo);
      if (task.status !== 'verified' || task.verification?.exitCode !== 0 || task.verification.head !== this.git(['rev-parse', 'HEAD'])) throw new Error('close requires successful post-integration verification of current HEAD');
      if (digest(fs.readFileSync(task.verification.log)) !== task.verification.sha256) throw new Error('verification log changed');
      task.status = 'closed';
      task.closedAt = new Date().toISOString();
      return { state, value: task };
    });
  }

  interrupt(id, { reason, agentStatus } = {}) {
    return this.transaction(state => {
      const task = this.task(state, id);
      if (task.integration || ['closed', 'integrated', 'verified'].includes(task.status)) throw new Error('task no longer has a writing worker');
      requireText(reason, 'interrupt reason');
      if (agentStatus && !['idle', 'completed', 'interrupted'].includes(agentStatus)) throw new Error('invalid agent stop receipt');
      task.status = agentStatus ? 'interrupted' : 'interrupt_requested';
      task.interruption = { reason, agentStatus, agentId: task.agentId, at: new Date().toISOString() };
      return { state, value: { ...task, needsAgentInterrupt: !agentStatus, instruction: 'The root chat must stop/observe the native agent using Codex tools. Node cannot call native agent tools.' } };
    });
  }

  cleanup(id) {
    return this.transaction(state => {
      const task = this.task(state, id);
      if (task.status !== 'closed' || !task.integration || task.verification?.exitCode !== 0 || task.registeredBy !== 'codex-coordination') throw new Error('cleanup requires a registered, integrated, verified and closed task');
      if (task.cleanedAt) return { value: task };
      const expected = path.join(state.managedRoot, task.id);
      if (task.worktree !== expected || !within(state.managedRoot, expected) || canonical(expected) !== expected || canonical(state.managedRoot) !== state.managedRoot) throw new Error('unsafe managed worktree path');
      this.clean(task.worktree);
      if (this.git(['branch', '--show-current'], task.worktree) !== task.branch || this.git(['rev-parse', 'HEAD'], task.worktree) !== task.handoff.commit || this.git(['rev-parse', task.branch]) !== task.handoff.commit) throw new Error('worker branch changed after handoff');
      this.git(['merge-base', '--is-ancestor', task.verification.head, this.git(['rev-parse', 'HEAD'])]);
      this.git(['worktree', 'remove', expected]);
      this.git(['branch', '-D', task.branch]);
      task.cleanedAt = new Date().toISOString();
      return { state, value: task };
    });
  }
}

const help = `Usage: node tools/coordination/orchestrator.mjs --root <repo> <command> [options]
Commands: init | status | next | create | assign | submit | review | integrate | verify | recover-verification | close | interrupt | cleanup
create --id slug --stream authority [--scope path] [--read-only] [--scope-authorization coordinator-reason] [--objective text]
create --id fix-slug --followup-of failed-integrated-task [--scope narrower-path]
assign --id slug --agent native-agent-id [--adapter native|cli]
submit --id slug --handoff absolute-handoff.json
review --id slug --reviewer independent-agent-id --decision pass|needs_fix --notes text
integrate --id slug
verify --id slug [--timeout 600000] -- node --test path/to/test.mjs
recover-verification --reason text (only after the recorded owner PID has exited)
close --id slug | cleanup --id slug
interrupt --id slug --reason text [--agent-status idle|completed|interrupted]
The root chat invokes native agents; this CLI registers their exclusive worktrees.
All persistent state is in the Git common directory. Model policy: inherit-parent.`;

export function main(args = process.argv.slice(2)) {
  const options = { scope: [] };
  const positional = [];
  for (let index = 0; index < args.length; index++) {
    const value = args[index];
    if (value === '--') { options.command = args.slice(index + 1); break; }
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
  else if (command === 'create') result = board.create({ id: options.id, stream: options.stream, scope: options.scope.length ? options.scope : undefined, readOnly: options.readOnly, scopeAuthorization: options['scope-authorization'], objective: options.objective, followupOf: options['followup-of'] });
  else if (command === 'assign') result = board.assign(options.id, { agentId: options.agent, adapter: options.adapter });
  else if (command === 'submit') result = board.submit(options.id, json(path.resolve(requireText(options.handoff, 'handoff file'))));
  else if (command === 'review') result = board.review(options.id, { reviewerId: options.reviewer, decision: options.decision, notes: options.notes });
  else if (command === 'verify') result = board.verify(options.id, { command: options.command, timeoutMs: options.timeout ? Number(options.timeout) : undefined });
  else if (command === 'recover-verification') result = board.recoverVerification({ reason: options.reason });
  else if (command === 'interrupt') result = board.interrupt(options.id, { reason: options.reason, agentStatus: options['agent-status'] });
  else if (['integrate', 'close', 'cleanup'].includes(command)) result = board[command](options.id);
  else throw new Error(`unknown command; ${help}`);
  process.stdout.write(`${JSON.stringify(result, null, 2)}\n`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { main(); } catch (error) { process.stderr.write(`${error.message}\n`); process.exitCode = 1; }
}
