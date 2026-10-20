# Dynamic campaign execution contracts — MODIFY implementation

2026-10-20. Starts at ef6681ed. Dynamic campaign authority is a required capability. Fixed-host
authority is not a replacement. Every player retains one native Campaign in one Bannerlord process.

## Public contract and ownership

1. **Identity**: existing ISessionIdentity owns room host and confirmed campaign lease. Existing
   PlayerManager/ILocalPlayerContext own local native Hero/Party identity independently of NetPeer.
   Mission assignment registries retain their own HostEpoch; Campaign Epoch cannot substitute it.
2. **Bootstrap**: native engine RegisterEvents runs once during normal campaign load on all potential
   writers. Do not register again on lease change. Runtime world callbacks require writer eligibility
   and must refuse Replay/Prediction/CheckpointInstall. Private initialization that needs native load
   is explicitly separated from simulation. Do not enable arbitrary callbacks solely via AllowedThread.
3. **WorldWrite / Replay**: ICampaignWorldExecution is implemented by the existing
   CampaignAuthorityContext, not a second manager. TryAcquireWritePermit returns an issuer/lease-bound
   CampaignWritePermit. TryExecuteWorldWrite revalidates under the existing session gate and keeps
   native patches active. It only wraps bounded synchronous actions, never waits/worker drains.
   TryApplyReplay validates the expected lease and enters Replay plus the original AllowedThread.
   Replay is not writer permission. CampaignExecutionScope is synchronous thread-bound provenance;
   inner scopes cannot elevate Replay/Prediction/CheckpointInstall/LocalIntent to WorldWrite.
   Game-thread queued work revalidates its own expected lease when it actually executes.
4. **Lifecycle**: Registered means addressable by a network handle; Published means the exact native
   object is in the world. Retiring retains identities until native outer callbacks and prior result
   publication are complete. Retired forbids later references. Reuse GraphReadiness/native publication
   and typed Army/MapEvent completion; do not implement a parallel object registry or dedup unknown lists.
5. **Handoff / ACK**: the existing Freeze/Barrier coordinator closes old work, drains known operations,
   flushes results and seals final state before promoting another writer. Received sequence is not
   applied success. Critical apply failure/pending work prevents successful ACK and activation. Preserve
   partial-install faults and safe freeze. Do not claim global rollback, durable exactly-once, complete
   private state, or lossless recovery without evidence. Result dedup and command dedup remain distinct.
6. **Mission endpoint**: use a typed local actor or actual remote NetPeer at the membership boundary.
   Local entry directly uses the same validated instance service and local reply path. No fake peer,
   loopback CoopClient or entire ClientModule/ServerModule composition. Preserve Mission algorithms.

## Message / operation flow

- Local writer: native operation once with patches active -> publish authoritative results once.
- Observer player: input/prediction -> actor/CommandId request -> room relay -> confirmed writer checks
  ownership/lease -> native operation once -> authoritative result -> observers apply, no request loop.
- Room control remains room-host-owned even when the host is an observer. Mission results remain
  instance-host-owned; campaign resource settlement waits for an eligible campaign writer.
- Freeze/paused campaign does not stop Mission rendering/network. A Mission participant is ineligible
  for campaign simulation; all participants away means no writer. Readiness transitions, not repeated
  preference polling, drive migration. Failure keeps no active writer rather than promoting stale state.

## Existing functionality / change boundary / regression obligations

The original authority context already resolves confirmed leases and exposes stamp-checked replay.
It does not distinguish a write capability from replay; extending that context solves this without
replacing ISessionIdentity, transport, AutoSync, Registry or AI/economy implementations. Existing
TryExecuteWithCurrentLease behavior remains compatible. New capability and phase boundaries must
preserve local actor identity, exact leases, original-registration visibility, and receive no-echo.
Required contract tests: room host != writer, stale/frozen/default/cross-context permits, Replay cannot
write even on the writer, nested scope cannot elevate, exception scope restoration, no async propagation.

Bootstrap worker owns native subscription/world callback adapters. State worker owns readiness,
component publication and actual-apply completion boundaries. Mission worker owns local endpoint and
instance coordination. Root owns this initial public contract and shared composition changes; workers
request shared-file changes instead of editing another worktree. No mechanical role replacement.

## Verification order

Commit small tested slices. Independently review before integration; integrated regression before
exclusive native acceptance. Start with two-player map/normal A-B-A, then asymmetric Mission load,
all-away pause/resume, ordinary shared battle/result, natural day/save, network faults and finally
200/500/1000-agent performance under matched settings. An unrun/failed stage remains unaccepted.
Original FAILs and the 195 Coop/10 E2E historical failure lists remain evidence, not excuses.
