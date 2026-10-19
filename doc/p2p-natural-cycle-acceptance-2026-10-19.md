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
