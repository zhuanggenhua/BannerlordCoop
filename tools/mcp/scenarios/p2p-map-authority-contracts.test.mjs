import assert from 'node:assert/strict';
import test from 'node:test';
import { capturePredictedMapState } from './p2p-map-authority-contracts.mjs';

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
