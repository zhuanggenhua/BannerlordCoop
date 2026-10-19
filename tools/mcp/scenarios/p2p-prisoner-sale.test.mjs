import assert from 'node:assert/strict';
import test from 'node:test';
import { execFileSync } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { assertSaleApplications, assertSaleTransition, assertStopped, options, saleApplications, OnceActions } from './p2p-prisoner-sale.mjs';

const clone = value => structuredClone(value);
const ids = [
    { controller: 'A', mainHero: 'hero-A', mainParty: 'party-A' },
    { controller: 'B', mainHero: 'hero-B', mainParty: 'party-B' },
];
const lease = { authorityControllerId: 'A', authorityId: 'world-3', roomId: 'room', epoch: 3, isPaused: true };
function state(controller, actor) {
    return { controller, mainHero: actor.mainHero, mainParty: actor.mainParty, roomHost: 'server', lease,
        localAuthority: controller === 'A', frozen: false };
}
function player(controller, gold) {
    const actor = ids.find(row => row.controller === controller);
    return { controllerId: controller, heroId: actor.mainHero, heroStringId: actor.mainHero, partyId: actor.mainParty,
        partyStringId: actor.mainParty, clanId: 'clan-' + controller, characterId: 'char-' + controller,
        gold, troops: 9, wounded: 1, settlementId: 'town_ES1', x: 1, y: 2, registeredPlayers: 2,
        heroRegistrations: 1, partyRegistrations: 1, clanRegistrations: 1, active: true };
}
function actor(controller, gold, prisonRoster) {
    const current = ids.find(row => row.controller === controller);
    return { targetController: controller, targetHeroId: current.mainHero, targetPartyId: current.mainParty,
        targetPartyHandle: controller === 'A' ? 101 : 202, settlement: 'town_ES1', gold,
        ransomRecipientHeroId: current.mainHero, ransomRecipientGold: gold,
        expectedRansomTotal: prisonRoster.length ? 120 : 0,
        memberRoster: [{ character: 'sturgian_recruit', number: 9, woundedNumber: 1, xp: 37, isHero: false }],
        prisonRoster };
}
function world(overrides = {}) {
    return { campaign: 'campaign', ticks: 600, partyIdentityHash: 'party-id', rosterCountsHash: 'counts-1',
        rosterXpHash: 'xp-1', volunteerHash: 'volunteers', heroGoldHash: 'gold-1', nextHandle: 500,
        allocationCounterHash: 'allocations', unregisteredParties: 0, ...overrides };
}
function snapshots() {
    const fixture = [{ character: 'imperial_infantryman', number: 2, woundedNumber: 1, xp: 137, isHero: false }];
    const beforePlayers = [player('A', 1000), player('B', 700)];
    const before = { states: [state('server', ids[0]), state('client1', ids[1])], worlds: [world(), world()],
        players: [clone(beforePlayers), clone(beforePlayers)],
        actors: [[actor('A', 1000, fixture), actor('B', 700, [])], [actor('A', 1000, fixture), actor('B', 700, [])]] };
    const afterPlayers = [player('A', 1120), player('B', 700)];
    const after = { states: clone(before.states), worlds: [world({ rosterCountsHash: 'counts-2', rosterXpHash: 'xp-2', heroGoldHash: 'gold-2' }),
            world({ rosterCountsHash: 'counts-2', rosterXpHash: 'xp-2', heroGoldHash: 'gold-2' })],
        players: [clone(afterPlayers), clone(afterPlayers)],
        actors: [[actor('A', 1120, []), actor('B', 700, [])], [actor('A', 1120, []), actor('B', 700, [])]] };
    return { before, after };
}
function appLine({ origin = 'A', writer = 'A', epoch = 3, authority = 'world-3', stream = 'a'.repeat(32), sequence = 1, party = 101,
    time = '12:00:00.000' } = {}) {
    return `[${time} INF] Prisoner sale applied originController="${origin}" writerController="${writer}" epoch=${epoch} authorityId=${authority} requestStream=${stream} requestSequence=${sequence} sellingPartyId=${party}`;
}

test('CLI parses only explicit public scenario arguments and help remains offline', () => {
    const argv = ['--repo', '.', '--server', 'server.exe', '--config', 'profiles.json', '--sdk-root', 'node_modules', '--record', 'new.jsonl'];
    assert.equal(options(argv).save, 'P1B_DirectIP_Recruit');
    assert.throws(() => options(argv.slice(0, -2)));
    assert.throws(() => options([...argv, '--proxy', '24200']));
    assert.throws(() => options([...argv, '--profile', 'one', '--profile', 'two']));
    const help = execFileSync(process.execPath, ['tools/mcp/scenarios/p2p-prisoner-sale.mjs', '--help'], { encoding: 'utf8' });
    assert.match(help, /real player sale-prefix requests/); assert.match(help, /player-hero ransom/);
});

test('application ledger deduplicates exact archived copies but preserves repeated native applications', () => {
    const line = appLine();
    assert.equal(saleApplications([line, line]).length, 1);
    assert.equal(saleApplications([line + '\n' + line]).length, 2);
    assert.equal(saleApplications([line, appLine({ time: '12:00:01.000' })]).length, 2);
    const intent = { originController: 'A', writerController: 'A', epoch: 3, authorityId: 'world-3', sellingPartyId: 101 };
    assert.equal(assertSaleApplications([line, line], [intent]).length, 1);
    assert.throws(() => assertSaleApplications([line, appLine({ time: '12:00:01.000' })], [intent]), /intent count/);
    assert.throws(() => assertSaleApplications([appLine({ sequence: 0 })], [intent]), /request sequence/);
    assert.throws(() => assertSaleApplications([appLine({ stream: 'bad' })], [intent]), /request stream/);
    assert.throws(() => assertSaleApplications([line], [{ ...intent, writerController: 'B' }]), /exactly one/);
});

test('completed sale credits the originating actor only and removes exact count, wound and XP state', () => {
    const { before, after } = snapshots();
    const intent = { originController: 'A', writerController: 'A', epoch: 3, authorityId: 'world-3',
        sellingPartyId: 101, expectedRansomTotal: 120 };
    assert.deepEqual(assertSaleTransition(before, after, intent),
        { amount: 120, controller: 'A', writer: 'A', epoch: 3, partyHandle: 101 });
});

test('sale assertions reject wrong-actor gold, duplicate roster changes, tick drift and hash divergence', () => {
    const { before, after } = snapshots();
    const intent = { originController: 'A', writerController: 'A', epoch: 3, authorityId: 'world-3',
        sellingPartyId: 101, expectedRansomTotal: 120 };
    const writerGotGold = clone(after);
    for (const viewpoint of writerGotGold.players) viewpoint.find(row => row.controllerId === 'B').gold += 120;
    for (const viewpoint of writerGotGold.actors) viewpoint.find(row => row.targetController === 'B').gold += 120;
    for (const viewpoint of writerGotGold.actors) viewpoint.find(row => row.targetController === 'B').ransomRecipientGold += 120;
    assert.throws(() => assertSaleTransition(before, writerGotGold, intent), /wrong actor|wrong hero/);

    const retainedXp = clone(after);
    for (const viewpoint of retainedXp.actors) viewpoint.find(row => row.targetController === 'A').prisonRoster =
        [{ character: 'imperial_infantryman', number: 1, woundedNumber: 0, xp: 137, isHero: false }];
    assert.throws(() => assertSaleTransition(before, retainedXp, intent), /clear|counts|XP/);

    const ticked = clone(after); for (const value of ticked.worlds) value.ticks--;
    assert.throws(() => assertSaleTransition(before, ticked, intent), /ticks/);
    const divergent = clone(after); divergent.worlds[1].rosterCountsHash = 'different';
    assert.throws(() => assertSaleTransition(before, divergent, intent), /World mismatch/);
});

test('each sale intent is dispatched at most once after an uncertain response', async () => {
    const once = new OnceActions(); let calls = 0;
    await assert.rejects(once.run('sale-A', async () => { calls++; throw new Error('response lost'); }));
    await assert.rejects(once.run('sale-A', async () => { calls++; }));
    await once.run('sale-B', async () => { calls++; });
    assert.equal(calls, 2);
});

test('cleanup means both owned native process trees have exited', () => {
    const stopped = { state: 'stopped', instances: ['server', 'client1'].map(name => ({ name,
        cleanupComplete: true, processAlive: false, processTreeAlive: false })) };
    assertStopped(stopped, 2);
    assert.throws(() => assertStopped({ ...stopped, instances: [stopped.instances[0]] }, 2));
    assert.throws(() => assertStopped({ ...stopped, instances: [{ ...stopped.instances[0], processTreeAlive: true }, stopped.instances[1]] }));
});

test('scenario uses the production sale prefix, exact application ledger, one-shot mutations, and strict cleanup', async () => {
    const source = await readFile(new URL('./p2p-prisoner-sale.mjs', import.meta.url), 'utf8');
    assert.match(source, /coop\.debug\.prisoner_sale\.submit/);
    assert.match(source, /application record/);
    assert.match(source, /'stop_run'/);
    assert.doesNotMatch(source, /PrisonerSaleProcessor|processor\.Sell|coop\.debug\.(?:teleport|set_gold)/);
    assert.match(source, /@modelcontextprotocol\/sdk/);
    assert.match(source, /nativeMap\(instance\)/);
});
