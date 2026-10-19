import fs from 'node:fs';import assert from 'node:assert/strict';import crypto from 'node:crypto';
const label='p2p-mission-r2-05', file='.mcp-local/'+label+'.jsonl';
const rows=fs.readFileSync(file,'utf8').trim().split(/\r?\n/).map(JSON.parse);
const response=id=>rows.find(r=>r.phase==='response'&&r.callId===id);
const output=id=>JSON.parse(response(id).data.result.output);
const summary=JSON.parse(fs.readFileSync(file+'.summary.json'));
assert.equal(summary.runId,'c9715d0f5e2b4ce5add01f9b2f1b6a25');assert.equal(summary.passed,true);assert.equal(summary.cleanupConfirmed,true);assert.equal(summary.features.length,5);assert.ok(summary.features.every(f=>f.status==='PASS'));
for(const id of [321,322,339,340]){const state=output(id);assert.equal(state.lease.IsPaused,true);assert.equal(state.lease.Epoch,6);assert.equal(state.gameState,'MissionState');}
const pairs=[[323,324],[341,342]].map(([a,b])=>({a,b,left:output(a),right:output(b)}));
assert.equal(pairs[0].left.ticks,pairs[1].left.ticks);const results=[];
for(const {a,b,left,right} of pairs){assert.equal(left.ticks,right.ticks);assert.equal(left.npcSample.length,24);assert.equal(right.npcSample.length,24);const differences=[];for(const x of left.npcSample){const y=right.npcSample.find(n=>n.handle===x.handle&&n.id===x.id);assert.ok(y,'Missing peer NPC identity');if(x.x!==y.x||x.y!==y.y)differences.push({handle:x.handle,id:x.id,a:[x.x,x.y],b:[y.x,y.y],distance:Math.hypot(x.x-y.x,x.y-y.y)});}results.push({calls:[a,b],ticks:left.ticks,differences});}
console.log(JSON.stringify({kind:'supplemental-original-native-acceptance',runId:summary.runId,originalFiveStages:'PASS_UNCHANGED',sourceRawSha256:crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex'),supplementalNpcPosition:results.some(r=>r.differences.length)?'FAIL':'PASS',results},null,2));
assert.ok(results.every(r=>r.differences.length===0),'Mission complete critical-state acceptance FAIL: same confirmed paused world has divergent NPC positions; original five-stage PASS is insufficient');
