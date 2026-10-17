# ADR-0001: Separate room hosting from campaign simulation authority

## Status

Accepted

## Date

2026-10-18

## Context

P1-B is accepted for the fixed-A, map-only phase. The broader design requires each player to keep a local campaign copy, exactly one player to advance campaign simulation, the room host to be preferred while able to simulate, another map-ready player to take over otherwise, and campaign time to pause when no player can simulate. Shop stock, listed goods, and presentation may differ per player; player gold, recruitment pools and results, player ownership, campaign time, NPC outcomes, and troop counts are shared state.

### Consistency boundary

The design keeps a local copy on every player and does not require every displayed detail to match. Each player's shop stock list and presentation may differ, and those differences are not repaired by campaign reconciliation. Player gold remains shared: a purchase can use that replica's local stock list, while its gold change is committed once by the current authority. The shared invariants are player-to-hero and party ownership, party position and troop counts, recruitment pools and recruitment outcomes, player gold, campaign time, and NPC party behavior and results. Recruitment availability must match on every replica, and a successful recruitment consumes the shared pool once. An operation that changes one of those shared facts must be committed once by the current campaign authority and then applied to each replica.

“Distributed maintenance” means every player retains a local campaign replica and an eligible player can maintain the shared simulation when holding the authority lease. It does not mean concurrent writes to the same shared fact. The room host has priority while map-ready; another map-ready player may hold the lease when the host cannot simulate; if nobody is map-ready, the shared world pauses. Shop stock and presentation remain replica-local, so their differences do not trigger reconciliation or block an authority handoff.

The current implementation does not yet represent room hosting and campaign authority as independent roles:

- `Common.ModInformation.IsServer` is true for `DedicatedAuthority` and `ListenAuthority`; `IsClient` is its inverse. A source scan finds 1,127 `ModInformation.IsServer` references across 452 C# files, including tests. These call sites currently cover simulation authority, network command direction, ownership checks, and server-only behavior.
- `ServerModule` composes `CoopServer` and `ServerSyncPolicy`; `ClientModule` composes `CoopClient` and `ClientSyncPolicy`. B joins A as a client, so only A owns the current room transport and server-side mutation handlers.
- `GameStateManagerPatches`, `MapStatePatch`, `MobilePartyExtensions.IsControlledByThisInstance`, and the `MobilePartyAi` tick patches gate campaign and NPC advancement from the current process role. `CampaignTimeSyncHandler` publishes A's local `MapTimeTracker` ticks every 250 ms.
- `SessionIdentity` creates a new `RoomId` and `AuthorityId` for each DI lifetime. The values are not negotiated with joining clients or written to the co-op save. There is no authority epoch or stale-message fence.
- `LocalPlayerContext.RegisterNativePlayer` binds ListenAuthority to `Hero.MainHero` and `MobileParty.MainParty`. The existing startup path rebuilds the container and either starts a new campaign or loads a save; it has no promotion path that preserves an already-loaded replica and selects its existing remote player as the new native actor.
- Network messages do not carry a campaign-authority lease. `MessagePacketHandler` only rejects server-to-client-only messages received by a process whose global `IsServer` is true.

P1-B tests and live runs prove synchronization with fixed A authority. They do not prove that B can safely become campaign authority while A remains the room host, nor that a fresh server container can preserve B's current player actor.

## Decision

Keep the room host/relay and campaign simulation authority as separate roles. The room host remains responsible for membership, message routing, and lease commits. A single map-ready player holds the campaign-authority lease and is the only process allowed to advance world simulation or publish authoritative campaign mutations. Prefer the room host when it is map-ready; otherwise grant the lease to another connected map-ready player. If no connected player is map-ready, commit a paused lease and stop campaign time.

Represent the campaign lease independently from local-player identity with:

- `RoomId`: stable for the co-op campaign and persisted with the co-op save.
- `AuthorityId`: a fresh opaque lease identifier for each handoff.
- `AuthorityControllerId`: the player currently holding the lease.
- `Epoch`: a monotonically increasing number scoped to `RoomId`.
- `Availability`: whether a connected player can safely advance the campaign map, reported separately from whether the player is in the room.

Every authority-originated world mutation and campaign-time sample must be accepted only for the current epoch. The relay rejects commands from stale epochs before applying or forwarding them. A room-host process is not automatically the campaign writer merely because it relays traffic.

A paused world is represented by a confirmed lease with `IsPaused = true`, an empty authority controller, and a new authority id at a higher epoch. Replicas reject campaign-clock samples while that lease is active. A ListenHost with a confirmed lease skips `Campaign.RealTick` whenever the lease is paused or belongs to another controller. The room host may resume its own lease after it reports map readiness; it must not directly activate a remote candidate before a freeze-and-barrier handoff.

Use a freeze-and-barrier handoff:

1. The relay selects an eligible target and sends a prepare request with the next epoch. The target verifies that its campaign, object registry, local player mapping, and module build are ready.
2. The current authority stops advancing the campaign and drains pending authoritative requests and ordered world deltas through a final sequence number.
3. The target applies the final checkpoint and deltas, verifies critical state, and acknowledges that sequence number. The checkpoint must include co-op player/session data and state omitted by ordinary observer deltas, including NPC roster XP needed for later promotion or battle.
4. The relay commits the new lease epoch. Only then may the target resume campaign advancement. The previous authority remains frozen and rejects all later writes under its old epoch.
5. If preparation or checkpoint validation fails before commit, the relay aborts the handoff and allows the old authority to resume. A failed or timed-out transfer leaves the campaign paused until one lease is known to be valid.

Authority eligibility is based on a campaign-simulation capability signal, not merely process liveness or room connectivity. A player in a Mission may remain connected as the room relay while being ineligible to simulate the campaign map.

## Alternatives considered

### Keep authority permanently on the room host

This is P1-B and is already verified. It cannot satisfy the stated rule when the host cannot advance the map but another player can.

### Rehost the network server on every authority change

This couples room membership and campaign authority again, interrupts the room transport, and does not let a host remain connected as a relay while in a Mission. It also does not solve preserving the promoted player's current actor in `Hero.MainHero` / `MobileParty.MainParty`.

### Let every map player simulate concurrently and reconcile afterward

This permits duplicate NPC AI, random outcomes, recruitment, and economy writes. It violates the single-authority invariant and cannot guarantee that one recruitment slot succeeds once.

## Consequences

- The next phase must decouple network-host responsibilities from world-authority checks. A global `IsServer` replacement with a second boolean is insufficient; each affected call site needs an explicit role such as room-host, campaign-authority, or local-player ownership.
- The relay needs an authority-aware message envelope, a stable campaign identity, a lease epoch, and a sequence barrier. Existing ordered queues and join snapshot/baseline mechanisms are reuse points, but do not provide the epoch fence or authority handoff by themselves.
- The promoted player must continue controlling its existing hero and party. The current native-player registration assumes `Hero.MainHero` / `MobileParty.MainParty`, so promotion requires an explicit actor-selection path and engine verification before implementation.
- NPC roster XP may differ in ordinary observer replicas during P1-B. It must be included in the final handoff checkpoint before any promoted player can become authoritative.
- With two peers, a room relay that remains online can arbitrate a safe handoff. If the relay process is lost or partitioned, the remaining peer cannot distinguish host failure from a network split while also guaranteeing that the old host will never keep simulating. Until an external arbiter or stronger membership protocol exists, the safe behavior is to stop/pause rather than self-elect during an unresolved partition.
- This ADR does not change the accepted P1-B behavior, add Mission synchronization, or claim any engine constraint has been confirmed. The `Hero.MainHero`/`MobileParty.MainParty` promotion path and campaign snapshot reload behavior require decompilation and live validation.

## Implementation sequence

1. Persist and distribute `RoomId`, current authority identity, and `Epoch`; add serialization and stale-epoch tests.
2. Implemented, awaiting live validation: the ListenHost and synchronized clients report map readiness immediately on state changes and renew it with a one-second heartbeat; heartbeats are queued on each process's game thread and coalesced so a stalled loop cannot accumulate an unbounded queue. The room host validates remote reports against the synchronized player/peer mapping, expires reports after three seconds, and selects itself first or a deterministic ready fallback. This tracks candidates and pauses/resumes the lease, but it does not yet complete authority transfer.
3. In progress: introduce an explicit local campaign-authority context. The campaign-time heartbeat now carries RoomId, AuthorityId, AuthorityControllerId, and Epoch, and replicas reject samples that do not match their confirmed lease. A paused lease is persisted and distributed, blocks campaign-time extrapolation and ListenHost `Campaign.RealTick`, and can be resumed locally when the room host returns to the map. TownMarketData keeps its town association but excludes `_itemDict` from AutoSync so shop stock remains replica-local. These changes do not yet enable remote campaign ticks; world behavior replication and command routing still assume the current process roles.
4. In progress: when the room host loses map readiness while a synchronized remote candidate remains ready, commit a paused lease and send that candidate a uniquely sequenced barrier on the reliable-ordered message stream. The client queues its acknowledgement on the game thread, after earlier deferred message applies; the host validates the peer/controller mapping, current paused lease, candidate freshness, and exact barrier sequence. This proves only the ordered message stream and its queued applies; direct packet channels still need their own watermarks or must be drained before promotion. The acknowledgement deliberately leaves the campaign paused: the client still lacks the server-authoritative world-mutation and relay path required to simulate safely. No remote lease activation or A-to-B-to-A handoff is implemented yet.
5. In progress: AutoSync network deltas now carry a `RoomId` / `AuthorityId` / `AuthorityControllerId` / `Epoch` envelope and a per-lease sequence. The relay accepts deltas only from the mapped, fully synchronized authority peer, applies each accepted delta locally, and forwards the same envelope; receivers validate the confirmed lease and drop replayed or stale deltas. Sequence assignment and reliable queue insertion are serialized together so concurrent publishers cannot invert wire order. This covers the marked AutoSync message stream only: custom world commands, direct packet channels, snapshot checkpointing, and remote authority activation still need explicit fencing and verification.
6. Complete authority-side command routing and epoch fencing for all shared world mutations, add a final checkpoint that covers co-op player/session data and observer-omitted NPC roster XP, and only then activate a prepared remote candidate. Verify A-to-B-to-A, stale messages, target disconnect, and aborted transfer before allowing mission availability to participate.
7. Add host-in-Mission / B-on-Map takeover, all-players-unavailable pause, and resume after a player returns to Map. Compare world time, player/party identity, NPC positions and rosters, recruitment pools, and economy across replicas, while confirming that shop stock and presentation can remain different.
