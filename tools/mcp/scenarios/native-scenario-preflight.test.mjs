import assert from 'node:assert/strict';
import test from 'node:test';
import { execFileSync } from 'node:child_process';
import { mkdtemp, mkdir, readFile, writeFile, rm, realpath, rename, unlink, cp } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { QUICK_TEST_FILES, runNativeScenarioPreflight } from './native-scenario-preflight.mjs';
import { run } from './p2p-mission-lifecycle.mjs';

async function fixture(t) {
    const repo = await mkdtemp(join(tmpdir(), 'coop preflight '));
    t.after(async () => {
        const evidence = resolve('artifacts/modify-native-preflight-m8/fixture-evidence', randomUUID());
        await mkdir(evidence, { recursive: true });
        await cp(join(repo, '.git/codex-coordination/preflight'), join(evidence, 'preflight'), { recursive: true });
        await writeFile(join(evidence, 'fixture.json'), JSON.stringify({ test: t.name, originalRepo: repo,
            scope: 'Offline controlled repository; no real game launch' }, null, 2));
        assert.equal(dirname(await realpath(repo)), await realpath(tmpdir()));
        assert.ok(repo.startsWith(join(tmpdir(), 'coop preflight ')));
        await rm(repo, { recursive: true, force: true });
    });
    await mkdir(join(repo, 'tools/mcp/scenarios'), { recursive: true });
    for (const file of QUICK_TEST_FILES)
        await writeFile(join(repo, file), "import test from 'node:test'; test('fixture', () => {});\n");
    const git = (...args) => execFileSync('git', args, { cwd: repo, encoding: 'utf8', windowsHide: true });
    git('init', '--quiet'); git('add', 'tools');
    git('-c', 'user.name=Tool Fixture', '-c', 'user.email=fixture@example.invalid', 'commit', '--quiet', '-m', 'fixture');
    return { repo, git };
}

test('same HEAD, Node, exact command and full source manifest reuse only verified successful tests', async t => {
    const { repo } = await fixture(t);
    const cold = await runNativeScenarioPreflight(repo), warm = await runNativeScenarioPreflight(repo);
    assert.equal(cold.success, true); assert.equal(cold.exitCode, 0); assert.equal(cold.reused, false);
    assert.equal(warm.reused, true); assert.equal(warm.sourceExitCode, 0);
    assert.equal(warm.sourceAttempt, dirname(cold.receipt));
    assert.equal(warm.logSha256, cold.logSha256);
    assert.deepEqual(cold.input.command, [process.execPath, '--test', '--test-reporter=tap', ...QUICK_TEST_FILES]);
    assert.equal(cold.input.nodeVersion, process.version);
    assert.ok(cold.durationMs >= 0 && warm.durationMs >= 0);
});

test('tampered, missing or failed cached evidence reruns rather than authorizing launch', async t => {
    const { repo } = await fixture(t);
    let last = await runNativeScenarioPreflight(repo);
    await writeFile(join(dirname(last.receipt), 'test.log'), 'tampered');
    last = await runNativeScenarioPreflight(repo); assert.equal(last.reused, false);
    await unlink(join(dirname(last.receipt), 'test.log'));
    last = await runNativeScenarioPreflight(repo); assert.equal(last.reused, false);
    const receipt = JSON.parse(await readFile(last.receipt, 'utf8'));
    receipt.exitCode = 1; await writeFile(last.receipt, JSON.stringify(receipt));
    last = await runNativeScenarioPreflight(repo); assert.equal(last.reused, false);
    const wrongNode = JSON.parse(await readFile(last.receipt, 'utf8'));
    wrongNode.input.nodeVersion = 'wrong'; await writeFile(last.receipt, JSON.stringify(wrongNode));
    last = await runNativeScenarioPreflight(repo); assert.equal(last.reused, false);
    assert.ok(last.rejected.length > 0);
});

test('a newer failed attempt cannot fall back to an older valid success', async t => {
    const { repo } = await fixture(t);
    const good = await runNativeScenarioPreflight(repo);
    const bad = join(dirname(dirname(good.receipt)), Date.now() + '-' + randomUUID());
    await mkdir(bad);
    await cp(join(dirname(good.receipt), 'test.log'), join(bad, 'test.log'));
    const failed = JSON.parse(await readFile(good.receipt, 'utf8'));
    failed.success = false; failed.exitCode = 1;
    await writeFile(join(bad, 'result.json'), JSON.stringify(failed));
    const retried = await runNativeScenarioPreflight(repo);
    assert.equal(retried.reused, false); assert.equal(retried.exitCode, 0);
    assert.ok(retried.rejected.length > 0);
});

test('untracked additions, renames, deletion, source edits and HEAD changes invalidate success', async t => {
    const { repo, git } = await fixture(t);
    let last = await runNativeScenarioPreflight(repo);
    const added = join(repo, 'tools/mcp/scenarios/new-untracked.mjs');
    await writeFile(added, '// new source\n');
    let changed = await runNativeScenarioPreflight(repo); assert.equal(changed.reused, false); assert.notEqual(changed.key, last.key);
    last = changed; await rename(added, added + '.renamed.mjs');
    changed = await runNativeScenarioPreflight(repo); assert.equal(changed.reused, false); assert.notEqual(changed.key, last.key);
    last = changed; await unlink(added + '.renamed.mjs');
    // Returning to identical inputs may reuse the original success.
    changed = await runNativeScenarioPreflight(repo); assert.equal(changed.reused, true);
    await writeFile(join(repo, QUICK_TEST_FILES[0]), "test('new case', () => {});\n", { flag: 'a' });
    changed = await runNativeScenarioPreflight(repo); assert.equal(changed.reused, false);
    last = changed; git('-c', 'user.name=Tool Fixture', '-c', 'user.email=fixture@example.invalid', 'commit', '--allow-empty', '--quiet', '-m', 'new HEAD');
    changed = await runNativeScenarioPreflight(repo); assert.equal(changed.reused, false); assert.notEqual(changed.key, last.key);
});

test('source changes during successful quick tests refuse the receipt', async t => {
    const { repo } = await fixture(t);
    await runNativeScenarioPreflight(repo);
    const probe = join(repo, 'tools/mcp/scenarios/input-probe.mjs'); await writeFile(probe, '// before');
    await writeFile(join(repo, QUICK_TEST_FILES[0]),
        `import fs from 'node:fs'; fs.writeFileSync(${JSON.stringify(probe)}, '// changed');\n`);
    await assert.rejects(() => runNativeScenarioPreflight(repo), /preflight failed before SDK/);
});

test('inherited filters are cleared only for the child and explicitly skipped quick cases still refuse launch', async t => {
    const { repo } = await fixture(t), previous = process.env.NODE_OPTIONS;
    try {
        process.env.NODE_OPTIONS = '--test-name-pattern=does-not-exist';
        const result = await runNativeScenarioPreflight(repo);
        assert.equal(result.success, true); assert.equal(result.counts.skipped, 0);
        assert.equal(result.counts.pass, result.counts.tests);
    } finally {
        if (previous === undefined) delete process.env.NODE_OPTIONS;
        else process.env.NODE_OPTIONS = previous;
    }
    await writeFile(join(repo, QUICK_TEST_FILES[0]), "import test from 'node:test'; test('skip', { skip: true }, () => {});\n");
    await assert.rejects(() => runNativeScenarioPreflight(repo), /preflight failed/);
});

test('actual shared run refuses a quick failure before any SDK, MCP or native launch', async t => {
    const { repo } = await fixture(t);
    await runNativeScenarioPreflight(repo);
    await writeFile(join(repo, QUICK_TEST_FILES[0]), 'process.exit(7);\n');
    const marker = join(repo, 'SDK-was-imported');
    const sdk = join(repo, 'sdk/@modelcontextprotocol/sdk/dist/esm/client'); await mkdir(sdk, { recursive: true });
    for (const file of ['index.js', 'stdio.js'])
        await writeFile(join(sdk, file), `require('node:fs').writeFileSync(${JSON.stringify(marker)}, 'unexpected SDK entry');\n`);
    await assert.rejects(() => run({ repo, config: join(repo, 'unused-profiles.json'), 'sdk-root': join(repo, 'sdk') }),
        /preflight failed before SDK\/MCP launch/);
    await assert.rejects(() => readFile(marker), error => error.code === 'ENOENT');
    const proof = resolve('artifacts/modify-native-preflight-m8/forced-failure-' + randomUUID() + '.json');
    await mkdir(dirname(proof), { recursive: true });
    await writeFile(proof, JSON.stringify({ originalRepo: repo, sdkImportMarkerExists: false,
        sdkOrMcpLaunchEntryReached: false, fixtureCommandExit: 7,
        evidence: 'Actual shared run rejected quick tests before SDK import; marker module never loaded.' }, null, 2));
    await assert.rejects(() => runNativeScenarioPreflight(repo), /preflight failed/);
});
