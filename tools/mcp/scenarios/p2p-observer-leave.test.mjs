import assert from 'node:assert/strict';
import test from 'node:test';
import { execFileSync } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { assertLeaveTransition, assertNativeMap, assertNoWorldEffect, assertStopped,
    assertTown, assertUiClosed, leaveApplications, OnceActions, options } from './p2p-observer-leave.mjs';
import { logDiagnostics } from './p2p-udp-reliability.mjs';

const clone = value => structuredClone(value);
const mapState = { missionActive: false, activeMenu: null, activeState: 'TaleWorlds.CampaignSystem.GameState.MapState' };
function player(controllerId) {
    return { controllerId, active: true, gold: 900, troops: 5, wounded: 0, heroId: 'h' + controllerId,
        clanId: 'c' + controllerId, characterId: 'ch' + controllerId, partyId: 'p' + controllerId,
        partyStringId: 'party' + controllerId, heroStringId: 'hero' + controllerId,
        settlementId: 'town_ES1', x: 10, y: 20, registeredPlayers: 2,
        heroRegistrations: 1, partyRegistrations: 1, clanRegistrations: 1 };
}
function snapshot() {
    const rows = [player('A'), player('B')];
    const world = { campaign: 'campaign', unregisteredParties: 0, partyIdentityHash: 'p', rosterCountsHash: 'r',
        rosterXpHash: 'xp', volunteerHash: 'v', heroGoldHash: 'g', nextHandle: 500, ticks: 1000 };
    return { worlds: [world, clone(world)], players: [rows, clone(rows)] };
}
function exited(before, controller = 'A') {
    const after = clone(before);
    for (const viewpoint of after.players) {
        const actor = viewpoint.find(row => row.controllerId === controller);
        actor.settlementId = null; actor.x = 11; actor.y = 22;
    }
    return after;
}

test('CLI accepts only explicit public SDK paths and rejects duplicate or missing options', () => {
    const argv = ['--repo', '.', '--server', 'server.exe', '--config', 'profiles.json', '--sdk-root', 'node_modules', '--record', 'new.jsonl'];
    assert.equal(options(argv).save, 'P1B_DirectIP_Recruit');
    assert.throws(() => options(argv.slice(0, -2)));
    assert.throws(() => options([...argv, '--private-pipe', 'name']));
    assert.throws(() => options([...argv, '--profile', 'local', '--profile', 'other']));
    assert.throws(() => options([...argv, '--record', 'another.jsonl']));
});

test('campaign readiness does not substitute for actual native MapState after tavern exit', () => {
    assertNativeMap({ instances: [{ name: 'client1', status: mapState }] }, 'client1');
    assert.throws(() => assertNativeMap({ instances: [{ name: 'client1', status: { ...mapState,
        readyForCampaignTests: true, missionActive: true, activeState: 'TaleWorlds.MountAndBlade.MissionState' } }] }, 'client1'));
    assert.throws(() => assertNativeMap({ instances: [] }, 'server'));
});

test('both native replicas must observe exactly one requested actor in the expected town', () => {
    const states = [{ players: [{ controller: 'A', currentSettlement: 'town_ES1' }] }];
    assertTown([states[0], clone(states[0])], 'A', 'town_ES1');
    assert.throws(() => assertTown([states[0], { players: [] }], 'A', 'town_ES1'));
    assert.throws(() => assertTown([states[0], { players: [{ controller: 'A', currentSettlement: null }] }], 'A', 'town_ES1'));
    assert.throws(() => assertTown([states[0], { players: [...states[0].players, ...states[0].players] }], 'A', 'town_ES1'));
});

test('owner encounter, native menu and rendered overlay must all close without repair commands', () => {
    const evidence = 'menuView=False pendingMenuView=False encounterOverlay=False cameraMode=FollowParty';
    const movement = 'position=1,2|target=1,2|behavior=Hold|settlement=none|encounter=none|time=Pause';
    assertUiClosed(evidence, movement, mapState);
    for (const key of ['menuView', 'pendingMenuView', 'encounterOverlay'])
        assert.throws(() => assertUiClosed(evidence.replace(key + '=False', key + '=True'), movement, mapState));
    assert.throws(() => assertUiClosed(evidence, movement.replace('encounter=none', 'encounter=town_ES1'), mapState));
    assert.throws(() => assertUiClosed(evidence, movement, { ...mapState, activeMenu: 'town' }));
    const missing = clone(mapState); delete missing.activeMenu;
    assert.throws(() => assertUiClosed(evidence, movement, missing));
});

test('observer leave permits only its own settlement and position change, on both replicas', () => {
    const before = snapshot(), after = exited(before);
    assertLeaveTransition(before, after, 'A');
    for (const key of ['gold', 'troops', 'wounded', 'heroId', 'clanId', 'characterId', 'partyId', 'partyStringId',
        'heroStringId', 'registeredPlayers', 'heroRegistrations', 'partyRegistrations', 'clanRegistrations']) {
        const bad = clone(after); for (const rows of bad.players) rows[0][key] = 'changed';
        assert.throws(() => assertLeaveTransition(before, bad, 'A'), key);
    }
    const otherMoved = clone(after); for (const rows of otherMoved.players) rows[1].x++;
    assert.throws(() => assertLeaveTransition(before, otherMoved, 'A'));
    const lostTown = clone(before); for (const rows of lostTown.players) rows[0].settlementId = null;
    assert.throws(() => assertLeaveTransition(lostTown, after, 'A'));
});

test('leave cannot change XP, troop counts, world gold, identity or paused clock', () => {
    const before = snapshot(), after = exited(before);
    for (const key of ['partyIdentityHash', 'rosterCountsHash', 'rosterXpHash', 'volunteerHash', 'heroGoldHash', 'ticks']) {
        const bad = clone(after); for (const world of bad.worlds) world[key] = 'changed';
        assert.throws(() => assertLeaveTransition(before, bad, 'A'), key);
    }
    const backwards = clone(after); for (const world of backwards.worlds) world.nextHandle--;
    assert.throws(() => assertLeaveTransition(before, backwards, 'A'));
});

test('already-outside repeat has no critical effect, while permitted local presentation differences are ignored', () => {
    const before = exited(snapshot()), after = clone(before);
    after.players[0][0].visual = true; after.players[1][0].items = [{ item: 'local-shop-item' }];
    assertNoWorldEffect(before, after);
    const bad = clone(after); for (const rows of bad.players) rows[0].gold--;
    assert.throws(() => assertNoWorldEffect(before, bad));
});

test('native leave submission cannot repeat even when the first transport outcome is uncertain', async () => {
    const once = new OnceActions(); let submits = 0;
    await assert.rejects(once.run('A-observer', async () => { submits++; throw new Error('response lost'); }));
    await assert.rejects(once.run('A-observer', async () => { submits++; }));
    assert.equal(submits, 1);
    await once.run('B-observer', async () => { submits++; });
    assert.equal(submits, 2);
});

test('all actual Serilog error/fatal formats fail the imported diagnostic gate', () => {
    const value = logDiagnostics('[13:01:02.003 ERR Mod] unexpected\n[13:01:03 FTL] fatal\n[13:01:04 INF] error word in normal text');
    assert.equal(value.errorLines.length, 2);
    assert.equal(logDiagnostics('[13:01:04 INF] normal').errorLines.length, 0);
});

test('cleanup requires both native process trees gone rather than just an SDK stop response', () => {
    const stopped = { state: 'stopped', instances: ['server', 'client1'].map(name => ({ name,
        cleanupComplete: true, processAlive: false, processTreeAlive: false })) };
    assertStopped(stopped, 2);
    assert.throws(() => assertStopped({ ...stopped, instances: [stopped.instances[0]] }, 2));
    assert.throws(() => assertStopped({ ...stopped, instances: [{ ...stopped.instances[0], processTreeAlive: true }, stopped.instances[1]] }, 2));
});

test('help is offline and source retains public SDK, raw calls, one-shot mutations and owned cleanup', async () => {
    const output = execFileSync(process.execPath, ['tools/mcp/scenarios/p2p-observer-leave.mjs', '--help'], { encoding: 'utf8' });
    assert.match(output, /not ordinary travel\/entry/);
    const source = await readFile(new URL('./p2p-observer-leave.mjs', import.meta.url), 'utf8');
    assert.match(source, /phase: 'request'/); assert.match(source, /phase: 'response'/);
    assert.match(source, /@modelcontextprotocol\/sdk/); assert.match(source, /'stop_run'/);
    assert.doesNotMatch(source, /coop\.debug\.ui\.(?:close_screen|prepare_evidence_map|leave_settlement_encounter)/);
    assert.doesNotMatch(source, /coop\.debug\.(?:teleport|set_gold)/);
});

test('native application ledger accepts real Serilog string rendering and retains duplicate applications', () => {
    const rows = leaveApplications('[12:30:00 INF] Settlement leave applied controller="LocalA" epoch=7 sequence=1 party=42\n' +
        '[12:30:01 INF] Settlement leave applied controller=testclient1 epoch=9 sequence=2 party=43\n' +
        '[12:30:02 INF] Settlement leave rejected reason=duplicate_or_reordered_sequence controller="LocalA" epoch=7 sequence=1');
    assert.deepEqual(rows, [{controller:'LocalA',epoch:7,sequence:1,party:42},{controller:'testclient1',epoch:9,sequence:2,party:43}]);
    assert.equal(leaveApplications('Settlement leave applied controller="LocalA" epoch=7 sequence=1 party=42\n'.repeat(2)).length,2);
});
