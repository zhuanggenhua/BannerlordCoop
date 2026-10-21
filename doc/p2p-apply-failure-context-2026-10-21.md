# Finite apply failure events, 2026-10-21

Base `94ae3fb6840ade1d5a5b7106c340c61295e1626d`. The pending consumer remains a separate sealed candidate; no Mission admission or native algorithm changes belong here.

## Core boundary before implementation

1. Existing receipts retain actual source/ambient DAG ancestry and per-edge lifetime, traverse unique ancestors per query, preserve source validity/domain coverage and compose captured observations with game-thread descendants. A retained owner stores a failure reason which each activated scope later forwards to its ambient parent.
2. Independent source review `m17-shared-failure-contract-review.md` identifies a real public schedule: owner Complete and activation Dispose each start a fresh Fail traversal, notifying a shared ancestor twice. A later observer or completion exception can replace the first during finally cleanup. A consumer cannot know all shared ancestors or restore an exception hidden centrally; one finite failure context in this existing API is required, rather than consumer deduplication.
3. Preserve every real retain/release edge, once-only completion, exact original exception identity and stack, validity short-circuit, configured application diagnostics and rejection of cancelled queued descendants. Notification deduplication belongs to one failure identity, never a reason string or receipt-lifetime global flag. User callbacks run outside owner/context locks. No queue scheduling, authority, replay, domain or Mission permission changes are allowed.
4. Deterministic RED/GREEN covers captured/ambient aliases and shared branches, multiple nested/concurrent activations, callback reentry, distinct ambient notification, same-text independent failures, callback E1 then disposal/completion E2 with original E1 preserved and real sink evidence, completion-only failures, queued cancellation and all existing composition/retention/graph tests. Explicit rebuild plus no-build test and source/DLL SHA/MVID evidence are required. All original failures remain retained; native acceptance is NOT_RUN.

## Proposed public behavior

No new public manager or permission API. A failed retained owner owns one private finite failure context containing its reason, unique receipt notification attempts and first ExceptionDispatchInfo. Completion and every activated ambient propagation reuse that identity. Mark an attempt before entering its callback, and retain only short state locks. Independent Fail calls/owners create distinct contexts even with the same text.

Cleanup always releases every actual hold. If a phase must report an exception, it reports the same first exception for that logical event; later callback/release failures are recorded through the existing configured LogManager. A second Dispose of the same scope and a repeated Complete of the closed owner remain no-ops. Different live scopes may each report the same primary exception when they finish the failed event; this does not re-notify observers or replace the primary.

Successful closure continues to permit already active/queued descendants. Failed owners keep descendants invalid. If an already queued descendant later propagates the owner's cancellation, its receipt ancestry must preserve the finite failure identity so it can notify distinct ambient observations without notifying the same source twice.

The actual-action exception boundary needs explicit attention: GameThread currently forwards completedFailure.ToString() into the internal continuation. Recovering that original exception object from text is impossible. Any exact exception argument extension must be separately authorized; the queue algorithm itself is outside this task.

## Authorized GameThread exception-transport seam

1. QueuedAction.Complete already stores the actual completedFailure and completion state, settles its captured observation, and signals its blocking waiter in finally.
2. It currently passes only completedFailure.ToString() into Continuation.Complete, losing the actual primary exception identity before failure callbacks/cleanup. No downstream adapter can reconstruct that identity from text.
3. Root approved one exception-argument transport extension at that existing call. Queue/FIFO/cancellation/timeout/thread eligibility and waiter signalling stay unchanged. The context uses the real primary ExceptionDispatchInfo; callback/release errors remain logged secondary failures.
4. A real queued action test must prove the same E0 instance and original action stack reach both Update and the blocking waiter while E1/E2 are visible in the configured OutputSink and all holds complete once. Cancellation without an actual exception still uses its own failure event or the inherited finite owner's event.

## Implemented contract and evidence

The existing receipt now shares a finite MessageApplyFailure across owner failure, activated ambient propagation and not-yet-started queued cancellation. Its per-event visited set marks callbacks before invocation. The existing iterative ancestry traversal and per-edge reference accounting remain in place. Callback execution stays outside the owner and failure-context locks. ExceptionDispatchInfo retains the first failure; later callback and completion errors use the configured application LogManager sink.

The authorized GameThread call now forwards the actual completedFailure object. A new exception from an already executing action is a distinct event, even if another active scope rejected its retained owner after source validation. The deterministic test coordinates those threads with event gates and proves Update and the blocking waiter still receive the same action exception and original stack. This extra boundary passed with the current implementation and required no further production change.

Evidence is retained under the ignored task artifact directory, with raw process exits as well as TRX. The original six cases failed 0/6; the expanded original-production run failed 0/9. The first implementation passed 55 and failed one unchanged existing test: a validator can successfully close its owner and then reject reactivation without faulting the original successful campaign receipt. That real regression was fixed at the ownership recheck; no assertion changed. A later test-edit declaration mistake produced a compile failure, which is also retained. The next complete targeted run passed 57/57, the full Common run passed 506/506, and the additional already-running-action case brought the new suite to 11/11.

Final acceptance requires an explicit Rebuild, source and actual output DLL SHA/MVID capture, then no-build/no-restore full Common and the scoped Coop/GameInterface regressions. The handoff records those final commands, process exits, TRX, binary manifests and all earlier failed attempts. Full unrelated Coop/E2E suites and actual native Mission scenarios are NOT_RUN by this worker; this API result is not native-game acceptance or a claim that overall P2P is complete.
