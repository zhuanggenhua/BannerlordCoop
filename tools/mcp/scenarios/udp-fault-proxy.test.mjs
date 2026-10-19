import { test } from 'node:test';
import assert from 'node:assert/strict';
import dgram from 'node:dgram';
import { setTimeout as delay } from 'node:timers/promises';
import { UdpFaultProxy } from './udp-fault-proxy.mjs';

async function bind(socket) {
  await new Promise(resolve => socket.bind(0, '127.0.0.1', resolve));
}
function send(socket, data, port) {
  return new Promise((resolve, reject) => {
    socket.send(Buffer.from(data), port, '127.0.0.1', error => error ? reject(error) : resolve());
  });
}
async function waitUntil(read, message) {
  const deadline = Date.now() + 1500;
  while (Date.now() < deadline) {
    if (read()) return;
    await delay(5);
  }
  assert.fail(message);
}

test('invalid ports and hold limits are rejected and a bind collision releases its backend socket', async () => {
  assert.throws(() => new UdpFaultProxy({ targetPort: 0 }));
  assert.throws(() => new UdpFaultProxy({ listenPort: 65536 }));
  assert.throws(() => new UdpFaultProxy({ maxHeldBytes: 0 }));
  assert.throws(() => new UdpFaultProxy({ maxHeldPackets: 0 }));
  const occupied = dgram.createSocket('udp4');
  await bind(occupied);
  const proxy = new UdpFaultProxy({ listenPort: occupied.address().port });
  try { await assert.rejects(proxy.start(), /EADDRINUSE/); assert.equal(proxy.closed, true); }
  finally { occupied.close(); }
});

test('real loopback UDP loses datagrams, changes arrival order, and restores traffic', async () => {
  const host = dgram.createSocket('udp4'), client = dgram.createSocket('udp4');
  let proxy;
  try {
    await bind(host);
    const received = [], replies = [];
    host.on('message', (data, peer) => { received.push(Number(data)); host.send(data, peer.port, peer.address); });
    client.on('message', data => replies.push(Number(data)));
    proxy = await new UdpFaultProxy({ targetPort: host.address().port }).start();
    proxy.setFault({ dropEvery: 4, delayEvery: 3, delayMs: 80 });
    for (let i = 1; i <= 8; i++) { await send(client, String(i), proxy.port); await delay(3); }
    await delay(250);
    assert.deepEqual([...received].sort((a,b)=>a-b), [1,2,3,5,6,7]);
    assert.ok(received.indexOf(5) < received.indexOf(3));
    assert.ok(proxy.stats.dropped >= 2); assert.ok(proxy.stats.delayed >= 2); assert.ok(proxy.stats.reordered >= 1);
    proxy.setFault({ blackout: true }); await send(client, '9', proxy.port); await delay(30);
    assert.ok(!received.includes(9));
    proxy.setFault(); await send(client, '10', proxy.port); await delay(40);
    assert.ok(received.includes(10)); assert.ok(replies.includes(10)); assert.deepEqual(proxy.stats.errors, []);
  } finally { await proxy?.close(); host.close(); client.close(); }
});

test('sudden blackout discards delayed datagrams already queued by the proxy', async () => {
  const host = dgram.createSocket('udp4'), client = dgram.createSocket('udp4');
  let proxy;
  try {
    await bind(host);
    const received = []; host.on('message', data => received.push(data.toString()));
    proxy = await new UdpFaultProxy({ targetPort: host.address().port }).start();
    proxy.setFault({ delayEvery: 1, delayMs: 80 });
    await send(client, 'before-cut', proxy.port);
    await waitUntil(() => proxy.stats.delayed === 1, 'proxy did not queue delayed datagram');
    proxy.setFault({ blackout: true }); await delay(110);
    assert.deepEqual(received, []); assert.equal(proxy.stats.dropped, 1);
    assert.equal(proxy.stats.samples[0].action, 'drop-on-blackout');
    proxy.setFault(); await send(client, 'after-restore', proxy.port); await delay(40);
    assert.deepEqual(received, ['after-restore']); assert.deepEqual(proxy.stats.errors, []);
  } finally { await proxy?.close(); host.close(); client.close(); }
});

test('hold-to-client keeps client-to-host UDP flowing while host replies wait in the queue', async () => {
  const host = dgram.createSocket('udp4'), client = dgram.createSocket('udp4');
  let proxy;
  try {
    await bind(host);
    const received = [], replies = [];
    host.on('message', (data, peer) => { received.push(data.toString()); host.send(data, peer.port, peer.address); });
    client.on('message', data => replies.push(data.toString()));
    proxy = await new UdpFaultProxy({ targetPort: host.address().port }).start();
    proxy.setFault({ holdToClient: true });
    await send(client, 'heartbeat', proxy.port);
    await waitUntil(() => received.length === 1 && proxy.stats.heldCurrentCount === 1,
      'client-to-host packet or held host reply did not arrive');
    await delay(30);
    assert.deepEqual(received, ['heartbeat']); assert.deepEqual(replies, []);
    assert.equal(proxy.stats.received.toHost, 1); assert.equal(proxy.stats.sent.toHost, 1);
    assert.equal(proxy.stats.received.toClient, 1); assert.equal(proxy.stats.sent.toClient, 0);
    assert.equal(proxy.stats.heldTotal, 1); assert.equal(proxy.stats.heldCurrentBytes, Buffer.byteLength('heartbeat'));
    assert.equal(proxy.stats.heldSamples[0].direction, 'toClient');
    assert.match(proxy.stats.heldSamples[0].sha256, /^[0-9a-f]{64}$/);
    assert.equal(proxy.stats.heldSamples[0].policyUtc, proxy.stats.policies.at(-1).utc);
    proxy.setFault({});
    await waitUntil(() => replies.length === 1, 'released host reply did not reach client');
    assert.deepEqual(replies, ['heartbeat']);
    assert.equal(proxy.stats.heldCurrentCount, 0); assert.equal(proxy.stats.heldCurrentBytes, 0);
    assert.equal(proxy.stats.heldSamples[0].action, 'release-held');
  } finally { await proxy?.close(); host.close(); client.close(); }
});

test('releasing a hold flushes held A-to-B datagrams in FIFO order', async () => {
  const host = dgram.createSocket('udp4'), client = dgram.createSocket('udp4');
  let proxy;
  try {
    await bind(host);
    const replies = [];
    host.on('message', (data, peer) => host.send(data, peer.port, peer.address));
    client.on('message', data => replies.push(data.toString()));
    proxy = await new UdpFaultProxy({ targetPort: host.address().port }).start();
    proxy.setFault({ holdToClient: true });
    for (const value of ['first', 'second', 'third']) await send(client, value, proxy.port);
    await waitUntil(() => proxy.stats.heldCurrentCount === 3, 'three host replies were not held');
    assert.deepEqual(replies, []);
    proxy.setFault({});
    await waitUntil(() => replies.length === 3, 'released replies did not all reach client');
    assert.deepEqual(replies, ['first', 'second', 'third']);
    assert.equal(proxy.stats.heldCurrentCount, 0); assert.equal(proxy.stats.heldCurrentBytes, 0);
    assert.ok(proxy.stats.heldSamples.slice(0, 3).every(sample => sample.action === 'release-held'));
  } finally { await proxy?.close(); host.close(); client.close(); }
});

test('starting a hold cancels prior delayed A-to-B datagrams so none leak through later', async () => {
  const host = dgram.createSocket('udp4'), client = dgram.createSocket('udp4');
  let proxy;
  try {
    await bind(host);
    const replies = [];
    host.on('message', (data, peer) => host.send(data, peer.port, peer.address));
    client.on('message', data => replies.push(data.toString()));
    proxy = await new UdpFaultProxy({ targetPort: host.address().port }).start();
    proxy.setFault({ delayEvery: 1, delayMs: 120 });
    await send(client, 'before-hold', proxy.port);
    await waitUntil(() => proxy.stats.received.toClient === 1 && proxy.stats.delayed === 2,
      'prior delayed host reply was not queued');
    proxy.setFault({ holdToClient: true });
    await delay(150);
    assert.deepEqual(replies, []);
    assert.equal(proxy.stats.dropped, 1);
    assert.ok(proxy.stats.samples.some(sample => sample.direction === 'toClient' && sample.action === 'drop-before-hold'));
    await send(client, 'during-hold', proxy.port);
    await waitUntil(() => proxy.stats.heldCurrentCount === 1, 'new host reply was not held');
    assert.deepEqual(replies, []);
  } finally { await proxy?.close(); host.close(); client.close(); }
});

test('blackout drops held packets and prevents a late held-packet leak', async () => {
  const host = dgram.createSocket('udp4'), client = dgram.createSocket('udp4');
  let proxy;
  try {
    await bind(host);
    const received = [], replies = [];
    host.on('message', (data, peer) => { received.push(data.toString()); host.send(data, peer.port, peer.address); });
    client.on('message', data => replies.push(data.toString()));
    proxy = await new UdpFaultProxy({ targetPort: host.address().port }).start();
    proxy.setFault({ holdToClient: true });
    await send(client, 'held-before-cut', proxy.port);
    await waitUntil(() => proxy.stats.heldCurrentCount === 1, 'host reply was not held before blackout');
    const queued = { count: proxy.stats.heldCurrentCount, bytes: proxy.stats.heldCurrentBytes, dropped: proxy.stats.dropped };
    assert.throws(() => proxy.setFault({ blackout: true, holdToClient: true }), /Cannot hold datagrams during blackout/);
    const cutUtc = new Date().toISOString();
    proxy.setFault({ blackout: true });
    assert.equal(proxy.stats.heldCurrentCount, 0); assert.equal(proxy.stats.heldCurrentBytes, 0);
    assert.equal(proxy.stats.dropped, queued.dropped + queued.count);
    assert.equal(proxy.stats.heldSamples[0].action, 'drop-on-blackout');
    assert.equal(proxy.stats.heldSamples[0].droppedUtc, proxy.stats.policies.at(-1).utc);
    await send(client, 'during-blackout', proxy.port); await delay(100);
    assert.deepEqual(received, ['held-before-cut']); assert.deepEqual(replies, []);
    assert.equal(proxy.stats.heldCurrentCount, 0);
    proxy.setFault({}); await send(client, 'after-cut', proxy.port);
    await waitUntil(() => replies.length === 1, 'post-blackout reply did not reach client');
    assert.deepEqual(replies, ['after-cut']); assert.ok(cutUtc <= proxy.stats.policies.at(-2).utc);
  } finally { await proxy?.close(); host.close(); client.close(); }
});

test('held queue byte and packet bounds fail visibly and close discards buffered datagrams', async () => {
  async function assertOverflow({ maxHeldBytes, maxHeldPackets, first, second }) {
    const host = dgram.createSocket('udp4'), client = dgram.createSocket('udp4');
    let proxy;
    try {
      await bind(host);
      host.on('message', (data, peer) => host.send(data, peer.port, peer.address));
      proxy = await new UdpFaultProxy({ targetPort: host.address().port, maxHeldBytes, maxHeldPackets }).start();
      proxy.setFault({ holdToClient: true });
      await send(client, first, proxy.port);
      await waitUntil(() => proxy.stats.heldCurrentCount === 1, 'first host reply was not held');
      await send(client, second, proxy.port);
      await waitUntil(() => proxy.stats.heldOverflow === 1, 'held queue overflow was not recorded');
      assert.equal(proxy.stats.errorCount, 1); assert.equal(proxy.stats.errors.length, 1);
      assert.match(proxy.stats.errors[0], /Held UDP queue exceeded configured packet or byte limit/);
      assert.equal(proxy.stats.heldCurrentCount, 1); assert.equal(proxy.stats.heldCurrentBytes, Buffer.byteLength(first));
      assert.equal(proxy.stats.dropped, 1);
      assert.ok(proxy.stats.heldSamples.some(sample => sample.action === 'hold-overflow'));
      await proxy.close();
      assert.equal(proxy.closed, true); assert.equal(proxy.stats.heldCurrentCount, 0); assert.equal(proxy.stats.heldCurrentBytes, 0);
      assert.ok(proxy.stats.heldSamples.some(sample => sample.action === 'drop-on-close'));
      assert.throws(() => proxy.front.address(), { code: 'ERR_SOCKET_DGRAM_NOT_RUNNING' });
      assert.throws(() => proxy.back.address(), { code: 'ERR_SOCKET_DGRAM_NOT_RUNNING' });
    } finally { await proxy?.close(); host.close(); client.close(); }
  }
  await assertOverflow({ maxHeldBytes: 5, maxHeldPackets: 3, first: '1234', second: '56' });
  await assertOverflow({ maxHeldBytes: 32, maxHeldPackets: 1, first: 'a', second: 'b' });
});
