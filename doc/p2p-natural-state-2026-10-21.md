# Native state install proof, M10

Base: 3d0060cb7619c5ae954712d9c95fda9698113f96. Existing accepted map run is retained; natural-day native acceptance is NOT_RUN in this worker.

## Core-change reasons recorded before edits

1. Existing finite adapter already captures, validates and installs Aging extra lives/young heroes, Caravans world history/prices and Kingdom proposal history. The scheduler already captures native ticker ordering/cursors/debt and periodic event phases, validates all references before mutation, and retains native callback bodies. These are implemented features, not gaps to rewrite.
2. Concrete remaining boundary: `CampaignSessionCheckpointService.TryApply` currently treats a returned install Action as success and publishes session metadata under the lease. The final handoff barrier trusts that boolean. In contrast, movement already recaptures the installed state. A thin native adapter readback plus the existing session bool suffices; no AI/economy/scheduler algorithm change is justified.
3. Preserve the exact confirmed lease guard, native execution phases, object identities, source arrays and native float bits (including the legal caravan empty-town NaN sentinel), and local caravan history. Failed installation proof returns false before metadata publication. This does not roll back partially applied native data or establish lossless recovery; existing barrier failure freezes activation.
4. Regressions: real finite readback after native install, all finite domain changes detected, legal NaN exact bits, failed recapture, no-op/corrupt install callback, lease replacement before installation, and unchanged native/scheduler baseline. Raw baseline failures are retained, no assertion relaxation.

## Baseline

`dotnet test source/GameInterface.Tests/GameInterface.Tests.csproj -c Debug -p:ModName= -p:NuGetAudit=false --filter FullyQualifiedName~CampaignAuthority.NativeState|FullyQualifiedName~CampaignSchedulerCheckpoint|FullyQualifiedName~CampaignSchedulerTombstone`: 77 passed, 0 failed, exit 0. Raw log and TRX are in the task evidence directory.

## Natural-day oracle retained

Reuse `tools/mcp/scenarios/p2p-world-lifecycle.mjs --natural`, which advances A and B through ordinary native `Play_2x`, each by at least 24 Campaign hours. Its due-slot comparison covers every expected hourly/daily dispatch exactly once, all 20 native tickers, native AI/late-AI/wait-task completion, finances/wages, strict paused shared state and A-to-B-to-A scheduler phase transfer. Call-volume coverage alone does not establish a complete day. Root owns the exclusive native run and deploy; this worker never launches games.

## Actual implementation and checks

The existing `ICampaignNativeBehaviorCheckpointService` now exposes `IsInstalled`. Its implementation recaptures the current native behavior singletons through the existing validated capture pipeline and compares protobuf bytes. This includes every finite domain and exact float representation, without changing the DTO or business algorithms. `CampaignSessionCheckpointService.TryApply` requires that proof inside the existing exact-lease callback, before changing player crime ratings, publishing the merged session, or initializing readers. Failure returns false to the existing final-barrier sticky failure path; no ACK or activation is authorized by that result.

The prepare/install path is synchronous on the game thread; no queue pumping or async handoff was introduced. Registry removal makes readback fail. A failed proof may leave already-installed native state in place, and must remain fenced. No rollback or lossless recovery is claimed.

Production-facing tests use actual native Aging/Caravans/KingdomProposal behavior instances under the Campaign behavior manager. They prove successful full-domain install, detect 11 finite-field changes, reject missing registry/Campaign state, preserve the legal `0xFFC00000` price sentinel, and accept independent local caravan rumor/interaction changes. Session tests prove a no-op, corrupt, or failed-readback action cannot publish metadata or return success, and a replaced lease cannot execute installation/readback.

- Baseline native/scheduler: 77 passed, 0 failed, exit 0 (`baseline.log`, `baseline.trx`).
- Initial fixture compile: exit 1 (`candidate01.log`), assigning a native readonly field directly was rejected; fixed using the established fixture's readonly-field setup. Failure retained.
- First candidate: 21 passed, 0 failed, exit 0 (`candidate02.log`, `candidate02.trx`).
- Related regression including existing session tests: 103 passed, 0 failed, exit 0 (`related03.log`, `related03.trx`).
- Final related regression after adding local-independent-history proof and normalizing C# encoding: 104 passed, 0 failed, exit 0 (`related04.log`, `related04.trx`).

Worker native Mission, natural 24h, A-to-B-to-A, load/cold-start, network fault and performance validation: **NOT_RUN**. Root must verify the integrated final game version under its exclusive lock. A successful finite readback is neither full native-day acceptance nor proof of all possible Campaign private state.
