import { test } from 'node:test';
import assert from 'node:assert/strict';
import dgram from 'node:dgram';
import { setTimeout as delay } from 'node:timers/promises';
import { UdpFaultProxy } from './udp-fault-proxy.mjs';

test('invalid ports are rejected and a bind collision releases its backend socket', async () => {
  assert.throws(() => new UdpFaultProxy({ targetPort: 0 }));
  assert.throws(() => new UdpFaultProxy({ listenPort: 65536 }));
  const occupied = dgram.createSocket('udp4');
  await new Promise(resolve => occupied.bind(0, '127.0.0.1', resolve));
  const proxy = new UdpFaultProxy({ listenPort: occupied.address().port });
  try { await assert.rejects(proxy.start(), /EADDRINUSE/); assert.equal(proxy.closed, true); }
  finally { occupied.close(); }
});

test('real loopback UDP loses datagrams, changes arrival order, and restores traffic', async () => {
  const host = dgram.createSocket('udp4'), client = dgram.createSocket('udp4');
  let proxy;
  try {
    await new Promise(resolve => host.bind(0, '127.0.0.1', resolve));
    const received = [], replies = [];
    host.on('message', (data, peer) => { received.push(Number(data)); host.send(data, peer.port, peer.address); });
    client.on('message', data => replies.push(Number(data)));
    proxy = await new UdpFaultProxy({ targetPort: host.address().port }).start();
    proxy.setFault({ dropEvery: 4, delayEvery: 3, delayMs: 80 });
    for (let i = 1; i <= 8; i++) { client.send(Buffer.from(String(i)), proxy.port, '127.0.0.1'); await delay(3); }
    await delay(250);
    assert.deepEqual([...received].sort((a,b)=>a-b), [1,2,3,5,6,7]);
    assert.ok(received.indexOf(5) < received.indexOf(3));
    assert.ok(proxy.stats.dropped >= 2); assert.ok(proxy.stats.delayed >= 2); assert.ok(proxy.stats.reordered >= 1);
    proxy.setFault({ blackout: true }); client.send(Buffer.from('9'), proxy.port, '127.0.0.1'); await delay(30);
    assert.ok(!received.includes(9));
    proxy.setFault(); client.send(Buffer.from('10'), proxy.port, '127.0.0.1'); await delay(40);
    assert.ok(received.includes(10)); assert.ok(replies.includes(10)); assert.deepEqual(proxy.stats.errors, []);
  } finally { await proxy?.close(); host.close(); client.close(); }
});

test('sudden blackout discards delayed datagrams already queued by the proxy', async () => {
  const host = dgram.createSocket('udp4'), client = dgram.createSocket('udp4');
  let proxy;
  try {
    await new Promise(resolve => host.bind(0, '127.0.0.1', resolve));
    const received = []; host.on('message', data => received.push(data.toString()));
    proxy = await new UdpFaultProxy({ targetPort: host.address().port }).start();
    proxy.setFault({ delayEvery: 1, delayMs: 80 });
    client.send(Buffer.from('before-cut'), proxy.port, '127.0.0.1');
    for (let i=0;i<30 && !proxy.stats.delayed;i++) await delay(5);
    assert.equal(proxy.stats.delayed,1);
    proxy.setFault({ blackout:true }); await delay(110);
    assert.deepEqual(received,[]); assert.equal(proxy.stats.dropped,1);
    assert.equal(proxy.stats.samples[0].action,'drop-on-blackout');
    proxy.setFault(); client.send(Buffer.from('after-restore'),proxy.port,'127.0.0.1');
    await delay(40); assert.deepEqual(received,['after-restore']); assert.deepEqual(proxy.stats.errors,[]);
  } finally { await proxy?.close(); host.close(); client.close(); }
});
