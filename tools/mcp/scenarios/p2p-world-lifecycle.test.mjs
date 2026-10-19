import assert from 'node:assert/strict';
import test from 'node:test';
import { captureProcessIdentities, collectSessionLogs, hasCompleteSessionEvidence, stopSession, options, assertSemantic, assertPersistent, assertCycles, assertCycleWindow, assertSchedulerHandoff, assertColdLease, freshSaveName, combineCyclePages } from './p2p-world-lifecycle.mjs';
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
