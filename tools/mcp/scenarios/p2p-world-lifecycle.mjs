import assert from 'node:assert/strict';
import { randomUUID, createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { appendFile, mkdir, readFile, realpath, stat, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { assertActors, assertPlayers, assertWorld, assertWriter, assertProcessEvidence,
    field, writer, epoch, jsonOutput, logDiagnostics } from './p2p-udp-reliability.mjs';

export const HELP = `Usage: node tools/mcp/scenarios/p2p-world-lifecycle.mjs --natural|--cold-save
  --repo <coordinator checkout> --server <CoopMcpServer.exe> --config <profiles.json>
  --sdk-root <node_modules> --record <new evidence.jsonl>
  [--profile local] [--save P1B_DirectIP_Recruit] [--day-timeout-seconds 900]
Coordinator/exclusive game lock only. No deployment. Natural mode advances each
writer through24 native hours with ordinary Play_2x (the actual public enum),
then merges completed native Hourly/Daily/DailyTickParty receipts across A/B.
Cold mode saves a unique native save+sidecar while A is writer and paused,
stops both process trees, loads that exact save into new processes and rejoins B.
Compares handle-free ALL-MobileParty count/XP hashes, players and24 NPC details.
All ERR/FTL require investigation. No clock setters, artificial daily callbacks,
private IPC, save-byte edits, mutation retries or automatic acceptance relaxations.
--help/import do not load SDK, connect or launch games.`;
const INSTANCES = ['server', 'client1'];
const REQUIRED = ['coop.debug.authority.state', 'coop.debug.authority.world_state', 'coop.debug.authority.semantic_state',
    'coop.debug.authority.cycles', 'coop.debug.authority.availability', 'coop.debug.players.party_state'];
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
export function assertColdLease(before, after, initial) {
    assertWriter(after, initial[0].controller, initial);
    if (field(before.lease,'roomId') !== field(after[0].lease,'roomId')) return 'new-room-fence';
    assert.ok(epoch(after[0]) > epoch(before) || field(after[0].lease,'authorityId') !== field(before.lease,'authorityId'),
        'Cold restart reused room/authority/epoch without a new fence');
    return epoch(after[0]) > epoch(before) ? 'new-epoch-fence' : 'new-authority-fence';
}
export function assertCycles(receipts) {
    assert.equal(receipts.length, 2); assert.equal(receipts[0].campaign, receipts[1].campaign);
    assert.notEqual(receipts[0].instance, receipts[1].instance);
    const counts = new Map(), rows = [];
    for (const receipt of receipts) {
        assert.equal(receipt.overflowed, false);
        for (const row of receipt.rows) {
            assert.equal(field(row,'leaseKnown'), true, 'Native cycle dispatched without known lease');
            assert.equal(field(row,'localWriter'), true, 'Observer dispatched native world cycle');
            assert.equal(field(row,'node'), field(row,'writerController'), 'Cycle node was not its recorded writer');
            assert.ok(field(row,'epoch') > 0 && field(row,'roomId') && field(row,'authorityId'));
            const kind = field(row,'kind'), party = field(row,'party'), bucket = kind === 'HourlyTick' ? field(row,'hour') : field(row,'day');
            assert.ok(['HourlyTick','DailyTick','DailyTickParty'].includes(kind));
            if (kind === 'DailyTickParty') assert.ok(party, 'Party daily callback missing native StringId');
            const key = JSON.stringify([receipt.campaign,kind,party,bucket]);
            const count = (counts.get(key) ?? 0) + field(row,'count'); counts.set(key,count);
            assert.equal(count,1,'Repeated native cycle across nodes/epochs: ' + key); rows.push(row);
        }
    }
    return { entries:rows.length, hourly:rows.filter(row => field(row,'kind')==='HourlyTick').length,
        daily:rows.filter(row => field(row,'kind')==='DailyTick').length,
        partyDaily:rows.filter(row => field(row,'kind')==='DailyTickParty').length, receipts };
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
    let session, initial, callId = 0, readDeadline;
    const emit = entry => appendFile(config.record,JSON.stringify({utc:new Date().toISOString(),...entry})+'\n');
    const save = () => writeFile(summaryPath,JSON.stringify(summary,null,2)+'\n');
    async function call(name,args,mutation=false) {
        assert.ok(!(readDeadline && mutation),'Poll callbacks cannot mutate');
        const timeout = readDeadline ? readDeadline-Date.now() : 360000; assert.ok(timeout>0,'Read deadline expired');
        const id = ++callId; await emit({phase:'request',callId:id,name,args,mutation}); let raw;
        try { raw = await client.callTool({name,arguments:args},undefined,{timeout}); }
        catch(error) { await emit({phase:'transport_error',callId:id,name,error:error.stack,outcomeUncertain:mutation}); throw error; }
        const data = raw.structuredContent ?? JSON.parse(raw.content.find(item=>item.type==='text').text);
        if(name==='start_run' && data.runId) {
            session = {runId:data.runId,saveName:args.save_name,logs:Object.fromEntries(INSTANCES.map(n=>[n,{text:'',resets:0}])),cleanupConfirmed:false,stopAttempted:false};
            summary.sessions.push(session);
        }
        await emit({phase:'response',callId:id,name,args,data,raw}); assert.ok(!raw.isError,JSON.stringify(data)); return data;
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
    async function collectLogs() {
        for(const n of INSTANCES){const log=session.logs[n];for(let page=0;page<1024;page++){
            const response=await call('read_logs',{run_id:session.runId,instance:n,cursor:log.cursor,max_bytes:65536});const chunk=response.result??response;
            assert.equal(typeof chunk.text,'string');assert.equal(typeof chunk.hasMore,'boolean');assert.equal(typeof chunk.reset,'boolean');assert.equal(typeof chunk.cursor,'string');
            if(chunk.reset){log.resets++;log.text+='\n--- retained previous log; cursor reset ---\n';}log.text+=chunk.text;log.cursor=chunk.cursor;if(!chunk.hasMore)break;assert.ok(page<1023,'Log page budget exhausted');
        }const path=config.record+'.'+session.runId+'.'+n+'.log';await writeFile(path,log.text);log.path=path;log.sha256=createHash('sha256').update(log.text).digest('hex');log.diagnostics=logDiagnostics(log.text);}
        assert.ok(INSTANCES.every(n=>{
            const value=session.logs[n].diagnostics;
            return value.errorLines.length===0&&value.realTick+value.duplicateIds+value.aiBatch===0;
        }),'Runtime ERR/FTL or critical diagnostics require investigation');
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
        const view=await call('get_run',{run_id:session.runId});assert.equal(view.instances.length,2);session.processes=view.instances;session.osProcesses=processes();
        assertProcessEvidence(session.osProcesses,view.instances.map(row=>row.identity.pid));
        const assemblies=row=>row.status.loadedAssemblies.map(x=>[x.name,x.mvid]).sort();assert.deepEqual(assemblies(view.instances[0]),assemblies(view.instances[1]));
        for(const n of INSTANCES){const catalog=JSON.stringify(await call('list_commands',{run_id:session.runId,instance:n}));for(const name of REQUIRED)assert.ok(catalog.includes(name),'Missing '+name);}
    }
    async function stop() {
        assert.equal(session.stopAttempted,false,'Uncertain stop mutations must not be retried');session.stopAttempted=true;
        const stopped=await call('stop_run',{run_id:session.runId},true);session.stopped=stopped;assert.equal(stopped.state,'stopped');assert.ok(stopped.instances.length>0);
        if(session.processes)assert.equal(stopped.instances.length,2);
        assert.ok(stopped.instances.every(row=>row.cleanupComplete&&!row.processAlive&&!row.processTreeAlive));session.afterStopProcesses=processes();
        assert.equal(session.afterStopProcesses.length,0,'Bannerlord process remained after stop');session.cleanupConfirmed=true;await collectLogs();await save();
    }
    async function cycleEvidence() {
        const receipts=[];
        for(const instance of INSTANCES){const pages=[];let offset=0;
            for(let index=0;index<128;index++){
                const page=jsonOutput(await command(instance,'coop.debug.authority.cycles',[String(offset)]));pages.push(page);
                if(!page.hasMore)break;assert.ok(page.nextOffset>offset,'Cycle page did not advance');offset=page.nextOffset;
            }
            receipts.push(combineCyclePages(pages));
        }
        return assertCycles(receipts);
    }
    async function naturalDay(instance) {
        const node=controllers[INSTANCES.indexOf(instance)],other=INSTANCES.find(n=>n!==instance);await awaitWriter(node);
        const before={writer:await state(instance),observer:await state(other)};await command('server','coop.debug.set_time_mode',['Play_2x','force-live-test'],true);
        const deadline=Date.now()+config['day-timeout-seconds']*1000;let values,lastLogs=Date.now();
        do {await delay(1000);values=await both();assertWriter(values,node,initial);const current=values[INSTANCES.indexOf(instance)];
            await emit({phase:'native_day_progress',instance,hours:current.hours,worldTicks:current.worldTicks});
            if(Date.now()-lastLogs>15000){await collectLogs();lastLogs=Date.now();await save();}
            if(current.hours>=before.writer.hours+24)break;
        }while(Date.now()<deadline);
        const after={writer:values[INSTANCES.indexOf(instance)],observer:values[INSTANCES.indexOf(other)]};
        assert.ok(after.writer.hours>=before.writer.hours+24,'Natural24h deadline expired');assert.equal(epoch(after.writer),epoch(before.writer));
        assert.ok(after.writer.worldTicks>before.writer.worldTicks);assert.equal(after.observer.worldTicks,before.observer.worldTicks);
        const paused=await pause('natural-day critical equality');const cycles=await cycleEvidence();
        assert.ok(cycles.receipts[INSTANCES.indexOf(instance)].rows.some(row=>field(row,'kind')==='DailyTick'&&field(row,'node')===node));
        assert.ok(cycles.receipts[INSTANCES.indexOf(instance)].rows.some(row=>field(row,'kind')==='DailyTickParty'&&field(row,'node')===node));
        return {before,after,paused,cycles};
    }
    async function saves() {const rows=[];let offset=0;for(let page=0;page<8;page++){const r=await call('list_saves',{profile:config.profile,offset});const data=r.result??r;assert.ok(Array.isArray(data.saves));rows.push(...data.saves);assert.equal(data.truncated,false,'Save catalog truncated; cannot prove exact existing-name absence');if(data.nextOffset===offset)return rows;assert.ok(data.nextOffset>offset);offset=data.nextOffset;}throw new Error('Save catalog page budget exhausted');}
    try {
        await client.connect(transport);await start(config.save);
        await feature('exact-two-process-baseline',()=>pause('baseline exact critical equality'));
        if(config.mode==='natural') {
            await feature('A-native-day',()=>naturalDay('server'));
            await feature('A-to-B-native-day',async()=>{await command('server','coop.debug.authority.availability',['yield'],true);await awaitWriter(controllers[1]);return naturalDay('client1');});
            await feature('B-to-A-cycle-uniqueness',async()=>{await command('server','coop.debug.authority.availability',['auto'],true);await awaitWriter(controllers[0]);return {snapshot:await pause('returned A'),cycles:await cycleEvidence()};});
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
        if(session&&!summary.features.some(row=>row.status==='FAIL')){try{await collectLogs();}catch(diagnostic){summary.failureLogError=diagnostic.stack;}for(const n of INSTANCES){try{await screenshot(n,'startup-or-between-features-failure');}catch(diagnostic){await emit({phase:'capture_failure',error:diagnostic.stack});}}}
    } finally {
        if(session&&!session.stopAttempted){try{await stop();}catch(error){summary.cleanupError=error.stack;}}
        try{await client.close();}catch(error){summary.transportCloseError=error.stack;}
        summary.completedUtc=new Date().toISOString();summary.passed=!summary.failed&&!summary.cleanupError&&!summary.transportCloseError&&summary.sessions.length>0&&summary.sessions.every(row=>row.cleanupConfirmed);
        await save();console.log(JSON.stringify({passed:summary.passed,features:summary.features.map(row=>[row.name,row.status]),summaryPath}));if(!summary.passed)process.exitCode=1;
    }
    return summary;
}
if(process.argv[1]&&import.meta.url===pathToFileURL(resolve(process.argv[1])).href){if(process.argv.includes('--help'))console.log(HELP);else await run(options(process.argv.slice(2)));}
