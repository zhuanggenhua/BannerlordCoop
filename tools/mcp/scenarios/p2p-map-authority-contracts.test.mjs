import assert from 'node:assert/strict';
import test from 'node:test';
import { capturePredictedMapState, exerciseMapAuthorityContracts, assertMapAiObservation, assertMapAiProgress } from './p2p-map-authority-contracts.mjs';

const aiClasses = ['writerNpc','writerPlayer','observerLocalNavigation','receiveApply','unqualified','unknown'];
function aiReceipt(peer) {
    const simulation = { 'MobilePartyAi.Tick.observed': 0, 'MobilePartyAi.Tick.beforeOriginal.failed': 0 };
    for (const outcome of ['completed','failed','skipped']) {
        simulation['MobilePartyAi.Tick.' + outcome] = 0;
        for (const kind of [...aiClasses, 'unexpectedWorld']) simulation['MobilePartyAi.Tick.' + kind + '.' + outcome] = 0;
    }
    return { campaign: 'world', instance: 'cycles-' + peer, processId: 100 + peer, overflowed: false,
        simulation, aiSimulation: { schemaVersion: 1, classes: [...aiClasses], firstEntries: [] } };
}
function addAi(receipt, kind, outcome, count = 1) {
    receipt.simulation['MobilePartyAi.Tick.' + kind + '.' + outcome] += count;
    receipt.simulation['MobilePartyAi.Tick.' + outcome] += count;
    receipt.simulation['MobilePartyAi.Tick.observed'] += count;
}

function fixture() {
    const initial = ['a', 'b'].map(controller => ({ controller, mainHero: 'hero-' + controller, mainParty: 'party-' + controller }));
    const states = initial.map(actor => ({ ...actor, roomHost: 'a', frozen: false, localAuthority: actor.controller === 'a',
        lease: { roomId: 'room', authorityId: 'writer', authorityControllerId: 'a', epoch: 3, isPaused: false } }));
    const world = { campaign: 'world', unregisteredParties: 0, partyIdentityHash: 'ids', rosterCountsHash: 'counts',
        rosterXpHash: 'xp', volunteerHash: 'recruitment', heroGoldHash: 'gold', nextHandle: 500, ticks: 12345 };
    const worlds = [structuredClone(world), structuredClone(world)];
    const players = [0, 1].map(peer => initial.map((actor, index) => ({ controllerId: actor.controller, gold: 1000,
        troops: 4, wounded: 0, heroId: 'hero-id-' + index, clanId: 'clan-' + index, characterId: 'character-' + index,
        partyId: 'party-id-' + index, partyStringId: actor.mainParty, heroStringId: actor.mainHero, settlementId: null,
        registeredPlayers: 2, heroRegistrations: 1, partyRegistrations: 1, clanRegistrations: 1, active: true,
        x: 650 + index + (peer ? 0.824939 : 0), y: 278 + index })));
    const mutations = [];
    return { worlds, players, mutations, context: { initial, bothStates: async () => states,
        poll: async (_label, read) => read(), setTime: async mode => mutations.push(mode),
        command: async (instance, name, args = []) => {
            const peer = instance === 'server' ? 0 : 1;
            if (name === 'coop.debug.authority.world_state') return JSON.stringify(worlds[peer]);
            assert.equal(name, 'coop.debug.players.party_state');
            return JSON.stringify(players[peer].find(row => row.controllerId === args[0]));
        } } };
}

test('pre-handoff observation retains raw predicted positions without dispatching a handoff', async () => {
    const f = fixture();
    const captured = await capturePredictedMapState(f.context, 'before');
    assert.notEqual(captured.players[0][0].x, captured.players[1][0].x);
    assert.deepEqual(captured.players, f.players);
    assert.deepEqual(f.mutations, ['Pause']);
});

test('prediction-compatible observation still rejects XP, resources, IDs and registration differences', async () => {
    for (const corrupt of [f => f.worlds[1].rosterXpHash = 'wrong', f => f.worlds[1].nextHandle++,
        f => f.worlds[1].ticks++, f => f.players[1][0].gold++, f => f.players[1][0].troops++,
        f => f.players[1][0].partyId = 'wrong', f => f.players[1][0].heroRegistrations++]) {
        const f = fixture(); corrupt(f);
        await assert.rejects(() => capturePredictedMapState(f.context, 'before'));
    }
});

function rehearsal(corrupt = () => {}) {
    const f = fixture(), actors = f.context.initial;
    const states = actors.map(actor => ({ ...actor, roomHost: 'a', frozen: false, yielded: false,
        worldTicks: 20, hours: 24, localAuthority: actor.controller === 'a',
        lease: { roomId: 'room', authorityId: 'writer3', authorityControllerId: 'a', epoch: 3, isPaused: false } }));
    f.players[1] = structuredClone(f.players[0]);
    const features = [], events = [], commands = [], cycles = [aiReceipt(0), aiReceipt(1)], trace = [];
    let movementCalls = 0;
    const context = { initial: actors, bothStates: async () => structuredClone(states),
        poll: async (label, read) => { corrupt(label, { states, worlds: f.worlds, players: f.players }); return read(); },
        setTime: async mode => {
            trace.push(mode);
            if (mode !== 'Play_1x') return;
            addAi(cycles[states.findIndex(state => state.localAuthority)], 'writerNpc', 'completed', 6);
            states.find(state => state.localAuthority).worldTicks++;
            states.forEach(state => state.hours++); f.worlds.forEach(world => world.ticks++);
        },
        feature: async (name, action) => features.push({ name, evidence: await action() }),
        emit: async event => events.push(event),
        command: async (instance, name, args = [], mutation = false) => {
            commands.push({ instance, name, args, mutation });
            trace.push(name + ':' + instance);
            const peer = instance === 'server' ? 0 : 1;
            if (name === 'coop.debug.authority.cycles') {
                assert.equal(mutation, false); assert.deepEqual(args, ['0', 'cycles']);
                const receipt = structuredClone(cycles[peer]); corrupt('cycles-' + peer, receipt);
                return JSON.stringify(receipt);
            }
            if (name === 'coop.debug.authority.availability') {
                assert.equal(instance, 'server'); assert.equal(mutation, true);
                assert.ok(['yield', 'auto'].includes(args[0]));
                const writer = args[0] === 'yield' ? 'b' : 'a';
                for (const state of states) {
                    state.lease.epoch += 2; state.lease.authorityId = 'writer' + state.lease.epoch;
                    state.lease.authorityControllerId = writer; state.localAuthority = state.controller === writer;
                    state.yielded = state.controller === 'a' && args[0] === 'yield';
                }
                return 'fixture availability';
            }
            if (name === 'coop.debug.authority.world_state') {
                const world = structuredClone(f.worlds[peer]);
                if (args[0] === 'positions') {
                    world.npcSample = [{ handle: 1, id: 'npc', x: 441, y: 512 }];
                    world.movement = { complete: true, ready: true, worldClockStopped: true,
                        campaign: world.campaign, epoch: states[peer].lease.epoch, ticks: world.ticks,
                        count: 3, rows: [...structuredClone(world.npcSample),
                            ...actors.map((actor, index) => ({handle: 10 + index, id: actor.mainParty, x: 650 + index, y: 278 + index}))] };
                    corrupt('frozen-' + peer, world);
                }
                return JSON.stringify(world);
            }
            assert.equal(name, 'coop.debug.players.party_state');
            return JSON.stringify(f.players[peer].find(player => player.controllerId === args[0]));
        } };
    return { context, features, events, commands, cycles, trace, movement: async received => {
        assert.equal(received, context); movementCalls++;
    }, movementCalls: () => movementCalls };
}

test('whole A-B-A caller rehearses explicit yield and auto with unchanged writer and frozen-state checks', async () => {
    const f = rehearsal(); await exerciseMapAuthorityContracts(f.context, f.movement);
    assert.equal(f.movementCalls(), 1);
    assert.deepEqual(f.features.map(row => row.name), ['A-to-B-map-handoff', 'B-to-A-map-handoff']);
    assert.deepEqual(f.features.map(row => row.evidence.afterProgress.map(state => state.yielded)), [[true, false], [false, false]]);
    assert.deepEqual(f.features.map(row => row.evidence.afterProgress[0].lease.epoch), [5, 7]);
    assert.equal(f.events.filter(event => event.phase === 'final_barrier_exact_movement').length, 2);
    assert.equal(f.events.filter(event => event.phase === 'map_ai_execution_observation').length, 4);
    assert.deepEqual(f.features.map(row => row.evidence.aiCoverage.tickBudget), [6, 6]);
    assert.deepEqual(f.features.map(row => row.evidence.aiCoverage.writerNpcCompletedDeltas), [[0, 6], [6, 0]]);
    for (const [index, item] of f.trace.entries()) if (item === 'Play_1x')
        assert.deepEqual(f.trace.slice(index - 2, index), ['coop.debug.authority.cycles:server', 'coop.debug.authority.cycles:client1']);
    assert.deepEqual(f.commands.filter(row => row.name === 'coop.debug.authority.availability').map(row => row.args), [['yield'], ['auto']]);
});

test('whole caller rejects wrong override, writer Tick, clock, epoch, resources and frozen positions', async () => {
    const corruptions = [
        (label, f) => { if (label.endsWith('single-writer-progress')) f.states[0].yielded = false; },
        (label, f) => { if (label.endsWith('single-writer-progress')) f.states[0].worldTicks++; },
        (label, f) => { if (label.endsWith('single-writer-progress')) f.states[1].hours--; },
        (label, f) => { if (label.endsWith('single-writer-progress')) f.states[1].worldTicks--; },
        (label, f) => { if (label.endsWith('single-writer-progress')) f.states.forEach(state => state.lease.epoch++); },
        (label, f) => { if (label.endsWith('single-writer-progress')) f.states[1].localAuthority = false; },
        (label, f) => { if (label.endsWith('single-writer-progress')) f.states[0].mainHero = 'wrong'; },
        (label, f) => { if (label.endsWith('critical-after-progress')) f.players[1][0].gold++; },
        (label, f) => { if (label.endsWith('critical-after-progress')) f.players[1][0].troops++; },
        (label, f) => { if (label.endsWith('critical-after-progress')) f.players[1][0].heroRegistrations++; },
        (label, f) => { if (label.endsWith('critical-after-progress')) f.worlds[1].rosterXpHash = 'wrong'; },
        (label, f) => { if (label.endsWith('critical-after-progress')) f.worlds[1].nextHandle++; },
        (label, f) => { if (label.endsWith('critical-after-progress')) f.worlds[1].ticks++; },
        (label, world) => { if (label === 'frozen-1') world.movement.complete = false; },
        (label, world) => { if (label === 'frozen-1') world.npcSample[0].x++; },
        (label, world) => { if (label === 'frozen-1') world.movement.rows[0].y++; },
    ];
    for (const corrupt of corruptions) {
        const f = rehearsal(corrupt);
        await assert.rejects(() => exerciseMapAuthorityContracts(f.context, f.movement));
    }
});

function aiWindow(writer = 1, count = 3) {
    const before = { cycles: [aiReceipt(0), aiReceipt(1)] }, after = structuredClone(before);
    addAi(after.cycles[writer], 'writerNpc', 'completed', count * 2);
    return { before, after };
}

test('AI window requires completed writer volume on both directions and permits idle or local-navigation observer', () => {
    for (const writer of [0, 1]) {
        const f = aiWindow(writer);
        assert.equal(assertMapAiProgress(f.before, f.after, writer, 3).tickBudget, 6);
        addAi(f.after.cycles[1 - writer], 'observerLocalNavigation', 'completed', 4);
        assert.doesNotThrow(() => assertMapAiProgress(f.before, f.after, writer, 3));
        addAi(f.after.cycles[1 - writer], 'unqualified', 'skipped', 4);
        assert.doesNotThrow(() => assertMapAiProgress(f.before, f.after, writer, 3));
    }
});

test('AI window rejects stale cumulative totals, failures, wrong partitions, rollback and authoritative observer calls', () => {
    const corruptions = [
        f => { f.before = structuredClone(f.after); },
        f => { f.after.cycles[1] = aiReceipt(1); addAi(f.after.cycles[1], 'writerNpc', 'completed', 5); },
        f => { f.after.cycles[1] = aiReceipt(1); addAi(f.after.cycles[1], 'writerNpc', 'skipped', 6); },
        f => { f.after.cycles[0].instance = 'reset'; },
        f => { f.after.cycles[0].processId++; },
        f => { f.after.cycles[0].campaign = 'different'; },
        f => { delete f.after.cycles[1].simulation['MobilePartyAi.Tick.writerNpc.failed']; },
        f => { f.after.cycles[1].simulation['MobilePartyAi.Tick.observed']++; },
        f => { f.after.cycles[0].simulation['MobilePartyAi.Tick.beforeOriginal.failed']++; },
        f => { f.after.cycles[1].simulation['Campaign.RealTick.sameFrameDuplicate.failed'] = 1; },
        f => { f.after.cycles[1].simulation['CampaignTicker.writerSkipped.failed'] = 1; },
        f => { addAi(f.after.cycles[1], 'writerNpc', 'failed'); },
        f => { addAi(f.after.cycles[0], 'observerLocalNavigation', 'failed'); },
        f => { addAi(f.after.cycles[0], 'writerNpc', 'completed'); },
        f => { addAi(f.after.cycles[0], 'writerPlayer', 'completed'); },
        f => { addAi(f.after.cycles[0], 'unknown', 'completed'); },
        f => { addAi(f.after.cycles[1], 'unqualified', 'completed'); },
        f => { f.after.cycles[1].simulation['MobilePartyAi.Tick.unexpectedWorld.completed']++; },
        f => { addAi(f.before.cycles[0], 'observerLocalNavigation', 'completed'); },
    ];
    for (const corrupt of corruptions) {
        const f = aiWindow(); corrupt(f);
        assert.throws(() => assertMapAiProgress(f.before, f.after, 1, 3));
    }
    for (const count of [0, NaN, 1.5, Number.MAX_SAFE_INTEGER]) {
        const f = aiWindow(); assert.throws(() => assertMapAiProgress(f.before, f.after, 1, count));
    }
});

test('AI observation brackets exact actor and epoch and rejects bad counter provenance', async () => {
    const f = rehearsal(); await exerciseMapAuthorityContracts(f.context, f.movement);
    const feature = f.features[0].evidence, valid = feature.beforeAi;
    const check = evidence => assertMapAiObservation(evidence, feature.committed, f.context.initial, 'world');
    assert.doesNotThrow(() => check(valid));
    for (const corrupt of [e => e.statesBefore[0].lease.epoch++, e => e.statesAfter[1].lease.authorityId = 'other',
        e => e.statesAfter[0].mainHero = 'other', e => e.cycles[0].campaign = 'other',
        e => e.cycles[0].overflowed = true, e => e.cycles[1].instance = e.cycles[0].instance,
        e => e.cycles[0].processId = 0, e => e.cycles[1].aiSimulation.schemaVersion = 0]) {
        const bad = structuredClone(valid); corrupt(bad); assert.throws(() => check(bad));
    }
});

test('whole caller cannot pass on world Tick alone or newly failed NPC work and keeps raw rejected counters', async () => {
    for (const corrupt of [
        receipt => { receipt.simulation['MobilePartyAi.Tick.writerNpc.completed'] = 1;
            receipt.simulation['MobilePartyAi.Tick.completed'] = 1; receipt.simulation['MobilePartyAi.Tick.observed'] = 1; },
        receipt => addAi(receipt, 'writerNpc', 'failed'),
        receipt => { receipt.simulation['MobilePartyAi.Tick.beforeOriginal.failed']++; },
    ]) {
        const f = rehearsal((label, receipt) => {
            if (label === 'cycles-1' && receipt.simulation['MobilePartyAi.Tick.writerNpc.completed'] > 0) corrupt(receipt);
        });
        await assert.rejects(() => exerciseMapAuthorityContracts(f.context, f.movement));
        const raw = f.events.find(event => event.name?.endsWith('-progress') && event.cycles);
        assert.ok(raw, 'Rejected raw cycles were not emitted');
        assert.equal(f.features.length, 0, 'Failed first writer window must not proceed to A return');
    }
});

test('whole A-B-A caller keeps polling after Tick progress until actual writer completion budget is reached', async () => {
    const f = rehearsal(), originalPoll = f.context.poll;
    let incompleteReads = 0;
    f.context.poll = async (label, read) => {
        if (!label.endsWith('-single-writer-progress')) return originalPoll(label, read);
        const writer = (await f.context.bothStates()).findIndex(state => state.localAuthority);
        addAi(f.cycles[writer], 'writerNpc', 'completed', -5);
        await assert.rejects(read, /execution-call coverage/); incompleteReads++;
        addAi(f.cycles[writer], 'writerNpc', 'completed', 5);
        return read();
    };
    await exerciseMapAuthorityContracts(f.context, f.movement);
    assert.equal(incompleteReads, 2);
    assert.deepEqual(f.features.map(row => row.evidence.aiCoverage.writerNpcCompletedDeltas), [[0, 6], [6, 0]]);
    assert.equal(f.events.filter(event => event.phase === 'map_ai_execution_observation').length, 6);
});
