# MODIFY state/apply boundaries — 2026-10-20

Dynamic campaign migration remains required. This slice does not replace it with fixed-host simulation.
Base d2738eda23f4406e7566b68fba0ff4b78ce89e90. Component publication candidate R13 is independently reviewed and committed separately; original Native08 remains FAIL until final integrated native rerun.

## Four-part core justification (before implementation)

1. MessageBroker currently preserves reliable per-channel publication order and logs/catches subscriber failures; GameThread preserves queue order, cancellation and RunSafe log handling. GenericHandler performs registry lookup and native replay on the game thread. AutoRegistry retains its original allocation, creation/destruction callbacks and logged-catch behavior.
2. An outer adapter observing Publish return cannot see a swallowed subscriber exception, a RunSafe failure, a queued descendant, or AutoRegistry's callback catch/AddExisting refusal. Those are concrete success-observation gaps. A narrow optional completion observer at those existing catch/queue boundaries is necessary; no algorithm replacement is proposed. Root approved only observation annotations in AutoRegistry after this gap was identified.
3. Keep original broker subscribers/logs/weak references, FIFO queues, cancellation, AllowedThread, allocator and Registry lifecycle. Completion is evidence only and grants no world-write/replay authority. Classified received results retain a lease-bound receipt through synchronous and explicitly queued game-thread descendants. Cancellation is a fault; changing epoch cannot erase an earlier fault. Pending tasks block acknowledgement. Different sessions/rooms remain isolated. Do not widen all messages into critical results: unobserved local events without subscribers are legitimate. No automatic Task.Run authority/receipt propagation is introduced.
4. Regression obligations: original broker and GameThread tests; nested queue completion, caught failure, discard/cancellation/closed queue, stale source lease and no-subscriber probes; actual GenericHandler missing-instance/reference tests; AutoRegistry create refusal/callback catches and destroy missing/catches; candidate barrier pending/fault acknowledgement rejection; original session handoff, movement and lifecycle tests.

## Implementation scope

CampaignApplyCompletion is weakly owned by the existing ISessionIdentity. The real broker binds its optional observer once to that same session. It is not a replacement session, object manager, Registry, transport or world state machine. States are keyed by full AuthorityLease. The final check includes preceding leases in the same room and initial unconfirmed-session evidence; later epochs do not launder faults. A queued apply from a confirmed source lease is refused if that exact source lease changed before execution.

The received sequence watermark remains a receive/dedup watermark, never an application certificate. Candidate barrier, movement baseline receipt, remote Freeze ACK and room host reconciliation additionally require actual classified applies quiescent and unfaulted. Existing checkpoint services remain sequential; refusal or throw records a sticky install fault and forbids subsequent successful promotion. There is no cross-service atomic rollback or lossless-recovery claim. A fully installed candidate barrier is remembered while its queued descendants finish, so a retry does not execute installation twice.

## Coverage and unresolved boundaries

Protected movement interpolation/prediction and final movement baseline are reused unchanged; typed Army/MapEvent completion is reused, not duplicated. Registry/AutoSync generators, LiteNetLib, Protobuf/LZ4 and native AI/economy remain unchanged algorithms.

This receipt covers ICampaignAuthorityScopedMessage publication and its synchronous/GameThread chains, plus observed checkpoint installs. It does not automatically classify arbitrary raw/custom packets, silently skipped guards in all custom handlers, external Task.Run work, or every private engine state. Non-marker component messages still rely on the original GraphReadiness publication/dependency gate; they are not mechanically reclassified. Initial join keeps its existing provenance validation; before initial lease confirmation the observation sentinel retains failures without inventing a source lease.

Aging _heroesYoungerThanHeroComesOfAge/_extraLivesContainer and Caravans _caravanLastHomeTownVisitTime remain explicit checkpoint continuity gaps. Twenty ticker counters are not complete private-state transfer. Full natural-day A→B→A, ordinary shared battle and lossless failure recovery remain unaccepted without final integrated native evidence. No game/deploy occurs in this worker.


## Independent review repair — bounded follow-up

The first receipt slice omitted paused snapshot install observation and could discard a fully authenticated freeze ACK while a nested prior apply was pending. These were real review blockers, not accepted behavior. Snapshot clock/movement now run in a source-lease receipt and CheckpointInstall scope; false/throw is sticky, pending descendants prevent lease commit/receipt, and a complete (source,target,ticks,hash) install is not repeated on redelivery. Initial active lease confirmation also checks session-sentinel pending/fault; unconfirmed queued apply cannot silently cross confirmation. A paused movement snapshot with no confirmed source is refused rather than inventing a source stamp.

The room host retains at most one authenticated complete final ACK, bound to exact source lease/current peer/request sequence/candidate and a SHA256 of all received checkpoint DTO data. This JSON fingerprint is local evidence only, never a wire-format change. Existing availability reconciliation resumes it on the game thread after actual critical quiescence, revalidates the full binding/fingerprint, and skips previously completed native installation. No second ACK transmission, fixed sleep, extended timeout or new global coordinator is introduced. Conflict/stale binding or install failure remains faulted/frozen. A true envelope→nested game-thread queue→same-frame ACK integration case proves the retained ACK can complete once after the descendant; a mutated checkpoint fingerprint case refuses promotion.

Critical party behavior mapper/party-reference/snapshot failure now marks the current receipt without replacing the original mapping, prediction, force-position or navigation algorithms. Initial synchronization and non-marker/custom-handler coverage remain explicit boundaries; arbitrary tasks and private behavior-state migration are still not certified.


Snapshot progress is also retained locally: the original server clears a successfully sent lease broadcast and does not normally resend it. Therefore a single deferred snapshot is resumed by the same session receipt's Progressed notification, marshalled once onto GameThread; completion-before-subscription is rechecked, failure stops, and Dispose removes the notification. No extra network resend is needed. The actual queue test proves descendants still see the old source lease and the paused receipt is emitted once after their completion. Redelivery only tests installation/send idempotence.


## Peer provenance and service lifetime review repair

The transport already owns the connection identity: CoopClient.IRoomHostPeerProvider checks exact room-peer reference, connected state and transport disposal. MessagePacketHandler's ingress role checks do not guarantee that peer remains current when queued work runs. Snapshot entry, actual game-thread apply, resumed apply, post-clock install, lease commit and ACK now revalidate that existing provider and service lifetime. Missing provider/source fails closed; tests explicitly bind a current real test peer instead of admitting this/null sources through a production fallback. An optional-network test now correctly requires no lease application when authentication is unavailable.

Service disposal invalidates already queued Freeze ACK and lease reconciliation as well as receive callbacks that entered before Dispose. The test uses a real in-flight availability callback around Dispose and the queued valid ACK; it does not directly repopulate private mappings. Prior plain-Dispose ACK behavior already refused via cleared mappings and is not falsely described as the discovered failure. Snapshot tests cover replacement with unchanged room/lease/controller strings, queued initial disposal and pending descendant replacement/disposal. The original Network/Registry/movement algorithms remain unchanged.

The notification-error fallback reads SessionIdentity before acquiring the completion ledger gate, preserving the established session→completion lock order. A bounded two-thread test holds the real session gate, triggers a throwing notification, and proves another completion-state read remains available. The previous production implementation fails that test; no production timeout is extended.
