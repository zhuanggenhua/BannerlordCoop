import assert from 'node:assert/strict';
import { collectNativeLog } from './native-log-archive.mjs';
import { randomUUID, createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { appendFile, mkdir, readFile, realpath, stat, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { assertActors, assertPlayers, assertWorld, assertWriter, assertProcessEvidence,
    field, writer, epoch, jsonOutput, logDiagnostics, assertProcessLogCompleteness } from './p2p-udp-reliability.mjs';

export const HELP = `Usage: node tools/mcp/scenarios/p2p-world-lifecycle.mjs --natural|--cold-save
  --repo <coordinator checkout> --server <CoopMcpServer.exe> --config <profiles.json>
  --sdk-root <node_modules> --record <new evidence.jsonl>
  [--profile local] [--save P1B_DirectIP_Recruit] [--day-timeout-seconds 900]
Coordinator/exclusive game lock only. No deployment. Natural mode advances each
writer through24 native hours with ordinary Play_2x (the actual public enum).
At day end, ordinary Pause checks critical state before one original handoff;
the confirmed barrier then requires exact NPC positions and seals cycle tails.
Cold mode saves a unique native save+sidecar while A is writer and paused,
stops both process trees, loads that exact save into new processes and rejoins B.
Compares handle-free ALL-MobileParty count/XP hashes, players and24 NPC details.
All ERR/FTL require investigation. No clock setters, artificial daily callbacks,
private IPC, save-byte edits, mutation retries or automatic acceptance relaxations.
--help/import do not load SDK, connect or launch games.`;
const INSTANCES = ['server', 'client1'];
const REQUIRED = ['coop.debug.authority.state', 'coop.debug.authority.world_state', 'coop.debug.authority.semantic_state',
    'coop.debug.authority.cycles', 'coop.debug.authority.availability', 'coop.debug.players.party_state', 'coop.debug.get_time_mode'];
export function options(argv) {
    const result = { profile: 'local', save: 'P1B_DirectIP_Recruit', 'day-timeout-seconds': 900 };
    const seen = new Set(), allowed = new Set(['repo','server','config','sdk-root','record','profile','save','day-timeout-seconds']);
    for (let i = 0; i < argv.length; i++) {
        const key = argv[i]?.replace(/^--/, '');
        assert.ok(argv[i]?.startsWith('--') && !seen.has(key), 'Invalid/duplicate option: ' + argv[i]); seen.add(key);
        if (key === 'natural' || key === 'cold-save') { assert.ok(!result.mode, 'Choose one mode'); result.mode = key; continue; }
        assert.ok(allowed.has(key) && argv[i+1] && !argv[i+1].startsWith('--'), 'Invalid option: ' + argv[i]);
        result[key] = argv[++i];
    }
    assert.ok(result.mode, 'Choose --natural or --cold-save');
    for (const key of ['repo','server','config','sdk-root','record']) { assert.ok(result[key], 'Missing --' + key); result[key] = resolve(result[key]); }
    result['day-timeout-seconds'] = Number(result['day-timeout-seconds']);
    assert.ok(Number.isInteger(result['day-timeout-seconds']) && result['day-timeout-seconds'] >= 120 && result['day-timeout-seconds'] <= 3600);
    return result;
}
export function assertSemantic(value) {
    assert.ok(value.campaign && value.partyCount > 0 && value.aliveHeroes > 0);
    assert.ok(Number.isInteger(value.ticks)); assert.equal(value.missingPartyIds, 0); assert.deepEqual(value.duplicatePartyIds, []);
    assert.ok(value.activePartyCount > 0 && value.activePartyCount <= value.partyCount);
    for (const key of ['worldIdentityHash','worldRostersHash','activeWorldRostersHash','heroGoldHash','volunteerHash']) assert.match(value[key], /^[A-Fa-f0-9]{64}$/);
    assert.ok(value.sampledNpcIds.length > 0 && value.sampledNpcIds.length <= 24);
    assert.equal(new Set(value.sampledNpcIds).size, value.sampledNpcIds.length);
    assert.equal(value.npcs.length, value.sampledNpcIds.length); assert.equal(value.players.length, 2);
    assert.equal(new Set(value.players.map(row => row.controller)).size, 2);
}
export function assertPersistent(before, after) {
    assertSemantic(before); assertSemantic(after);
    assert.deepEqual(after, before, 'Cold restart changed handle-free semantic state (no inactive-party or clock tolerance)');
}
export function assertPreHandoffSemantic(left, right) {
    assertSemantic(left); assertSemantic(right);
    const withoutPredictedPositions = value => ({...value,npcs:value.npcs.map(npc=>{
        assert.ok(Object.hasOwn(npc,'x') && Object.hasOwn(npc,'y') && Number.isFinite(npc.x) && Number.isFinite(npc.y),
            'Pre-handoff NPC position missing/nonfinite');
        const {x,y,...critical}=npc;return critical;
    })});
    assert.deepEqual(withoutPredictedPositions(right),withoutPredictedPositions(left),
        'Pre-handoff non-position semantic mismatch');
    return left.npcs.map((npc,index)=>({index,party:npc.party,
        left:{x:npc.x,y:npc.y},right:{x:right.npcs[index].x,y:right.npcs[index].y},
        dx:right.npcs[index].x-npc.x,dy:right.npcs[index].y-npc.y}));
}
export function assertColdLease(before, after, initial) {
    assertWriter(after, initial[0].controller, initial);
    if (field(before.lease,'roomId') !== field(after[0].lease,'roomId')) return 'new-room-fence';
    assert.ok(epoch(after[0]) > epoch(before) || field(after[0].lease,'authorityId') !== field(before.lease,'authorityId'),
        'Cold restart reused room/authority/epoch without a new fence');
    return epoch(after[0]) > epoch(before) ? 'new-epoch-fence' : 'new-authority-fence';
}
const AI_CLASSES=['writerNpc','writerPlayer','observerLocalNavigation','receiveApply','unqualified','unknown'];
const AI_OUTCOMES=['completed','failed','skipped'];
const aiKey=(kind,outcome)=>'MobilePartyAi.Tick.'+(kind?kind+'.':'')+outcome;
export function assertAiSimulationEvidence(receipt, { allowZeroObserved = false } = {}) {
    assert.equal(typeof allowZeroObserved,'boolean','AI zero-coverage option must be a boolean');
    assert.equal(receipt.aiSimulation?.schemaVersion,1,'AI role probe schema unavailable');
    assert.deepEqual(receipt.aiSimulation.classes,AI_CLASSES,'AI role partitions unavailable');
    assert.ok(Array.isArray(receipt.aiSimulation.firstEntries) && receipt.aiSimulation.firstEntries.length<=36,'AI first evidence unavailable');
    const counters=receipt.simulation;
    const count=name=>{
        assert.ok(Object.hasOwn(counters??{},name),'AI probe counter unavailable: '+name);
        assert.ok(Number.isSafeInteger(counters[name]) && counters[name]>=0,'Invalid AI probe counter: '+name);
        return counters[name];
    };
    for(const outcome of AI_OUTCOMES) {
        const sum=AI_CLASSES.reduce((total,kind)=>total+count(aiKey(kind,outcome)),0);
        assert.equal(count(aiKey('',outcome)),sum,'AI role partition differs from raw '+outcome);
        assert.ok(count(aiKey('unexpectedWorld',outcome))<=count(aiKey('',outcome)),'Unsafe AI count exceeds raw '+outcome);
    }
    assert.equal(count(aiKey('','observed')),AI_OUTCOMES.reduce((sum,outcome)=>sum+count(aiKey('',outcome)),0),'AI probe coverage differs from raw');
    if(!allowZeroObserved) assert.ok(count(aiKey('','observed'))>0,'AI probe has no actual entry coverage');
    count(aiKey('beforeOriginal','failed'));
    return counters;
}
function assertAiWindow(before,after,writer) {
    const start=assertAiSimulationEvidence(before),end=assertAiSimulationEvidence(after);
    for(const key of Object.keys(start).filter(key=>key.startsWith('MobilePartyAi.Tick.')))
        assert.ok(end[key]>=start[key],'AI counter moved backwards: '+key);
    for(const kind of ['unexpectedWorld','unknown','unqualified'])
        for(const outcome of ['completed','failed'])
            assert.equal(end[aiKey(kind,outcome)],start[aiKey(kind,outcome)],'Unqualified AI in confirmed window: '+kind+'.'+outcome);
    const positive=writer?'writerNpc':'observerLocalNavigation';
    assert.ok(end[aiKey(positive,'completed')]>start[aiKey(positive,'completed')],'Qualified AI not observed: '+positive);
    if(!writer)for(const kind of ['writerNpc','writerPlayer'])
        assert.equal(end[aiKey(kind,'completed')],start[aiKey(kind,'completed')],'Observer advanced authoritative AI: '+kind);
}
export function assertCycles(receipts) {
    assert.equal(receipts.length, 2); assert.equal(receipts[0].campaign, receipts[1].campaign);
    assert.notEqual(receipts[0].instance, receipts[1].instance);
    const counts = new Map(), rows = [];
    for (const receipt of receipts) {
        assert.equal(receipt.overflowed, false);
        assertAiSimulationEvidence(receipt);
        for (const row of receipt.rows) {
            assert.equal(field(row,'leaseKnown'), true, 'Native cycle dispatched without known lease');
            assert.equal(field(row,'localWriter'), true, 'Observer dispatched native world cycle');
            assert.equal(field(row,'node'), field(row,'writerController'), 'Cycle node was not its recorded writer');
            assert.ok(field(row,'epoch') > 0 && field(row,'roomId') && field(row,'authorityId'));
            const kind = field(row,'kind'), party = field(row,'party'), due = field(row,'dueTicks');
            assert.ok(['HourlyTick','DailyTick','DailyTickParty'].includes(kind));
            if (kind === 'DailyTickParty') assert.ok(party, 'Party daily callback missing native StringId');
            assert.ok(Number.isInteger(field(row,'count')) && field(row,'count') > 0);
            if (kind !== 'DailyTickParty') {
                assert.ok(Number.isSafeInteger(due) && due >= 0, 'Native hourly/daily due slot unavailable');
                const key = JSON.stringify([receipt.campaign,kind,due]);
                const count = (counts.get(key) ?? 0) + field(row,'count'); counts.set(key,count);
                assert.equal(count,1,'Repeated native due slot across nodes/epochs: ' + key);
            }
            rows.push(row);
        }
        assert.ok(Array.isArray(receipt.tickerAudits), 'Native ticker audit unavailable');
        for(const audit of receipt.tickerAudits) {
            assert.equal(field(audit,'leaseKnown'),true);assert.equal(field(audit,'localWriter'),true);
            assert.equal(field(audit,'mismatches'),0,JSON.stringify(audit));
            assert.equal(field(audit,'expected'),field(audit,'completed'),'Native ticker callback count mismatch');
            assert.equal(field(audit,'expectedHash'),field(audit,'completedHash'),'Native ticker identity/order mismatch');
        }
        assert.equal(receipt.duplicateRealTicks,0,'Repeated Campaign.RealTick');
        assert.equal(receipt.failedRealTicks,0,'Failed Campaign.RealTick');
        for(const [name,count] of Object.entries(receipt.simulation ?? {}))
            if(name.endsWith('.failed'))assert.equal(count,0,'Native simulation exception: '+name);
        for(const effect of receipt.effects ?? []) {
            assert.equal(field(effect,'known'),true);assert.equal(field(effect,'localWriter'),true);
            assert.equal(field(effect,'error'),null,'Native finance exception');assert.ok(field(effect,'entity'));
        }
    }
    return { entries:rows.length, hourly:rows.filter(row => field(row,'kind')==='HourlyTick').length,
        daily:rows.filter(row => field(row,'kind')==='DailyTick').length,
        partyDaily:rows.filter(row => field(row,'kind')==='DailyTickParty').length, receipts };
}
export function assertCycleWindow(before, after, node) {
    const index=before.findIndex(value=>value.node===node || value.controller===node);
    assert.ok(index >= 0,'Writer receipt unavailable');
    const start=before[index],end=after[index];
    assert.equal(end.instance,start.instance);assert.ok(end.hours >= start.hours+24,'Native 24h not reached');
    const coverage={sameFrameRealTickCoverage:end.sameFrameRealTickCoverage??'NOT_COVERED'};
    for(const [timer,kind] of [['hourly','HourlyTick'],['daily','DailyTick']]) {
        assert.equal(end[timer].period,start[timer].period);assert.ok(start[timer].period>0);
        const expected=[];
        for(let due=start[timer].next;due<end[timer].next;due+=start[timer].period)expected.push(due);
        assert.equal(expected.at(-1)+start[timer].period,end[timer].next,'Native scheduler phase changed');
        assert.ok(expected.length >= (timer==='hourly'?24:1),'Missing native '+timer+' cadence');
        const actual=after.flatMap(receipt=>receipt.rows).filter(row=>field(row,'kind')===kind &&
            field(row,'dueTicks')>=start[timer].next && field(row,'dueTicks')<end[timer].next);
        assert.deepEqual(actual.map(row=>field(row,'dueTicks')).sort((a,b)=>a-b),expected,'Missing/repeated native due slot');
        assert.ok(actual.every(row=>field(row,'node')===node),'Other node executed writer window');coverage[timer]=expected.length;
    }
    const required=['_mobilePartyHourlyTicker','_mobilePartyDailyTicker','_dailyTickPartyTicker','_hourlyTickMobilePartyTicker',
        '_hourlyTickSettlementTicker','_hourlyTickClanTicker','_dailyTickTownTicker','_dailyTickSettlementTicker',
        '_dailyTickHeroTicker','_dailyTickClanTicker','_quarterDailyPartyTicker',
        ...['caravan','garrison','militia','villager','custom','bandit','lord','partiesWithoutPartyComponents','patrol']
            .map(name=>name==='patrol'?'_patrolPartyPartialHourlyAiEventTicker':'_'+name+(name==='partiesWithoutPartyComponents'?'':'MobileParty')+'PartialHourlyAiEventTicker')];
    coverage.tickers={};
    for(const ticker of required) {
        const total=(receipt,key)=>receipt.tickerAudits.filter(row=>field(row,'ticker')===ticker).reduce((sum,row)=>sum+field(row,key),0);
        const batches=total(end,'batches')-total(start,'batches'),completed=total(end,'completed')-total(start,'completed');
        assert.ok(batches>0,'Native ticker never scheduled: '+ticker);
        if(!ticker.includes('PartialHourlyAi'))assert.ok(completed>0,'Native ticker completed no callbacks: '+ticker);
        coverage.tickers[ticker]={batches,completed};
    }
    assertAiWindow(start,end,true);
    for(const name of ['MobilePartyAi.Tick.completed','Campaign.RealTick.completed','Campaign.Tick.completed','Campaign.LateAITickAux.completed','Campaign.WaitAsyncTasks.completed'])
        assert.ok((end.simulation[name]??0)>(start.simulation[name]??0),'Native simulation did not complete: '+name);
    const effects=(end.effects??[]).filter(row=>field(row,'ticks')>=start.ticks && field(row,'ticks')<=end.ticks);
    const finances=effects.filter(row=>field(row,'kind')==='ClanFinance'),wages=effects.filter(row=>field(row,'kind')==='Wage');
    assert.ok(finances.length>0,'Daily finance effects NOT_OBSERVED');assert.ok(wages.length>0,'Native wage application evaluations NOT_OBSERVED');
    coverage.economy={financeEffects:finances.length,wageEvaluations:wages.length,
        goldChanges:finances.filter(row=>field(row,'goldBefore')!==field(row,'goldAfter')).length,
        calculatedWages:wages.reduce((sum,row)=>sum+field(row,'wage'),0),effects,
        paymentAcceptance:'NOT_OBSERVED',paymentLimit:'Wage model evaluations and net clan gold changes do not identify the exact wage deduction causal action.'};
    for(const receipt of after.filter(value=>value.node!==node)) {
        const prior=before.find(value=>value.node===receipt.node);
        assertAiWindow(prior,receipt,false);
        for(const name of ['Campaign.Tick.completed'])
            assert.equal(receipt.simulation[name]??0,prior.simulation[name]??0,'Observer advanced authoritative simulation: '+name);
    }
    return coverage;
}
export function assertSchedulerHandoff(previous, next) {
    assert.ok(previous.scheduler,'Confirmed writer scheduler unavailable');
    for(const receipt of next) {
        assert.deepEqual(receipt.scheduler,previous.scheduler,'Handoff lost native ticker identity/order, debt or cursor');
        assert.deepEqual(receipt.hourly,previous.hourly,'Handoff lost hourly phase');
        assert.deepEqual(receipt.daily,previous.daily,'Handoff lost daily phase');
    }
}
export async function completeNaturalDayHandoff({node,nextController,initial,beforeCycles,
    pause,readPreSnapshot,readCycles,handoff,confirmWriter,readStrictSnapshot,readTimeModes,record}) {
    const assertPausedModes = modes => {
        assert.equal(modes.length,2);assert.deepEqual(modes,['Pause','Pause'],'Natural handoff resumed world time');
    };
    await pause();
    const pre=await readPreSnapshot();assertWriter(pre.states,node,initial);
    assertWorld(pre.worlds);assertPlayers(pre.players);assertPreHandoffSemantic(...pre.semantics);
    assertPausedModes(await readTimeModes());
    const preCycles=assertCycles(await readCycles());
    const preCoverage=assertCycleWindow(beforeCycles,preCycles.receipts,node);
    await record({phase:'native_day_pre_handoff',node,nextController,pre,preCycles,preCoverage});
    await handoff(); // Mutation exactly once; read-only confirmation must not retry it.
    const confirmed=await confirmWriter();assertWriter(confirmed,nextController,initial);
    assert.ok(epoch(confirmed[0])>epoch(pre.states[0]),'Natural handoff did not advance Epoch');
    assert.equal(field(confirmed[0].lease,'roomId'),field(pre.states[0].lease,'roomId'),'Natural handoff changed room');
    assertPausedModes(await readTimeModes());
    const post=await readStrictSnapshot(pre.semantics[0].sampledNpcIds);
    assertWriter(post.states,nextController,initial);
    assertWorld(post.worlds);assertPlayers(post.players);post.semantics.forEach(assertSemantic);
    assert.deepEqual(post.semantics[1],post.semantics[0],'Live semantic world mismatch');
    for(let i=0;i<2;i++) {
        for(const key of ['roomId','authorityId','epoch'])
            assert.equal(field(post.states[i].lease,key),field(confirmed[i].lease,key),'Confirmed handoff lease changed');
        assert.equal(post.worlds[i].ticks,pre.worlds[i].ticks,'Natural handoff advanced paused world clock');
        assert.equal(post.semantics[i].ticks,pre.semantics[i].ticks,'Natural handoff advanced semantic clock');
        assertWorld([pre.worlds[i],post.worlds[i]]);
        assertPlayers([pre.players[i],post.players[i]]);
        assertPreHandoffSemantic(pre.semantics[i],post.semantics[i]);
    }
    // Re-read the original node's collector after the barrier drained its pending AI.
    // Keep full-history failures/duplicates and the complete original day window strict.
    const cycles=assertCycles(await readCycles());
    const coverage=assertCycleWindow(beforeCycles,cycles.receipts,node);
    const sealedWriter=cycles.receipts.find(receipt=>receipt.node===node);
    assertSchedulerHandoff(sealedWriter,cycles.receipts);
    await record({phase:'native_day_post_handoff',node,nextController,confirmed,post,cycles,coverage});
    return {pre,preCycles,preCoverage,confirmed,post,cycles,coverage,sealedWriter};
}
export function combineCyclePages(pages) {
    assert.ok(pages.length > 0); const first=pages[0], rows=[];
    assert.ok(first.totalRows <= 32768 && first.totalRows >= 0);
    for(const page of pages) {
        for(const key of ['instance','campaign','processId','totalRows','overflowed'])assert.equal(page[key],first[key],'Cycle pages changed: '+key);
        assert.equal(page.offset,rows.length);assert.ok(page.rows.length<=256);
        rows.push(...page.rows);assert.equal(page.nextOffset,rows.length);
        assert.equal(page.hasMore,rows.length<first.totalRows);
        assert.ok(page.rows.length>0||!page.hasMore,'Cycle page did not advance');
    }
    assert.equal(pages.at(-1).hasMore,false,'Incomplete cycle pages');assert.equal(rows.length,first.totalRows);
    return {...first,rows};
}
export function freshSaveName(now = new Date(), suffix = randomUUID().slice(0,8)) {
    const name = 'P2P_Cold_' + now.toISOString().replace(/[-:.]/g,'') + '_' + suffix;
    assert.match(name,/^[A-Za-z0-9_-]{1,64}$/); return name;
}

export function captureProcessIdentities(session, view, source) {
    assert.equal(view.runId, session.runId, 'Process receipt belongs to another run');
    session.processes ??= []; session.identitySources ??= [];
    session.launchStatus ??= Object.fromEntries(INSTANCES.map(name => [name, 'NOT_STARTED']));
    for (const row of view.instances ?? []) {
        assert.ok(INSTANCES.includes(row.name), 'Unexpected native instance');
        if (!row.identity) { assert.ok(!row.processAlive, 'Live process has no identity'); continue; }
        assert.ok(Number.isSafeInteger(row.identity.pid) && row.identity.pid > 0, 'Missing actual native PID');
        assert.equal(row.identity.runToken, session.runId, 'Native process token does not match run');
        assert.ok(row.identity.startedUtc && row.identity.role && row.identity.platformId, 'Incomplete native identity');
        const index = session.processes.findIndex(value => value.name === row.name);
        if (index >= 0) assert.deepEqual(row.identity, session.processes[index].identity, 'Native process identity changed: ' + row.name);
        assert.ok(!session.processes.some(value => value.name !== row.name && value.identity.pid === row.identity.pid), 'Two instances share a PID');
        if (index < 0) session.processes.push(row); else session.processes[index] = row;
        session.launchStatus[row.name] = 'STARTED';
        session.identitySources.push({ source, instance: row.name, identity: structuredClone(row.identity) });
    }
}

export async function collectSessionLogs(session, readLogs, persist, emit, { archived = false, rawRecord } = {}) {
    const failures = [];
    for (const instance of INSTANCES) {
        const log = session.logs[instance], process = session.processes?.find(value => value.name === instance);
        if (!process) {
            log.verification = session.launchStatus[instance] === 'NOT_STARTED' ? 'NOT_STARTED' : 'UNKNOWN';
            if (log.verification === 'UNKNOWN') failures.push(instance + ': launch identity remains unknown');
            continue;
        }
        try {
            await collectNativeLog(log, cursor => readLogs(instance, cursor),
                { archived, source: { runId: session.runId, instance, rawRecord } });
            Object.assign(log, await persist(instance, log.text));
            log.diagnostics = logDiagnostics(log.text);
            log.completeness = assertProcessLogCompleteness(log.text, process.identity.pid);
            assert.equal(log.diagnostics.errorLines.length + log.diagnostics.realTick + log.diagnostics.duplicateIds + log.diagnostics.aiBatch, 0,
                'Runtime ERR/FTL or critical diagnostics require investigation');
            log.verification = 'COMPLETE';
        } catch (error) {
            log.verification = 'FAILED'; log.error = error.stack; failures.push(instance + ': ' + error.message);
            await emit({ phase: 'log_evidence_error', instance, error: error.stack, collection: log.collections?.at(-1) });
        }
    }
    assert.deepEqual(failures, [], 'Incomplete or failed process log evidence');
}

export function hasCompleteSessionEvidence(session) {
    return session.twoProcessReady === true && session.cleanupConfirmed === true && !session.cleanupError &&
        !session.logVerificationError && INSTANCES.every(instance => session.launchStatus[instance] === 'STARTED' &&
            session.processes?.some(value => value.name === instance) && session.logs[instance].verification === 'COMPLETE');
}

export async function stopSession(session, { stopRun, readProcesses, collectLogs, save }) {
    assert.equal(session.stopAttempted, false, 'Uncertain stop mutations must not be retried'); session.stopAttempted = true;
    try {
        const stopped = await stopRun(); session.stopped = stopped;
        assert.equal(stopped.state, 'stopped'); assert.ok(stopped.instances.length > 0);
        assert.deepEqual(stopped.instances.map(row => row.name).sort(), session.processes.map(row => row.name).sort(), 'Stop receipt omitted an observed process');
        assert.ok(stopped.instances.every(row => row.cleanupComplete && !row.processAlive && !row.processTreeAlive));
        session.afterStopProcesses = await readProcesses();
        assert.equal(session.afterStopProcesses.length, 0, 'Bannerlord process remained after stop'); session.cleanupConfirmed = true;
    } catch (error) { session.cleanupError = error.stack; throw error; }
    try { await collectLogs(true); }
    catch (error) { session.logVerificationError = error.stack; throw error; }
    finally { await save(); }
}

export async function run(config) {
    assert.ok((await stat(join(config.repo,'.git'))).isDirectory(), 'Coordinator checkout required');
    assert.equal(await realpath(config.repo),await realpath(execFileSync('git',['rev-parse','--show-toplevel'],{cwd:config.repo,encoding:'utf8',windowsHide:true}).trim()));
    const profile = JSON.parse(await readFile(config.config,'utf8')).profiles?.[config.profile];
    assert.equal(profile?.listenAuthority,true); const controllers = [profile.serverPlatformId,profile.clientPlatformIds?.[0]];
    assert.ok(controllers.every(Boolean) && controllers[0]!==controllers[1]);
    assert.ok(!profile.clientProxyPort,'Use a direct profile; lifecycle mode does not start a UDP proxy');
    const sdk = join(config['sdk-root'],'@modelcontextprotocol/sdk/dist/esm/client');
    const [{Client},{StdioClientTransport}] = await Promise.all([import(pathToFileURL(join(sdk,'index.js')).href),import(pathToFileURL(join(sdk,'stdio.js')).href)]);
    await mkdir(dirname(config.record),{recursive:true}); await writeFile(config.record,'',{flag:'wx'});
    const summaryPath = config.record+'.summary.json'; await writeFile(summaryPath,'{}',{flag:'wx'});
    const summary = {scenario:'p2p-world-lifecycle',config,codeHead:execFileSync('git',['rev-parse','HEAD'],{cwd:config.repo,encoding:'utf8',windowsHide:true}).trim(),
        startedUtc:new Date().toISOString(),sessions:[],features:[],screenshots:[],passed:false,failed:null};
    const client = new Client({name:'codex-world-lifecycle',version:'1.0.0'});
    const transport = new StdioClientTransport({command:config.server,args:['--config',config.config],cwd:config.repo,stderr:'inherit',maxBufferSize:16*1024*1024});
    let session, initial, callId = 0, readDeadline, finalNaturalHandoff;
    const emit = entry => appendFile(config.record,JSON.stringify({utc:new Date().toISOString(),...entry})+'\n');
    const save = () => writeFile(summaryPath,JSON.stringify(summary,null,2)+'\n');
    async function call(name,args,mutation=false) {
        assert.ok(!(readDeadline && mutation),'Poll callbacks cannot mutate');
        const timeout = readDeadline ? readDeadline-Date.now() : 360000; assert.ok(timeout>0,'Read deadline expired');
        const id = ++callId;
        if(name==='start_client' && session)session.launchStatus.client1='UNKNOWN';
        await emit({phase:'request',callId:id,name,args,mutation}); let raw;
        try { raw = await client.callTool({name,arguments:args},undefined,{timeout}); }
        catch(error) { await emit({phase:'transport_error',callId:id,name,error:error.stack,outcomeUncertain:mutation}); throw error; }
        const data = raw.structuredContent ?? JSON.parse(raw.content.find(item=>item.type==='text').text);
        await emit({phase:'response',callId:id,name,args,data,raw});
        if(name==='start_run' && data.runId) {
            session = {runId:data.runId,saveName:args.save_name,processes:[],launchStatus:Object.fromEntries(INSTANCES.map(n=>[n,'NOT_STARTED'])),
                logs:Object.fromEntries(INSTANCES.map(n=>[n,{text:'',verification:'NOT_STARTED'}])),cleanupConfirmed:false,stopAttempted:false};
            summary.sessions.push(session); session.launchStatus.server='UNKNOWN';captureProcessIdentities(session,data,'start_run#'+id);
        }
        if(name==='start_client' && data.run)captureProcessIdentities(session,data.run,'start_client#'+id);
        if((name==='get_run'||name==='stop_run') && session)captureProcessIdentities(session,data,name+'#'+id);
        assert.ok(!raw.isError,JSON.stringify(data)); return {...data,qaCallId:id};
    }
    async function command(instance,name,args=[],mutation=false) {
        const result = await call('execute_command',{run_id:session.runId,instance,name,arguments:args},mutation);
        assert.equal(result.ok,true,JSON.stringify(result)); assert.equal(result.result.succeeded,true,JSON.stringify(result)); return result.result.output;
    }
    const state = n => command(n,'coop.debug.authority.state').then(jsonOutput), both = () => Promise.all(INSTANCES.map(state));
    async function poll(label,read,timeout=45000) {
        const deadline=Date.now()+timeout; let last;
        do { const previous=readDeadline; try { readDeadline=deadline; return await read(); }
            catch(error) { last=error; await emit({phase:'poll_pending',label,error:error.message}); }
            finally { readDeadline=previous; } await delay(500);
        } while(Date.now()<deadline); throw new Error(label+' did not converge: '+last?.message,{cause:last});
    }
    const awaitWriter = controller => poll('writer '+controller,async()=>{const values=await both();assertWriter(values,controller,initial);return values;});
    async function wait(instance,stateName) {
        const value=await call('wait_for_state',{run_id:session.runId,instance,state:stateName,timeout_seconds:300}); assert.equal((value.result??value).reached,true,JSON.stringify(value));
    }
    async function snapshot(label,npcIds) {
        return poll(label,async()=>{
            const worlds=await Promise.all(INSTANCES.map(n=>command(n,'coop.debug.authority.world_state').then(jsonOutput))); assertWorld(worlds);
            const players=[];for(const n of INSTANCES){const rows=[];for(const controller of controllers)rows.push(jsonOutput(await command(n,'coop.debug.players.party_state',[controller])));players.push(rows);}assertPlayers(players);
            const left=jsonOutput(await command('server','coop.debug.authority.semantic_state',npcIds?[npcIds.join(',')]:[])); assertSemantic(left);
            const right=jsonOutput(await command('client1','coop.debug.authority.semantic_state',[left.sampledNpcIds.join(',')])); assertSemantic(right); assert.deepEqual(right,left,'Live semantic world mismatch');
            const states=await both();assertActors(states,initial);return {worlds,players,semantics:[left,right],states};
        });
    }
    async function pause(label,npcIds) { await command('server','coop.debug.set_time_mode',['Pause','force-live-test'],true);return snapshot(label,npcIds); }
    async function preHandoffSnapshot(label) {
        return poll(label,async()=>{
            const worlds=await Promise.all(INSTANCES.map(n=>command(n,'coop.debug.authority.world_state').then(jsonOutput)));assertWorld(worlds);
            const players=[];for(const n of INSTANCES){const rows=[];for(const controller of controllers)rows.push(jsonOutput(await command(n,'coop.debug.players.party_state',[controller])));players.push(rows);}assertPlayers(players);
            const left=jsonOutput(await command('server','coop.debug.authority.semantic_state'));assertSemantic(left);
            const right=jsonOutput(await command('client1','coop.debug.authority.semantic_state',[left.sampledNpcIds.join(',')]));
            const predictedNpcPositions=assertPreHandoffSemantic(left,right);
            const states=await both();assertActors(states,initial);return {worlds,players,semantics:[left,right],states,predictedNpcPositions};
        });
    }
    const pausedTimeModes = () => poll('natural handoff both time modes Pause',async()=>{
        const modes=await Promise.all(INSTANCES.map(n=>command(n,'coop.debug.get_time_mode').then(value=>value.trim())));
        assert.deepEqual(modes,['Pause','Pause']);return modes;
    });
    async function collectLogs(archived=false) {
        return collectSessionLogs(session,(instance,cursor)=>call('read_logs',{run_id:session.runId,instance,cursor,max_bytes:65536}),
            async(instance,text)=>{const path=config.record+'.'+session.runId+'.'+instance+'.log';await writeFile(path,text);
                return {path,sha256:createHash('sha256').update(text).digest('hex')};},emit,{archived,rawRecord:config.record});
    }
    async function screenshot(n,label) {
        const requested=await call('screenshot',{run_id:session.runId,instance:n},true);assert.equal(requested.ok,true);const id=requested.result.captureId;assert.ok(id);
        const capture=await poll('BMP '+id,async()=>{const r=await call('screenshot_status',{run_id:session.runId,instance:n,capture_id:id});assert.equal(r.ok,true);const c=r.result;assert.ok(c.complete&&c.headerValid&&c.isBmp&&c.stable&&c.path&&c.sha256);return c;},60000);
        summary.screenshots.push({runId:session.runId,instance:n,label,...capture});
    }
    async function feature(name,action) {
        const row={name,status:'RUNNING',startedUtc:new Date().toISOString()};summary.features.push(row);await save();
        try { row.evidence=await action();await collectLogs();for(const n of INSTANCES)await screenshot(n,name);row.status='PASS'; }
        catch(error){row.status='FAIL';row.error=error.stack;try{await collectLogs();}catch(diagnostic){row.logError=diagnostic.stack;}for(const n of INSTANCES){try{await screenshot(n,name+'-failed');}catch(diagnostic){row.captureError=diagnostic.stack;}}throw error;}
        finally {row.completedUtc=new Date().toISOString();await emit({phase:'feature_result',feature:row});await save();}
    }
    function processes() {
        const text=execFileSync('powershell.exe',['-NoProfile','-Command',"Get-CimInstance Win32_Process | Where-Object { $_.Name -like '*Bannerlord*' } | Select-Object Name,ProcessId,ParentProcessId,CreationDate,CommandLine | ConvertTo-Json -Depth 4"],{encoding:'utf8',windowsHide:true});
        const rows=text.trim()?JSON.parse(text):[];return Array.isArray(rows)?rows:[rows];
    }
    async function start(saveName) {
        const started=await call('start_run',{profile:config.profile,client_count:0,save_name:saveName},true);assert.equal(started.error,null);await wait('server','readyForCampaignTests');
        await command('server','coop.debug.set_time_mode',['Pause','force-live-test'],true);
        const launch=await call('start_client',{run_id:session.runId,client_index:1},true);assert.equal(launch.outcome,'launched');await wait('client1','readyToJoin');
        const joined=await call('join_client',{run_id:session.runId,instance:'client1'},true);assert.equal(joined.ok,true);await wait('client1','readyForCampaignTests');
        initial=await both();assert.deepEqual(initial.map(row=>row.controller),controllers);await awaitWriter(controllers[0]);
        const view=await call('get_run',{run_id:session.runId});assert.equal(view.instances.length,2);session.osProcesses=processes();
        assertProcessEvidence(session.osProcesses,view.instances.map(row=>row.identity.pid));
        const assemblies=row=>row.status.loadedAssemblies.map(x=>[x.name,x.mvid]).sort();assert.deepEqual(assemblies(view.instances[0]),assemblies(view.instances[1]));
        for(const n of INSTANCES){const catalog=JSON.stringify(await call('list_commands',{run_id:session.runId,instance:n}));for(const name of REQUIRED)assert.ok(catalog.includes(name),'Missing '+name);}
        session.twoProcessReady=true;
    }
    async function stop() {
        return stopSession(session,{stopRun:()=>call('stop_run',{run_id:session.runId},true),readProcesses:processes,
            collectLogs,save});
    }
    async function cycleEvidence() {
        const receipts=[];
        for(const instance of INSTANCES){const sections={};
            for(const section of ['cycles','effects']){const pages=[];let offset=0;
                for(let index=0;index<128;index++){
                    const page=jsonOutput(await command(instance,'coop.debug.authority.cycles',[String(offset),section]));pages.push(page);
                    assert.equal(page.section,section);
                    if(!page.hasMore)break;assert.ok(page.nextOffset>offset,'Cycle page did not advance');offset=page.nextOffset;
                }
                sections[section]=combineCyclePages(pages);
            }
            assert.equal(sections.effects.instance,sections.cycles.instance);
            receipts.push({...sections.cycles,node:controllers[INSTANCES.indexOf(instance)],effects:sections.effects.rows});
        }
        return assertCycles(receipts);
    }
    async function naturalDay(instance,nextController,availability) {
        const node=controllers[INSTANCES.indexOf(instance)],other=INSTANCES.find(n=>n!==instance);await awaitWriter(node);
        const before={writer:await state(instance),observer:await state(other),snapshot:await snapshot('natural-day before state'),cycles:await cycleEvidence()};await command('server','coop.debug.set_time_mode',['Play_2x','force-live-test'],true);
        const deadline=Date.now()+config['day-timeout-seconds']*1000;let values,lastLogs=Date.now();
        do {await delay(1000);values=await both();assertWriter(values,node,initial);const current=values[INSTANCES.indexOf(instance)];
            await emit({phase:'native_day_progress',instance,hours:current.hours,worldTicks:current.worldTicks});
            if(Date.now()-lastLogs>15000){await collectLogs();lastLogs=Date.now();await save();}
            if(current.hours>=before.writer.hours+24)break;
        }while(Date.now()<deadline);
        const after={writer:values[INSTANCES.indexOf(instance)],observer:values[INSTANCES.indexOf(other)]};
        assert.ok(after.writer.hours>=before.writer.hours+24,'Natural24h deadline expired');assert.equal(epoch(after.writer),epoch(before.writer));
        assert.ok(after.writer.worldTicks>before.writer.worldTicks);assert.equal(after.observer.worldTicks,before.observer.worldTicks);
        const handoff=await completeNaturalDayHandoff({node,nextController,initial,beforeCycles:before.cycles.receipts,
            pause:()=>command('server','coop.debug.set_time_mode',['Pause','force-live-test'],true),
            readPreSnapshot:()=>preHandoffSnapshot('natural-day pre-handoff critical equality'),
            readCycles:async()=>(await cycleEvidence()).receipts,
            handoff:()=>command('server','coop.debug.authority.availability',[availability],true),
            confirmWriter:()=>awaitWriter(nextController),readTimeModes:pausedTimeModes,
            readStrictSnapshot:npcIds=>snapshot('natural-day confirmed handoff exact equality',npcIds),record:emit});
        finalNaturalHandoff=handoff;
        return {before,after,...handoff};
    }
    async function saves() {const rows=[];let offset=0;for(let page=0;page<8;page++){const r=await call('list_saves',{profile:config.profile,offset});const data=r.result??r;assert.ok(Array.isArray(data.saves));rows.push(...data.saves);assert.equal(data.truncated,false,'Save catalog truncated; cannot prove exact existing-name absence');if(data.nextOffset===offset)return rows;assert.ok(data.nextOffset>offset);offset=data.nextOffset;}throw new Error('Save catalog page budget exhausted');}
    try {
        await client.connect(transport);await start(config.save);
        await feature('exact-two-process-baseline',()=>pause('baseline exact critical equality'));
        if(config.mode==='natural') {
            await feature('A-native-day',()=>naturalDay('server',controllers[1],'yield'));
            await feature('A-to-B-native-day',()=>naturalDay('client1',controllers[0],'auto'));
            await feature('B-to-A-cycle-uniqueness',async()=>{
                await awaitWriter(controllers[0]);const state=await snapshot('returned A'),cycles=await cycleEvidence();
                assertSchedulerHandoff(finalNaturalHandoff.sealedWriter,cycles.receipts);return {snapshot:state,cycles};
            });
        } else {
            const name=freshSaveName();summary.generatedSave=name;assert.ok(!(await saves()).some(row=>row.name===name),'Generated basename already exists; no save will be overwritten');
            let baseline;
            await feature('native-unique-save-with-A-writer',async()=>{
                await awaitWriter(controllers[0]);baseline=await pause('pre-save exact state');summary.savedLease=baseline.states[0];
                const output=await command('server','coop.debug.save.save_as',[name],true);assert.ok(output.includes(name));
                const observed=await poll('native save and sidecar complete',async()=>{assert.match(await command('server','coop.debug.save.state'),/isSaving=False/);const entry=(await saves()).find(row=>row.name===name);assert.ok(entry&&entry.bytes>0&&entry.sessionSidecarPresent,'Native save+session sidecar not observed');return entry;},120000);
                const afterSave=await snapshot('post-save paused state',baseline.semantics[0].sampledNpcIds);assertPersistent(baseline.semantics[0],afterSave.semantics[0]);return {name,output,observed,baseline,afterSave};
            });
            const previousInitial=initial,previousPids=session.processes.map(row=>row.identity.pid);await stop();await start(name);
            await feature('fresh-process-exact-save-restored',async()=>{
                assert.ok(session.processes.every(row=>!previousPids.includes(row.identity.pid)),'Cold restart reused a process');assertActors(initial,previousInitial);
                const state=await pause('cold restored exact state',baseline.semantics[0].sampledNpcIds);const fence=assertColdLease(summary.savedLease,state.states,initial);
                assertPersistent(baseline.semantics[0],state.semantics[0]);return {fence,previousPids,processes:session.processes,osProcesses:session.osProcesses,state};
            });
        }
    } catch(error) {
        summary.failed={message:error.message,stack:error.stack};await emit({phase:'scenario_failure',...summary.failed});
        if(session&&!summary.features.some(row=>row.status==='FAIL')){try{await call('get_run',{run_id:session.runId});}catch(diagnostic){await emit({phase:'startup_identity_error',error:diagnostic.stack});}try{await collectLogs();}catch(diagnostic){summary.failureLogError=diagnostic.stack;}for(const n of INSTANCES.filter(n=>session.processes.some(row=>row.name===n))){try{await screenshot(n,'startup-or-between-features-failure');}catch(diagnostic){await emit({phase:'capture_failure',error:diagnostic.stack});}}}
    } finally {
        if(session&&!session.stopAttempted){try{await stop();}catch(error){if(session.cleanupConfirmed)summary.logVerificationError=error.stack;else summary.cleanupError=error.stack;}}
        try{await client.close();}catch(error){summary.transportCloseError=error.stack;}
        summary.completedUtc=new Date().toISOString();summary.passed=!summary.failed&&!summary.cleanupError&&!summary.logVerificationError&&!summary.transportCloseError&&summary.sessions.length>0&&summary.sessions.every(hasCompleteSessionEvidence);
        await save();console.log(JSON.stringify({passed:summary.passed,features:summary.features.map(row=>[row.name,row.status]),summaryPath}));if(!summary.passed)process.exitCode=1;
    }
    return summary;
}
if(process.argv[1]&&import.meta.url===pathToFileURL(resolve(process.argv[1])).href){if(process.argv.includes('--help'))console.log(HELP);else await run(options(process.argv.slice(2)));}
