import dgram from 'node:dgram';
import { createHash } from 'node:crypto';

const DEFAULT_MAX_HELD_BYTES = 16 * 1024 * 1024;
const DEFAULT_MAX_HELD_PACKETS = 2048;
const MAX_RECORDED_ERRORS = 100;
const MAX_HELD_HASH_SAMPLES = 128;

// Single loopback peer only. Faults affect real UDP datagrams, including ACKs/retransmits.
export class UdpFaultProxy {
  constructor({
    targetPort = 4200,
    listenPort = 0,
    maxHeldBytes = DEFAULT_MAX_HELD_BYTES,
    maxHeldPackets = DEFAULT_MAX_HELD_PACKETS,
  } = {}) {
    if (!Number.isInteger(targetPort) || targetPort < 1 || targetPort > 65535 ||
        !Number.isInteger(listenPort) || listenPort < 0 || listenPort > 65535) throw Error('Invalid UDP port');
    if (!Number.isSafeInteger(maxHeldBytes) || maxHeldBytes < 1) throw Error('Invalid maxHeldBytes');
    if (!Number.isSafeInteger(maxHeldPackets) || maxHeldPackets < 1) throw Error('Invalid maxHeldPackets');
    this.targetPort = targetPort; this.listenPort = listenPort;
    this.maxHeldBytes = maxHeldBytes; this.maxHeldPackets = maxHeldPackets;
    this.front = dgram.createSocket('udp4'); this.back = dgram.createSocket('udp4');
    this.policy = {}; this.policyUtc = null; this.timers = new Map(); this.heldQueue = [];
    this.heldBytes = 0; this.peer = null; this.closed = false;
    this.stats = { received: { toHost: 0, toClient: 0 }, sent: { toHost: 0, toClient: 0 },
      dropped: 0, delayed: 0, reordered: 0, heldTotal: 0, heldCurrentCount: 0, heldCurrentBytes: 0,
      heldOverflow: 0, heldSamples: [], errorCount: 0, errors: [], samples: [], policies: [] };
    this.lastSent = { toHost: 0, toClient: 0 };
    for (const socket of [this.front, this.back]) socket.on('error', e => this.recordError(e.message));
    this.front.on('message', (data, peer) => {
      if (peer.address !== '127.0.0.1') return;
      this.peer = peer; this.forward('toHost', data);
    });
    this.back.on('message', (data, peer) => {
      if (peer.address === '127.0.0.1' && peer.port === this.targetPort) this.forward('toClient', data);
    });
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
  queueHeld(direction, data, sequence, sample) {
    sample.action = 'hold'; sample.heldUtc = new Date().toISOString(); sample.policyUtc = this.policyUtc;
    if (this.heldQueue.length + 1 > this.maxHeldPackets || this.heldBytes + data.length > this.maxHeldBytes) {
      sample.action = 'hold-overflow';
      this.stats.heldOverflow++; this.stats.dropped++;
      this.recordError('Held UDP queue exceeded configured packet or byte limit');
      this.recordSample(sample);
      return;
    }
    const queued = { direction, data: Buffer.from(data), sequence, sample };
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
      this.transmit(queued.direction, queued.data, queued.sequence, queued.sample);
    }
  }
  forward(direction, data) {
    if (this.closed) return;
    const sequence = ++this.stats.received[direction];
    const sample = { utc: new Date().toISOString(), direction, sequence, bytes: data.length,
      sha256: createHash('sha256').update(data).digest('hex'), action: 'send' };

    if (this.policy.blackout) {
      sample.action = 'drop'; this.stats.dropped++; this.recordSample(sample); return;
    }
    if (direction === 'toClient' && this.policy.holdToClient) {
      this.queueHeld(direction, data, sequence, sample); return;
    }

    const drop = this.policy.dropEvery && sequence % this.policy.dropEvery === 0;
    const delayed = !drop && this.policy.delayEvery && sequence % this.policy.delayEvery === 0;
    sample.action = drop ? 'drop' : delayed ? 'delay' : 'send';
    this.recordSample(sample);
    if (drop) { this.stats.dropped++; return; }
    if (delayed) {
      this.stats.delayed++; const timer = setTimeout(() => {
        this.timers.delete(timer); this.transmit(direction, data, sequence, sample);
      }, this.policy.delayMs ?? 200);
      this.timers.set(timer, sample);
      return;
    }
    this.transmit(direction, data, sequence, sample);
  }
  transmit(direction, data, sequence, sample) {
    if (this.closed || this.policy.blackout) return;
    if (sequence < this.lastSent[direction]) this.stats.reordered++;
    this.lastSent[direction] = Math.max(sequence, this.lastSent[direction]);
    sample.sentUtc = new Date().toISOString();
    const callback = error => { if (error) this.recordError(error.message); else this.stats.sent[direction]++; };
    if (direction === 'toHost') this.back.send(data, this.targetPort, '127.0.0.1', callback);
    else if (this.peer) this.front.send(data, this.peer.port, this.peer.address, callback);
  }
  async close() {
    if (this.closed) return;
    this.closed = true;
    const closedUtc = new Date().toISOString();
    for (const [timer, sample] of this.timers) {
      clearTimeout(timer); sample.action = 'drop-on-close'; sample.droppedUtc = closedUtc; this.stats.dropped++;
    }
    this.timers.clear();
    this.dropHeld('drop-on-close', closedUtc);
    await Promise.all([this.front, this.back].map(socket => new Promise(resolve => {
      try { socket.close(resolve); } catch { resolve(); }
    })));
  }
}
