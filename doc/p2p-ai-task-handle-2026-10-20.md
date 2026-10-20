# Preserve the native campaign AI task across drains

The native late-AI task is a reusable map-scene callback, not a disposable completed-work slot. `CampaignAsyncTaskDrain` now retains the same `ITask` after waiting. It still checks graph/lifetime readiness before and after the wait and logs/refuses a failed wait. This is an adapter lifecycle fix only; no AI algorithm, world driver, authority gate, task creation or manual Invoke is added.

## Core-change boundary and evidence

1. Existing functionality: drain native in-flight AI while the old lease still permits its writes, then require completed object/lifetime readiness before freeze/capture. The original failure log and refusal behavior are retained.
2. Concrete evidence: installed `SandBox.MapScene.Load` creates `AsyncTask.CreateWithDelegate(Campaign.LateAITick, false)` and stores it in `Campaign.CampaignLateAITickTask`; `MapScreen.OnPostFrameTick` repeatedly invokes that existing handle when world Tick advances. Native `Campaign.WaitAsyncTasks` only waits it, and `AsyncTask.Invoke/Wait` use the same native pointer. Clearing the handle in the adapter removes the callback entry without a recreation path. No native scheduler or mature core rewrite is needed.
3. Invariants: preserve the exact native task handle and cadence, old-lease waiting, failed-wait refusal, readiness checks before/after, null-campaign safety and an absent task remaining absent. Do not create a task, call LateAITick/Invoke from the drain, add a driver, or run writer logic under AllowedThread.
4. Regression: same handle survives one and repeated drains; subsequent caller Invoke remains usable; failed Wait preserves the task; pending graph/lifetime skips Wait; graph/lifetime becoming incomplete during Wait still refuses completion with the task retained; no campaign fails and absent native task remains absent. Existing role/frame routing tests remain unchanged.

The prior read-only investigation is `p2p-ai-eligibility-audit-r4`, base37cadb3c, handoffSHA256 `4b017692290d4bde830116ea622703adce273df5eb72db135941a0c331ce9c48`. Its retained `proof.json` hashes the inspected native binaries and extractions; `native-MapScene.cs:245–249`, `native-AsyncTask.cs:16–29`, and the installed MapScreen/Campaign excerpts establish the creation/Invoke/Wait chain.

Natural02 (`656e04aa882f409594a193b75e835bf3`, E7, A20604/B21204) retained A's absent task and missing Late AI evidence. The exact historical null-write time/callsite was not logged. B's aggregate AI Tick count can be deliberate local-player observer navigation; this fix does not infer NPC AI leakage or absence of all NPC AI. The original native FAIL remains unchanged.

## Validation boundary

A new same-handle regression test was run against the original implementation and actually failed: expected original task, actual null after Wait. `artifacts/ai-task-handle-r4/red.log` and `red.trx` preserve that RED. Tests use the actual `Campaign.WaitAsyncTasks` managed method with a mocked `ITask`; a later explicit fixture Invoke proves the retained interface is usable, not an engine/backend execution.

Target tests and their real commands, exits, TRX and hashes are in the task handoff. Compile/test commands use `-p:ModName=` to disable deployment. This worker has not launched a game or established native acceptance. The coordinator must verify the same task survives drain/pause/resume in-game and late-AI/batch activity resumes before another natural24h and A→B→A acceptance. .NET6 test fixtures cannot certify engine task scheduling or full P2P reliability; historical195 Coop/10 specified E2E failures are outside this bounded regression and must remain tracked.

Actual bounded result: the original implementation produced RED1/1 as expected; after the single-line adapter fix,23/23 drain/readiness/party-AI-routing/frame-routing tests passed in Debug/net6.0, with the GameInterface target compiled. This is not a full-suite or native-game PASS.
