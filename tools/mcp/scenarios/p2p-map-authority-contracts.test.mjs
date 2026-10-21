import assert from 'node:assert/strict';
import test from 'node:test';
import { capturePredictedMapState, exerciseMapAuthorityContracts } from './p2p-map-authority-contracts.mjs';

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
    const features = [], events = [], commands = [];
    let movementCalls = 0;
    const context = { initial: actors, bothStates: async () => structuredClone(states),
        poll: async (label, read) => { corrupt(label, { states, worlds: f.worlds, players: f.players }); return read(); },
        setTime: async mode => {
            if (mode !== 'Play_1x') return;
            states.find(state => state.localAuthority).worldTicks++;
            states.forEach(state => state.hours++); f.worlds.forEach(world => world.ticks++);
        },
        feature: async (name, action) => features.push({ name, evidence: await action() }),
        emit: async event => events.push(event),
        command: async (instance, name, args = [], mutation = false) => {
            commands.push({ instance, name, args, mutation });
            const peer = instance === 'server' ? 0 : 1;
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
                        count: 1, rows: structuredClone(world.npcSample) };
                    corrupt('frozen-' + peer, world);
                }
                return JSON.stringify(world);
            }
            assert.equal(name, 'coop.debug.players.party_state');
            return JSON.stringify(f.players[peer].find(player => player.controllerId === args[0]));
        } };
    return { context, features, events, commands, movement: async received => {
        assert.equal(received, context); movementCalls++;
    }, movementCalls: () => movementCalls };
}

test('whole A-B-A caller rehearses explicit yield and auto with unchanged writer and frozen-state checks', async () => {
    const f = rehearsal(); await exerciseMapAuthorityContracts(f.context, f.movement);
    assert.equal(f.movementCalls(), 1);
    assert.deepEqual(f.features.map(row => row.name), ['A-to-B-map-handoff', 'B-to-A-map-handoff']);
    assert.deepEqual(f.features.map(row => row.evidence.afterProgress.map(state => state.yielded)), [[true, false], [false, false]]);
    assert.deepEqual(f.features.map(row => row.evidence.afterProgress[0].lease.epoch), [5, 7]);
    assert.equal(f.events.length, 2);
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
