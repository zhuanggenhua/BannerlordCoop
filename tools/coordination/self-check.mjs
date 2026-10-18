import { spawnSync } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, realpathSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = realpathSync(path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..'));
const git = (...args) => {
    const result = spawnSync('git', args, { cwd: repo, encoding: 'utf8', windowsHide: true });
    if (result.status !== 0) throw new Error(result.stderr || `git ${args[0]} failed`);
    return result.stdout.trim();
};
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const common = path.resolve(repo, git('rev-parse', '--git-common-dir'));
const output = path.join(common, 'codex-coordination', 'self-check', `${Date.now()}-${randomUUID().slice(0, 8)}`);
mkdirSync(output, { recursive: true });
const report = {
    schemaVersion: 1, kind: 'orchestration-self-check', utc: new Date().toISOString(),
    head: git('rev-parse', 'HEAD'), branch: git('branch', '--show-current'),
    modelPolicy: 'inherit-parent', nativeToolCallsPerformedByThisScript: false,
    independentModelInferencePerformed: false, gameLaunchOrDeploymentPerformed: false,
    checks: [], tests: [], output,
};

function requireCheck(condition, description) {
    report.checks.push({ description, passed: Boolean(condition) });
    if (!condition) throw new Error(description);
}

try {
    const streams = JSON.parse(readFileSync(path.join(repo, 'tools/coordination/p2p-workstreams.json'), 'utf8'));
    const expected = ['authority', 'checkpoint', 'ai', 'pause', 'e2e', 'fault', 'review'];
    requireCheck(streams.modelPolicy === 'inherit-parent' && streams.maxWorkers === 3, 'Inherited model and three-worker limit');
    requireCheck(expected.every(id => streams.workstreams.filter(item => item.id === id).length === 1), 'All seven workstreams registered exactly once');
    git('merge-base', '--is-ancestor', streams.acceptedBaseline.commit, 'HEAD');
    requireCheck(existsSync(path.join(repo, streams.acceptedBaseline.report)), 'Accepted baseline report exists');
    report.acceptedEvidence = streams.acceptedBaseline.evidence.map(relative => {
        const bytes = readFileSync(path.join(repo, relative));
        const value = JSON.parse(bytes.toString('utf8').replace(/^\uFEFF/, ''));
        if (value.runId) {
            requireCheck(value.status.startsWith('passed'), `${relative}: accepted live result`);
            requireCheck(value.processes.length === 2 && new Set(value.processes.map(item => item.pid)).size === 2,
                `${relative}: two distinct player processes`);
            requireCheck(value.logChecks.length === 2 && value.logChecks.every(item =>
                item.realTickFailures === 0 && item.duplicateIds === 0 && item.aiBatchFailures === 0),
                `${relative}: recorded critical log checks are zero`);
            requireCheck(value.cleanup.length === 2 && value.cleanup.every(item =>
                item.cleanupComplete && !item.processAlive && !item.processTreeAlive), `${relative}: process cleanup recorded`);
        } else {
            requireCheck(value.coop.difference.length === 0 && value.rosterMovementE2E.difference.length === 0,
                `${relative}: failing-name comparison retained`);
        }
        return { path: relative, sha256: hash(bytes), runId: value.runId ?? null };
    });

    for (const file of ['orchestrator.test.mjs', 'resource-lock.test.mjs', 'codex-session.test.mjs']) {
        const testFile = path.join(repo, 'tools/coordination', file);
        requireCheck(existsSync(testFile), `${file}: implemented suite exists`);
        const result = spawnSync(process.execPath, ['--test', '--test-reporter=tap', testFile], {
            cwd: repo, encoding: 'utf8', windowsHide: true, timeout: 300_000,
            maxBuffer: 16 * 1024 * 1024,
        });
        const bytes = `${result.stdout ?? ''}${result.stderr ?? ''}`;
        const log = path.join(output, file.replace('.test.mjs', '.tap.log'));
        writeFileSync(log, bytes);
        const count = /# tests (\d+)/.exec(bytes)?.[1];
        const failures = /# fail (\d+)/.exec(bytes)?.[1];
        report.tests.push({ command: ['node', '--test', '--test-reporter=tap', path.relative(repo, testFile)],
            exitCode: result.status, signal: result.signal, tests: Number(count ?? 0), failures: Number(failures ?? -1),
            log, sha256: hash(bytes), error: result.error?.message ?? null });
        requireCheck(result.status === 0 && count && failures === '0', `${file}: real test process passed`);
    }
    const cli = spawnSync(process.platform === 'win32' ? 'codex.exe' : 'codex', ['--version'],
        { cwd: repo, encoding: 'utf8', windowsHide: true, timeout: 15_000 });
    report.codexCli = { exitCode: cli.status, version: cli.stdout?.trim() ?? '', error: cli.error?.message ?? null };
    requireCheck(cli.status === 0, 'Installed Codex CLI responds to a read-only version probe');
    report.status = 'passed';
} catch (error) {
    report.status = 'failed';
    report.error = error.message;
    process.exitCode = 1;
} finally {
    const destination = path.join(output, 'report.json');
    writeFileSync(destination, JSON.stringify(report, null, 2) + '\n');
    console.log(JSON.stringify({ status: report.status, report: destination, checks: report.checks.length,
        tests: report.tests.reduce((sum, item) => sum + item.tests, 0), error: report.error ?? null }));
}
