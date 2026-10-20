# QA UDP source-endpoint isolation

This change is confined to the Node QA fault proxy and its tests. Game networking, LiteNetLib/NAT messages, Mission Owner/Puppet, movement and authority/checkpoint behavior are unchanged. A UDP routing reproduction is not a game acceptance result; the original failed native runs remain failed and retained.

## Evidence and change boundary

1. The existing proxy supports real loopback UDP forwarding, drop/delay/hold/blackout, payload hashes, bounded held queues and shutdown. It previously updated one mutable `peer` on every incoming source datagram and reused one host-facing `back` socket.
2. The same real UDP counterexample was run before and after the fix: room traffic establishes a route, a second Mission/NAT source sends, then the host sends an unsolicited follow-up to the original room route. Before the fix both sources appeared at the host on one port and the follow-up reached Mission instead of room. After the fix the host sees two distinct ports and the follow-up reaches room. An outer scenario cannot recover the destination once the shared internal mapping has been overwritten; the smallest repair belongs in this QA adapter, not game or transport-core code.
3. Preserve each existing fault policy, global sequence/counter behavior, held FIFO, explicit overflow, hashes and blackout/close dropping. Every delayed closure, held item and bind-pending item now captures the original route. A later source cannot replace that endpoint. No second source is ignored and no wire packet is parsed or rewritten.
4. Regressions retain the original eight top-level tests unchanged and add real multi-source forwarding, unsolicited reply, delayed and held route isolation, blackout, limits, bind failure and complete resource cleanup. The retained RED reproduction is not deleted or treated as PASS.

## Routing and bounds

`UdpFaultProxy` keeps a map from loopback source address/port to an immutable return endpoint and an independent host-facing UDP socket. The original pre-bound `back` is the first route's socket; subsequent sources receive new sockets. Host replies are associated by the socket that received them. Backend creation does not reconnect or change the other routes.

`maxRoutes` defaults to **32** and permits **1–256**. Routes are not silently evicted or reused until the whole proxy closes. Overflow increments `routeLimitRejected`, `dropped` and `errorCount`, records the rejected endpoint and retains existing routes. A new backend bind failure is visible through `routeBindFailures`/`errorCount`; its queued datagrams are dropped, not rerouted.

Packets waiting for a new backend's readiness share global pending-bind bounds using the configured `maxHeldPackets`/`maxHeldBytes`. Overflow is visible separately from the original held-queue limit. Blackout and close discard that queue as well as existing delayed/held traffic. Concurrent `close()` calls await closure of the front and every backend, including failed/pending ones. Real port-rebind tests verify release rather than relying only on a boolean.

Samples add `routeId`, `sourceEndpoint` and `backendPort`; bounded `stats.routes` records endpoint-to-backend correspondence and bind readiness/failure. These are loopback QA diagnostics, not application identities. Existing hash/sample/error retention bounds remain; there is no unbounded per-packet log.

The original `stats.reordered` and global `lastSent` calculations remain unchanged. A real two-source readiness counterexample dispatches room sequences1/3 before Mission sequence2, incrementing the original global counter despite both independent flows staying ordered. The additional `stats.reorderedWithinRoute` and each route's `lastSent`/`reorderedWithinRoute` count dispatch inversions within that route and direction; the aggregate equals the sum of route counts. A separate real single-flow delayed inversion increments both metrics. These are proxy dispatch-order counters, not a measurement of downstream wire arrival. No native Mission oracle or historical evidence is changed here; missing new telemetry must not be interpreted as zero.

`stats.routes` has exactly one retained row per accepted endpoint, up to `maxRoutes`; `routesCreated === routes.length` because there is no eviction. Each row is `{ id, sourceEndpoint, backendPort, ready, failed, lastSent: { toHost, toClient }, reorderedWithinRoute }`. `backendPort` is null until readiness and numeric afterwards; failure is explicit. The aggregate counter is updated for every transmit attempt, independent of the bounded payload-sample list.

## Validation boundary

The task handoff retains the original-source RED, repaired-source GREEN, original test run and final test receipts with actual exit codes and SHA256. No game, deployment, native MCP call or C# build is performed by this worker. The coordinator must independently review/integrate and repeat proxy-backed Mission/fault scenarios on the final integrated source. A successful direct-profile game control does not prove the repaired proxy-backed scenario passed.

Final localized test result: **18 PASS / 0 FAIL**, comprising the eight unchanged tests plus ten new tests. Final test log SHA256: `aa8434a5d2288f78ad64926e4e3645c3d25bd44fca7e3fb46af21666b2675f96`. Retained RED SHA256: `1a09863b4976820b0daf68d3d67cb3a0b6febcbd635017898106d3919c944524`; final same-counterexample GREEN SHA256: `4e00572744ce9df730e8460be0a5a8f8f444349e30caacfa3ad455e4fb2a0f62`. The metadata verifier checks the original test file remains an unchanged prefix and validates these receipts; it does not replay a native game.
