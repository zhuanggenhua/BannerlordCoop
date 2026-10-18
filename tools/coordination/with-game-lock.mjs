import { realpath, writeFile, stat } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { setTimeout as delay } from 'node:timers/promises';
import { acquireResourceLock, processIdentity } from './resource-lock.mjs';

const execute = promisify(execFile);
const receipt = (event, data = {}) => console.log(JSON.stringify({ event, at: new Date().toISOString(), ...data }));

export async function resolveGameResource(repo) {
    const path = await realpath(join(repo, 'mb2'));
    return process.platform === 'win32' ? path.toLowerCase() : path;
}

async function coordinatorOnly(repo) {
    if (!(await stat(join(repo, '.git'))).isDirectory()) throw new Error('Live work requires the coordinator main checkout, not a worker worktree');
    const { stdout } = await execute('git', ['rev-parse', '--show-toplevel'], { cwd: repo, windowsHide: true });
    if (await realpath(stdout.trim()) !== await realpath(repo)) throw new Error('repo must name the main checkout root');
}

async function processSnapshot() {
    if (process.platform === 'win32') {
        const { stdout } = await execute('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command',
            '@(Get-CimInstance Win32_Process -ErrorAction Stop | Select-Object ProcessId,ParentProcessId,Name) | ConvertTo-Json -Compress'],
        { windowsHide: true, timeout: 15000, maxBuffer: 2 * 1024 * 1024 });
        const rows = stdout.trim() ? JSON.parse(stdout) : [];
        return Array.isArray(rows) ? rows : [rows];
    }
    const { stdout } = await execute('ps', ['-eo', 'pid=,ppid=,comm='], { timeout: 5000 });
    return stdout.trim().split('\n').map(line => {
        const [pid, ppid, ...name] = line.trim().split(/\s+/);
        return { ProcessId: Number(pid), ParentProcessId: Number(ppid), Name: name.join(' ') };
    });
}

const games = snapshot => snapshot.filter(row => /bannerlord/i.test(row.Name));
function descendants(pid, snapshot) {
    const owned = new Set([pid]);
    for (let changed = true; changed;) {
        changed = false;
        for (const row of snapshot) if (owned.has(row.ParentProcessId) && !owned.has(row.ProcessId)) {
            owned.add(row.ProcessId); changed = true;
        }
    }
    return snapshot.filter(row => row.ProcessId !== pid && owned.has(row.ProcessId));
}

async function terminateOwnedTree(child, identity) {
    const current = await processIdentity(child.pid);
    if (current.state === 'dead') {
        if (process.platform !== 'win32') { try { process.kill(-child.pid, 'SIGKILL'); } catch (e) { if (e.code !== 'ESRCH') throw e; } }
        return;
    }
    if (current.state !== 'alive' || current.start !== identity.start) throw new Error('Child PID identity changed or is unknown; no tree kill attempted');
    if (process.platform === 'win32') {
        await execute('taskkill.exe', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true, timeout: 15000 });
    } else { process.kill(-child.pid, 'SIGKILL'); }
}

function parse(argv) {
    const divider = argv.indexOf('--');
    if (divider < 0) throw new Error('Usage: --repo <coordinatorRepo> [--timeout-ms N] -- <executable> <args...>');
    const options = {};
    for (let i = 0; i < divider; i += 2) {
        if (!['--repo', '--resource-key', '--lock-root', '--timeout-ms'].includes(argv[i]) || !argv[i + 1]) throw new Error(`Invalid option ${argv[i]}`);
        options[argv[i].slice(2)] = argv[i + 1];
    }
    const command = argv.slice(divider + 1);
    if (!options.repo || !command.length) throw new Error('repo and executable are required');
    const timeout = Number(options['timeout-ms'] ?? 0);
    if (!Number.isSafeInteger(timeout) || timeout < 0) throw new Error('timeout-ms must be a nonnegative integer');
    return { options, command, timeout };
}

export async function main(argv = process.argv.slice(2)) {
    const { options, command, timeout } = parse(argv);
    const repo = await realpath(resolve(options.repo));
    const fixture = options['resource-key'] !== undefined || options['lock-root'] !== undefined;
    if (fixture) {
        const root = resolve(options['lock-root'] ?? '');
        if (!options['resource-key']?.startsWith('fixture:') || !root.startsWith(`${tmpdir()}${process.platform === 'win32' ? '\\' : '/'}`)
            || await realpath(command[0]) !== await realpath(process.execPath)) throw new Error('Lock overrides are only for Node fixtures under OS tmp');
    } else { await coordinatorOnly(repo); }
    const lease = await acquireResourceLock({ resourceKey: fixture ? options['resource-key'] : await resolveGameResource(repo), lockRoot: options['lock-root'] });
    receipt('lock_acquired', { path: lease.path, owner: lease.owner });
    let child, childIdentity, stopReason, stopError, timer, exit;
    let stopping;
    let finishStop;
    const stopped = new Promise(resolveStop => { finishStop = resolveStop; });
    const stop = reason => {
        stopReason ??= reason;
        if (!child?.pid || !childIdentity) return;
        stopping ??= terminateOwnedTree(child, childIdentity).catch(error => { stopError = error.message; })
            .finally(() => finishStop({ cleanupUnknown: true }));
    };
    const interrupted = () => stop('SIGINT');
    const terminated = () => stop('SIGTERM');
    process.on('SIGINT', interrupted); process.on('SIGTERM', terminated);
    let clean = false;
    try {
        const existing = games(await processSnapshot());
        if (existing.length) throw new Error(`Existing game processes prevent launch: ${JSON.stringify(existing)}`);
        child = spawn(command[0], command.slice(1), { cwd: repo, windowsHide: true,
            detached: process.platform !== 'win32', stdio: 'inherit', shell: false });
        const closed = new Promise((resolveExit, reject) => {
            child.once('error', reject);
            child.once('close', (code, signal) => resolveExit({ code, signal }));
        });
        // Attach the rejection immediately, even while resolving the process identity.
        closed.catch(() => {});
        childIdentity = child.pid ? await processIdentity(child.pid) : undefined;
        receipt('child_started', { pid: child.pid, identity: childIdentity, token: lease.token });
        if (childIdentity?.state !== 'alive' && child.exitCode === null) throw new Error('Cannot establish child identity');
        if (stopReason) stop(stopReason);
        if (timeout) timer = setTimeout(() => stop('timeout'), timeout);
        exit = await Promise.race([closed, stopped]);
        await stopping;
        if (exit.cleanupUnknown) exit = await Promise.race([closed, delay(2000).then(() => ({ cleanupUnknown: true }))]);
        if (exit.cleanupUnknown) { child.unref(); throw new Error('Child cleanup did not finish; lock remains unavailable'); }
        receipt('child_exit', { ...exit, stopReason, stopError });
        const snapshot = await processSnapshot();
        const remaining = [...games(snapshot), ...descendants(child.pid, snapshot)];
        clean = !stopError && remaining.length === 0;
        if (!clean) throw new Error(`Cleanup not proven: ${stopError ?? JSON.stringify(remaining)}`);
        return stopReason ? 130 : exit.code ?? 1;
    } catch (error) {
        stop('wrapper_error'); await stopping;
        receipt('wrapper_error', { message: error.message });
        // A failed launch with no child and no game is safe; otherwise retain for inspection.
        if (!child) { try { clean = games(await processSnapshot()).length === 0; } catch {} }
        return 1;
    } finally {
        clearTimeout(timer); process.off('SIGINT', interrupted); process.off('SIGTERM', terminated);
        if (clean) { await lease.release(); receipt('lock_released', { token: lease.token, cleanup: 'confirmed' }); }
        else {
            const record = { token: lease.token, owner: lease.owner, childPid: child?.pid, exit, stopReason, stopError, cleanup: 'recovery_required' };
            await writeFile(join(lease.path, 'recovery_required.json'), JSON.stringify(record, null, 2));
            receipt('recovery_required', { path: lease.path, ...record });
        }
    }
}

if (process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1])) {
    main().then(code => { process.exitCode = code; }).catch(error => {
        receipt('lock_refused', { code: error.code, message: error.message, owner: error.owner }); process.exitCode = 1;
    });
}
