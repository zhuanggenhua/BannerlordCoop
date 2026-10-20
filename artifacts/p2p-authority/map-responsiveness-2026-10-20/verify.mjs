// Verify this compact report. This does not launch or replay a native game.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';

const dir=path.dirname(fileURLToPath(import.meta.url));
const read=p=>JSON.parse(fs.readFileSync(p,'utf8'));
const sha=p=>crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex');
const a=read(path.join(dir,'accepted.json'));
const index=read(path.join(dir,'evidence-index.json'));
const expected='e7e7f66a7765c34e0e8a99fb8fa2a2e4761c1631';
assert.equal(a.sourceCommit,expected);assert.equal(a.qaCommit,expected);
assert.equal(index.sourceCommit,expected);assert.equal(a.status,'PASS_SCOPED_MAP_ACCEPTANCE');
assert.equal(a.run.id,'4bf0d90cf80348a1a79ab5790b12a5e7');
assert.deepEqual(a.run.pids.map(p=>p.pid),[452,18504]);
assert.equal(a.run.additionalGameServerProcess,false);assert.equal(a.run.cleanupConfirmed,true);
assert.ok(a.run.stoppedProcesses.every(p=>!p.processAlive&&!p.processTreeAlive&&p.cleanupComplete));
assert.equal(a.profiles.length,2);
assert.equal(a.profiles.reduce((n,p)=>n+p.adopted,0),16);
assert.equal(a.profiles.reduce((n,p)=>n+p.uniqueRemoteAssociations,0),15);
assert.equal(a.profiles.reduce((n,p)=>n+p.ambiguousRemoteAssociations,0),1);
for(const p of a.profiles){
  assert.equal(p.status,'PASS');assert.equal(p.inputs,8);assert.equal(p.adopted,8);
  assert.deepEqual(p.modelObservedFrames,[1]);
  for(const field of ['localTargetAdoptionMs','modelFrameObservationMs','visualFrameReadyMs']){
    assert.ok(p[field].min>=0&&p[field].max>=p[field].min);
  }
  assert.equal(p.sameWriterNextHandle[0],p.sameWriterNextHandle[1]);
  assert.equal(p.criticalWorld.nextHandle,p.sameWriterNextHandle[0]);
  assert.equal(p.criticalWorld.unregisteredParties,0);
  assert.ok(p.players.every(x=>x.registeredPlayers===2&&x.heroRegistrations===1&&x.partyRegistrations===1&&x.clanRegistrations===1));
}
assert.equal(a.network.observedDelayed,1909);assert.equal(a.network.delayedInsideMeasurement,1612);
assert.equal(a.network.observedReordered,2);assert.equal(a.network.injectedReorder,false);
assert.equal(a.network.observedDropped,0);assert.equal(a.network.errorCount,0);
assert.equal(a.critical.movingPositionEqualityRequired,false);
assert.equal(a.regression.allSuitesGreen,false);assert.equal(a.regression.buildExitCode,0);
assert.deepEqual(a.regression.suites.map(s=>[s.name,s.passed,s.failed,s.rawExitCode]),[
  ['gi',3185,0,0],['coop',1461,195,1],['common',423,0,0],['e2e',61,10,1]
]);
assert.ok(a.regression.suites.every(s=>s.addedFailures.length===0&&s.removedBaselineFailures.length===0));
assert.equal(a.regression.node.receiptSourceCommit,expected);
assert.equal(a.regression.node.passed,42);assert.equal(a.regression.node.failed,0);
assert.equal(a.regression.nodeEarlierIndependent.receiptSourceCommit,'e78c93918be8827b7c082c5691b3c42a2c00d651');
assert.equal(a.regression.nodeEarlierIndependent.sourceComparison.length,6);
assert.ok(a.retainedFailures.every(x=>x.recordedPassed===false));
assert.equal(a.retainedFailures[1].retrospectiveAdoption.notAdopted,9);
assert.ok(a.limits.some(s=>s.includes('Overall P2P NOT_COMPLETE')));
assert.ok(a.limits.some(s=>s.includes('Genuine Hold stop NOT_OBSERVED')));
const keys=new Set();
for(const entry of index.entries){
  assert.ok(!path.posix.isAbsolute(entry.path)&&!entry.path.includes('\\')&&!entry.path.split('/').includes('..')&&!/^[a-z]:/i.test(entry.path));
  assert.match(entry.sha256,/^[a-f0-9]{64}$/);assert.ok(Number.isSafeInteger(entry.bytes)&&entry.bytes>=0);
  const key=[entry.storage,entry.taskId??'',entry.path].join('|');assert.ok(!keys.has(key));keys.add(key);
}
for(const file of ['accepted.json','evidence-index.json']){
  assert.doesNotMatch(fs.readFileSync(path.join(dir,file),'utf8'),/[a-z]:[\\/]/i,'machine absolute path leaked');
}
const args=process.argv.slice(2);
assert.ok(args.length===0||(args.length===2&&args[0]==='--repo'),'Usage: node verify.mjs [--repo <coordinator-repository>]');
let checked=0;
if(args.length){
  const repo=path.resolve(args[1]);
  // Native raw JSONL/BMP remain independently audited; recheck small JSON/QA receipts only.
  for(const entry of index.entries.filter(e=>e.storage==='coordinator-repository'&&(e.path.endsWith('.json')||e.path.endsWith('map-qa42-e7.log')))){
    const file=path.join(repo,entry.path);assert.equal(fs.statSync(file).size,entry.bytes);assert.equal(sha(file),entry.sha256);checked++;
  }
  const summary=read(path.join(repo,'.mcp-local/p2p-map-movement-r3-03.jsonl.summary.json'));
  assert.equal(summary.runId,a.run.id);assert.equal(summary.codeHead,a.sourceCommit);assert.equal(summary.passed,true);
  assert.ok(summary.features.every(f=>f.status==='PASS'));
  for(const p of a.profiles){
    const data=read(path.join(repo,`.mcp-local/p2p-map-movement-r3-03.jsonl.${p.name}.movement.json`));
    assert.equal(data.analysis.observations.length,p.inputs);
    assert.equal(data.analysis.observations.filter(x=>x.localTargetAdoptionMs!==null).length,p.adopted);
    const worlds=summary.features.find(f=>f.name==='two-moving-players-'+p.name).evidence.finalCriticalWorld;
    for(const world of worlds.worlds){
      for(const [k,v] of Object.entries(p.criticalWorld))assert.equal(world[k],v,`${p.name}: ${k}`);
    }
  }
  const qa=index.entries.find(e=>e.sha256===a.regression.node.logSha256);
  const text=fs.readFileSync(path.join(repo,qa.path),'utf8');assert.match(text,/# pass 42/);assert.match(text,/# fail 0/);
}
const sums=fs.readFileSync(path.join(dir,'SHA256SUMS'),'utf8').trim().split('\n');
for(const line of sums){const [,expectedHash,file]=/^([a-f0-9]{64})  ([^/\\]+)$/.exec(line)??[];assert.ok(file);assert.equal(sha(path.join(dir,file)),expectedHash);}
console.log(JSON.stringify({status:'PASS',kind:'report-metadata-and-retained-receipt-check',nativeReplay:false,sourceCommit:a.sourceCommit,indexEntries:index.entries.length,coordinatorFilesRehashed:checked,scope:args.length?'compact metadata plus small retained coordinator receipts':'metadata only; no external retained evidence rechecked'}));
