# Observed Mission dispatch lifetime, 2026-10-21

Base `d5c7d39c50fc4d14a657295635777531f62cd2da`. First slice is generic observation composition, not Mission payload admission or a world-write capability.

## Core change rationale, recorded before edits

1. `MessageApplyScope` already tracks synchronous applies and explicitly retained `GameThread` descendants. `CampaignApplyCompletion` owns per-session, exact-source-lease pending/failure evidence. `MessageBroker` starts that observation for classified results. Existing queue cancellation, stale checks, discard and `RunSafe` failures report through the captured receipt.
2. An external receiver cannot fix the current nested-scope replacement: `Begin` replaces the parent, and queued children retain only the innermost receipt. Conversely, `MessageBroker` treats any current receipt as Campaign coverage, so a different observation can suppress the real classified-world receipt. A thin composition change at these existing receipt/broker seams is required; no gameplay or queue algorithm needs replacing.
3. Preserve actual-apply completion, exact lease/session invalidation, FIFO scheduling, cancellation and original failure accounting. Nested observations retain both lifetimes and validate both sources. Domain identity is reference-based and only identifies existing observation coverage; it grants no peer, Replay, authority, execution permit or ACK. Existing explicit checkpoint-source observation must retain the same Campaign domain as its owner, so nested classified results are not counted twice.
4. Regress both nesting directions, at least two descendant queue frames, parent/child stale cancellation, discarded/cancelled/throwing descendants and unchanged Campaign failure evidence. Verify a distinct domain starts the actual Campaign observation and an already covered classified result does not start a duplicate one. Regress the original complete Common suite. No native game or reliable Mission payload enablement is claimed.

## Remaining reliable consumers

`PuppetSpawner.pendingPuppets`, `pendingFormations`, `pendingAuthorities`, and `RemoteAgentActionProcessor` pending actions outlive an initial queued callback. Their actual drains require source-bound continuation evidence before reliable payload families can be enabled. Initial membership verification plus a GameThread guard is insufficient. Production Mission entry stays closed until the independent result and protected-preparation admission boundaries are complete.

## Implemented observation contract

`MessageApplyScope.Observe` accepts source validation, failure and completion callbacks, and exposes no caller-selected Campaign domain. Existing internal observer entry composes its receipt with the current parent. The child retains the parent until all explicitly captured GameThread descendants finish, validates both sources, and forwards failures to both. A throwing failure callback still releases the retained receipts in `finally`.

Receipt ancestry records opaque reference-based observer domains. `MessageBroker` reuses only an observation that covers its actual registered Campaign observer. A distinct scope therefore cannot suppress classified-world tracking. The existing private `CampaignApplyCompletion.Begin` tags its receipt with the owning completion instance; its explicit `SourceObserver` consequently shares that same domain without changing lease validation, completion rules or protocol.

Behavioral RED against unchanged production code failed all 10 initial composition tests. The first candidate ran 9 PASS / 1 FAIL: the new test's static local subscriber had no instance target for the original weak broker subscription. The fixture now captures its test instance; no assertion or production broker subscription behavior was weakened. The subsequent 12-test focused run passed. Final coverage adds a three-level distinct/Campaign-source/distinct failure schedule, and the full Common suite passes 462/462, zero skipped. Related Coop session, freeze, apply-binding and local-relay regression passes 165/165; GameInterface apply completion, world execution and movement-freeze preparation passes 42/42. Original RED and first-GREEN failures remain in the task's ignored evidence directory.

This slice does not enable reliable Mission payloads, change pending Owner/Puppet/action algorithms, deploy a module, or run a game. Mission admission and full battle/day/pause/performance acceptance remain `NOT_RUN`.
