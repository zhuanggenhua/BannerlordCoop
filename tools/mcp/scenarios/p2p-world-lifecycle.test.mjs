import assert from 'node:assert/strict';
import test from 'node:test';
import { options, assertSemantic, assertPersistent, assertCycles, assertColdLease, freshSaveName } from './p2p-world-lifecycle.mjs';
const hash = 'a'.repeat(64);
const semantic = () => ({campaign:'campaign',ticks:120,partyCount:25,activePartyCount:23,missingPartyIds:0,duplicatePartyIds:[],
    worldIdentityHash:hash,worldRostersHash:hash,activeWorldRostersHash:hash,heroGoldHash:hash,volunteerHash:hash,aliveHeroes:10,
    sampledNpcIds:['lord_2_4'],npcs:[{party:'lord_2_4',members:[{id:'recruit',Number:7,WoundedNumber:1,Xp:30}]}],
    players:[{controller:'a',gold:90},{controller:'b',gold:80}]});
const row = (node,day,kind='DailyTickParty') => ({Kind:kind,Party:kind==='DailyTickParty'?'lord_2_4':null,Day:day,Hour:day*24,
    Node:node,WriterController:node,LeaseKnown:true,LocalWriter:true,Epoch:node==='a'?3:5,RoomId:'room',AuthorityId:'world-'+node,Count:1});
const receipts = () => [{instance:'one',campaign:'campaign',overflowed:false,rows:[row('a',2),row('a',2,'DailyTick'),row('a',2,'HourlyTick')]},
    {instance:'two',campaign:'campaign',overflowed:false,rows:[row('b',3),row('b',3,'DailyTick'),row('b',3,'HourlyTick')]}];
const initial = [{controller:'a',roomHost:'a',mainHero:'hero-a',mainParty:'party-a'},{controller:'b',roomHost:'a',mainHero:'hero-b',mainParty:'party-b'}];
const states = (roomId,epoch,authorityId='world') => initial.map(value=>({...value,localAuthority:value.controller==='a',frozen:false,
    lease:{authorityControllerId:'a',roomId,epoch,authorityId,isPaused:false}}));
test('modes are independent, bounded and mutually exclusive',()=>{
    const paths=['--repo','.','--server','server','--config','config','--sdk-root','sdk','--record','record'];
    assert.equal(options(['--natural',...paths]).mode,'natural'); assert.equal(options(['--cold-save',...paths]).mode,'cold-save');
    assert.throws(()=>options(paths)); assert.throws(()=>options(['--natural','--cold-save',...paths]));
    assert.throws(()=>options(['--natural',...paths,'--day-timeout-seconds','0']));
});
test('cold equality covers full world hashes, inactive state, time, gold and sampled XP',()=>{
    const value=semantic();assertSemantic(value);assertPersistent(value,structuredClone(value));
    for(const key of ['worldRostersHash','activeWorldRostersHash','worldIdentityHash','heroGoldHash','volunteerHash']){
        const after=structuredClone(value);after[key]='b'.repeat(64);assert.throws(()=>assertPersistent(value,after),key);
    }
    for(const edit of [v=>v.ticks++,v=>v.players[0].gold--,v=>v.npcs[0].members[0].Xp++,v=>v.duplicatePartyIds.push('duplicate')]){
        const after=structuredClone(value);edit(after);assert.throws(()=>assertPersistent(value,after));
    }
});
test('merged natural dispatches preserve one callback per party/day across writers',()=>{
    const result=assertCycles(receipts());assert.equal(result.partyDaily,2);assert.equal(result.daily,2);assert.equal(result.hourly,2);
    const duplicate=receipts();duplicate[1].rows.push(row('b',2));assert.throws(()=>assertCycles(duplicate),/Repeated native cycle/);
    const same=receipts();same[0].rows[0].Count=2;assert.throws(()=>assertCycles(same),/Repeated native cycle/);
});
test('observer, unknown lease, wrong node, and overflow never pass evidence checks',()=>{
    for(const change of [r=>r[0].rows[0].LocalWriter=false,r=>r[0].rows[0].LeaseKnown=false,
        r=>r[0].rows[0].Node='observer',r=>r[1].overflowed=true]){const value=receipts();change(value);assert.throws(()=>assertCycles(value));}
});
test('cold start needs a fresh room, epoch or authority fence',()=>{
    const before=states('room',3)[0];assert.throws(()=>assertColdLease(before,states('room',3),initial));
    assert.equal(assertColdLease(before,states('room',4),initial),'new-epoch-fence');
    assert.equal(assertColdLease(before,states('new-room',1),initial),'new-room-fence');
    assert.equal(assertColdLease(before,states('room',3,'new-authority'),initial),'new-authority-fence');
});
test('generated save basenames are unique, native-valid and never the supplied source name',()=>{
    const now=new Date('2026-10-19T01:00:00.000Z');const a=freshSaveName(now,'abcdefgh'),b=freshSaveName(now,'ijklmnop');
    assert.notEqual(a,b);assert.match(a,/^[A-Za-z0-9_-]{1,64}$/);assert.notEqual(a,'P1B_DirectIP_Recruit');
});
