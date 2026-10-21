import assert from 'node:assert/strict';
import test from 'node:test';
import { captureProcessIdentities, collectSessionLogs, hasCompleteSessionEvidence, stopSession, options, assertSemantic, assertPersistent, assertPreHandoffSemantic, completeNaturalDayHandoff, assertCycles, assertAiSimulationEvidence, assertCycleWindow, assertSchedulerHandoff, assertColdLease, freshSaveName, combineCyclePages } from './p2p-world-lifecycle.mjs';
const hash = 'a'.repeat(64);
const semantic = () => ({campaign:'campaign',ticks:120,partyCount:25,activePartyCount:23,missingPartyIds:0,duplicatePartyIds:[],
    worldIdentityHash:hash,worldRostersHash:hash,activeWorldRostersHash:hash,heroGoldHash:hash,volunteerHash:hash,aliveHeroes:10,
    sampledNpcIds:['lord_2_4'],npcs:[{party:'lord_2_4',members:[{id:'recruit',Number:7,WoundedNumber:1,Xp:30}]}],
    players:[{controller:'a',gold:90},{controller:'b',gold:80}]});
const row = (node,day,kind='DailyTickParty') => ({Kind:kind,Party:kind==='DailyTickParty'?'lord_2_4':null,Day:day,Hour:day*24,
    Node:node,WriterController:node,LeaseKnown:true,LocalWriter:true,Epoch:node==='a'?3:5,RoomId:'room',AuthorityId:'world-'+node,Count:1,DueTicks:day*24});
const aiClasses=['writerNpc','writerPlayer','observerLocalNavigation','receiveApply','unqualified','unknown'];
function aiEvidence(kind='writerNpc',completed=1,priorUnknown=0) {
    const simulation={'MobilePartyAi.Tick.observed':completed+priorUnknown,'MobilePartyAi.Tick.beforeOriginal.failed':0};
    for(const outcome of ['completed','failed','skipped']) {
        simulation['MobilePartyAi.Tick.'+outcome]=outcome==='completed'?completed+priorUnknown:0;
        for(const name of [...aiClasses,'unexpectedWorld']) simulation['MobilePartyAi.Tick.'+name+'.'+outcome]=0;
    }
    simulation['MobilePartyAi.Tick.'+kind+'.completed']=completed;
    simulation['MobilePartyAi.Tick.unknown.completed']+=priorUnknown;
    simulation['MobilePartyAi.Tick.unexpectedWorld.completed']=priorUnknown;
    return {simulation,aiSimulation:{schemaVersion:1,classes:aiClasses,firstEntries:[]}};
}
const receipt = (node,rows) => ({node,instance:node,campaign:'campaign',overflowed:false,rows,tickerAudits:[],duplicateRealTicks:0,failedRealTicks:0,...aiEvidence(node==='a'?'writerNpc':'observerLocalNavigation')});

test('AI explicit zero baseline preserves default positivity and complete schema validation', () => {
    const empty = aiEvidence('writerNpc', 0);
    assert.throws(() => assertAiSimulationEvidence(empty), /no actual entry/);
    assert.doesNotThrow(() => assertAiSimulationEvidence(empty, { allowZeroObserved: true }));
    assert.throws(() => assertAiSimulationEvidence(empty, { allowZeroObserved: 'true' }), /boolean/);
    for (const corrupt of [value => delete value.simulation['MobilePartyAi.Tick.unknown.failed'],
        value => value.aiSimulation.schemaVersion = 0,
        value => value.simulation['MobilePartyAi.Tick.completed'] = 1,
        value => value.simulation['MobilePartyAi.Tick.observed'] = -1]) {
        const invalid = structuredClone(empty); corrupt(invalid);
        assert.throws(() => assertAiSimulationEvidence(invalid, { allowZeroObserved: true }));
    }
});
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
test('pre-handoff permits only explicit finite NPC x/y and records every original position',()=>{
    const left=semantic();Object.assign(left.npcs[0],{x:10,y:20});
    const right=structuredClone(left);Object.assign(right.npcs[0],{x:13,y:15});
    const original=structuredClone([left,right]);
    assert.deepEqual(assertPreHandoffSemantic(left,right),[{index:0,party:'lord_2_4',left:{x:10,y:20},right:{x:13,y:15},dx:3,dy:-5}]);
    assert.deepEqual([left,right],original);
    assert.throws(()=>assertPersistent(left,right),'post-handoff/cold full equality still includes NPC positions');
    for(const change of [v=>delete v.npcs[0].x,v=>v.npcs[0].x=NaN,v=>v.npcs[0].y=Infinity,
        v=>v.npcs[0].gold=1,v=>v.npcs[0].party='wrong',v=>v.npcs[0].members[0].Number++,
        v=>v.npcs[0].members[0].Xp++,v=>v.npcs[0].unknown={x:1,y:2},v=>v.unknown=1,
        v=>v.players[0].gold++,v=>v.players[0].x=1,v=>v.worldIdentityHash='b'.repeat(64)]){
        const bad=structuredClone(right);change(bad);assert.throws(()=>assertPreHandoffSemantic(left,bad));
    }
    const missing=structuredClone(left);delete missing.npcs[0].y;
    assert.throws(()=>assertPreHandoffSemantic(missing,structuredClone(missing)));
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
    const before=[{...receipt('a',[]),ticks:100,hours:100,hourly:{next:101,period:1},daily:{next:120,period:24},}, {...receipt('b',[]),}];
    const after=structuredClone(before);Object.assign(after[0],{hours:124,hourly:{next:125,period:1},daily:{next:144,period:24},aiTicks:20,realTicks:80});
    after[0].rows=Array.from({length:24},(_,i)=>({...row('a',5,'HourlyTick'),DueTicks:101+i}));
    after[0].rows.push({...row('a',5,'DailyTick'),DueTicks:120});
    after[0].tickerAudits=names.map(Ticker=>({Ticker,Batches:10,Completed:100}));
    after[0].ticks=124;
    Object.assign(after[0],aiEvidence('writerNpc',6));Object.assign(after[1],aiEvidence('observerLocalNavigation',6));
    Object.assign(after[0].simulation,Object.fromEntries(['Campaign.RealTick.completed','Campaign.Tick.completed','Campaign.LateAITickAux.completed','Campaign.WaitAsyncTasks.completed'].map(name=>[name,5])));
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

function naturalHandoffFixture() {
    const tickerNames=['_mobilePartyHourlyTicker','_mobilePartyDailyTicker','_dailyTickPartyTicker','_hourlyTickMobilePartyTicker',
        '_hourlyTickSettlementTicker','_hourlyTickClanTicker','_dailyTickTownTicker','_dailyTickSettlementTicker',
        '_dailyTickHeroTicker','_dailyTickClanTicker','_quarterDailyPartyTicker',
        ...['caravan','garrison','militia','villager','custom','bandit','lord','partiesWithoutPartyComponents','patrol']
            .map(n=>n==='patrol'?'_patrolPartyPartialHourlyAiEventTicker':'_'+n+(n==='partiesWithoutPartyComponents'?'':'MobileParty')+'PartialHourlyAiEventTicker')];
    const scheduler={LastGameTimeTicks:124,tickers:[{Name:'daily',Index:5,TickDebt:0.75,count:10,orderedIdsHash:hash}]};
    const beforeCycles=['a','b'].map(node=>({...receipt(node,[]),hours:100,ticks:100,
        hourly:{next:101,period:1},daily:{next:120,period:24},scheduler:structuredClone(scheduler)}));
    const preCycles=structuredClone(beforeCycles);for(const c of preCycles)Object.assign(c,{hours:124,ticks:124,hourly:{next:125,period:1},daily:{next:144,period:24}});
    preCycles[0].rows=Array.from({length:24},(_,i)=>({...row('a',5,'HourlyTick'),DueTicks:101+i}));
    preCycles[0].rows.push({...row('a',5,'DailyTick'),DueTicks:120});
    preCycles[0].tickerAudits=tickerNames.map(Ticker=>({Ticker,Batches:10,Expected:100,Completed:100,ExpectedHash:hash,CompletedHash:hash,Mismatches:0,LeaseKnown:true,LocalWriter:true}));
    Object.assign(preCycles[0],aiEvidence('writerNpc',6));Object.assign(preCycles[1],aiEvidence('observerLocalNavigation',6));
    Object.assign(preCycles[0].simulation,Object.fromEntries(['Campaign.RealTick.completed','Campaign.Tick.completed','Campaign.LateAITickAux.completed','Campaign.WaitAsyncTasks.completed'].map(n=>[n,5])));
    preCycles[0].effects=[{Kind:'ClanFinance',Ticks:120,GoldBefore:100,GoldAfter:100,Known:true,LocalWriter:true,Error:null,Entity:'clan'},
        {Kind:'Wage',Ticks:120,Wage:0,Known:true,LocalWriter:true,Error:null,Entity:'clan'}];
    const world={campaign:'campaign',unregisteredParties:0,partyIdentityHash:hash,rosterCountsHash:hash,rosterXpHash:hash,volunteerHash:hash,heroGoldHash:hash,nextHandle:100,ticks:124};
    const players=initial.map(v=>({controllerId:v.controller,heroId:v.mainHero,heroStringId:v.mainHero,partyId:v.mainParty,partyStringId:v.mainParty,
        clanId:'clan-'+v.controller,characterId:'char-'+v.controller,gold:100,troops:2,wounded:0,settlementId:null,x:5,y:6,
        active:true,registeredPlayers:2,heroRegistrations:1,partyRegistrations:1,clanRegistrations:1}));
    const sem=semantic();Object.assign(sem,{ticks:124});Object.assign(sem.npcs[0],{x:10,y:20,gold:100});
    const pre={worlds:[structuredClone(world),structuredClone(world)],players:[structuredClone(players),structuredClone(players)],semantics:[structuredClone(sem),structuredClone(sem)],states:states('room',3)};
    pre.semantics[1].npcs[0].x=13;
    const post=structuredClone(pre);post.semantics[1].npcs[0].x=10;
    post.states=initial.map(v=>({...v,localAuthority:v.controller==='b',frozen:false,lease:{authorityControllerId:'b',roomId:'room',epoch:4,authorityId:'world',isPaused:false}}));
    const tailCycles=structuredClone(preCycles);tailCycles[0].simulation['Campaign.LateAITickAux.completed']++;
    const events=[],records=[];let cycleReads=0,modeReads=0;
    const fixture={pre,post,preCycles,tailCycles,events,records,beforeCycles,
        modes:[['Pause','Pause'],['Pause','Pause']],confirmed:structuredClone(post.states)};
    fixture.args={node:'a',nextController:'b',initial,beforeCycles,
        pause:async()=>{events.push('pause');},readPreSnapshot:async()=>{events.push('pre');return fixture.pre;},
        readTimeModes:async()=>{events.push('modes');return fixture.modes[modeReads++];},
        readCycles:async()=>{events.push(cycleReads?'tail-cycles':'pre-cycles');return cycleReads++?fixture.tailCycles:fixture.preCycles;},
        handoff:async()=>{events.push('handoff');},confirmWriter:async()=>{events.push('confirmed');return fixture.confirmed;},
        readStrictSnapshot:async ids=>{events.push('strict-post');assert.deepEqual(ids,fixture.pre.semantics[0].sampledNpcIds);return fixture.post;},
        record:async r=>{events.push(r.phase);records.push(structuredClone(r));}};
    return fixture;
}
test('natural-day uses one confirmed handoff then full strict post snapshot and old-writer tail collector',async()=>{
    const f=naturalHandoffFixture(),result=await completeNaturalDayHandoff(f.args);
    assert.deepEqual(f.events,['pause','pre','modes','pre-cycles','native_day_pre_handoff','handoff','confirmed','modes','strict-post','tail-cycles','native_day_post_handoff']);
    assert.equal(f.events.filter(x=>x==='handoff').length,1);
    assert.equal(result.sealedWriter.simulation['Campaign.LateAITickAux.completed'],6);
    assert.equal(result.preCoverage.hourly,24);assert.equal(result.coverage.daily,1);
    assert.equal(f.records[0].pre.semantics[1].npcs[0].x,13);assert.equal(f.records[1].post.semantics[1].npcs[0].x,10);
});
test('return handoff seals B collector rather than assuming the room host is the old writer',async()=>{
    const f=naturalHandoffFixture(),swap=n=>n==='a'?'b':'a';
    for(const group of [f.beforeCycles,f.preCycles,f.tailCycles])for(const c of group){
        c.node=swap(c.node);c.instance=swap(c.instance);
        for(const r of c.rows){r.Node=swap(r.Node);r.WriterController=swap(r.WriterController);r.Epoch=5;}
    }
    const assignLease=(pair,node,epoch)=>pair.forEach(s=>{s.localAuthority=s.controller===node;s.lease={authorityControllerId:node,roomId:'room',epoch,authorityId:'world',isPaused:false};});
    assignLease(f.pre.states,'b',5);assignLease(f.post.states,'a',6);f.confirmed=structuredClone(f.post.states);
    f.args.node='b';f.args.nextController='a';
    const result=await completeNaturalDayHandoff(f.args);
    assert.equal(result.sealedWriter.node,'b');assert.equal(result.coverage.hourly,24);
    assert.equal(f.events.filter(x=>x==='handoff').length,1);
});
test('pre-handoff critical and cycle failures cannot be repaired by checkpoint or trigger a mutation',async()=>{
    for(const change of [f=>f.pre.semantics[1].players[0].gold++,f=>f.pre.semantics[1].npcs[0].members[0].Number++,
        f=>f.pre.semantics[1].npcs[0].members[0].Xp++,f=>f.pre.semantics[1].npcs[0].party='wrong',
        f=>f.pre.semantics[1].npcs[0].unknown={x:1},f=>f.pre.worlds[1].rosterXpHash='b'.repeat(64),
        f=>f.pre.players[1][0].gold++,f=>f.preCycles[0].overflowed=true,f=>f.preCycles[0].rows.shift(),
        f=>f.preCycles[0].simulation['Campaign.Tick.failed']=1,f=>delete f.preCycles[0].simulation['Campaign.LateAITickAux.completed'],
        f=>f.preCycles[0].tickerAudits[0].Mismatches=1,f=>f.modes[0][0]='Play_2x']){
        const f=naturalHandoffFixture();change(f);await assert.rejects(completeNaturalDayHandoff(f.args));
        assert.equal(f.events.includes('handoff'),false);assert.equal(f.events.includes('strict-post'),false);
    }
});
test('post-handoff exact coordinates, resource constancy, Pause and clock are mandatory',async()=>{
    for(const change of [f=>f.post.semantics[1].npcs[0].x++,f=>f.post.semantics[1].npcs[0].y++,
        f=>f.post.semantics.forEach(v=>v.npcs[0].gold++),f=>f.post.semantics.forEach(v=>v.npcs[0].members[0].Xp++),
        f=>f.post.worlds.forEach(v=>v.ticks++),f=>f.post.semantics.forEach(v=>v.ticks++),
        f=>f.modes[1][1]='Play_2x',f=>f.confirmed.forEach(v=>v.lease.epoch=3),
        f=>f.confirmed.forEach(v=>v.lease.roomId='unrelated-room'),
        f=>f.confirmed.forEach(v=>v.lease.authorityControllerId='a')]){
        const f=naturalHandoffFixture();change(f);await assert.rejects(completeNaturalDayHandoff(f.args));
        assert.equal(f.events.filter(x=>x==='handoff').length,1);assert.equal(f.events.includes('native_day_post_handoff'),false);
    }
});
test('old collector is revalidated after barrier; tail exceptions, duplicates, lost coverage and phase reject',async()=>{
    for(const change of [f=>f.tailCycles[0].simulation['Campaign.LateAITickAux.failed']=1,
        f=>f.tailCycles[0].overflowed=true,f=>f.tailCycles[0].rows.push({...f.tailCycles[0].rows[0]}),
        f=>f.tailCycles[0].tickerAudits[0].Mismatches++,f=>f.tailCycles[0].rows.shift(),f=>f.tailCycles[0].failedRealTicks++,
        f=>f.tailCycles[0].simulation['Campaign.LateAITickAux.completed']=0,
        f=>f.tailCycles[1].simulation['Campaign.Tick.completed']=1,
        f=>f.tailCycles[1].scheduler.tickers[0].Index++,f=>f.tailCycles[1].hourly.next++]){
        const f=naturalHandoffFixture();change(f);await assert.rejects(completeNaturalDayHandoff(f.args));
        assert.equal(f.events.includes('strict-post'),true);assert.equal(f.events.includes('tail-cycles'),true);
        assert.equal(f.events.includes('native_day_post_handoff'),false);
    }
});
test('uncertain mutation and confirmation failures preserve failure and never retry yield/auto',async()=>{
    for(const step of ['handoff','confirmed']){
        const f=naturalHandoffFixture(),original=new Error('uncertain '+step);
        f.args[step==='handoff'?'handoff':'confirmWriter']=async()=>{f.events.push(step);throw original;};
        await assert.rejects(completeNaturalDayHandoff(f.args),error=>error===original);
        assert.equal(f.events.filter(x=>x==='handoff').length,1);assert.equal(f.events.includes('strict-post'),false);
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


// Actual Native01 start_run identity, before campaign readiness failed; B was not launched.
const native01 = {runId:'52ed16a2137f4a99a78af8b2608114d1',state:'started',error:null,instances:[{
    name:'server',identity:{pid:22872,startedUtc:'2026-10-19T09:57:27.793423Z',role:'server',platformId:'LocalA',
        runToken:'52ed16a2137f4a99a78af8b2608114d1'},processAlive:true,status:null,processTreeAlive:true,cleanupComplete:false}]};
function startupSession() {
    return {runId:native01.runId,processes:[],launchStatus:{server:'UNKNOWN',client1:'NOT_STARTED'},
        logs:{server:{text:''},client1:{text:''}},cleanupConfirmed:false,stopAttempted:false};
}
const nativeLog = pid => `[(${pid}) 17:57:27 INF Coop.Startup] Real process startup\r\n`;
const readPage = (text,callId=10) => ({qaCallId:callId,result:{text,cursor:'cursor-'+callId,reset:false,hasMore:false}});
const persistLog = async (instance,text) => ({path:instance+'.log',persistedText:text});
const emitLog = async () => {};
function joinedView() {
    return {...structuredClone(native01),instances:[...structuredClone(native01.instances),
        {name:'client1',identity:{...native01.instances[0].identity,pid:22873,role:'client',platformId:'testclient1'},
            processAlive:true,status:null,processTreeAlive:true,cleanupComplete:false}]};
}
function stopView(view) {
    return {...view,state:'stopped',instances:view.instances.map(row=>({...row,processAlive:false,processTreeAlive:false,cleanupComplete:true}))};
}

test('Native01 startup response captures A log identity before readiness and records B NOT_STARTED',async()=>{
    const session=startupSession();captureProcessIdentities(session,native01,'start_run#1');
    assert.equal(session.processes[0].identity.pid,22872);
    assert.equal(session.identitySources[0].source,'start_run#1');
    const reads=[];
    await collectSessionLogs(session,async(instance)=>{reads.push(instance);return readPage(nativeLog(22872));},persistLog,emitLog);
    assert.deepEqual(reads,['server']);assert.equal(session.logs.server.verification,'COMPLETE');
    assert.equal(session.logs.client1.verification,'NOT_STARTED');assert.equal(hasCompleteSessionEvidence(session),false);
});

test('uncertain B launch remains UNKNOWN and fails, but readonly get_run can discover its exact identity',async()=>{
    const session=startupSession();captureProcessIdentities(session,native01,'start_run#1');
    session.launchStatus.client1='UNKNOWN';
    const reads=[];
    await assert.rejects(collectSessionLogs(session,async(instance)=>{reads.push(instance);return readPage(nativeLog(22872));},persistLog,emitLog),/process log evidence/);
    assert.deepEqual(reads,['server']);assert.equal(session.logs.client1.verification,'UNKNOWN');
    captureProcessIdentities(session,joinedView(),'get_run#11');
    assert.equal(session.launchStatus.client1,'STARTED');assert.equal(session.processes[1].identity.pid,22873);
    assert.equal(hasCompleteSessionEvidence(session),false);
});

test('later views cannot swap PID, start time, run token, role, platform or duplicate identities',()=>{
    for(const change of [v=>v.instances[0].identity.pid++,v=>v.instances[0].identity.startedUtc='different',
        v=>v.instances[0].identity.runToken='other',v=>v.instances[0].identity.role='client',
        v=>v.instances[0].identity.platformId='other',v=>v.runId='other']) {
        const session=startupSession();captureProcessIdentities(session,native01,'start_run#1');
        const changed=structuredClone(native01);change(changed);assert.throws(()=>captureProcessIdentities(session,changed,'get_run#2'));
        assert.deepEqual(session.processes[0].identity,native01.instances[0].identity);
    }
    const duplicate=joinedView();duplicate.instances[1].identity.pid=22872;
    assert.throws(()=>captureProcessIdentities(startupSession(),duplicate,'get_run#2'),/share a PID/);
});

test('empty, wrong-PID and genuine Harmony error logs fail without suppressing raw text',async()=>{
    const error=`[(22872) 17:57:27 ERR Coop.Startup] HarmonyException: native init failed\r\n`;
    for(const text of ['',nativeLog(99999),error]) {
        const session=startupSession();captureProcessIdentities(session,native01,'start_run#1');const errors=[];
        await assert.rejects(collectSessionLogs(session,async()=>readPage(text),persistLog,async row=>errors.push(row)),/process log evidence/);
        assert.equal(session.logs.server.text,text);assert.equal(session.logs.server.verification,'FAILED');
        assert.equal(errors.length,1);assert.equal(session.logs.client1.verification,'NOT_STARTED');
        if(text===error)assert.equal(session.logs.server.diagnostics.errorLines.length,1);
    }
});

test('stop can discover uncertain B and requires complete logs from both real processes',async()=>{
    const session=startupSession();captureProcessIdentities(session,native01,'start_run#1');session.launchStatus.client1='UNKNOWN';
    const stopped=stopView(joinedView()), reads=[];
    await stopSession(session,{stopRun:async()=>{captureProcessIdentities(session,stopped,'stop_run#9');return stopped;},
        readProcesses:()=>[],collectLogs:archived=>collectSessionLogs(session,async instance=>{
            reads.push(instance);return readPage(nativeLog(instance==='server'?22872:22873));},persistLog,emitLog,{archived}),save:async()=>{}});
    assert.equal(session.cleanupConfirmed,true);assert.deepEqual(reads,['server','client1']);
    assert.equal(session.logs.client1.verification,'COMPLETE');assert.equal(hasCompleteSessionEvidence(session),false);
    session.twoProcessReady=true;assert.equal(hasCompleteSessionEvidence(session),true);
});

test('successful OS cleanup and failed log verification remain separate and preserve startup failure',async()=>{
    const session=startupSession();captureProcessIdentities(session,native01,'start_run#1');
    const original={message:'HarmonyException: native initialization failed'}, summary={failed:original};
    let saved=false;
    await assert.rejects(stopSession(session,{stopRun:async()=>stopView(native01),readProcesses:()=>[],
        collectLogs:async()=>{throw new Error('archive verification failed');},save:async()=>{saved=true;}}),/archive verification failed/);
    assert.equal(session.cleanupConfirmed,true);assert.equal(session.cleanupError,undefined);
    assert.match(session.logVerificationError,/archive verification failed/);assert.equal(saved,true);
    assert.equal(summary.failed,original);assert.equal(hasCompleteSessionEvidence(session),false);
    await assert.rejects(stopSession(session,{}),/must not be retried/);
});

test('live process or unconfirmed stop fails cleanup and never asserts archive success',async()=>{
    for(const remaining of [[{ProcessId:22872}],[]]) {
        const session=startupSession();captureProcessIdentities(session,native01,'start_run#1');let logReads=0;
        const stopped=stopView(native01);if(remaining.length===0)stopped.instances[0].processTreeAlive=true;
        await assert.rejects(stopSession(session,{stopRun:async()=>stopped,readProcesses:()=>remaining,
            collectLogs:async()=>{logReads++;},save:async()=>{}}));
        assert.equal(session.cleanupConfirmed,false);assert.ok(session.cleanupError);
        assert.equal(session.logVerificationError,undefined);assert.equal(logReads,0);
    }
});

test('dual-client acceptance requires both known identities, both strict logs, readiness and cleanup',async()=>{
    const session=startupSession();captureProcessIdentities(session,joinedView(),'get_run#2');
    await collectSessionLogs(session,async instance=>readPage(nativeLog(instance==='server'?22872:22873)),persistLog,emitLog);
    session.twoProcessReady=true;session.cleanupConfirmed=true;assert.equal(hasCompleteSessionEvidence(session),true);
    for(const change of [s=>s.logs.client1.verification='FAILED',s=>s.launchStatus.client1='UNKNOWN',s=>s.twoProcessReady=false,
        s=>s.cleanupConfirmed=false,s=>s.logVerificationError='archive failed',s=>s.processes.pop()]) {
        const bad=structuredClone(session);change(bad);assert.equal(hasCompleteSessionEvidence(bad),false);
    }
});

test('AI probe requires version, every counter, exact partitions and actual observed entries',()=>{
    const good={...aiEvidence()};assertAiSimulationEvidence(good);
    for(const change of [v=>delete v.aiSimulation,v=>v.aiSimulation.schemaVersion=0,v=>v.aiSimulation.classes.pop(),
        v=>delete v.simulation['MobilePartyAi.Tick.unknown.failed'],v=>v.simulation['MobilePartyAi.Tick.writerNpc.completed']++,
        v=>v.simulation['MobilePartyAi.Tick.observed']=0,v=>v.simulation['MobilePartyAi.Tick.completed']=NaN]){
        const bad=structuredClone(good);change(bad);assert.throws(()=>assertAiSimulationEvidence(bad));
    }
    assert.throws(()=>assertAiSimulationEvidence(aiEvidence('writerNpc',0)));
});
test('confirmed natural window allows own navigation but rejects newly unqualified NPC work',async()=>{
    const clean=naturalHandoffFixture();await completeNaturalDayHandoff(clean.args);
    for(const kind of ['unknown','unqualified','unexpectedWorld','writerNpc','writerPlayer']){
        const f=naturalHandoffFixture(),sim=f.preCycles[1].simulation;
        if(kind==='unexpectedWorld')sim['MobilePartyAi.Tick.unexpectedWorld.completed']++;
        else {sim['MobilePartyAi.Tick.'+kind+'.completed']++;sim['MobilePartyAi.Tick.completed']++;sim['MobilePartyAi.Tick.observed']++;}
        await assert.rejects(completeNaturalDayHandoff(f.args));
    }
    const f=naturalHandoffFixture();
    for(const pair of [f.beforeCycles,f.preCycles,f.tailCycles])for(const receipt of pair) {
        receipt.simulation['MobilePartyAi.Tick.unknown.completed']+=100;
        receipt.simulation['MobilePartyAi.Tick.unexpectedWorld.completed']+=100;
        receipt.simulation['MobilePartyAi.Tick.completed']+=100;receipt.simulation['MobilePartyAi.Tick.observed']+=100;
    }
    await completeNaturalDayHandoff(f.args);
});
