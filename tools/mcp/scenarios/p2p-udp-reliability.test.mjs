import { test } from 'node:test';
import assert from 'node:assert/strict';
import { options, proxyProfile, assertActors, assertWriter, assertWorld, assertPlayers, assertFrozenHost,
    assertPacketEvidence, assertProcessEvidence, logDiagnostics, jsonOutput, assertReconnectResult,
    authorityBarrierEvents, assertHeldBarrierFaultPoint } from './p2p-udp-reliability.mjs';

const argv = ['--repo','.', '--server','mcp.exe','--config','profiles.json','--sdk-root','node_modules','--record','new.jsonl'];
const initial = [ { controller:'A', mainHero:'hero_A', mainParty:'party_A', roomHost:'A' },
    { controller:'B', mainHero:'hero_B', mainParty:'party_B', roomHost:'A' } ];
const states = () => initial.map((actor,index) => ({ ...actor, localAuthority:index===0, frozen:false,
    worldTicks:10, hours:123, lease:{ AuthorityControllerId:'A', AuthorityId:'lease', RoomId:'room', Epoch:7, IsPaused:false } }));
const clone = value => structuredClone(value);

test('CLI is bounded, rejects duplicate/unknown flags, and optional loss mode remains explicit', () => {
    assert.equal(options(argv).rounds,3); assert.equal(options([...argv,'--lost-writer'])['lost-writer'],true);
    assert.throws(() => options([...argv,'--rounds','2']));
    assert.throws(() => options([...argv,'--timeout-seconds','60']));
    assert.throws(() => options([...argv,'--rounds','3','--rounds','4']));
    assert.throws(() => options([...argv,'--private-pipe','x']));
    assert.throws(() => options(argv.slice(0,-2)));
});

test('only an explicit ListenAuthority single-client proxy profile is accepted', () => {
    const document = {profiles:{local:{listenAuthority:true,clientProxyPort:24200,serverPlatformId:'A',clientPlatformIds:['B']}}};
    assert.deepEqual(proxyProfile(document,'local'),{port:24200,controllers:['A','B']});
    for (const patch of [{listenAuthority:false},{clientProxyPort:4200},{clientProxyPort:65536},{clientPlatformIds:['A']}])
        assert.throws(() => proxyProfile({profiles:{local:{...document.profiles.local,...patch}}},'local'));
});

test('exact native actors, common epoch and sole writer reject stale leases and dual authority', () => {
    const pair=states(); assertActors(pair,initial); assertWriter(pair,'A',initial);
    const stale=clone(pair); stale[1].lease.Epoch=6; assert.throws(() => assertWriter(stale,'A',initial));
    const dual=clone(pair); dual[1].localAuthority=true; assert.throws(() => assertWriter(dual,'A',initial));
    const swapped=clone(pair); swapped[0].mainHero='hero_B'; assert.throws(() => assertWriter(swapped,'A',initial));
    const frozen=clone(pair); frozen[0].frozen=true; assert.throws(() => assertWriter(frozen,'A',initial));
});

test('world comparison includes exact time, count, XP, gold, volunteers and allocation without position tolerance', () => {
    const world={campaign:'campaign',unregisteredParties:0,partyIdentityHash:'p',rosterCountsHash:'r',rosterXpHash:'xp',
        volunteerHash:'v',heroGoldHash:'g',nextHandle:500,ticks:1000};
    assertWorld([world,clone(world)]);
    for (const key of ['partyIdentityHash','rosterCountsHash','rosterXpHash','volunteerHash','heroGoldHash','nextHandle','ticks']) {
        const bad=clone(world); bad[key]='different'; assert.throws(() => assertWorld([world,bad]));
    }
    const absent=clone(world); delete absent.rosterXpHash; assert.throws(() => assertWorld([absent,clone(absent)]));
});

function player(controllerId) { return { controllerId,active:true,gold:900,troops:5,wounded:0,heroId:'h_'+controllerId,clanId:'c_'+controllerId,
    characterId:'ch_'+controllerId,partyId:'p_'+controllerId,partyStringId:'party_'+controllerId,heroStringId:'hero_'+controllerId,
    settlementId:null,x:12,y:34,registeredPlayers:2,heroRegistrations:1,partyRegistrations:1,clanRegistrations:1 }; }
test('all16 player fields are compared by ControllerId with unique hero/clan/party registrations', () => {
    const rows=[player('A'),player('B')]; assertPlayers([rows,[clone(rows[1]),clone(rows[0])]]);
    for (const key of ['gold','troops','wounded','heroId','clanId','characterId','partyId','partyStringId','heroStringId',
        'settlementId','x','y','registeredPlayers','heroRegistrations','partyRegistrations','clanRegistrations']) {
        const bad=clone(rows); bad[0][key]='different'; assert.throws(() => assertPlayers([rows,bad]));
    }
    const duplicate=clone(rows); duplicate[1].controllerId='A'; assert.throws(() => assertPlayers([rows,duplicate]));
});

test('unresolved handoff never advances host time, writes, epoch or original native actor', () => {
    const held=states()[0]; held.localAuthority=false; held.lease.IsPaused=true; held.lease.AuthorityControllerId='';
    assertFrozenHost(held,clone(held),initial[0],true);
    for(const patch of [{hours:124},{worldTicks:11},{localAuthority:true},{mainParty:'other'}])
        assert.throws(() => assertFrozenHost(held,{...clone(held),...patch},initial[0],true));
    const epoch=clone(held); epoch.lease.Epoch++; assert.throws(() => assertFrozenHost(held,epoch,initial[0],true));
    const lost=clone(held); lost.lease.IsPaused=false;lost.lease.AuthorityControllerId='B';
    assertFrozenHost(lost,clone(lost),initial[0],false);
});

test('packet acceptance requires real loss, delay, reorder, bidirectional traffic and byte hashes', () => {
    const stats={dropped:2,delayed:3,reordered:1,received:{toHost:20,toClient:20},sent:{toHost:18,toClient:18},errors:[],
        samples:[{action:'drop',sha256:'a'.repeat(64)},{action:'delay',sha256:'b'.repeat(64)}]};
    assertPacketEvidence(stats);
    for(const key of ['dropped','delayed','reordered']) assert.throws(() => assertPacketEvidence({...clone(stats),[key]:0}));
    const bad=clone(stats);bad.samples[0].sha256='missing';assert.throws(() => assertPacketEvidence(bad));
});

test('OS process evidence matches exactly the two owned native games and rejects dedicated server', () => {
    const rows=[1,2].map(ProcessId => ({Name:'Bannerlord.exe',ProcessId,CreationDate:'date',CommandLine:'/singleplayer'}));
    assertProcessEvidence(rows,[2,1]);assert.throws(() => assertProcessEvidence([...rows,clone(rows[0])],[1,2]));
    assert.throws(() => assertProcessEvidence([...rows,{Name:'Bannerlord.DedicatedServer.exe'}],[1,2]));
    assert.throws(() => assertProcessEvidence(rows,[1,3]));
});

test('logs retain all error lines rather than excusing the historical unit-test failure count', () => {
    const diagnostics=logDiagnostics('[ERR] Failed to run Campaign.RealTick\n[ERR] Duplicate object handle:12\n[FTL] unclassified failure');
    assert.equal(diagnostics.realTick,1);assert.equal(diagnostics.duplicateIds,1);assert.equal(diagnostics.errorLines.length,3);
    assert.deepEqual(jsonOutput('prefix\nLIVE_TEST_JSON={"x":1}'),{x:1});
});

test('actual Serilog process/time/context prefixes expose every ERR and FTL severity', () => {
    const actual = [
        '[(280) 09:28:25 ERR GameInterface.Services.ItemRosters.Patches.ItemRosterPatch] Client changed managed AddToCounts',
        '[(8548) 09:28:26 FTL Coop.Core.Server.CoopServer] native fatal exception',
        '[(8548) 09:28:27 INF Coop.Core.Server.CoopServer] error counter=0, ERROR text and [ERR] are message content',
        '[(280) 09:28:28 WRN Coop.Core.Client.CoopClient] previous error is diagnostic text',
        '[ERR] legacy abbreviated prefix',
    ];
    assert.deepEqual(logDiagnostics(actual.join('\r\n')).errorLines, [actual[0], actual[1], actual[4]]);
});

test('barrier-only selects one paused handoff without accepting multi-round stress', () => {
    const selected=options([...argv,'--barrier-only']);
    assert.equal(selected['barrier-only'],true); assert.equal(selected.rounds,1);
    assert.throws(()=>options([...argv,'--barrier-only','--rounds','3']));
    assert.throws(()=>options([...argv,'--barrier-only','--lost-writer']));
    assert.throws(()=>options([...argv,'--rounds','1']));
});

test('candidate fault point requires a new current-epoch barrier, held A-to-B UDP, and live B-to-A traffic', () => {
    const stats = {
        errors: [],
        policies: [{ utc: 'hold-policy-utc', holdToClient: true }],
        heldTotal: 4, heldCurrentCount: 2, heldCurrentBytes: 180,
        heldSamples: [{ direction: 'toClient', heldUtc: 'held-utc', policyUtc: 'hold-policy-utc', bytes: 90, sha256: 'a'.repeat(64) }],
        received: { toHost: 8 }, sent: { toHost: 7 },
    };
    const events = authorityBarrierEvents([
        'Sent campaign authority handoff barrier 7 for paused epoch 6',
        'Sent campaign authority handoff barrier 10 for paused epoch 8',
    ].join('\n'));
    const input = {
        events, baselineMaxSequence: 9, pausedEpoch: 8, heldBaseline: 2,
        toHostBaseline: 7, sentToHostBaseline: 6, policyUtc: 'hold-policy-utc', stats,
    };
    assert.deepEqual(assertHeldBarrierFaultPoint(input), { sequence: 10, epoch: 8 });
    assert.throws(() => assertHeldBarrierFaultPoint({ ...input, events: [{ sequence: 9, epoch: 8 }] }),
        /No new final barrier/);
    assert.throws(() => assertHeldBarrierFaultPoint({ ...input, pausedEpoch: 7 }), /No new final barrier/);
    assert.throws(() => assertHeldBarrierFaultPoint({ ...input, heldBaseline: 4 }), /No A-to-B UDP datagram/);
    assert.throws(() => assertHeldBarrierFaultPoint({ ...input, stats: { ...stats, errors: ['hold overflow'] } }),
        /Fault proxy reported an error/);
    assert.throws(() => assertHeldBarrierFaultPoint({ ...input, toHostBaseline: 8 }), /Candidate-to-host UDP/);
});

test('actual teardown reconnect fallback accepts its exact outcome without invented structured fields', () => {
    const native={name:'coop.debug.connection.reconnect', arguments:[], found:true,
        output:'Client co-op session restarted after teardown.',hasStructuredResult:false,structuredResult:null};
    assert.equal(assertReconnectResult(native),native.output);
    assert.equal(assertReconnectResult({...native,succeeded:true,errorCode:null}),native.output);
    for(const patch of [{succeeded:false},{found:false},{errorCode:'failed'},
        {output:'The process client-session starter is unavailable.'},{output:'Usage: coop.debug.connection.reconnect'},
        {name:'other'},{arguments:['retry']}]) assert.throws(()=>assertReconnectResult({...native,...patch}));
});
