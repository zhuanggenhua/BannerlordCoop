# Native campaign ticker evidence boundary

This DEBUG diagnostic adapter changes observation boundaries only. It does not change native tick scheduling, world authority, Registry, object lifetime, AI, or callbacks. Natural02 remains FAIL and requires a new full native run after integration.

## Evidence and change boundary

1. The old implementation observed all20 native periodic ticker fields through five nongeneric groups. It predicted ordered callback identities and strictly compared membership/order, Index and exact TickDebt. The native algorithm and those strict rejection rules are retained.
2. Natural02 (`656e04aa882f409594a193b75e835bf3`, E7, A20604/B21204) advanced24.110000527929515 natural hours, then failed9 daily-party audits. First stored full four-call sequences match (`Created_2639/2641/2643/2645`); native logs show15 party creations inside later DailyTickClan callbacks. The old group Prefix captured the first two tickers' shared MobileParty.All and checked it only after the later clan callbacks. This is concrete evidence to move the diagnostic boundary; it does not prove every one of9 failures was spurious. Per-batch predicate/index/debt/membership were not retained in that run.
3. No core change is needed: a read-only Prefix/Finalizer on each installed closed `CampaignPeriodicEventManager.PeriodicTicker<T>.PeriodicTickSome(double)` captures that ticker at its own entry and exit. It must not invoke, replace or suppress the original, mutate membership/phase, replace exceptions, or attribute another ticker's callbacks to this one. A separate group observer retains intentional-skip/writer-skip evidence without taking group-wide phase snapshots.
4. Required regression protects native exact callback counts/order, cursor/debt/membership rejection, sibling creation, nested boundaries, unchanged exception identity, bounds, and closed-generic runtime binding. Existing cursor/debt/membership rejection tests remain. If actual game callbacks legitimately mutate a ticker's own list within its call, this adapter still reports FAIL with exact predicates; investigate that native chain before changing any assertion.

## Implementation

`CampaignCycleDiagnostics.cs` obtains real field types from the20 existing native fields and resolves closed `PeriodicTickSome(double)` methods, distinct by MethodInfo. Prefix resolves the **actual __instance reference** against this Campaign's fields, not the reported generic argument. This matters because reference-type generic code may be shared. There is no guessed function-pointer deduplication or per-field repeated detour registration.

Each call has a thread-local parent boundary. Callbacks append to only the current boundary; finalization restores its parent even on a native exception. Expected sequences use the actual method's double elapsed argument, not an outer group's independently recomputed delta. List identity/order, Index and exact debt remain strict. Full failure JSON records all predicate results, before/after IDs, full expected/completed arrays, original and expected/final indices, debt/elapsed IEEE bits, UTC, engine frame, thread IDs/depth and lease identity. The first failure remains immutable in the existing aggregate.

Capture is bounded at32768 identities and32768 callbacks per native batch. Exceeding a bound is an explicit diagnostic mismatch with `complete=false`; it never becomes PASS and is never silently represented as complete evidence. Prediction also rejects nonfinite debt and cannot loop indefinitely on excessive catchup. Original native execution is not changed by those diagnostic bounds.

## Verification

The new closed-boundary test fails on the original implementation (RED, exit1); `artifacts/cycle-boundary-r4/red.log` and `boundary-red.trx` retain that result. Target tests compile with `-p:ModName=` to disable deployment.

The installed-DLL test actually initializes and invokes all20 native ticker fields across the five closed reference types with Harmony installed. Each native callback executes once and each corresponding audit has Batches1/Expected1/Completed1/Mismatches0. An unbound same-closed-type ticker still executes without being attributed to the Campaign. A native callback exception escapes as the identical object; its frame and failure details remain recorded. The fixture replaces only the unavailable engine-frame read with17. This verifies binding in the net6 test runtime, not the game's .NET Framework process.

Other cases cover later clan/settlement sibling creation after an already finalized party batch, actual inside-boundary membership mutation, cursor-only/debt-only failures, omitted/duplicate/reordered callbacks, empty callbacks, nesting/parent restoration, all original skipped-group semantics, complete sequences longer than12 and explicitly incomplete evidence at the cap. Related scheduler/checkpoint/async-drain regressions are included in the final test receipt. Exact commands, exits, TRX and SHA256 are in the artifact handoff.

No game was launched or deployed by this worker. The coordinator must independently review, integrate, run the wider historical-failure comparison, verify hook counts in the actual game runtime, and rerun true natural24h/A→B→A. Natural02's raw FAIL and AI completion gaps remain valid: A aiTicks0/no MobilePartyAi.Tick completion evidence and B observer AI eligibility require their own investigation. This diagnostic repair does not certify NPC AI, wage deduction, XP awards, migrations, or overall P2P reliability.
