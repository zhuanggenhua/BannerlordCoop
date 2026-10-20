import dgram from 'node:dgram';
import { createHash } from 'node:crypto';

const DEFAULT_MAX_HELD_BYTES = 16 * 1024 * 1024;
const DEFAULT_MAX_HELD_PACKETS = 2048;
const MAX_RECORDED_ERRORS = 100;
const MAX_HELD_HASH_SAMPLES = 128;
const DEFAULT_MAX_ROUTES = 32;

// Each loopback source endpoint keeps its own host-facing socket, including Mission/NAT traffic.
// Faults affect real UDP datagrams, including ACKs/retransmits.
export class UdpFaultProxy {
  constructor({
    targetPort = 4200,
    listenPort = 0,
    maxHeldBytes = DEFAULT_MAX_HELD_BYTES,
    maxHeldPackets = DEFAULT_MAX_HELD_PACKETS,
    maxRoutes = DEFAULT_MAX_ROUTES,
  } = {}) {
    if (!Number.isInteger(targetPort) || targetPort < 1 || targetPort > 65535 ||
        !Number.isInteger(listenPort) || listenPort < 0 || listenPort > 65535) throw Error('Invalid UDP port');
    if (!Number.isSafeInteger(maxHeldBytes) || maxHeldBytes < 1) throw Error('Invalid maxHeldBytes');
    if (!Number.isSafeInteger(maxHeldPackets) || maxHeldPackets < 1) throw Error('Invalid maxHeldPackets');
    if (!Number.isInteger(maxRoutes) || maxRoutes < 1 || maxRoutes > 256) throw Error('Invalid maxRoutes');
    this.targetPort = targetPort; this.listenPort = listenPort;
    this.maxHeldBytes = maxHeldBytes; this.maxHeldPackets = maxHeldPackets; this.maxRoutes = maxRoutes;
    this.front = dgram.createSocket('udp4'); this.back = dgram.createSocket('udp4');
    this.policy = {}; this.policyUtc = null; this.timers = new Map(); this.heldQueue = [];
    this.heldBytes = 0; this.closed = false; this.closePromise = null;
    this.routes = new Map(); this.backendSockets = new Set([this.back]); this.backendRoutes = new Map();
    this.pendingRoutePackets = 0; this.pendingRouteBytes = 0;
    this.stats = { received: { toHost: 0, toClient: 0 }, sent: { toHost: 0, toClient: 0 },
      dropped: 0, delayed: 0, reordered: 0, heldTotal: 0, heldCurrentCount: 0, heldCurrentBytes: 0,
      heldOverflow: 0, heldSamples: [], errorCount: 0, errors: [], samples: [], policies: [] };
    Object.assign(this.stats, { routesCreated: 0, routeLimitRejected: 0, routeBindFailures: 0,
      routePendingOverflow: 0, routePendingCurrentCount: 0, routePendingCurrentBytes: 0,
      reorderedWithinRoute: 0, routes: [] });
    this.lastSent = { toHost: 0, toClient: 0 };
    this.front.on('error', e => this.recordError(e.message));
    this.attachBackend(this.back);
    this.front.on('message', (data, peer) => {
      if (this.closed || peer.address !== '127.0.0.1') return;
      this.forward('toHost', data, this.getRoute(peer), `${peer.address}:${peer.port}`);
    });
  }
  attachBackend(socket) {
    socket.on('error', error => this.backendError(socket, error));
    socket.on('message', (data, peer) => {
      const route = this.backendRoutes.get(socket);
      if (route && peer.address === '127.0.0.1' && peer.port === this.targetPort)
        this.forward('toClient', data, route);
    });
  }
  backendError(socket, error) {
    const route = this.backendRoutes.get(socket);
    this.recordError(route ? `UDP backend route ${route.id} source ${route.endpoint.address}:${route.endpoint.port}: ${error.message}` : error.message);
    if (route && !route.ready && !route.failed) {
      route.failed = true; route.stats.failed = true; this.stats.routeBindFailures++;
      this.drainRoutePending(route, 'drop-route-bind-failed');
    }
  }
  getRoute(peer) {
    const key = `${peer.address}:${peer.port}`;
    if (this.routes.has(key)) return this.routes.get(key);
    if (this.routes.size >= this.maxRoutes) {
      this.stats.routeLimitRejected++;
      this.recordError(`UDP source endpoint route limit exceeded for ${key}; existing routes retained`);
      return null;
    }
    const first = this.routes.size === 0;
    const socket = first ? this.back : dgram.createSocket('udp4');
    const route = { id: this.routes.size + 1, endpoint: Object.freeze({ address: peer.address, port: peer.port }),
      socket, ready: first, failed: false, pending: [] };
    route.stats = { id: route.id, sourceEndpoint: `${peer.address}:${peer.port}`,
      backendPort: first ? socket.address().port : null, ready: first, failed: false,
      lastSent: { toHost: 0, toClient: 0 }, reorderedWithinRoute: 0 };
    this.stats.routes.push(route.stats);
    this.routes.set(key, route); this.backendRoutes.set(socket, route); this.backendSockets.add(socket);
    this.stats.routesCreated++;
    if (!first) {
      this.attachBackend(socket);
      try {
        socket.bind(0, '127.0.0.1', () => {
          if (this.closed || route.failed) return;
          route.ready = true; route.stats.ready = true; route.stats.backendPort = socket.address().port;
          this.drainRoutePending(route);
        });
      } catch (error) { this.backendError(socket, error); }
    }
    return route;
  }
  drainRoutePending(route, dropAction, utc = new Date().toISOString()) {
    const pending = route.pending.splice(0);
    for (const queued of pending) {
      this.pendingRoutePackets--; this.pendingRouteBytes -= queued.data.length;
      if (dropAction) {
        queued.sample.action = dropAction; queued.sample.droppedUtc = utc; this.stats.dropped++;
      } else this.transmit(queued.direction, queued.data, queued.sequence, queued.sample, route);
    }
    this.stats.routePendingCurrentCount = this.pendingRoutePackets;
    this.stats.routePendingCurrentBytes = this.pendingRouteBytes;
  }
  recordError(message) {
    this.stats.errorCount++;
    if (this.stats.errors.length < MAX_RECORDED_ERRORS) this.stats.errors.push(String(message));
  }
  async start() {
    const bind = (socket, port) => new Promise((resolve, reject) => {
      socket.once('error', reject); socket.bind(port, '127.0.0.1', () => { socket.removeListener('error', reject); resolve(); });
    });
    try { await bind(this.back, 0); await bind(this.front, this.listenPort); }
    catch (error) { await this.close(); throw error; }
    this.port = this.front.address().port; return this;
  }
  setFault(policy = {}) {
    if (policy == null || typeof policy !== 'object' || Array.isArray(policy)) throw Error('Invalid fault policy');
    for (const key of ['dropEvery', 'delayEvery', 'delayMs'])
      if (policy[key] !== undefined && (!Number.isInteger(policy[key]) || policy[key] < 0 || policy[key] > 60000)) throw Error('Invalid ' + key);
    if (policy.holdToClient !== undefined && typeof policy.holdToClient !== 'boolean') throw Error('Invalid holdToClient');
    if (policy.blackout && policy.holdToClient) throw Error('Cannot hold datagrams during blackout');

    const previous = this.policy;
    const policyUtc = new Date().toISOString();
    if (policy.blackout) {
      for (const [timer, sample] of this.timers) {
        clearTimeout(timer); sample.action = 'drop-on-blackout'; sample.droppedUtc = policyUtc; this.stats.dropped++;
      }
      this.timers.clear();
      this.dropHeld('drop-on-blackout', policyUtc);
      for (const route of this.routes.values()) this.drainRoutePending(route, 'drop-on-blackout', policyUtc);
    } else if (policy.holdToClient) {
      for (const [timer, sample] of this.timers) {
        if (sample.direction !== 'toClient') continue;
        clearTimeout(timer); this.timers.delete(timer);
        sample.action = 'drop-before-hold'; sample.droppedUtc = policyUtc; this.stats.dropped++;
      }
    }

    this.policy = { ...policy };
    this.policyUtc = policyUtc;
    this.stats.policies.push({ utc: policyUtc, ...policy });
    if (!policy.blackout && previous.holdToClient && !policy.holdToClient) this.flushHeld();
  }
  recordSample(sample) {
    if (this.stats.samples.length < 2000 &&
        (sample.action !== 'send' || this.stats.samples.length < 32)) this.stats.samples.push(sample);
    if (sample.direction === 'toClient' && sample.policyUtc &&
        this.stats.heldSamples.length < MAX_HELD_HASH_SAMPLES) this.stats.heldSamples.push(sample);
  }
  queueHeld(direction, data, sequence, sample, route) {
    sample.action = 'hold'; sample.heldUtc = new Date().toISOString(); sample.policyUtc = this.policyUtc;
    if (this.heldQueue.length + 1 > this.maxHeldPackets || this.heldBytes + data.length > this.maxHeldBytes) {
      sample.action = 'hold-overflow';
      this.stats.heldOverflow++; this.stats.dropped++;
      this.recordError('Held UDP queue exceeded configured packet or byte limit');
      this.recordSample(sample);
      return;
    }
    const queued = { direction, data: Buffer.from(data), sequence, sample, route };
    this.heldQueue.push(queued); this.heldBytes += queued.data.length;
    this.stats.heldTotal++;
    this.updateHeldStats();
    this.recordSample(sample);
  }
  updateHeldStats() {
    this.stats.heldCurrentCount = this.heldQueue.length;
    this.stats.heldCurrentBytes = this.heldBytes;
  }
  dropHeld(action, utc = new Date().toISOString()) {
    for (const queued of this.heldQueue) {
      queued.sample.action = action; queued.sample.droppedUtc = utc; this.stats.dropped++;
    }
    this.heldQueue.length = 0; this.heldBytes = 0; this.updateHeldStats();
  }
  flushHeld() {
    if (this.closed || this.policy.blackout) return;
    const held = this.heldQueue.splice(0);
    this.heldBytes = 0; this.updateHeldStats();
    for (const queued of held) {
      queued.sample.action = 'release-held'; queued.sample.releasedUtc = new Date().toISOString();
      this.transmit(queued.direction, queued.data, queued.sequence, queued.sample, queued.route);
    }
  }
  forward(direction, data, route, sourceEndpoint = null) {
    if (this.closed) return;
    const sequence = ++this.stats.received[direction];
    const sample = { utc: new Date().toISOString(), direction, sequence, bytes: data.length,
      sha256: createHash('sha256').update(data).digest('hex'), action: 'send', routeId: route?.id ?? null,
      sourceEndpoint: route ? route.stats.sourceEndpoint : sourceEndpoint, backendPort: route?.stats.backendPort ?? null };

    if (!route) {
      sample.action = 'drop-route-limit'; this.stats.dropped++; this.recordSample(sample); return;
    }

    if (this.policy.blackout) {
      sample.action = 'drop'; this.stats.dropped++; this.recordSample(sample); return;
    }
    if (direction === 'toClient' && this.policy.holdToClient) {
      this.queueHeld(direction, data, sequence, sample, route); return;
    }

    const drop = this.policy.dropEvery && sequence % this.policy.dropEvery === 0;
    const delayed = !drop && this.policy.delayEvery && sequence % this.policy.delayEvery === 0;
    sample.action = drop ? 'drop' : delayed ? 'delay' : 'send';
    this.recordSample(sample);
    if (drop) { this.stats.dropped++; return; }
    if (delayed) {
      this.stats.delayed++; const timer = setTimeout(() => {
        this.timers.delete(timer); this.transmit(direction, data, sequence, sample, route);
      }, this.policy.delayMs ?? 200);
      this.timers.set(timer, sample);
      return;
    }
    this.transmit(direction, data, sequence, sample, route);
  }
  transmit(direction, data, sequence, sample, route) {
    if (this.closed || this.policy.blackout) return;
    if (route.failed) {
      sample.action = 'drop-route-bind-failed'; this.stats.dropped++; return;
    }
    if (direction === 'toHost' && !route.ready) {
      if (this.pendingRoutePackets + 1 > this.maxHeldPackets || this.pendingRouteBytes + data.length > this.maxHeldBytes) {
        sample.action = 'drop-route-pending-overflow'; this.stats.routePendingOverflow++; this.stats.dropped++;
        this.recordError('Pending UDP route bind queue exceeded configured packet or byte limit'); return;
      }
      route.pending.push({ direction, data: Buffer.from(data), sequence, sample });
      this.stats.routePendingCurrentCount = ++this.pendingRoutePackets;
      this.stats.routePendingCurrentBytes = this.pendingRouteBytes += data.length;
      return;
    }
    if (sequence < this.lastSent[direction]) this.stats.reordered++;
    this.lastSent[direction] = Math.max(sequence, this.lastSent[direction]);
    if (sequence < route.stats.lastSent[direction]) {
      this.stats.reorderedWithinRoute++; route.stats.reorderedWithinRoute++;
    }
    route.stats.lastSent[direction] = Math.max(sequence, route.stats.lastSent[direction]);
    sample.backendPort = route.stats.backendPort;
    sample.sentUtc = new Date().toISOString();
    const callback = error => { if (error) this.recordError(error.message); else this.stats.sent[direction]++; };
    if (direction === 'toHost') route.socket.send(data, this.targetPort, '127.0.0.1', callback);
    else this.front.send(data, route.endpoint.port, route.endpoint.address, callback);
  }
  async close() {
    if (this.closePromise) return this.closePromise;
    this.closed = true;
    const closedUtc = new Date().toISOString();
    for (const [timer, sample] of this.timers) {
      clearTimeout(timer); sample.action = 'drop-on-close'; sample.droppedUtc = closedUtc; this.stats.dropped++;
    }
    this.timers.clear();
    this.dropHeld('drop-on-close', closedUtc);
    for (const route of this.routes.values()) this.drainRoutePending(route, 'drop-on-close', closedUtc);
    this.closePromise = Promise.all([this.front, ...this.backendSockets].map(socket => new Promise(resolve => {
      try { socket.close(resolve); } catch { resolve(); }
    })));
    await this.closePromise;
  }
}
