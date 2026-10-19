import assert from 'node:assert/strict';
import test from 'node:test';
import { execFileSync } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { allocationCheckpoint, assertAllocationCheckpointFloor, assertPausedSnapshot, assertSaleAllocationTransition,
    assertSaleApplications, assertSaleRegistrations, assertSaleTransition,
    assertNoPollingMutation, assertStopped, awaitAllPreservingFirstFailure, createReadDeadlineScope,
    nextSettlementPreparationAction, options, pollReadOnly, saleApplications, OnceActions } from './p2p-prisoner-sale.mjs';

const clone = value => structuredClone(value);
const ids = [
    { controller: 'A', mainHero: 'hero-A', mainParty: 'party-A' },
    { controller: 'B', mainHero: 'hero-B', mainParty: 'party-B' },
];
const lease = { authorityControllerId: 'A', authorityId: 'world-3', roomId: 'room', epoch: 3, isPaused: true };
function state(controller, actor, writerController = 'A') {
    return { controller, mainHero: actor.mainHero, mainParty: actor.mainParty, roomHost: 'server',
        lease: { ...lease, authorityControllerId: writerController },
        localAuthority: actor.controller === writerController, frozen: false };
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
        allocationCounters: {
            'TaleWorlds.CampaignSystem.Roster.TroopRoster, TaleWorlds.CampaignSystem': 4185,
            'TaleWorlds.CampaignSystem.Roster.TroopRosterElement, TaleWorlds.CampaignSystem': 11,
        }, allocationCounterHash: 'allocations', unregisteredParties: 0, ...overrides };
}
function snapshots(writerController = 'A') {
    const fixture = [{ character: 'imperial_infantryman', number: 2, woundedNumber: 1, xp: 137, isHero: false }];
    const beforePlayers = [player('A', 1000), player('B', 700)];
    const before = { states: [state('server', ids[0], writerController), state('client1', ids[1], writerController)], worlds: [world(), world()],
        players: [clone(beforePlayers), clone(beforePlayers)],
        actors: [[actor('A', 1000, fixture), actor('B', 700, [])], [actor('A', 1000, fixture), actor('B', 700, [])]] };
    const afterPlayers = [player('A', 1120), player('B', 700)];
    const writerCounters = { ...before.worlds[0].allocationCounters,
        'TaleWorlds.CampaignSystem.Roster.TroopRoster, TaleWorlds.CampaignSystem': 4193 };
    const replicaCounters = { ...before.worlds[1].allocationCounters,
        'TaleWorlds.CampaignSystem.Roster.TroopRoster, TaleWorlds.CampaignSystem': 4189 };
    const afterWorld = (allocationCounters, allocationCounterHash) => world({ rosterCountsHash: 'counts-2', rosterXpHash: 'xp-2',
        heroGoldHash: 'gold-2', nextHandle: 504, allocationCounters, allocationCounterHash });
    const after = { states: clone(before.states), worlds: writerController === 'A' ?
            [afterWorld(writerCounters, 'writer-counter-hash'), afterWorld(replicaCounters, 'replica-counter-hash')] :
            [afterWorld(replicaCounters, 'replica-counter-hash'), afterWorld(writerCounters, 'writer-counter-hash')],
        players: [clone(afterPlayers), clone(afterPlayers)],
        actors: [[actor('A', 1120, []), actor('B', 700, [])], [actor('A', 1120, []), actor('B', 700, [])]] };
    return { before, after };
}

test('native Pause is independent of a live, unique, unfrozen authority lease', () => {
    const activeLease = { ...lease, authorityControllerId: 'server', isPaused: false };
    const states = [state('server', ids[0]), state('client1', ids[1])]
        .map((value, index) => ({ ...value, lease: activeLease, localAuthority: index === 0 }));
    const actors = [{ ...ids[0], controller: 'server' }, { ...ids[1], controller: 'client1' }];
    const worlds = [world(), world()];
    assert.equal(assertPausedSnapshot(['Pause', 'Pause'], states, actors, worlds, clone(worlds)), true);
    assert.throws(() => assertPausedSnapshot(['Pause', 'Pause'], states, actors, worlds,
        [world({ ticks: 601 }), world({ ticks: 601 })]), /World changed while campaign time is paused/);
    assert.throws(() => assertPausedSnapshot(['Play_1x', 'Pause'], states, actors, worlds, clone(worlds)), /Pause/);

    const leasePausedStates = clone(states);
    for (const value of leasePausedStates) value.lease = { ...value.lease, isPaused: true };
    assert.throws(() => assertPausedSnapshot(['Pause', 'Pause'], leasePausedStates, actors, worlds, clone(worlds)),
        /retain an active authority lease/);

    const frozenStates = clone(states); frozenStates[1].frozen = true;
    assert.throws(() => assertPausedSnapshot(['Pause', 'Pause'], frozenStates, actors, worlds, clone(worlds)),
        /must not freeze the authority world/);

    const doubleWriterStates = clone(states); doubleWriterStates[1].localAuthority = true;
    assert.throws(() => assertPausedSnapshot(['Pause', 'Pause'], doubleWriterStates, actors, worlds, clone(worlds)),
        error => error.name === 'AssertionError');
});

test('registration polling cannot pass while the joining actor is inactive', async () => {
    const complete = [player('A', 1000), player('B', 700)];
    const pendingB = { ...player('B', 700), active: false };
    const pending = [clone(complete), [clone(complete[0]), clone(pendingB)]];
    const ready = [clone(complete), clone(complete)];
    let now = 0, reads = 0;
    const result = await pollReadOnly('registered actors', async () => {
        const pair = reads++ === 0 ? pending : ready;
        assertSaleRegistrations(pair, ids);
        return pair;
    }, { timeoutMs: 25, intervalMs: 5, now: () => now, sleep: async milliseconds => { now += milliseconds; } });
    assert.equal(reads, 2);
    assert.deepEqual(result, ready);
});

test('concurrent read deadlines stay isolated through either completion order and leave outer writes enabled', async () => {
    for (const order of [['A', 'B'], ['B', 'A']]) {
        const scope = createReadDeadlineScope();
        const baseDeadline = Date.now() + 60000;
        const deadlines = { A: baseDeadline + 100, B: baseDeadline + 200 };
        const releases = {}, gates = {};
        for (const name of ['A', 'B']) gates[name] = new Promise(resolve => { releases[name] = resolve; });
        const started = [];
        const tasks = Object.fromEntries(['A', 'B'].map(name => [name, scope.run(deadlines[name], async () => {
            started.push(name);
            await gates[name];
            return scope.currentDeadline();
        })]));
        await Promise.resolve();
        assert.deepEqual(started, ['A', 'B']);
        for (const name of order) {
            releases[name]();
            assert.equal(await tasks[name], deadlines[name]);
            assert.equal(scope.currentDeadline(), undefined);
        }
        let writes = 0;
        assertNoPollingMutation(scope.currentDeadline(), true);
        writes++;
        assert.equal(writes, 1, 'A successful concurrent poll must not leave a stale read context around writes');
    }
});

test('nested deadlines inherit the earlier deadline and throwing callbacks restore their parent context', async () => {
    const scope = createReadDeadlineScope();
    const early = Date.now() + 100;
    const late = early + 1000;
    const failure = new Error('nested read failed');
    await scope.run(late, async () => {
        assert.equal(scope.currentDeadline(), late);
        await scope.run(early, async () => { assert.equal(scope.currentDeadline(), early); });
    });
    await scope.run(early, async () => {
        assert.equal(scope.currentDeadline(), early);
        await assert.rejects(scope.run(late, async () => {
            assert.equal(scope.currentDeadline(), early);
            throw failure;
        }), error => error === failure);
        assert.equal(scope.currentDeadline(), early);
    });
    assert.equal(scope.currentDeadline(), undefined);
});

test('polling mutation guards abort immediately without dispatch or retry', async () => {
    const scope = createReadDeadlineScope();
    let reads = 0, writes = 0, pending = 0;
    await assert.rejects(scope.run(Date.now() + 30000, () => pollReadOnly('mutation guard', async () => {
        reads++;
        assertNoPollingMutation(scope.currentDeadline(), true);
        writes++;
    }, { timeoutMs: 30000, onPending: async () => { pending++; } })), error =>
        error.code === 'ERR_POLLING_MUTATION' && /Mutations cannot run inside a polling callback/.test(error.message));
    assert.equal(reads, 1);
    assert.equal(writes, 0);
    assert.equal(pending, 0);
    assert.equal(scope.currentDeadline(), undefined);
});

test('parallel settlement preparation waits for every poll and preserves the first actual rejection', async () => {
    const failureB = new Error('B settlement read failed first');
    const failureA = new Error('A settlement read failed later');
    let rejectA, rejectB, completed = false;
    const pendingA = new Promise((_, reject) => { rejectA = reject; });
    const pendingB = new Promise((_, reject) => { rejectB = reject; });
    const result = awaitAllPreservingFirstFailure([pendingA, pendingB]).then(
        () => { completed = true; return null; }, error => { completed = true; return error; });
    rejectB(failureB);
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(completed, false, 'Preparation must wait while its other poll is still active');
    rejectA(failureA);
    assert.equal(await result, failureB);
});

test('an existing town menu adapts once instead of entering the tavern again', () => {
    for (const { stage, inspection, expected } of [
        { stage: 'initial inspect', inspection: { settlement: 'town_ES1', currentMenu: 'town' }, expected: 'open-backstreet' },
        { stage: 'first enter inspect', inspection: { settlement: 'town_ES1', currentMenu: 'town' }, expected: 'open-backstreet' },
        { stage: 'already in backstreet', inspection: { settlement: 'town_ES1', currentMenu: 'town_backstreet' }, expected: 'ready' },
        { stage: 'not yet in target settlement', inspection: { settlement: 'map', currentMenu: 'map' }, expected: 'enter' },
    ]) assert.equal(nextSettlementPreparationAction(inspection), expected, stage);
});

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

test('native dummy-roster allocation deltas are accepted for either current writer', () => {
    for (const writerController of ['A', 'B']) {
        const { before, after } = snapshots(writerController);
        const intent = { originController: 'A', writerController, epoch: 3, authorityId: 'world-3',
            sellingPartyId: 101, expectedRansomTotal: 120 };
        assert.deepEqual(assertSaleTransition(before, after, intent),
            { amount: 120, controller: 'A', writer: writerController, epoch: 3, partyHandle: 101 });
        assert.notEqual(after.worlds[0].allocationCounterHash, after.worlds[1].allocationCounterHash,
            'Different role-local type-counter hashes are expected after native dummy allocation');
    }
});

test('sale allocation oracle rejects handle drift, rollback, wrong type deltas and changed counter keys', () => {
    const { before, after } = snapshots();
    const rosterType = Object.keys(before.worlds[0].allocationCounters)
        .find(type => type.startsWith('TaleWorlds.CampaignSystem.Roster.TroopRoster,'));
    const nonRosterType = Object.keys(before.worlds[0].allocationCounters).find(type => type !== rosterType);
    const unequalHandles = clone(after.worlds); unequalHandles[1].nextHandle++;
    assert.throws(() => assertSaleAllocationTransition(before.worlds, unequalHandles, 0), /World mismatch: nextHandle/);

    const rollback = clone(after.worlds); for (const value of rollback) value.nextHandle = 499;
    assert.throws(() => assertSaleAllocationTransition(before.worlds, rollback, 0), /increase monotonically/);
    const unchangedHandle = clone(after.worlds); for (const value of unchangedHandle) value.nextHandle = 500;
    assert.throws(() => assertSaleAllocationTransition(before.worlds, unchangedHandle, 0), /increase monotonically/);

    const wrongWriterDelta = clone(after.worlds); wrongWriterDelta[0].allocationCounters[rosterType]--;
    assert.throws(() => assertSaleAllocationTransition(before.worlds, wrongWriterDelta, 0), /Unexpected TroopRoster allocation delta on writer/);
    const wrongReplicaDelta = clone(after.worlds); wrongReplicaDelta[1].allocationCounters[rosterType]++;
    assert.throws(() => assertSaleAllocationTransition(before.worlds, wrongReplicaDelta, 0), /Unexpected TroopRoster allocation delta on replica/);
    const otherTypeChanged = clone(after.worlds); otherTypeChanged[0].allocationCounters[nonRosterType]++;
    assert.throws(() => assertSaleAllocationTransition(before.worlds, otherTypeChanged, 0), /non-TroopRoster allocation counter/);
    const keyChanged = clone(after.worlds); delete keyChanged[0].allocationCounters[nonRosterType];
    keyChanged[0].allocationCounters['Example.UnexpectedType, Example'] = 11;
    assert.throws(() => assertSaleAllocationTransition(before.worlds, keyChanged, 0), /changed allocation counter type keys/);
});

test('handoff peers must retain the previous writer checkpoint while allowing Max-merged values', () => {
    const { before, after } = snapshots();
    const previousWriterCheckpoint = allocationCheckpoint(before.worlds[0]);
    assert.doesNotThrow(() => assertAllocationCheckpointFloor(after.worlds, previousWriterCheckpoint));

    const lostHandle = clone(after.worlds); for (const value of lostHandle) value.nextHandle = 499;
    assert.throws(() => assertAllocationCheckpointFloor(lostHandle, previousWriterCheckpoint), /nextHandle fell below/);
    const lostCounter = clone(after.worlds); lostCounter[1].allocationCounters[
        'TaleWorlds.CampaignSystem.Roster.TroopRoster, TaleWorlds.CampaignSystem'] = 4184;
    assert.throws(() => assertAllocationCheckpointFloor(lostCounter, previousWriterCheckpoint), /counter fell below/);
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
    assert.match(source, /coop\.debug\.prisoner_sale\.open_backstreet/);
    assert.match(source, /coop\.debug\.get_time_mode/);
    assert.match(source, /application record/);
    assert.match(source, /'stop_run'/);
    assert.doesNotMatch(source, /PrisonerSaleProcessor|processor\.Sell|coop\.debug\.(?:teleport|set_gold)/);
    assert.match(source, /@modelcontextprotocol\/sdk/);
    assert.match(source, /nativeMap\(instance\)/);
});
