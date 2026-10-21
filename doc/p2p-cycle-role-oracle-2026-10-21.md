# M16 role-window evidence draft, before production/oracle edits

Base e2689840ae62f9f3200b6f3497ad042c1273017a. Native FAIL `p2p-modify-natural-m14-01` remains FAIL. Its summary reports `Observer advanced authoritative simulation: Campaign.Tick.completed` in the post-handoff second `assertCycleWindow` call. No original log or verdict will be rewritten.

## Source basis and four core reasons

1. Existing DEBUG `CampaignCycleDiagnostics` owns Campaign-scoped bounded native cycle/ticker/economy evidence and coarse simulation counters. `CampaignSimulationEvidencePatches` observes actual original completion without granting authority; AI already captures entry/exit exact leases and identity. Existing `assertCycleWindow` proves a full natural day but assumes its supplied writer remains the writer for the entire before-to-after interval.
2. Endpoint state and aggregate Tick totals cannot establish which lease/role each intervening original call held. Current `Campaign.Tick` evidence has no entry context. A thin diagnostic boundary and role-window oracle adapter suffice; no Campaign Tick, scheduler, AI or handoff algorithm needs change. Repeating the full-day observer assertion across a role transition is the demonstrated test conflict, but simply removing that assertion would conceal an observer violation.
3. Preserve all original total counters, exact due slots, 20 ticker audits, errors, original exceptions, world/semantic equality, economic evidence and full-day strict observer Tick zero. New evidence must partition every observed native call, retain failures/unknown contexts and reject overflow/missing/contradictory data. DEBUG capture must not acquire an execution permit, call a native Tick, alter dt/time/scheduling, or expand observer eligibility.
4. Required negatives: B calls native Tick while still an observer; frozen/paused/unknown/changed lease; restricted phase/AllowedThread; positive/nonfinite dt; bootstrap count; clock advance; missing call/partition/context identity; new-writer NPC/player AI; duplicate/missing hourly/daily or ticker callbacks; old-writer exceptions and scheduler changes. Only exactly proved confirmed-writer zero-dt bookkeeping can explain the paused new-writer Tick delta. Old raw FAIL remains historical evidence until a new real day run.

Native source grounding: saved original `vanilla-Campaign.cs` (`p2p-cold-checkpoint-audit`) shows `Campaign.Tick` increments `CurrentTickCount` unconditionally; simulation branches use `_dt > 0` or `CurrentTickCount < 3`. It also contains unconditional `_tickData.Tick`/captivity and menu bookkeeping, so this proposal does **not** equate zero dt with absence of every native side effect. Installed CampaignSystem DLL SHA256 is `1f8e33e2ed73e6ec653d7629180afb70649ddc6e5bd1657a802a264efda1c3ae`. Classification is limited to time/AI/cycle advancement evidenced below.

## Proposed additions inside the existing collector

Add bounded role-context aggregates, not a second manager. Existing rows/effects/ticker audits/counters remain intact. Each group records:

- method (`Campaign.Tick` or `MobilePartyAi.Tick`), actual node, original outcome (`completed`, `failed`, `skipped`), complete entry and exit lease, confirmed/frozen/local-writer flags, phase plus recursive world-write restriction and AllowedThread;
- classification and reason; AI retains its existing class and party role/identity validity;
- count, first actual entry/exit evidence and last actual entry/exit evidence, including raw float dt bits/value, `CurrentTickCount`, world ticks, thread/time and Campaign identity;
- stable grouping context includes both exact leases and roles/phases/outcome/classification, so lease changes cannot be folded into a normal group. Per-call qualification occurs in the native entry/finalizer, before aggregation.

Header schema version 1 includes explicit per-method outcome counts and group coverage. Every existing `Campaign.Tick.completed/failed` total remains preserved and must equal the completed/failed role partitions. AI role counts must equal its existing outcome/class partitions. Counter keys are explicitly emitted even at zero; consumers never supply missing zero via `?? 0`.

`writerBookkeeping` is permitted only when **each observed call** has: the same valid confirmed non-paused lease at both ends; actual node equals the lease writer and current local-writer lookup; not frozen; stable permitted native business phase (`Unspecified` or `WorldWrite`), no recursive restriction/AllowedThread; finite dt numerically zero at both ends; world ticks unchanged; entry `CurrentTickCount >= 3` and exactly one native count increment; no original exception; same actual Campaign/node. Raw boundaries remain available in first/last evidence. All other original calls outside a confirmed writer's progress are unexpected, including unknown/observer/frozen/replay/install/bootstrap or changed context. Skipped originals remain skipped and cannot explain completed totals.

## Read-only command wire addition

Extend existing `coop.debug.authority.cycles` with section `simulation` using its existing <=256-row pagination. Cycle/effect headers carry the explicit schema/partition metadata, native node and current confirmed endpoint lease. The simulation section carries complete bounded aggregate groups. Native identity is checked against the expected slot before merging; JS cannot overwrite the actual node with a guessed configuration identity. All section pages must match Campaign/process/collector identity and total/coverage metadata. Overflow, paging truncation or missing method counters is a hard failure.

## Oracle split

1. **before day to pre-handoff:** retain the original `assertCycleWindow` unchanged, including observer `Campaign.Tick.completed == 0`, exact 24h/due slots, all ticker and economic/AI checks.
2. **pre-handoff to final post-handoff:** add `assertHandoffCycleTail(pre, post, sourceStates, targetStates)` validating endpoint identities/exact source and target leases and complete per-call role partitions.
3. Revalidate the entire original writer day with its post-drain collector for due slots, coverage, ticker audits and errors. Do not apply the obsolete observer role to the other node after its activation. Its progress is independently constrained by the role-tail assertion rather than omitted.
4. Old-writer native tail is allowed only under its exact confirmed unfrozen source lease and genuine writer qualification before freeze. New writer completed Tick is allowed only under exact target lease and `writerBookkeeping`; any positive dt/clock/cycle/authoritative-AI advancement in the paused tail rejects.
5. AI tail is classified by actual call context: observer-local navigation on either node is permitted under the corresponding exact lease, including B's navigation before promotion and A's after demotion. Old source-writer NPC/player tail may drain under its old valid lease. New writer NPC/player AI must not advance while paused. Total AI zero is not assumed. Existing unknown/unqualified/context-changed original calls, exceptions and role-count mismatches remain failures.
6. No rows/due slots/economic events or mismatched scheduler output are forgiven by a role classification. The existing final checkpoint/world/player/semantic equality and complete old-writer collector checks remain required.

No production/oracle code is edited until root approves this contract. Tests will use new `source/GameInterface.Tests/Diagnostics` files to avoid the scheduler worker's test scope. Native acceptance requires a new final integrated natural-day run; old evidence has no such per-call role coverage and cannot pass this new contract retroactively.

## Root-reviewed amendments before pause

1. Exact-role segmentation must cover Campaign.RealTick, Campaign.Tick and Campaign.LateAITickAux as well as AI. CurrentTickCount +1 is required only for Campaign.Tick; RealTick/LateAITickAux use their own native boundary invariants. No original world-write path is excused by Tick-only evidence.
2. The original full-day strict oracle stays intact. Post-window source-writer tail must retain its exact confirmed unfrozen source lease; target-writer zero-dt bookkeeping must retain the exact target lease. ObserverLocalNavigation counts may increase, but unsafe/unknown/failed executions remain rejected.
3. Export counts, bounded role aggregates and first/last evidence from one existing collector lock as one atomic simulation snapshot (at most 256 groups), or use an explicit immutable paging token. Never merge changing header/counters and rows across live pages. Groups use class/method/lease/phase/outcome, not party identity. Missing data and overflow are hard failures.

Paused before any tracked source, test or oracle modification, pending the independently reviewed M13 API graph repair. This document is an ignored planning artifact only.

## Implemented M16 contract

This section supersedes the pre-edit pause notice above. Implementation remains entirely DEBUG evidence and scenario-oracle adaptation, using the same CampaignCycleDiagnostics collector and existing Harmony evidence patch. No AI, economics, scheduler, movement, authority permission or handoff algorithm is changed.

CampaignSimulationEvidencePatches.Prefix now binds the actual Campaign instance, its collector, and a native entry boundary for RealTick, Tick, LateAITickAux and AI. Finalizer records on that same collector and returns the original exception unchanged. CaptureNative reads the actual Campaign ID/reference, GameStarted, raw dt value/bits, CurrentTickCount, native world ticks, thread/time, exact confirmed lease, actual node/local-writer, frozen state, execution phase, recursive write restriction and AllowedThread. Changed current Campaign, capture errors, changed lease or context remain unsafe. The actual Campaign reference is JsonIgnore; serialized ID/hash are finite evidence, not the identity check itself.

NativeTick records original totals and role groups in one collector lock. AiTick records its existing outcomes/classes/unsafe counts and the new role group in that same lock. SimulationEvidenceSnapshot copies counts, first AI entries, bounded groups and overflow in one lock. Groups include method/outcome/classification, exact entry/exit role contexts and AI original class/unsafe flag. They retain first and last complete boundaries, including real AI party/controller/identity. Groups never key by party, clock or frame; maximum 256 groups, explicit overflow remains a hard failure. Simulation evidence is read as one <=256-row section, never merged from changing live pages. Actual native node must match the configured slot; the test cannot overwrite it with a guessed node.

Every call is classified before aggregation. writerBookkeeping requires a coherent confirmed unfrozen nonpaused writer at both ends, same actual Campaign/node/lease and allowed phase, no recursive restriction/AllowedThread, GameStarted, CurrentTickCount >=3, zero finite dt at both ends, unchanged clock and native count delta. Native source shows Tick increments CurrentTickCount exactly once, while RealTick and LateAITickAux do not increment it directly. Positive native progress is a separate writerProgress class. Unknown/observer/frozen/replay/context-changed actual originals remain unexpected. Intentionally skipped originals remain explicit skipped partitions and cannot explain completed totals; an error before original is retained as beforeOriginal.failed. Zero-dt bookkeeping does not assert that native menu/captivity/cache work has no side effects.

The oracle keeps assertCycleWindow unchanged. It adds day-before to pre-handoff partition deltas, requiring qualified native RealTick progress under the exact source lease; permitted observer local navigation is separately checked. Post-handoff uses exact source/target role-tail deltas. Source writer may finish genuinely qualified AI work under its source lease before freeze; target writer may perform proved zero-dt bookkeeping, but no target NPC/player AI while paused. Actual originals in unknown/frozen/replay/observer roles, cross-lease calls, incomplete/overflowing partitions, failures, missing counters and changed identity reject. Initial startup/Bootstrap history remains present and unqualified; unchanged historical groups cannot be silently reclassified as runtime writer work.

After the barrier, assertCycles rechecks complete raw-history exceptions, duplicate due slots, ticker identities/orders, counts and economic effects. The original writer's entire day is revalidated using its drained final collector and the peer's sealed pre-handoff observer window; the peer's subsequent execution must pass the separate exact-role tail, rather than the obsolete observer assumption. Exact world/semantic/roster/gold/XP/time/scheduler equality and all original failure records remain required. No endpoint-only lease inference or assertion tolerance was introduced.

## Evidence and acceptance

Original natural01 raw SHA256 cf197e2cbf91119633b124a68c769a99d9e93deacf9b348bde3b7d03c3d948da is unchanged. original-natural01-readback.json streams that original record, hashes its summary and preserves the actual line857 native_day_pre_handoff coverage. The original run remains FAIL: it has no per-call role partitions and cannot pass the new oracle retrospectively. Its A natural24h bounded progress does not certify B24h or return A.

Native-library tests invoke the actual original LateAITickAux zero-dt guard on a real Campaign object with controlled finite fields. Other classification tests exercise the evidence boundary and collector, not a substitute full Campaign.Tick or a claim of in-game 24h. New tests cover bootstrap/GameStarted, unknown/observer/frozen/paused/changed Epoch, replay/AllowedThread, dt/clock/count errors, original failure/skips, reference/identity, capacity and concurrent atomic snapshot coverage. Node tests preserve old negative assertions and add exact-role AI navigation/drain, illegal new-writer AI, natural-day qualification and atomic section identity/overflow/missing-counter checks.

Worker candidate native game acceptance is NOT_RUN. Root must review, integrate, rebuild and run the real complete A24h to B24h to A natural-day scenario with the final integrated binary. Managed and Node checks do not grant that acceptance, nor performance or ordinary field-battle acceptance.

## Actual automated runs

All managed commands use GameInterface.Tests Debug, -p:ModName= -p:NuGetAudit=false; no game deployment occurs. Final related filter includes CampaignRoleWindowEvidenceTests, CampaignSimulationEvidenceTests, CampaignCycleDiagnosticsTests, CampaignMenuWorldDeltaTests, CampaignPartyAiRoutingTests and CampaignWorldExecutionTests.

| Evidence | Result |
| --- | --- |
| native-red01.log / TRX | 0 PASS / 19 FAIL, actual exit1 before production edits |
| native-green02.log / TRX | 19 PASS / 0 FAIL, actual exit0 |
| related03.log | compile FAIL exit1, missing command IControllerIdProvider import; raw retained |
| related04 through related07 logs/TRX | 102,107,107,108 PASS respectively; each actual exit0 |
| related08.log / TRX, final normalized C# | 108 PASS / 0 FAIL, actual exit0, including25 new cases |
| node-red01.log | 2 PASS / 1 FAIL, exit1; absent-function negatives were not accepted as useful proof |
| node-red02.log | 0 PASS / 3 FAIL, exit1 after requiring the missing entry point explicitly |
| node-full04/05/06 logs | 28/3,30/1,30/1 PASS/FAIL, exit1; new fixture role groups incomplete/duplicated, raw retained |
| node-full07/08/09 logs | 31,34,35 PASS, exit0 |
| node-full10.log | 31 PASS / 4 FAIL, exit1; explicit before-original counters missing from zero-AI fixture, raw retained |
| node-full11/final12/final13 logs | 35,36,36 PASS, actual exit0; final13 is final JS source |

No production assertion was relaxed to resolve fixture failures. Fixtures now describe all raw observations and exact bounded groups, preserve baseline unknown history and include both valid and invalid role transitions. strict-oracle-integrity.json confirms the entire original assertCycleWindow function is unchanged from e268. AI local-navigation qualification does not require an observer to advance CurrentTickCount or initialize world AI; it records those fields and retains the existing confirmed identity qualification. World-method bookkeeping still requires GameStarted and nonbootstrap count, with no unknown-to-bootstrap reclassification. The existing observer-frame initialization adapter remains unmodified.

## Independent-review correction, revision02

The independent counterexample against 65a67dfc76c2a11addcab60a4305a2b94fcca51a showed a real oracle coverage gap: a valid target Wage, DailyTickParty or ticker batch could be added during the paused handoff while assertCycles, role-only tail validation and final scheduler equality all accepted it. This is a validator defect, not evidence of a newly observed native/network failure. The original submitted commit, handoff and all failures remain sealed.

The corrected assertHandoffCycleTail now requires the target's actual rows/effects/tickerAudits to remain exactly unchanged, including no additions, edits or deletion of its prior legitimate writer history. Its installed hourly/daily/scheduler phase must equal the strictly audited drained source phase. It is intentionally not compared to the target's stale pre-install scheduler. Actual original natural01 pre receipts have different A/B phases; final B installs A's phase, while both nodes' business arrays remain unchanged. That bounded historical comparison is evidence for the contract, not retrospective PASS.

Existing economic rows contain Node and Epoch, but do not carry exact RoomId/AuthorityId plus callback entry/exit/freeze proof. DailyTickParty lacks an exact due slot, and aggregate ticker audits cannot establish complete per-callback freeze/drain timing. Therefore a new source periodic/economic/phase tail cannot be proved safe from those aggregates alone. This version rejects such changes explicitly as Missing source drain provenance. It continues to permit actually qualified source old-lease AI drain and zero-dt bookkeeping. This is an evidence limitation, not a blanket ban on the old writer or a claim that every observed source callback is illegitimate. Supporting additional source business drain requires a separately reviewed finite proof extension; it is not guessed here.

Final assertCycles still audits both actual final histories. The entire original two-node day-before to pre-handoff strict oracle is unchanged. After role/business tails pass, the same unchanged assertCycleWindow re-audits the actual old writer's source-only before/final collectors; no target pre receipt or simulated counter replaces actual final target data. Target actual final history is covered separately by the full-history audit, immutable business history, installed-phase equality and exact per-call role tail.

Revision02 is limited to this existing oracle, its tests and this document; native diagnostics/permissions/algorithms have no diff from 65a, so the unchanged 108-case native regression was not rerun. No new game was launched.

### Revision02 verification

- target-red01: three counterexamples initially reproduce missing rejection, exit1. The first narrow green used incomplete old fixtures and is retained but not accepted as sufficient fix proof.
- source-red03: retained early negative run; incomplete target fixture prevented the intended source-specific failure, so it is not accepted as source proof.
- node-full04: 39 PASS /4 FAIL, exit1; old valid fixtures omitted the now-required explicit empty target effects array. Correcting fixture shape strengthens evidence completeness; no assertion was relaxed.
- sealed baseline replay uses the unmodified 65a oracle with only import paths rebound to existing tool modules. With the final complete fixtures, target-baseline-red06 and source-baseline-red07 each show 0 PASS /3 FAIL, actual exit1, all with Missing expected rejection. These establish six genuine missing-rejection cases; baseline-replay-provenance.json retains exact source/fixture hashes.
- node-full05: 43 PASS /0 FAIL, exit0.
- node-final08: final 45 PASS /0 FAIL, exit0. Tests include correct stale-phase installation, target history edits/deletion, illegal target Wage/party-day/ticker work, unsupported source business/phase changes, plus prior exact-role AI drain/navigation positives and all original cycle/semantic negatives.

The review counterexample SHA256 remains 5c405df160a28d2e1665203e094cdf01a94938b4af3141963a447b9804e388a0. Original assertCycleWindow integrity is rechecked against e268 in revision02/strict-oracle-integrity.json. Candidate real-game acceptance remains NOT_RUN and the original natural01 FAIL remains unchanged.
