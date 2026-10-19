import assert from 'node:assert/strict';
import test from 'node:test';
import { options, assertSemantic, assertPersistent, assertCycles, assertCycleWindow, assertSchedulerHandoff, assertColdLease, freshSaveName, combineCyclePages } from './p2p-world-lifecycle.mjs';
const hash = 'a'.repeat(64);
const semantic = () => ({campaign:'campaign',ticks:120,partyCount:25,activePartyCount:23,missingPartyIds:0,duplicatePartyIds:[],
    worldIdentityHash:hash,worldRostersHash:hash,activeWorldRostersHash:hash,heroGoldHash:hash,volunteerHash:hash,aliveHeroes:10,
    sampledNpcIds:['lord_2_4'],npcs:[{party:'lord_2_4',members:[{id:'recruit',Number:7,WoundedNumber:1,Xp:30}]}],
    players:[{controller:'a',gold:90},{controller:'b',gold:80}]});
const row = (node,day,kind='DailyTickParty') => ({Kind:kind,Party:kind==='DailyTickParty'?'lord_2_4':null,Day:day,Hour:day*24,
    Node:node,WriterController:node,LeaseKnown:true,LocalWriter:true,Epoch:node==='a'?3:5,RoomId:'room',AuthorityId:'world-'+node,Count:1,DueTicks:day*24});
const receipt = (node,rows) => ({node,instance:node,campaign:'campaign',overflowed:false,rows,tickerAudits:[],duplicateRealTicks:0,failedRealTicks:0});
const receipts = () => [receipt('a',[row('a',2),row('a',2,'DailyTick'),row('a',2,'HourlyTick')]),
    receipt('b',[row('b',3),row('b',3,'DailyTick'),row('b',3,'HourlyTick')])];
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
test('merged dispatches use real due slots across writers rather than execution-time buckets',()=>{
    const result=assertCycles(receipts());assert.equal(result.partyDaily,2);assert.equal(result.daily,2);assert.equal(result.hourly,2);
    const duplicate=receipts();duplicate[1].rows.push(row('b',2,'DailyTick'));assert.throws(()=>assertCycles(duplicate),/Repeated native due slot/);
    const same=receipts();same[0].rows[1].Count=2;assert.throws(()=>assertCycles(same),/Repeated native due slot/);
    const catchup=receipts();catchup[0].rows.push({...row('a',2,'HourlyTick'),DueTicks:49});assert.equal(assertCycles(catchup).hourly,3);
    const unknown=receipts();delete unknown[0].rows[1].DueTicks;assert.throws(()=>assertCycles(unknown),/due slot unavailable/);
});
test('native ticker expected and completed identity/count mismatches never pass',()=>{
    const good={LeaseKnown:true,LocalWriter:true,Expected:7,Completed:7,ExpectedHash:hash,CompletedHash:hash,Mismatches:0};
    const value=receipts();value[0].tickerAudits=[good];assertCycles(value);
    for(const change of [a=>a.Completed--,a=>a.CompletedHash='b'.repeat(64),a=>a.Mismatches++,a=>a.LocalWriter=false]){
        const bad=structuredClone(value);change(bad[0].tickerAudits[0]);assert.throws(()=>assertCycles(bad));
    }
});
test('natural window requires every real hourly/daily slot and all scheduler ticker families',()=>{
    const names=['_mobilePartyHourlyTicker','_mobilePartyDailyTicker','_dailyTickPartyTicker','_hourlyTickMobilePartyTicker',
        '_hourlyTickSettlementTicker','_hourlyTickClanTicker','_dailyTickTownTicker','_dailyTickSettlementTicker','_dailyTickHeroTicker','_dailyTickClanTicker','_quarterDailyPartyTicker',
        '_caravanMobilePartyPartialHourlyAiEventTicker','_garrisonMobilePartyPartialHourlyAiEventTicker',
        '_militiaMobilePartyPartialHourlyAiEventTicker','_villagerMobilePartyPartialHourlyAiEventTicker',
        '_customMobilePartyPartialHourlyAiEventTicker','_banditMobilePartyPartialHourlyAiEventTicker',
        '_lordMobilePartyPartialHourlyAiEventTicker','_partiesWithoutPartyComponentsPartialHourlyAiEventTicker','_patrolPartyPartialHourlyAiEventTicker'];
    const before=[{...receipt('a',[]),ticks:100,hours:100,hourly:{next:101,period:1},daily:{next:120,period:24},simulation:{}}, {...receipt('b',[]),simulation:{}}];
    const after=structuredClone(before);Object.assign(after[0],{hours:124,hourly:{next:125,period:1},daily:{next:144,period:24},aiTicks:20,realTicks:80});
    after[0].rows=Array.from({length:24},(_,i)=>({...row('a',5,'HourlyTick'),DueTicks:101+i}));
    after[0].rows.push({...row('a',5,'DailyTick'),DueTicks:120});
    after[0].tickerAudits=names.map(Ticker=>({Ticker,Batches:10,Completed:100}));
    after[0].ticks=124;
    after[0].simulation=Object.fromEntries(['MobilePartyAi.Tick.completed','Campaign.RealTick.completed','Campaign.Tick.completed','Campaign.LateAITickAux.completed','Campaign.WaitAsyncTasks.completed'].map(name=>[name,5]));
    after[0].effects=[{Kind:'ClanFinance',Ticks:120,GoldBefore:100,GoldAfter:100},{Kind:'Wage',Ticks:120,Wage:0}];
    assert.equal(assertCycleWindow(before,after,'a').hourly,24);
    assert.equal(assertCycleWindow(before,after,'a').economy.paymentAcceptance,'NOT_OBSERVED');
    for(const change of [a=>a[0].rows.shift(),a=>a[0].rows.push({...a[0].rows[0]}),a=>a[0].tickerAudits.pop(),a=>a[0].simulation={},a=>a[0].hours=123.9,a=>a[0].effects=[]]){
        const bad=structuredClone(after);change(bad);assert.throws(()=>assertCycleWindow(before,bad,'a'));
    }
});
test('observer, unknown lease, wrong node, and overflow never pass evidence checks',()=>{
    for(const change of [r=>r[0].rows[0].LocalWriter=false,r=>r[0].rows[0].LeaseKnown=false,
        r=>r[0].rows[0].Node='observer',r=>r[1].overflowed=true]){const value=receipts();change(value);assert.throws(()=>assertCycles(value));}
});
test('handoff preserves native debt cursor and membership identity/order as well as time phase',()=>{
    const previous={hourly:{next:24,period:1},daily:{next:48,period:24},scheduler:{LastGameTimeTicks:12,
        tickers:[{Name:'daily',Index:5,TickDebt:0.75,count:10,orderedIdsHash:hash}]}};
    assertSchedulerHandoff(previous,[structuredClone(previous),structuredClone(previous)]);
    for(const change of [v=>v.scheduler.tickers[0].Index--,v=>v.scheduler.tickers[0].TickDebt=0,
        v=>v.scheduler.tickers[0].orderedIdsHash='b'.repeat(64),v=>v.hourly.next--]){
        const bad=structuredClone(previous);change(bad);assert.throws(()=>assertSchedulerHandoff(previous,[bad]));
    }
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
test('bounded cycle pagination rejects incomplete or mixed-run evidence',()=>{
    const first={instance:'id',campaign:'campaign',processId:1,totalRows:2,overflowed:false,offset:0,nextOffset:1,hasMore:true,rows:[row('a',1)]};
    const second={...first,offset:1,nextOffset:2,hasMore:false,rows:[row('a',2)]};
    assert.equal(combineCyclePages([first,second]).rows.length,2);
    assert.throws(()=>combineCyclePages([first]));assert.throws(()=>combineCyclePages([first,{...second,instance:'other'}]));
    assert.throws(()=>combineCyclePages([first,{...second,offset:0}]));
});
