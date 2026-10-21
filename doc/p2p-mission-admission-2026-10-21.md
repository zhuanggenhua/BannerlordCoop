# Mission admission continuation, 2026-10-21

Base `3d0060cb7619c5ae954712d9c95fda9698113f96`. Native Mission entry remains closed until the separate reliable control, result and protected-admission dependencies are proven. This work does not replace a real Mission with QA readiness.

## First slice: room-to-local troop reserves

Recorded before production edits:

1. `BattleHostHandler.SendController` already elects the original Mission host, builds the original two-side reserve snapshot, and sends it either to the real remote peer or through the local broker. `BattleTroopReserveHandler` feeds the original supplier registry, captures dropped-party pointers atomically through `SetReserve`, and emits one flush reply per flagged message.
2. The Listen receiver currently returns because `IsServer` is true. Merely removing this return would also accept undirected local publications and would send its flush to every remote room peer through `INetwork.SendAll`. A typed room-to-local endpoint and the existing `IRelayNetwork` room adapter suffice; supplier allocation, descriptor seeds, progress and serialization need no change.
3. Preserve original snapshot revisions, monotonically supplied pointers, dropped-party flush capture, real remote delivery, and separation of room membership from Campaign authority. Local replies must bind to the exact session object, room, room host and target actor. A prior session with the same textual room id is not the current session. Mission control is not a Campaign write permit and does not bind to the changing world Epoch.
4. Regress valid local receipt, pre-supplier buffering, wrong target/room/session, missing session/room adapter, dedicated rejection, unchanged remote receipt/flush, repeated reserve pointer monotonicity and exactly one reply per flagged refresh. Regress existing local election and relay suites unchanged. Native Mission, shared battle and settlement remain `NOT_RUN`.

## Next bounded seam: observed reliable dispatch lifetime

The existing movement relay validates actual source peer, both memberships/credentials/generations and local scene generation, then dispatches through the original packet manager. It deliberately refuses reliable Mission payloads. Reliable handlers can enqueue descendants, and spawn state may also outlive a queued callback in pending native consumers. Initial receive validation cannot certify those later applies.

The approved Common extension is generic observation only: a nested observation must retain and compose the existing parent apply receipt until all GameThread descendants complete. It grants no Replay, world write, peer identity or ACK permission. A bounded allowlist and each original sender/ownership/HostEpoch validation remain necessary. Arbitrary `IMessage`, world commands, aggregates and wrappers stay refused. Production Mission entry must not be opened by this slice.

## Reserve slice evidence

The original election/relay baseline passed 17/17. Against the restored original reserve handler, the new consumer contract ran 13 tests: 11 passed, 2 failed because Listen reserves never populated the original supplier. The final candidate runs 33/33, including both original suites, the new reserve cases and an original election-to-supplier path. The first attempted green build retained an older incremental reference after the RED source restore; its compiler failure is retained. Touching the restored candidate source caused the correct rebuild, without changing any assertion.

The broader selected Mission battle, room-instance and MapEvent regression passed 584/584, zero skipped. This is a bounded selected suite, not a claim that the historical Coop/E2E failures disappeared.

`LocalMissionControlEndpoint` is the room-to-local direction, distinct from the existing player-to-room endpoint. It binds the exact session object, room id, room host and target. World handoff and paused leases intentionally do not invalidate room control. A local flush uses the existing `IRelayNetwork`; remote replicas use the original `INetwork` path. Every flagged duplicate refresh still receives its original one-per-message flush response, and the unchanged supplier preserves its maximum local supplied pointer. This adapter is not a result-commit receipt or admission grant.

Native Mission admission, ordinary shared battle/casualties, all-away Mission pause, full natural day and performance are `NOT_RUN` for this slice. Reliable spawn/action remains gated pending complete source-bound lifetime coverage.
