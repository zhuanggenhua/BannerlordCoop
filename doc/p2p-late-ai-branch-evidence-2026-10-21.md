# Late AI empty-branch evidence (M20)

This change adds DEBUG evidence, not campaign execution permission. The previous Natural02 remains FAIL. Its zero entry/exit dt is source-supported evidence that an empty call is plausible, not proof that its native branch was unexecuted.

## Source and boundary

The hash-matched native `Campaign.LateAITickAux` invokes `PartiesThink(_dt)` only when `_dt > 0f || CurrentTickCount < 3`. `PartiesThink` dispatches the original `MobilePartyAi.Tick(dt)` loop. The archived Campaign source SHA256 is `ed576a52ec08cc63ddee34a1b6920a39d1a9e44bc6328289d6081f1f293e8f43`; Campaign DLL SHA256 is `1f8e33e2ed73e6ec653d7629180afb70649ddc6e5bd1657a802a264efda1c3ae`, MVID `886629fe-6e60-40d7-9a57-8d46017179d9`.

The existing `CampaignSimulationEvidencePatches` now observes `PartiesThink` attempts on the same actual Campaign and managed thread inside a nested, thread-local LateAI scope. It counts the attempted call even if another prefix prevents the PartiesThink body. Prefix installation is checked at both boundaries. Scope restoration runs in `finally`, and the original exception instance is returned unchanged. The original total counts and role groups are still recorded under the same collector lock.

Entry and exit contain `LateAiCoverageVersion`, `LateAiPartiesThinkAttempts`, and `LateAiScopeMatched`. Each boundary's proof is part of the C# and JavaScript group identity, separate from the ordinary role context: a normal writer may have entry attempts 0 and exit attempts 1. First and last witnesses may not combine different proof shapes. Existing bounded groups and overflow rejection remain.

Only completed original LateAI calls with coverage version 1, scope match, zero actual attempts, known confirmed identity, same actual Campaign/thread/node/lease/phase, no freeze/replay/AllowedThread, GameStarted, tick count at least 3, exact positive-zero dt bits, unchanged tick count and world clock may be classified `observerLateAiNoOp`. It does not qualify Tick or RealTick, or grant world authority. Missing evidence, bootstrap, unknown identity, failed calls, attempted PartiesThink and context changes remain unexpected. Existing writer classifications remain intact.

The paused handoff tail additionally requires the exact source or target unpaused lease, or a confirmed same-room paused lease strictly between their Epochs. Business rows, economic effects, ticker audits, scheduler installation, authority AI and complete history checks retain their existing strict checks. The strict natural-day assertion is unchanged. No prior raw log is relabeled or accepted retroactively.

## Verification and limits

Managed fixtures execute the actual native LateAITickAux method from the DLL above. They prove empty branch coverage, missing-coverage rejection, positive-dt and bootstrap attempts even with a stubbed PartiesThink body, transient positive dt with both boundary dt values zero, normal writer attempts 0→1, nested scopes, original exception identity and subsequent scope recovery, wrong Campaign attempts, and unsafe role/phase/clock/counter changes. These fixtures prove the branch instrumentation, not a full native campaign day or AI algorithm.

The initial sealed selection had 126 managed PASS / 0 FAIL and all 212 scenario-tool Node tests passed. After withdrawing the unneeded production experiment, the same selection plus the genuine higher-priority regression has 127 PASS / 0 FAIL; 49 world lifecycle Node tests pass again. The production and Node files match the initial sealed version exactly. Full Coop/E2E suite was not run by this worker; historical failures are not claimed resolved. Artifacts retain the original RED and intermediate compile/fixture failures with actual exit codes. Four change reasons were recorded before editing in `artifacts/modify-late-ai-branch-proof-m20/contract-before-edit.md`.

Native dual-process game: NOT_RUN by worker. Actual caller stack: NOT_OBSERVED; whether an empty call comes from Wait or normal engine dispatch must not be inferred. Root must integrate and run a fresh natural A24h→B24h→A scenario, requiring actual branch coverage. Natural02 raw SHA256 `4172615509f2632b8f176384c11bd1184e72a741f212dc42b66f13093fada571`, summary SHA256 `7b338e46094818b698521866fb50d9b3db8ad803d8bf1aea0e79fc3bc32a46a3`, passed=false, are preserved.

## Independent review correction

The proposed blocker that a higher-priority false prefix necessarily suppresses this observer was withdrawn after checking actual Harmony 2.4.2 implementations. Both the test-loaded DLL (SHA256 `12eed32de73312b376f616ee8659ba5d3766d96859181728437fa85c6b5ce309`) and installed Coop DLL (SHA256 `7b9e756306fa3d7620e02a857c8927a6ab04973f9bd8a77d3866700a6deac55c`) exclude `__state`, `__instance`, and `__originalMethod` before the general out/ref check in `MethodPatcherTools.PrefixAffectsOriginal`. `MethodCreator.AddPrefixes` gates only prefixes that affect original execution.

A new real native regression installs a `Priority.First + 100` false prefix ahead of the existing observer, temporarily raises dt after LateAI entry and resets it before exit. The skipping prefix actually observes attempt count 0; the final diagnostic records attempt count 1 and rejects the empty classification. This test already passes on the original production implementation. It is a regression, not a reproduced defect or RED→GREEN business fix.

The proposed independent observer/version2 experiment was abandoned. Exact modified-file snapshots, original failure/TRX logs, dependency decompilation and the uncommitted experiment patch remain in artifacts. The wrong-prefix negative registered the original legitimate prefix and expected a new future contract, so its failure is explicitly INVALID_TEST, not a product defect; it is excluded from accepted tests. Version1 production and Node implementation are unchanged relative to `6630e8953`. Natural02 still remains FAIL, and fresh dual-process native acceptance is still required.
