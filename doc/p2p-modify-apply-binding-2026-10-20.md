# Owned critical-apply observation binding, 2026-10-20

Base `62fc231fbb3b8deec73290284e191d15061d32db`.

## Four-part core-change justification (before implementation)

1. Existing behavior: MessageBroker preserves weak subscriptions, synchronous per-message delivery, per-subscriber exception logging and original GameThread FIFO/AllowedThread behavior. Critical results optionally create a session completion receipt, retained by game-thread descendants; CampaignApplyCompletion tracks pending/fault by full lease. CommonModule intentionally uses process-wide externally owned MessageBroker.Instance, while SessionIdentity and handlers were per lifetime scope.
2. Concrete need: regression02 closing-coop has 1525 PASS / 201 FAIL, including six newly introduced DI failures saying `A broker cannot observe two campaign sessions.` Its original TRX SHA256 is `2475512ecf01724c23daf0c35acc8133b494a442c59265c44345c8520d7dea4c`. Constructor Attach never detached and therefore permanently pinned the previous session on the global broker. Actual EndCoop/rejoin has the same ownership mismatch. Child-scope handlers also obtain another identity while the root auto-activated handlers remain alive. A caller-only disposal adapter cannot revoke receipts already retained in GameThread; this requires a narrow completion-binding generation inside the existing observer/ledger, not a second global authority manager. Concurrent idle DI containers must not be mistaken for simultaneously active game sessions.
3. Protected invariants: preserve original message ordering, weak subscription management, exception logs, FIFO, cancellation and AllowedThread behavior; retain sticky faults and exact lease fencing; do not permit two active session observers, do not route old queued work into a newly loaded Campaign, and do not change network/Registry/AutoSync/native simulation algorithms. Token disposal must be idempotent and reference counted for handlers sharing one session. Any child-scope identity lifetime adjustment must be limited to the existing session registration and explicitly approved by the coordinator.
4. Required regression: actual production module build/dispose/rebuild, concurrent idle production containers, same-container child-scope identity consistency, same-session reference counting, different active session rejection, final detach cancellation of retained old queued descendants, no new-session fault contamination, initial unconfirmed join observation, and normal classified result completion. Run full Common, focused production DI/session tests, then full Coop against the exact historical 195 list; do not relabel the six new failures as historical.

Implementation and receipts pending. Worker never deploys or runs games.

## Bounded subscription concurrency adaptation (approved before change)

The first binding-fix focused Coop run retained in `coop01` has 109 PASS / 7 FAIL. Once idle containers could register, their original concurrent constructors reached MessageBroker.Subscribe/Unsubscribe and corrupted the shared non-concurrent Dictionary. All seven are newly exposed failures, not historical baseline. Existing bookkeepingLock protected only pruning/walk lists; dictionary lookup, list append/removal and Publish Count/index reads remained outside it.

The coordinator approved extending that existing short lock to dictionary lookup, subscription mutation, disposal and per-index/dead-entry access. An external wrapper cannot protect internal Dictionary/List operations made by existing handlers, so this is the smallest internal synchronization hook. Preserve the same live list, index order, reentrant append visibility, pruning, weak delegate behavior and original catches/logs; never hold the lock while invoking handlers. Regression covers bounded concurrent subscribe/unsubscribe/publish, the original MessageBroker reentry/GC/pruning/error tests, actual parallel idle container construction and the full exact-baseline suite.

## Global-role fixture isolation found by the full run

The first full Coop run after the binding/concurrency fix has 1535 PASS / 196 FAIL (1731 total). Comparing exact names against regression02 recovers all six binding DI failures, but exposes one additional `SaveGameHandlerTests.GameLoaded_RestoresSavedAuthorityLease` failure. Its TRX interval is `23:47:47.0829530–23:47:47.0966560 +08:00`. The uncollected `ListenAuthorityStartupTests.NativeWorldRegistersOnceWithoutDedicatedCleanupOrDebugAutoLoad` writes global SessionMode=ListenAuthority during `23:47:47.0778929–23:47:47.0861285 +08:00`; these intervals actually overlap. The save test then observes the ListenAuthority cold-boot branch instead of its existing dedicated restore path. Scanning all Coop test SessionMode/IsServer writers found this startup fixture was the only writer without a collection.

The coordinator approved adding only the existing ModInformationRoleCollection attribute to this role-mutating fixture. No Save production change, assertion change, DI-construction serialization, or retry-to-hide-error is used. The full 196-failure TRX remains in `coop-full` and is an explicit failed attempt.

## Implemented ownership contract

- Attach returns an idempotent owned Binding; all six owning production session handlers release it. All handlers for the same identity share existing session-ledger state and reference count. Last detach advances a captured generation; old pending/fault facts remain. Broker.Dispose releases registrations, including tokens ignored by isolated legacy tests.
- CommonModule registers SessionIdentity once per root container, inherited by its child scopes; a new root gets a new identity. This matches existing StartAsServerCore/StartAsClient container destruction/recreation. Other per-scope world services and original handlers remain unchanged.
- Multiple unconfirmed idle roots can construct. A classified publication never guesses from confirmation counts: multiple distinct registered sessions fault before invoking a subscriber, including confirmed+idle. A second confirmed session cannot register beside another confirmed session. Confirmation races are rejected again at publication. Identity reads occur outside broker/collection locks.
- Normal envelope apply opens its own binding observation after existing validation. Checkpoint observation shares the same generation fence. Queued envelope/freeze/barrier work checks the owned service lifetime. A receipt needs its full lease and original active binding generation; no ExecutionContext permission is added.
- The broker retains live subscription lists and indexed traversal. Locks cover collection bookkeeping only; delegate invocation remains outside locks. Original cancellation/failure logs, AllowedThread, Registry, serialization, transport and native algorithms remain.

No atomic full-world rollback, complete checkpoint, lossless takeover, natural-day or Mission native acceptance is claimed. No worker deploy/game was performed.

## Verification and remaining acceptance

- Full Common: **443 PASS / 0 FAIL** (`common-sealed`), including original broker reentrant/GC/prune/throw tests, owned-binding generation/cancellation, and bounded concurrent collection mutation.
- Production DI + authority apply focused run: **117 PASS / 0 FAIL** (`coop02`). The final full Coop run includes these cases and all five new production binding tests.
- Full Coop: **1536 PASS / 195 FAIL / 1731 total** (`coop-sealed`), compared by exact test names against the original 195 list. No additional failure, no missing historical failure, no removed test; regression02's six new DI failures all pass. The raw dotnet test exit is 1 and remains 1; the separate exact-baseline comparison receipt is exit 0. This is not an all-green suite.
- Full GameInterface: **3373 PASS / 0 FAIL** (`gi-final`); final frozen-source repeat `gi-sealed` retained alongside it.
- Every build/test uses `-p:ModName=`; no game deployment, process or physical test is run by this worker. Coordinator integrated and dual-process native acceptance remains required.

Raw command logs/TRX, exact commands, SHA256, baseline comparison, failedAttempts and handoff are retained under `artifacts/modify-apply-binding-m3`. The original 201-failure coordinator TRX, first failing 4-case production reproduction, seven collection-corruption failures and 196-failure role-race run remain unchanged. The original baseline-name JSON stores XML attribute entities; comparison decodes XML entities once to match XML parser decoding, records each decoded name, and changes no test name or exclusion rule.

## V2 independent-review mustfix: reentrant critical subscriber fencing

Four-part justification recorded before this correction:

1. Existing implementation retains the original live indexed subscription list: a subscriber may append another subscriber, and ordinary delivery may reach it in the same publish. Critical session selection and receipt currentness were checked only at Publish entry.
2. Actual RED evidence against `a01cf147f1fde210b9ada4a45289d2e3cc043850`: two real synchronous reentry cases in `v2/red02` both fail with expected later-call count 0, actual 1. The first subscriber can attach an idle foreign session and append its handler; alternatively it can release the last current binding while an existing later handler still runs. An external adapter cannot fence individual invocations inside this live broker loop.
3. Preserve ordinary-message live-index reentry, weak subscribers, invocation order and lock-free delegate invocation. Only classified critical messages must revalidate their current receipt generation/source lease and unambiguous session registrations immediately before each actual invocation. A rejection records the original current receipt fault, not just whichever lease the identities currently hold, and stops later execution. No snapshot enumeration or delegate lock is added.
4. Required regression: both synchronous reentry cases RED then GREEN, including zero later calls and sticky fault/no ACK after outer completion; full Common regression retains original ordinary reentry/GC/pruning/throw behavior. Original `a01` receipts are immutable; new receipts live under `v2`. Coordinator runs integrated Coop/GI/native acceptance separately.

V2 verification: actual synchronous cases **2 FAIL / 0 PASS** on `a01` (`v2/red02`, each later-call count actual 1), then **2 PASS / 0 FAIL** (`v2/green`). Complete Common regression **445 PASS / 0 FAIL** (`v2/common-full`). Previous a01 Coop/GI receipts remain original-version evidence; this worker does not relabel those as V2 integrated results. Coordinator performs full integrated reruns and native acceptance.
