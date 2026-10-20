# MODIFY M3: two remaining behavior boundaries

Base e3a1741a6c6ebb8dddaa6ca2b467aa409f650e26. No deployment or native acceptance is claimed.

## Four required grounds before core edits

1. Existing Coop Kingdom proposal/hourly replacement already provides player-free proposal, election and result routing; HeroSpawn keeps original native startup and world behavior. Existing decision converters serialize stable registry references.
2. Static transport-role gates prevent promoted B from running these callbacks and allow observing A/replay to run business. An existing CampaignSimulationAuthorityPolicy adapter suffices: change only the runtime gates and permit original subscription once. No AI/economy/proposal algorithms or general converters need rewriting.
3. Preserve native subscriptions, original Coop business bodies, local player exclusion, startup behavior, registry identity, one-authority writes and non-authoritative replay. Kingdom proposal private five-day history must also transfer: unresolved decisions alone omit already resolved history used for duplicate prevention.
4. Regress actual native subscriptions and callback invocation across lease changes/replay/AllowedThread; checkpoint history reference validation, no constructor/model invocation, prepare-before-mutation, stable ordering, protobuf roundtrip, production DI, and existing Kingdom/NativeState suites.

## Finite state manifest

Required: KingdomDecisionProposalBehavior._kingdomDecisionsList (SyncData, OnKingdomDecisionAdded, DailyTick pruning, Coop DailyTickClan duplicate guard). Restore its existing decision value DTOs and local registry references, not AddDecision, elections or registry registration. Preserve order and repeated values. Missing/unsupported references refuse the checkpoint.
Reconstructible: lazy trade/alliance behavior service references remain local. AcceptCallToWarAgreementDecision lazy alliance cache is not serialized.
Local/non-required: SupportStatusOfFinalDecision is not read by proposal-history duplicate/prune algorithms. Original converter omits it; this adapter does not expand history into active election-state replacement.
HeroSpawn SyncData is empty; no new private-state domain. Three startup prefixes remain unchanged.

Accept native ctor initializes CallingKingdom, KingdomToCallToWarAgainst and CallToWarCost (last invokes AllianceModel); base ctor initializes ProposerClan, _kingdom and TriggerTime, plus notify/enforced/examined defaults. Existing DTO contains the complete explicit base/value set. The checkpoint-only adapter uses original SkipConstructor convention and sets three readonly value fields, avoiding native constructor/model computation. No original converter body changes.

This is a bounded extension, not evidence that every native behavior is checkpoint-complete. Existing M2 Caravan/Aging manifest and limitations remain applicable. Diagnostic refusal retains false behavior and records domain/field/reference rather than relaxing validation.

## Verification boundaries

The managed native-event test registers actual native listeners once, invokes the original subscribed delegate and observes whether the production prefix authorizes entry using a final test-only prefix. It deliberately stops before invoking AI against the incomplete managed world. It does not claim native spawning, economic continuity or natural-day acceptance. Original v2 failed fixture and compile logs remain archived.

Production TryCapture/TryPrepare require all three finite domains and version 3. Internal single/two-domain overloads remain solely useful for managed domain tests; they do not assert a complete checkpoint. All three prepare actions complete validation before any install action is returned. No old-version or absent-history fallback becomes authority.

Diagnostics use existing logger Warning plus LastRejectedReason on this transient adapter. Each failing Aging/Caravan registry/value branch reports its domain, native ID/handle and relevant value; validation/freeze semantics remain unchanged.

## Managed results (M3 final source)

- Focused NativeState + additional boundaries + existing Kingdom tests: 158 passed, 0 failed, 0 skipped.
- Entire GameInterface.Tests: 3354 passed, 0 failed, 0 skipped (includes 16 new M3 cases). Command: `dotnet test source/GameInterface.Tests/GameInterface.Tests.csproj -c Debug -p:ModName= --no-restore --logger "console;verbosity=minimal" --logger "trx;LogFileName=full-final.trx" --results-directory artifacts/modify-bootstrap-boundaries-m3/tests`.
- Existing compiler/framework-support/analyzer warnings remain visible. Coop 195 and E2E 10 historical failures were not executed or relabeled by this worker.
- No game launch, deployment, Mission acceptance, A→B→A native continuity, natural-day or CPU performance measurement in this worker.
- Raw initial compile failure, two fixture failures, invariant-format diagnostic failure and all TRX outputs remain in ignored artifacts. Assertions were not relaxed.
- Runtime native DLL SHA256: `1f8e33e2ed73e6ec653d7629180afb70649ddc6e5bd1657a802a264efda1c3ae`; copied decompiled native behavior evidence is retained beside receipts.
