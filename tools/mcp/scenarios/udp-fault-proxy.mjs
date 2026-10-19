import dgram from 'node:dgram';
import { createHash } from 'node:crypto';

// Single loopback peer only. Faults affect real UDP datagrams, including ACKs/retransmits.
export class UdpFaultProxy {
  constructor({ targetPort = 4200, listenPort = 0 } = {}) {
    if (!Number.isInteger(targetPort) || targetPort < 1 || targetPort > 65535 ||
        !Number.isInteger(listenPort) || listenPort < 0 || listenPort > 65535) throw Error('Invalid UDP port');
    this.targetPort = targetPort; this.listenPort = listenPort;
    this.front = dgram.createSocket('udp4'); this.back = dgram.createSocket('udp4');
    this.policy = {}; this.timers = new Map(); this.peer = null; this.closed = false;
    this.stats = { received: { toHost: 0, toClient: 0 }, sent: { toHost: 0, toClient: 0 },
      dropped: 0, delayed: 0, reordered: 0, errors: [], samples: [], policies: [] };
    this.lastSent = { toHost: 0, toClient: 0 };
    for (const socket of [this.front, this.back]) socket.on('error', e => this.stats.errors.push(e.message));
    this.front.on('message', (data, peer) => {
      if (peer.address !== '127.0.0.1') return;
      this.peer = peer; this.forward('toHost', data);
    });
    this.back.on('message', (data, peer) => {
      if (peer.address === '127.0.0.1' && peer.port === this.targetPort) this.forward('toClient', data);
    });
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
    for (const key of ['dropEvery', 'delayEvery', 'delayMs'])
      if (policy[key] !== undefined && (!Number.isInteger(policy[key]) || policy[key] < 0 || policy[key] > 60000)) throw Error('Invalid ' + key);
    if (policy.blackout) {
      for (const [timer, sample] of this.timers) {
        clearTimeout(timer); sample.action = 'drop-on-blackout'; this.stats.dropped++;
      }
      this.timers.clear();
    }
    this.policy = { ...policy };
    this.stats.policies.push({ utc: new Date().toISOString(), ...policy });
  }
  forward(direction, data) {
    if (this.closed) return;
    const sequence = ++this.stats.received[direction];
    const drop = this.policy.blackout || (this.policy.dropEvery && sequence % this.policy.dropEvery === 0);
    const delayed = !drop && this.policy.delayEvery && sequence % this.policy.delayEvery === 0;
    const sample = { utc: new Date().toISOString(), direction, sequence, bytes: data.length,
      sha256: createHash('sha256').update(data).digest('hex'), action: drop ? 'drop' : delayed ? 'delay' : 'send' };
    if (this.stats.samples.length < 2000 &&
        (drop || delayed || this.stats.samples.length < 32)) this.stats.samples.push(sample);
    if (drop) { this.stats.dropped++; return; }
    const send = () => {
      if (this.closed) return;
      if (sequence < this.lastSent[direction]) this.stats.reordered++;
      this.lastSent[direction] = Math.max(sequence, this.lastSent[direction]);
      sample.sentUtc = new Date().toISOString();
      const callback = error => { if (error) this.stats.errors.push(error.message); else this.stats.sent[direction]++; };
      if (direction === 'toHost') this.back.send(data, this.targetPort, '127.0.0.1', callback);
      else if (this.peer) this.front.send(data, this.peer.port, this.peer.address, callback);
    };
    if (delayed) {
      this.stats.delayed++; const timer = setTimeout(() => { this.timers.delete(timer); send(); }, this.policy.delayMs ?? 200);
      this.timers.set(timer, sample);
    } else send();
  }
  async close() {
    this.closed = true; for (const timer of this.timers.keys()) clearTimeout(timer); this.timers.clear();
    await Promise.all([this.front, this.back].map(socket => new Promise(resolve => {
      try { socket.close(resolve); } catch { resolve(); }
    })));
  }
}
