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
