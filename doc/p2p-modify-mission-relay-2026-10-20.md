# Bounded local Mission relay receipt — 2026-10-20

Base e3a1741a6c6ebb8dddaa6ca2b467aa409f650e26. No worker games/deployment; native Mission entry remains closed until consumer/result boundaries are complete.

## Mandatory core boundary rationale (recorded before edits)

1. Original MissionManager authenticates remote relay membership/current peers and preserves credentials; RelayPacketHandler forwards raw bytes to a real remote target. Original AgentMovementHandler accepts verified direct Mission peers or the actual room relay peer, checks per-agent authority/revision/sample order, then uses established prediction/interpolation. IMissionContext mirrors membership/direct mappings; the original P2P socket owns scene connect/disconnect.
2. A Listen local target has no room-self NetPeer. A new receiver and membership receipt adapt delivery without replacing transport/serialization/Registry or movement. The existing movement source predicate nevertheless rejects the actual remote campaign peer because it is neither a direct Mission peer nor the remote room-server peer. The sole required movement-handler adaptation is a narrow optional source qualification receipt; no mapping or endpoint can legitimately make the remote player the room-selfpeer.
3. Preserve actual peer identity, source/target instance membership, rotating credentials and membership generation; bind queued work to the active local scene generation. Admission alone is insufficient. Keep default direct and remote-relay checks, Agent authority revisions/sample ordering, Owner/Puppet, prediction/interpolation, actions/damage and original serializers unchanged. Local relay qualification cannot grant world writes or create a Campaign loopback connection.
4. Required managed regressions: source spoof, wrong instance/target, old credential/generation, peer replacement, receive then leave/rejoin before game-thread delivery, scene change during queue, source receipt revalidation before movement apply, receiver disposal, and unchanged original direct/remote-relay movement qualification. Reliable spawn messages may enqueue twice; this slice must explicitly refuse them unless all descendant execution is source-bound, never claim all raw packets safe from an initial check.

## Planned public boundaries

Reuse MissionManager as the receipt issuer/verifier, with typed GI MissionRelayReceipt and ILocalMissionRelayReceiver. Common IMissionPacketSourcePolicy exposes only capture/current-check of an opaque receipt. The Missions receiver owns synchronous dispatch provenance; packet handlers retain the receipt explicitly when queueing. Existing IMissionContext owns active-instance generation. No second global session manager is introduced.

First slice is movement and mount movement only. Reliable spawn/action/message delivery, local reserve consumption, result submission and native entry activation remain outside this bounded change.

## Receipt issuer slice

MissionManager now issues opaque receipts bound to the actual source campaign peer, exact source/target membership objects, both credentials and both monotonically increasing membership generations. Repeated unchanged entry retains generation; leave/rejoin or remote peer replacement invalidates the receipt. Verification consults the original current-player peer mapping and relay revocation. No sender controller is trusted from the packet itself. `receipt01` managed regression passed 94/94 (original manager/membership tests plus new receipt cases); no native acceptance.

## Bounded receiver slice

`LocalMissionRelayReceiver` restores compressed movement with the existing `IMovementPacketCompressor.TryRestore`, and admits only `MovementPacket`/`MountMovementPacket`. It checks the inner controller against the real issuer receipt before queueing, then checks current source/target membership credentials and generations plus the local Mission visit generation at GT dispatch. It calls the original `PacketManager` with the actual remote campaign peer; it never fabricates a direct peer or a room-selfpeer.

The optional `IMissionPacketSourcePolicy` changes only movement source qualification. The original direct/remote-room relay paths and per-agent ownership/revision, sequencing, mount, interpolation and prediction logic remain intact. The receiver exposes capture only during its synchronous verified dispatch. A movement handler explicitly retains that opaque receipt and revalidates it at actual GT apply; implicit ambient propagation to descendant tasks is not used.

`TryReceive == true` means admitted, not applied or acknowledged. Queued departure/rejoin, scene replacement or disposal can safely discard it. `MissionContext.BeginInstance` is called when the existing P2P client starts a fresh socket visit (which already resets its own instance generation); it does not run on repeated unchanged membership entry. Membership generations independently stay unchanged for idempotent room entries.

NOT_IMPLEMENTED: reliable spawn, action, IMessage, PacketWrapper and nested relay delivery to the local target. They are deliberately refused; reliable spawn's second Enqueue lacks a fully retained source continuation today. Local reserve consumption, complete result submission/commit ACK, and actual Mission entry remain closed. No full Mission gameplay or native acceptance is claimed.

Managed evidence retained: receipt01 and receipt02 each 94/94 PASS; receiver01 15/15 PASS; receiver02 16 PASS / 1 FAIL because the new test attempted an unsupported existing nested-IPacket serializer shape. Its original log/TRX remain intact. The test now exercises the decoded wrapper type gate without changing the serializer or relaxing the refusal assertion. receiver03 107/107 PASS, including real Protobuf+LZ4 movement roundtrip through RelayPacketHandler and the unchanged movement handler, actual receipt invalidation at queued movement apply, default direct/room relay source qualification, spoof rejection and stale queued receipts. Final additional mount roundtrip/regressions recorded in the handoff.


Final receiver06 managed run: 108/108 PASS (94 membership/local-control cases and 14 receiver/source cases). Both rider and masterless-mount compressed wire shapes use the unmodified production serializer/compressor. Native Agent movement is not simulated by this suite: registry misses deliberately terminate after the actual source/GT qualification boundary; existing movement E2E tests separately cover algorithms. receiver04 retains 107 PASS / 1 FAIL from null AgentMountData DTO elements; receiver05 retains its compile failure from an ambiguous test-only ObjectManager import. Test DTO construction/imports were corrected without production codec changes or assertion reduction. No FAIL artifact was overwritten.
