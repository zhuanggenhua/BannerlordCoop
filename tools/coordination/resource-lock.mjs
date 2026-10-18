import { mkdir, readFile, writeFile, rm, realpath } from 'node:fs/promises';
import { join, resolve, dirname, basename } from 'node:path';
import { tmpdir, hostname } from 'node:os';
import { randomUUID, createHash } from 'node:crypto';
import { promisify } from 'node:util';
import { execFile } from 'node:child_process';

const execute = promisify(execFile);
export const defaultLockRoot = join(tmpdir(), 'bannerlordcoop-coordination');

export function lockPath({ resourceKey, lockRoot = defaultLockRoot }) {
    if (!resourceKey) throw new Error('resourceKey is required');
    return join(lockRoot, `game-${createHash('sha256').update(resourceKey).digest('hex')}`);
}

export async function processIdentity(pid) {
    try { process.kill(pid, 0); }
    catch (error) { return { state: error.code === 'ESRCH' ? 'dead' : 'unknown' }; }
    try {
        let start;
        if (process.platform === 'win32') {
            const { stdout } = await execute('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command',
                `(Get-Process -Id ${Number(pid)} -ErrorAction Stop).StartTime.ToUniversalTime().Ticks`],
            { windowsHide: true, timeout: 10000 });
            start = stdout.trim();
        } else {
            const stat = await readFile(`/proc/${pid}/stat`, 'utf8');
            start = `${(await readFile('/proc/sys/kernel/random/boot_id', 'utf8')).trim()}:${stat.slice(stat.lastIndexOf(')') + 2).split(' ')[19]}`;
        }
        return start ? { state: 'alive', start } : { state: 'unknown' };
    } catch { return { state: 'unknown' }; }
}

function failure(code, message, details = {}) {
    return Object.assign(new Error(message), { code, ...details });
}

async function readOwner(path) {
    try { return JSON.parse(await readFile(join(path, 'owner.json'), 'utf8')); }
    catch { throw failure('LOCK_UNKNOWN', 'Missing or unreadable owner; preserve lock for inspection', { path }); }
}

async function removeLock(options, path) {
    const root = resolve(options.lockRoot ?? defaultLockRoot), target = resolve(path);
    const comparable = value => process.platform === 'win32' ? value.toLowerCase() : value;
    if (comparable(dirname(target)) !== comparable(root) || !/^game-[0-9a-f]{64}$/.test(basename(target))
        || comparable(await realpath(target)) !== comparable(join(await realpath(root), basename(target)))) {
        throw failure('LOCK_PATH_INVALID', 'Resolved lock is outside its designated root; deletion refused');
    }
    await rm(target, { recursive: true });
}

export async function acquireResourceLock(options) {
    const path = lockPath(options);
    await mkdir(options.lockRoot ?? defaultLockRoot, { recursive: true });
    try { await mkdir(path); }
    catch (error) {
        if (error.code !== 'EEXIST') throw error;
        const owner = await readOwner(path);
        throw failure('LOCK_BUSY', 'Resource remains locked; age never grants ownership', { path, owner });
    }
    const identity = await processIdentity(process.pid);
    if (identity.state !== 'alive') {
        await removeLock(options, path);
        throw failure('IDENTITY_UNKNOWN', 'Cannot establish owner start identity');
    }
    const owner = { token: randomUUID(), pid: process.pid, start: identity.start,
        host: hostname(), createdAt: new Date().toISOString(), resourceKey: options.resourceKey };
    await writeFile(join(path, 'owner.json'), JSON.stringify(owner, null, 2), { flag: 'wx' });
    let released = false;
    return { token: owner.token, path, owner, async release() {
        if (released) return;
        const current = await readOwner(path);
        if (current.token !== owner.token) throw failure('TOKEN_MISMATCH', 'Owner token changed; preserve current lock');
        try {
            await readFile(join(path, 'recovery_required.json'));
            throw failure('CLEANUP_REQUIRED', 'Unknown cleanup preserves the lock');
        } catch (error) { if (error.code !== 'ENOENT') throw error; }
        await removeLock(options, path);
        released = true;
    } };
}

export async function withResourceLock(options, action) {
    const lease = await acquireResourceLock(options);
    try { return await action(lease); }
    finally { await lease.release(); }
}

// Recovery is explicit, token-bound, serialized, and never based on elapsed time.
export async function recoverResourceLock(options) {
    const path = lockPath(options);
    const owner = await readOwner(path);
    if (!options.token || owner.token !== options.token) throw failure('TOKEN_MISMATCH', 'Recovery requires exact owner token');
    try {
        await readFile(join(path, 'recovery_required.json'));
        throw failure('CLEANUP_REQUIRED', 'Inspect remaining game processes before explicitly clearing the recovery receipt');
    } catch (error) { if (error.code !== 'ENOENT') throw error; }
    if (owner.host !== hostname()) throw failure('OWNER_UNKNOWN', 'Owner belongs to a different host');
    const identity = await processIdentity(owner.pid);
    if (identity.state !== 'dead') throw failure('OWNER_ACTIVE_OR_UNKNOWN', 'Original PID is present or unknown; recovery refused');
    try { await writeFile(join(path, 'recovering'), options.token, { flag: 'wx' }); }
    catch (error) { throw failure('RECOVERY_BUSY', 'Recovery is already claimed; inspect before retrying', { cause: error }); }
    const current = await readOwner(path);
    if (current.token !== options.token) throw failure('TOKEN_MISMATCH', 'Recovery owner changed');
    await removeLock(options, path);
    return { recovered: true, owner, path };
}
