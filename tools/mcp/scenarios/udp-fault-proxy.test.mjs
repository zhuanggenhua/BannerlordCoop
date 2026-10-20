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

async function sourceFixture(count, options = {}) {
  const host = dgram.createSocket('udp4');
  const clients = Array.from({ length: count }, () => dgram.createSocket('udp4'));
  const received = [], replies = clients.map(() => []);
  let proxy;
  try {
    await Promise.all([host, ...clients].map(bind));
    host.on('message', (data, peer) => {
      received.push({ data: data.toString(), peer });
      host.send(data, peer.port, peer.address);
    });
    clients.forEach((socket, index) => socket.on('message', data => replies[index].push(data.toString())));
    proxy = await new UdpFaultProxy({ targetPort: host.address().port, ...options }).start();
    return { host, clients, received, replies, proxy, async close() {
      await proxy.close();for (const socket of [host, ...clients]) socket.close();
    } };
  } catch (error) {
    await proxy?.close();
    for (const socket of [host, ...clients]) { try { socket.close(); } catch {} }
    throw error;
  }
}

test('three real source endpoints retain separate routes for echo and unsolicited host replies', async () => {
  const f = await sourceFixture(3);
  try {
    for (let i = 0; i < 3; i++) await send(f.clients[i], 'source-' + i, f.proxy.port);
    await waitUntil(() => f.replies.every(messages => messages.length === 1), 'three separate replies did not arrive');
    assert.deepEqual(f.replies, [['source-0'], ['source-1'], ['source-2']]);
    assert.equal(new Set(f.received.map(row => row.peer.port)).size, 3);
    assert.equal(f.proxy.stats.routesCreated, 3);
    const original = f.received.find(row => row.data === 'source-0');
    await send(f.host, 'unsolicited-room-followup', original.peer.port);
    await waitUntil(() => f.replies[0].length === 2, 'original room route lost unsolicited host reply');
    assert.deepEqual(f.replies, [['source-0', 'unsolicited-room-followup'], ['source-1'], ['source-2']]);
    assert.equal(f.proxy.stats.errorCount, 0);
  } finally { await f.close(); }
});

test('a new Mission source cannot redirect an already delayed room reply', async () => {
  const f = await sourceFixture(2);
  try {
    f.proxy.setFault({ delayEvery: 1, delayMs: 40 });
    await send(f.clients[0], 'room-delayed', f.proxy.port);
    await waitUntil(() => f.proxy.stats.received.toClient === 1, 'room reply was not queued for delay');
    await send(f.clients[1], 'mission-delayed', f.proxy.port);
    await waitUntil(() => f.proxy.routes.size === 2, 'Mission source route was not registered');
    await waitUntil(() => f.replies.every(messages => messages.length === 1), 'route-bound delayed replies did not arrive');
    assert.deepEqual(f.replies, [['room-delayed'], ['mission-delayed']]);
    assert.equal(f.proxy.stats.delayed, 4);
    assert.equal(f.proxy.stats.errorCount, 0);
  } finally { await f.close(); }
});

test('held replies retain each original endpoint and per-source FIFO after another source sends', async () => {
  const f = await sourceFixture(2);
  try {
    f.proxy.setFault({ holdToClient: true });
    for (const [index, value] of [[0, 'room-first'], [1, 'mission-first'], [0, 'room-second']]) {
      await send(f.clients[index], value, f.proxy.port);
      await waitUntil(() => f.proxy.stats.heldCurrentCount === f.received.length, 'host reply did not join held queue');
    }
    await waitUntil(() => f.proxy.stats.heldCurrentCount === 3, 'three route-bound replies were not held');
    assert.deepEqual(f.replies, [[], []]);
    f.proxy.setFault({});
    await waitUntil(() => f.replies[0].length === 2 && f.replies[1].length === 1, 'held replies did not return to original endpoints');
    assert.deepEqual(f.replies, [['room-first', 'room-second'], ['mission-first']]);
    assert.equal(f.proxy.stats.heldCurrentCount, 0);
    assert.deepEqual(f.proxy.stats.errors, []);
  } finally { await f.close(); }
});

test('blackout drops delayed traffic across routes without replacing surviving endpoint mappings', async () => {
  const f = await sourceFixture(2);
  try {
    for (let i = 0; i < 2; i++) await send(f.clients[i], 'initial-' + i, f.proxy.port);
    await waitUntil(() => f.replies.every(messages => messages.length === 1), 'initial routes not ready');
    f.proxy.setFault({ delayEvery: 1, delayMs: 80 });
    for (let i = 0; i < 2; i++) await send(f.clients[i], 'before-blackout-' + i, f.proxy.port);
    await waitUntil(() => f.proxy.timers.size === 2, 'two routes did not queue delayed packets');
    f.proxy.setFault({ blackout: true });
    assert.equal(f.proxy.timers.size, 0);assert.equal(f.proxy.stats.dropped, 2);
    f.proxy.setFault({});
    for (let i = 0; i < 2; i++) await send(f.clients[i], 'after-blackout-' + i, f.proxy.port);
    await waitUntil(() => f.replies.every(messages => messages.length === 2), 'surviving routes lost restored traffic');
    await delay(100);
    assert.deepEqual(f.replies, [['initial-0', 'after-blackout-0'], ['initial-1', 'after-blackout-1']]);
    assert.ok(!f.received.some(row => row.data.startsWith('before-blackout-')));
  } finally { await f.close(); }
});

test('route bound fails visibly and does not evict or steal an existing source route', async () => {
  assert.throws(() => new UdpFaultProxy({ maxRoutes: 0 }), /Invalid maxRoutes/);
  assert.throws(() => new UdpFaultProxy({ maxRoutes: 257 }), /Invalid maxRoutes/);
  const f = await sourceFixture(3, { maxRoutes: 2 });
  try {
    for (let i = 0; i < 2; i++) await send(f.clients[i], 'allowed-' + i, f.proxy.port);
    await waitUntil(() => f.replies[0].length === 1 && f.replies[1].length === 1, 'allowed routes not ready');
    await send(f.clients[2], 'rejected-third', f.proxy.port);
    await waitUntil(() => f.proxy.stats.routeLimitRejected === 1, 'route limit was not visible');
    for (let i = 0; i < 2; i++) await send(f.clients[i], 'still-allowed-' + i, f.proxy.port);
    await waitUntil(() => f.replies[0].length === 2 && f.replies[1].length === 2, 'existing routes changed after rejection');
    assert.deepEqual(f.replies, [['allowed-0', 'still-allowed-0'], ['allowed-1', 'still-allowed-1'], []]);
    assert.equal(f.proxy.routes.size, 2);assert.equal(f.proxy.stats.dropped, 1);
    assert.equal(f.proxy.stats.errorCount, 1);
    assert.match(f.proxy.stats.errors[0], /route limit exceeded/);
  } finally { await f.close(); }
});

test('an additional backend real bind collision is visible while the original room route survives', async t => {
  const f = await sourceFixture(2);
  try {
    await send(f.clients[0], 'room-initial', f.proxy.port);
    await waitUntil(() => f.replies[0].length === 1, 'room route not ready');
    const create = dgram.createSocket;
    t.mock.method(dgram, 'createSocket', (...args) => {
      const socket = create(...args), realBind = socket.bind.bind(socket);
      socket.bind = (_port, address, callback) => realBind(f.host.address().port, address, callback);
      return socket;
    });
    await send(f.clients[1], 'mission-bind-failure', f.proxy.port);
    await waitUntil(() => f.proxy.stats.routeBindFailures === 1, 'backend bind failure was not visible');
    await send(f.clients[0], 'room-still-connected', f.proxy.port);
    await waitUntil(() => f.replies[0].length === 2, 'room route changed after other backend bind failed');
    assert.deepEqual(f.replies, [['room-initial', 'room-still-connected'], []]);
    assert.equal(f.proxy.stats.errorCount, 1);assert.match(f.proxy.stats.errors[0], /EADDRINUSE/);
    assert.equal(f.proxy.stats.routePendingCurrentCount, 0);
    assert.equal(f.proxy.stats.dropped, 1);
  } finally { await f.close(); }
});

test('pending backend readiness is bounded and blackout drops it before a late bind callback', async t => {
  const f = await sourceFixture(2, { maxHeldPackets: 1, maxHeldBytes: 3 });
  try {
    await send(f.clients[0], 'room-ready', f.proxy.port);
    await waitUntil(() => f.replies[0].length === 1, 'room route not ready');
    const create = dgram.createSocket;let release;
    t.mock.method(dgram, 'createSocket', (...args) => {
      const socket = create(...args), realBind = socket.bind.bind(socket);
      socket.bind = (port, address, callback) => realBind(port, address, () => { release = callback; });
      return socket;
    });
    await send(f.clients[1], 'a', f.proxy.port);
    await waitUntil(() => f.proxy.stats.routePendingCurrentCount === 1 && release, 'pending route readiness was not retained');
    await send(f.clients[1], 'b', f.proxy.port);
    await waitUntil(() => f.proxy.stats.routePendingOverflow === 1, 'pending route packet limit did not fail visibly');
    assert.equal(f.proxy.stats.routePendingCurrentBytes, 1);
    f.proxy.setFault({ blackout: true });
    assert.equal(f.proxy.stats.routePendingCurrentCount, 0);assert.equal(f.proxy.stats.routePendingCurrentBytes, 0);
    release();f.proxy.setFault({});
    await send(f.clients[1], 'restored', f.proxy.port);
    await waitUntil(() => f.replies[1].length === 1, 'ready Mission route did not recover after blackout');
    assert.deepEqual(f.replies, [['room-ready'], ['restored']]);
    assert.equal(f.proxy.stats.dropped, 2);assert.equal(f.proxy.stats.errorCount, 1);
    assert.ok(f.proxy.stats.samples.some(sample => sample.action === 'drop-on-blackout'));
  } finally { await f.close(); }
});

test('concurrent close waits for every source socket and cancels held and delayed routes', async () => {
  const f = await sourceFixture(3);
  try {
    for (let i = 0; i < 3; i++) await send(f.clients[i], 'ready-' + i, f.proxy.port);
    await waitUntil(() => f.replies.every(messages => messages.length === 1), 'three ready routes missing');
    f.proxy.setFault({ holdToClient: true, delayEvery: 1, delayMs: 80 });
    await send(f.clients[0], 'never-forward-after-close', f.proxy.port);
    await send(f.host, 'never-release-after-close', f.received[1].peer.port);
    await waitUntil(() => f.proxy.timers.size === 1 && f.proxy.stats.heldCurrentCount === 1, 'close queues not ready');
    const sockets = [f.proxy.front, ...f.proxy.backendSockets], ports = sockets.map(socket => socket.address().port);
    await Promise.all([f.proxy.close(), f.proxy.close()]);
    assert.equal(f.proxy.timers.size, 0);assert.equal(f.proxy.stats.heldCurrentCount, 0);
    assert.equal(f.proxy.stats.routePendingCurrentCount, 0);assert.equal(f.proxy.stats.dropped, 2);
    for (const socket of sockets) assert.throws(() => socket.address(), { code: 'ERR_SOCKET_DGRAM_NOT_RUNNING' });
    const reused = [];
    try {
      for (const port of ports) {
        const socket = dgram.createSocket('udp4');reused.push(socket);
        await new Promise((resolve, reject) => { socket.once('error', reject);socket.bind(port, '127.0.0.1', resolve); });
      }
      await delay(100);
      assert.equal(f.received.length, 3);assert.ok(f.replies.every(messages => messages.length === 1));
    } finally { for (const socket of reused) socket.close(); }
  } finally { await f.close(); }
});

test('cross-route bind readiness keeps original global reorder evidence while each source flow stays ordered', async t => {
  const f = await sourceFixture(2);
  try {
    await send(f.clients[0], 'room-one', f.proxy.port);
    await waitUntil(() => f.replies[0].length === 1, 'initial room packet missing');
    const create = dgram.createSocket;let release;
    t.mock.method(dgram, 'createSocket', (...args) => {
      const socket = create(...args), realBind = socket.bind.bind(socket);
      socket.bind = (port, address, callback) => realBind(port, address, () => { release = callback; });
      return socket;
    });
    await send(f.clients[1], 'mission-one', f.proxy.port);
    await waitUntil(() => release && f.proxy.stats.routePendingCurrentCount === 1, 'Mission route not waiting for readiness');
    await send(f.clients[0], 'room-two', f.proxy.port);
    await waitUntil(() => f.replies[0].length === 2, 'room flow was blocked by unrelated Mission binding');
    release();
    await waitUntil(() => f.replies[1].length === 1, 'Mission packet did not follow delayed readiness');
    assert.deepEqual(f.received.map(row => row.data), ['room-one', 'room-two', 'mission-one']);
    assert.equal(f.proxy.stats.reordered, 1);
    assert.equal(f.proxy.stats.reorderedWithinRoute, 0);
    assert.deepEqual(f.proxy.stats.routes.map(route => route.lastSent.toHost), [3, 2]);
    assert.ok(f.proxy.stats.routes.every(route => route.ready && !route.failed && route.reorderedWithinRoute === 0));
    assert.deepEqual(f.proxy.stats.routes.map(route => route.sourceEndpoint),
      f.clients.map(socket => '127.0.0.1:' + socket.address().port));
    assert.deepEqual(f.proxy.stats.routes.map(route => route.backendPort),
      [f.received[0].peer.port, f.received[2].peer.port]);
    assert.equal(f.proxy.stats.errorCount, 0);
  } finally { await f.close(); }
});

test('single-flow delayed inversion increments both unchanged global and additional within-route counters', async () => {
  const f = await sourceFixture(1);
  try {
    f.proxy.setFault({ delayEvery: 2, delayMs: 50 });
    for (const value of ['one', 'two', 'three']) await send(f.clients[0], value, f.proxy.port);
    await waitUntil(() => f.received.length === 3 && f.replies[0].length === 3, 'single-flow reordered packets missing');
    assert.deepEqual(f.received.map(row => row.data), ['one', 'three', 'two']);
    assert.ok(f.proxy.stats.reordered > 0);
    assert.equal(f.proxy.stats.reorderedWithinRoute, f.proxy.stats.reordered);
    assert.equal(f.proxy.stats.routes[0].reorderedWithinRoute, f.proxy.stats.reorderedWithinRoute);
    assert.equal(f.proxy.stats.errorCount, 0);
  } finally { await f.close(); }
});
