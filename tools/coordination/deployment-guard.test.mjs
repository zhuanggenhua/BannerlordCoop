import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, existsSync, realpathSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const targets = readFileSync(fileURLToPath(new URL('../../Deploy.targets', import.meta.url)), 'utf8');
function fixture(t, gitKind, withGame = true) {
    const root = realpathSync(mkdtempSync(path.join(tmpdir(), 'coop-deploy-guard-')));
    t.after(() => {
        const parent = realpathSync(tmpdir());
        const relative = path.relative(parent, realpathSync(root));
        assert.ok(relative && !relative.startsWith('..') && !path.isAbsolute(relative));
        rmSync(root, { recursive: true });
    });
    if (gitKind === 'directory') mkdirSync(path.join(root, '.git'));
    else writeFileSync(path.join(root, '.git'), 'gitdir: fixture-only\n');
    for (const directory of ['deploy', 'output', ...(withGame ? ['mb2/Modules'] : [])]) {
        mkdirSync(path.join(root, directory), { recursive: true });
    }
    writeFileSync(path.join(root, 'Deploy.targets'), targets);
    writeFileSync(path.join(root, 'output', 'fixture.dll'), 'temporary test bytes, not a game assembly');
    writeFileSync(path.join(root, 'deploy', 'SubModule.xml'), '<Module><Name>${name}</Name></Module>');
    writeFileSync(path.join(root, 'probe.proj'), `<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
      <PropertyGroup><ModName>Fixture</ModName><TargetDir>$(MSBuildThisFileDirectory)output/</TargetDir>
      <MainClass>Fixture</MainClass><ModVersion>v1.0.0</ModVersion><GameVersion>v1.4.8</GameVersion></PropertyGroup>
      <Target Name="Build"/><Import Project="Deploy.targets"/></Project>`);
    return root;
}
function build(root, args = []) {
    const result = spawnSync('dotnet', ['msbuild', 'probe.proj', '/t:Build', '/nologo', '/v:q', ...args],
        { cwd: root, encoding: 'utf8', windowsHide: true, timeout: 30_000 });
    assert.equal(result.status, 0, `${result.error?.message ?? ''}\n${result.stdout}\n${result.stderr}`);
    return existsSync(path.join(root, 'mb2/Modules/Fixture/bin/Win64_Shipping_Client/fixture.dll'));
}

test('linked worker build cannot deploy even with a game Modules directory', t => {
    assert.equal(build(fixture(t, 'file')), false);
});
test('main checkout retains its existing explicit build deployment behavior in a temporary fake install', t => {
    assert.equal(build(fixture(t, 'directory')), true);
});
test('empty global ModName still disables deployment for a main checkout', t => {
    assert.equal(build(fixture(t, 'directory'), ['/p:ModName=']), false);
});
test('missing game Modules still skips deployment', t => {
    assert.equal(build(fixture(t, 'directory', false)), false);
});
