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

The related managed selection has 126 PASS / 0 FAIL. All 212 scenario-tool Node tests pass, including 49 world lifecycle tests. Full Coop/E2E suite was not run by this worker; historical failures are not claimed resolved. Artifacts retain the original RED and intermediate compile/fixture failures with actual exit codes. Four change reasons were recorded before editing in `artifacts/modify-late-ai-branch-proof-m20/contract-before-edit.md`.

Native dual-process game: NOT_RUN by worker. Actual caller stack: NOT_OBSERVED; whether an empty call comes from Wait or normal engine dispatch must not be inferred. Root must integrate and run a fresh natural A24h→B24h→A scenario, requiring actual branch coverage. Natural02 raw SHA256 `4172615509f2632b8f176384c11bd1184e72a741f212dc42b66f13093fada571`, summary SHA256 `7b338e46094818b698521866fb50d9b3db8ad803d8bf1aea0e79fc3bc32a46a3`, passed=false, are preserved.
