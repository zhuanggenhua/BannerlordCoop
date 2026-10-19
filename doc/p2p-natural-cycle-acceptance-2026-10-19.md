# Natural campaign-cycle acceptance, 2026-10-19

Status: NOT_RUN in a native game by this worker. Compilation and diagnostic tests are not native acceptance.

## Boundary

The original Campaign time events, twenty PeriodicTicker instances, PartyAiBatchRunner, finance adapter, registry and checkpoint implementations remain authoritative. This change only observes completed managed methods in DEBUG and strengthens the existing natural driver. It does not invoke callbacks, set CampaignTime, replace ticker actions, change scheduler debt/cursors, or suppress exceptions.

The previous `p2p-natural-cycle-oracle-review` is reused rather than repeated: completed callbacks cannot be uniquely identified by execution-time hour/day because native catch-up can run several distinct due slots at the same Now. The new hourly/daily receipts retain both execution hour and actual NextTriggerTime. A duplicate due slot still fails across nodes and Epochs.

## Installed-engine evidence

The previously retained engine evidence is under the coordinator's `p2p-natural-cycle-oracle-review/8dadbfb8fbaf3b3c7cd2cf47540138a1006e4b0a` evidence directory. CampaignSystem DLL SHA256 is `1f8e33e2ed73e6ec653d7629180afb70649ddc6e5bd1657a802a264efda1c3ae`, MVID `886629fe-6e60-40d7-9a57-8d46017179d9`.

- `MBCampaignEvent.CheckUpdate`, retained evidence-3.cs lines 61-68: RunHandlers(Now) precedes NextTriggerTime += TriggerPeriod. Dispatcher postfixes therefore see the actual due slot before increment.
- `CampaignPeriodicEventManager.PeriodicTicker<T>.PeriodicTickSome`, evidence-2.cs lines 43-78: add elapsed times current membership count to TickDebt, visit the next index while debt > 1, then decrement one. Empty membership clears debt without a callback. The adapter predicts from that exact instance's pre-call list/index/debt and verifies actual completed StringId sequence plus post-call index/debt.
- `InitializeTickers`, evidence-2.cs lines 206-292: native actions call MobileParty.HourlyTick/DailyTick and dispatcher per-party, settlement, clan, hero, town and partial AI methods. All twenty configured tickers use synchronous actions in the installed binary.
- `Campaign.RealTick`, evidence-1.cs lines 885-902: WaitAsyncTasks then TickMapTime. `Campaign.Tick` lines 954-983 drives event manager, hourly party processing and partial hourly AI. Existing checkpoint/drain services retain production behavior.
- Fresh local static inspection of `MobileParty.HourlyTick/DailyTick` confirms these methods do not additionally dispatch HourlyTickParty/DailyTickParty, avoiding double counting those callbacks.
- Fresh local static inspection of `DefaultClanFinanceModel.CalculatePartyWage` shows `(MobileParty, int budget, bool applyWithdrawals) -> int`. With withdrawals it limits the wage to budget and applies morale; the caller later applies gold. A wage result alone is therefore not proof of gold payment. The separate existing `ClanVariablesCampaignBehaviorInterface.DailyTickClan` observer records actual clan leader gold before and after the complete daily settlement adapter.

Fresh decompilation is ignored local evidence, not redistributed source. Root must bind running assembly MVIDs/build commits to its native run evidence.

## Native execution

Run only from the integration checkout with the existing physical game lock. After integrating and deploying the exact tested DEBUG source, use:

```powershell
node tools/coordination/with-game-lock.mjs --repo . -- node tools/mcp/scenarios/p2p-world-lifecycle.mjs --natural --repo . --server .mcp-local/server/CoopMcpServer.exe --config .mcp-local/profiles-reliability-direct.json --sdk-root .mcp-local/client/node_modules --record .mcp-local/p2p-natural-r2.jsonl --day-timeout-seconds 900
```

Use a fresh record basename. Native A runs ordinary Play_2x through at least 24 elapsed campaign hours, yields to B, B runs at least another 24 hours, then A takes back authority. Neither clock setting nor manual daily callbacks is allowed. The existing deadline is unchanged.

Each measured interval requires every native hourly/daily due slot from the initial next-trigger phase to the final phase, completed exactly once by the confirmed writer. Each of the twenty tickers must have scheduled batches; empty partial-AI partitions can complete zero callbacks. Expected and completed identity/order hashes, counts, index and debt must match every batch. Membership changes inside a batch produce an explicit incomplete-evidence failure with the first mismatch, rather than being waived as a duplicate or silently accepted. Membership changes between batches use the new native membership list normally.

Native RealTick, Tick, LateAITickAux and MobilePartyAi.Tick completed/failed counts are observed for both session modes. The older ListenAuthority-only AiTicks is retained as evidence but is not used to require B's AI progress. Observer Tick/AI completion counts must not advance. Existing fatal/RealTick/AI error checks remain strict. `lateAiTaskPresent` reports presence, not completion; successful handoff relies on the unchanged production async drain and synchronized scheduler checkpoint, and must be correlated with native logs/state.

## Economy and world effects

`coop.debug.authority.cycles 0 effects` pages actual existing DailyTickClan finance adapter completions and native CalculatePartyWage calls with applyWithdrawals=true. Rows retain node/Epoch/ticks, exact clan/party and hero StringIds, gold before/after, calculated wage, and the original exception. Zero wage and unchanged gold are legal and are not fabricated into positive payments. The driver requires both finance and wage evaluations inside each measured window and retains their amounts, changes and every receipt. These show the actual completed settlement path, not isolated model queries.

At paused checkpoints on each side and handoffs, original world_state and semantic_state compare critical world clock, party IDs/registrations, all roster counts/wounds/XP, hero gold, recruitment, player identities, and NPC samples including position, party trade gold and rosters. A local shop inventory is not newly made authoritative. No assertion forces an NPC decision, XP award or nonzero wage when vanilla conditions legitimately do not cause one. Those effects must be reported as observed amounts or NOT_OBSERVED, not inferred solely from ticker dispatch.

## Limits

No native PASS is claimed here. Wage calculation receipts do not by themselves prove a particular player's deduction; correlate the completed daily adapter and matching world/player gold snapshots. A failed callback, dynamic in-batch membership, missing due slot, absent real settlement, silent simulation failure or observer simulation must fail the run and retain all raw evidence. Full default Coop and E2E suites retain their historical 195 and 10 failures; root must compare exact names after integration. This worker's focused tests do not replace that comparison.

The inherited P1-A RealTick counters remain ListenAuthority-only. The added DEBUG per-Campaign counter separately reads the installed native `TaleWorlds.Engine.Utilities.EngineFrameNo` after each completed original RealTick. Two completions in the same native frame increment `Campaign.RealTick.sameFrameDuplicate.failed`, independently of session mode, so B is covered. `sameFrameRealTickCoverage` reports NativeEngineFrameNo. This property was verified by local Utilities decompilation, line109; its getter delegates to IUtil.GetEngineFrameNo. A pure collector test verifies repeated-frame and separate-Campaign behavior without calling the native getter in unit tests.

`cycles` also reuses the existing scheduler checkpoint capture as a read-only compact fingerprint: exact Index/TickDebt, list count and SHA256 of the original ordered registry handles for all twenty tickers, plus native last-game-time and minimum interval. A→B and B→A while the ordinary game clock is paused must preserve this fingerprint and hourly/daily phases on both nodes. No sorting of native entity order or numeric debt tolerance is introduced. The world counter may continue frame ticks while clock is paused and is intentionally excluded from this phase fingerprint.

The driver's returned `coverage.economy.paymentAcceptance` is explicitly NOT_OBSERVED even if all cadence assertions pass. Actual wage deduction causality still requires native evidence beyond these model/adapter observations; no financial helper is called for test setup.

Focused delivery validation: natural lifecycle plus shared log archive Node tests 20/20; CampaignCycleDiagnosticsTests 11/11, including real Harmony binding of all added native method overloads. No game started by this worker. Completed native WaitAsyncTasks calls are counted without calling Wait from the read command; ITask in the installed Library exposes Invoke/Wait only, so task presence is not represented as a completion flag. Root must still run full regression and native A→B→A acceptance on the integrated version.
## Native01 compatibility failure and bounded diagnostic repair

The first integrated natural run did not reach a campaign or two-player acceptance. Server PID22872, run52ed16a2137f4a99a78af8b2608114d1, logged an Applying patches failed HarmonyException at17:57:49 while detouring closed generic PeriodicTicker<MobileParty>.PeriodicTickSome(Double). Its inner ArgumentException says the given generic instantiation was invalid in PatchFunctions.UpdateWrapper. The original startup failure and cleanup remain FAIL evidence; earlier net6 diagnostic test success is not game CLR compatibility evidence.

The mandatory four-point review for this repair is:

1. The original DEBUG probe observes completed native callbacks and predicts the twenty existing tickers' exact StringId sequence, index and TickDebt. It does not execute the scheduler or create world objects.
2. A real Windows PowerShell5.1 Desktop .NET Framework4.8.9363.0 64-bit process reproduced the same wrapper failure with the actual deployed GameInterface and Harmony2.4.2, without starting Bannerlord. The needed repair is inside this observer adapter. No scheduler, Action delegate, registry, AutoSync or networking implementation needs replacing.
3. The adapter now detours only five native nongeneric leaf methods: MobilePartyHourlyTick, PeriodicHourlyTick, PeriodicDailyTick, PeriodicQuarterDailyTick and TickPartialHourlyAi. Their installed-engine call orders cover respectively1/3/6/1/9 existing tickers. Expected elapsed units remain the original CampaignTime.DeltaTime Hours or Days, quarter-day times4, and partial hourly AI times0.99. The observer snapshots those exact native ticker instances, retains callback-method identity plus StringId in ordered receipts, and still checks every expected/completed sequence, native final cursor and exact debt. A membership change within the observed group explicitly fails evidence rather than being waived. Exception objects propagate unchanged and are not replaced by reads of a faulting ticker. Intentional skipped original bodies are not counted as completed scheduler batches; skipping a writer is a diagnostic failure and cannot satisfy window coverage.
4. Regression tests retain catch-up, due-slot duplicate rejection, identity/count/order comparisons and original exception preservation. Added negatives cover missing/extra/reordered callbacks, empty partitions, cursor/debt/membership changes, skipped observer versus skipped writer, real installed twenty-field membership and exact nongeneric target signatures. Desktop CLR probes must bind every diagnostic patch class in this file, not just the changed ticker observer. Successful independent binding still does not replace the next two-process native startup and full natural-day run.

Raw Desktop CLR probe evidence is attached through the coordinator handoff with executable commands, process/runtime versions and assembly SHA256. It does not launch or deploy a game. The root must rerun full regression and native acceptance on the integrated repair. The wages-payment causal limitation remains NOT_OBSERVED.

Compatibility repair validation: focused CampaignCycleDiagnosticsTests16/16. Desktop PowerShell5.1.26100.9711, .NET Framework4.8.9363.0, 64-bit reproduced the original closed-generic failure with actual deployed Harmony2.4.2. The repaired observer and all six companion diagnostic classes then bound27 real installed-engine targets successfully in a fresh DesktopCLR process without starting a game. Complete probe outputs, version/SHA metadata and scripts are handoff evidence; native02 remains NOT_RUN by this worker. Root must keep native01 as FAIL and rerun integrated game acceptance.
