import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, writeFile, rm, mkdir, symlink, rename, unlink } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { setTimeout as delay } from 'node:timers/promises';
import { acquireResourceLock, withResourceLock, recoverResourceLock, processIdentity } from './resource-lock.mjs';
import { resolveGameResource } from './with-game-lock.mjs';

const moduleUrl = new URL('./resource-lock.mjs', import.meta.url).href;
async function fixture(t) {
    const lockRoot = await mkdtemp(join(tmpdir(), 'coop-lock-test-'));
    t.after(() => rm(lockRoot, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 }));
    return { lockRoot, resourceKey: 'fixture-only' };
}
function run(source, args = []) {
    const child = spawn(process.execPath, ['--input-type=module', '-e', source, ...args], { windowsHide: true });
    let stdout = '', stderr = '';
    child.stdout.on('data', chunk => { stdout += chunk; });
    child.stderr.on('data', chunk => { stderr += chunk; });
    return { child, done: once(child, 'close').then(([code, signal]) => ({ code, signal, stdout, stderr })) };
}
async function ready(child) {
    return await new Promise((resolve, reject) => {
        child.stdout.once('data', data => resolve(JSON.parse(data.toString())));
        child.once('error', reject);
    });
}
test('active lease refuses all contenders and releases after exceptions', async t => {
    const options = await fixture(t);
    await assert.rejects(withResourceLock(options, async () => {
        await assert.rejects(acquireResourceLock(options), { code: 'LOCK_BUSY' });
        throw new Error('fixture failure');
    }), /fixture failure/);
    await (await acquireResourceLock(options)).release();
});
test('release never deletes a replaced token', async t => {
    const options = await fixture(t), lease = await acquireResourceLock(options);
    await writeFile(join(lease.path, 'owner.json'), JSON.stringify({ ...lease.owner, token: 'replacement' }));
    await assert.rejects(lease.release(), { code: 'TOKEN_MISMATCH' });
    assert.equal(JSON.parse(await readFile(join(lease.path, 'owner.json'))).token, 'replacement');
});
test('real processes contend and dead owner requires explicit exact-token recovery', async t => {
    const options = await fixture(t);
    const owner = run(`import { acquireResourceLock } from ${JSON.stringify(moduleUrl)};
        const lease=await acquireResourceLock(JSON.parse(process.argv[1]));
        console.log(JSON.stringify(lease.owner)); setInterval(()=>{},1000);`, [JSON.stringify(options)]);
    const receipt = await ready(owner.child);
    await assert.rejects(recoverResourceLock({ ...options, token: receipt.token }), { code: 'OWNER_ACTIVE_OR_UNKNOWN' });
    const contender = run(`import { acquireResourceLock } from ${JSON.stringify(moduleUrl)};
        try { await acquireResourceLock(JSON.parse(process.argv[1])); process.exitCode=3; }
        catch(e) { console.log(e.code); }`, [JSON.stringify(options)]);
    assert.match((await contender.done).stdout, /LOCK_BUSY/);
    owner.child.kill(); await owner.done;
    await assert.rejects(acquireResourceLock(options), { code: 'LOCK_BUSY' });
    await assert.rejects(recoverResourceLock({ ...options, token: 'wrong' }), { code: 'TOKEN_MISMATCH' });
    assert.equal((await recoverResourceLock({ ...options, token: receipt.token })).recovered, true);
    await (await acquireResourceLock(options)).release();
});

test('unknown cleanup receipt prevents release and recovery', async t => {
    const options = await fixture(t), lease = await acquireResourceLock(options);
    await writeFile(join(lease.path, 'recovery_required.json'), '{}');
    await assert.rejects(recoverResourceLock({ ...options, token: lease.token }), { code: 'CLEANUP_REQUIRED' });
    await assert.rejects(lease.release(), { code: 'CLEANUP_REQUIRED' });
    await rm(join(lease.path, 'recovery_required.json'));
    await lease.release();
});

test('a present reused or unverifiable PID is never reclaimed', async t => {
    const options = await fixture(t), lease = await acquireResourceLock(options);
    await writeFile(join(lease.path, 'owner.json'), JSON.stringify({ ...lease.owner, start: 'different-start-identity' }));
    await assert.rejects(recoverResourceLock({ ...options, token: lease.token }), { code: 'OWNER_ACTIVE_OR_UNKNOWN' });
    await lease.release();
});

test('different checkout junctions resolve to the same physical game resource', async t => {
    const options = await fixture(t), game = join(options.lockRoot, 'game');
    await mkdir(game);
    const resources = [];
    for (const name of ['checkout-a', 'checkout-b']) {
        const repo = join(options.lockRoot, name); await mkdir(repo);
        await symlink(game, join(repo, 'mb2'), process.platform === 'win32' ? 'junction' : 'dir');
        resources.push(await resolveGameResource(repo));
    }
    assert.equal(resources[0], resources[1]);
    const lease = await acquireResourceLock({ ...options, resourceKey: resources[0] });
    await assert.rejects(acquireResourceLock({ ...options, resourceKey: resources[1] }), { code: 'LOCK_BUSY' });
    await lease.release();
});

const wrapper = fileURLToPath(new URL('./with-game-lock.mjs', import.meta.url));
function wrapped(options, source, extra = []) {
    const args = [wrapper, '--repo', options.lockRoot, '--resource-key', 'fixture:wrapper',
        '--lock-root', options.lockRoot, ...extra, '--', process.execPath, '-e', source];
    const child = spawn(process.execPath, args, { windowsHide: true });
    let stdout = '', stderr = '';
    child.stdout.on('data', chunk => { stdout += chunk; });
    child.stderr.on('data', chunk => { stderr += chunk; });
    return { child, done: once(child, 'close').then(([code]) => ({ code, stdout, stderr })) };
}

test('wrapper releases on a failed child exit and forwards its exit code', async t => {
    const options = await fixture(t), result = await wrapped(options, 'process.exit(7)').done;
    assert.equal(result.code, 7, result.stdout + result.stderr);
    assert.match(result.stdout, /"event":"lock_released"/);
    const lease = await acquireResourceLock({ ...options, resourceKey: 'fixture:wrapper' }); await lease.release();
});

test('wrapper timeout terminates its own child tree before releasing', async t => {
    const options = await fixture(t), marker = join(options.lockRoot, 'grandchild.json');
    const source = `const {spawn}=require('node:child_process');
        const grandchild=spawn(process.execPath,['-e','setInterval(()=>{},1000)'],{windowsHide:true,stdio:'ignore'});
        require('node:fs').writeFileSync(${JSON.stringify(marker)},JSON.stringify({pid:grandchild.pid})); setInterval(()=>{},1000);`;
    const result = await wrapped(options, source, ['--timeout-ms', '1500']).done;
    assert.equal(result.code, 130, result.stdout + result.stderr);
    assert.match(result.stdout, /"stopReason":"timeout"/);
    assert.match(result.stdout, /"event":"lock_released"/);
    assert.equal((await processIdentity(JSON.parse(await readFile(marker)).pid)).state, 'dead');
});

test('worker checkout cannot use the live wrapper entry', async t => {
    const options = await fixture(t);
    await writeFile(join(options.lockRoot, '.git'), 'gitdir: fixture-worker');
    const child = spawn(process.execPath, [wrapper, '--repo', options.lockRoot,
        '--', process.execPath, '-e', 'process.exit(99)'], { windowsHide: true });
    let output = ''; child.stdout.on('data', data => { output += data; });
    assert.equal((await once(child, 'close'))[0], 1);
    assert.match(output, /not a worker worktree/);
    assert.doesNotMatch(output, /child_started/);
});

test('a replaced lock junction cannot delete a path outside the hashed resource', async t => {
    const options = await fixture(t), lease = await acquireResourceLock(options);
    const saved = `${lease.path}-saved`, unrelated = join(options.lockRoot, 'unrelated');
    await mkdir(unrelated); await writeFile(join(unrelated, 'owner.json'), JSON.stringify(lease.owner));
    await rename(lease.path, saved);
    await symlink(unrelated, lease.path, process.platform === 'win32' ? 'junction' : 'dir');
    await assert.rejects(lease.release(), { code: 'LOCK_PATH_INVALID' });
    assert.equal(JSON.parse(await readFile(join(unrelated, 'owner.json'))).token, lease.token);
    await unlink(lease.path); await rename(saved, lease.path); await lease.release();
});

test('normal root exit with a surviving fixture descendant retains recovery receipt', async t => {
    const options = await fixture(t), marker = join(options.lockRoot, 'orphan.json');
    const source = `const child=require('node:child_process').spawn(process.execPath,['-e','setInterval(()=>{},1000)'],{windowsHide:true,detached:true,stdio:'ignore'});
        require('node:fs').writeFileSync(${JSON.stringify(marker)},JSON.stringify({pid:child.pid})); child.unref();`;
    const result = await wrapped(options, source).done;
    const pid = JSON.parse(await readFile(marker)).pid;
    try {
        assert.equal(result.code, 1, result.stdout + result.stderr);
        assert.match(result.stdout, /"event":"recovery_required"/);
        assert.doesNotMatch(result.stdout, /"event":"lock_released"/);
        await assert.rejects(acquireResourceLock({ ...options, resourceKey: 'fixture:wrapper' }), { code: 'LOCK_BUSY' });
    } finally {
        try { process.kill(pid); } catch (error) { if (error.code !== 'ESRCH') throw error; }
        for (let i = 0; i < 20 && (await processIdentity(pid)).state !== 'dead'; i++) await delay(100);
        assert.equal((await processIdentity(pid)).state, 'dead');
    }
});

test('SIGINT handler cleans the owned child before releasing', async t => {
    const options = await fixture(t);
    const args = ['--repo', options.lockRoot, '--resource-key', 'fixture:interrupt', '--lock-root', options.lockRoot,
        '--', process.execPath, '-e', 'setInterval(()=>{},1000)'];
    const runner = run(`import {main} from ${JSON.stringify(new URL('./with-game-lock.mjs', import.meta.url).href)};
        const timer=setTimeout(()=>process.emit('SIGINT'),2500);
        process.exitCode=await main(JSON.parse(process.argv[1])); clearTimeout(timer);`, [JSON.stringify(args)]);
    const result = await runner.done;
    assert.equal(result.code, 130, result.stdout + result.stderr);
    assert.match(result.stdout, /"stopReason":"SIGINT"/);
    assert.match(result.stdout, /"event":"lock_released"/);
});
