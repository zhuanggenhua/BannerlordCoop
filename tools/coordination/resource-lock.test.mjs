import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { acquireResourceLock, withResourceLock, recoverResourceLock } from './resource-lock.mjs';

const moduleUrl = new URL('./resource-lock.mjs', import.meta.url).href;
async function fixture(t) {
    const lockRoot = await mkdtemp(join(tmpdir(), 'coop-lock-test-'));
    t.after(() => rm(lockRoot, { recursive: true, force: true }));
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
