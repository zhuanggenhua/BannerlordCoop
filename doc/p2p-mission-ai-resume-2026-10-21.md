# Mission return world-time/AI continuation, M12

Base cc2a111646e11e342db38e8af7b285b0ee31446d. Native FAIL c834c6420baa4178a85c272d05ffedd9 remains FAIL.

## Reasons recorded before production edits

1. Existing native Campaign.RealTick computes the shared frame delta and advances map time. Campaign.Tick applies events/encounters, then directly sets `_dt=0` for an active non-wait local MapState menu. Campaign.LateAITickAux calls PartiesThink only for positive `_dt` (or its first three bootstrap ticks). Existing Coop DisableGameMenuPausePatches redirects TimeControlMode setters, but does not cover that direct native delta write. Original batch runner and native AI algorithms stay intact.
2. Runtime A-return epoch9 has +97 Campaign.RealTick/Tick/LateAITickAux, advancing Campaign hours, `_dt=0` at both snapshots, and zero new AI observed/skipped/failed calls. The native code explains why the AI batch is never entered: local menu pause resets the world delta before late/partial AI. This is a missing world-time/local-UI responsibility adaptation, not broken AI business. Extending the existing menu adapter suffices; a delta-restoring postfix would be too late for TickPartialHourlyAi.
3. Preserve native callback order, event counts, input/menu state and getter/null behavior. Only the existing qualified CanRunWorldSimulation policy can suppress this local-menu pause. Observer/replay/bootstrap/frozen/paused contexts retain native behavior. Do not introduce another tick driver, modify IsWaitActive, restore a guessed delta, or run background Campaign under Mission.
4. Tests must pin the actual native Campaign.Tick guard shape, reject missing/duplicate shape, and execute that extracted native guard followed by the actual native LateAITickAux with only its PartiesThink leaf recorded. Cover qualified A/B writers, observer, frozen, paused, replay, bootstrap, existing active waits and null menu. This native-library fragment test is not a full Campaign.Tick or real Mission acceptance; root reruns the complete real-game scenario after independent review.

## Provenance

Archived `p2p-cold-checkpoint-audit/engine-assemblies.json` Campaign DLL SHA256 `1f8e33e2ed73e6ec653d7629180afb70649ddc6e5bd1657a802a264efda1c3ae` matches this worktree's actual game DLL. Its `vanilla-Campaign.cs` shows TickMapTime/RealTick at840/885, LateAITickAux at946, Tick at954; native local-menu `_dt=0` occurs before TickPartialHourlyAi. The actual `vanilla-MapState.cs` OnMapModeTick calls RealTick and Tick once. Original logs contain no ERR; diagnostics preserve the failed verdict.

## Evidence qualification and implementation

Mission01 did not record `MapState.AtMenu` directly. The positive clock delta, zero post-Tick `_dt`, no AI entries, and matched native code localize a missing local-menu/world-time boundary. This remains a strongly supported causal inference until the final integrated Mission02 rerun. The new read-only DEBUG `coop.debug.authority.state.mapAtMenu` field lets that run prove the actual local menu condition.

The existing `DisableGameMenuPausePatches` now also targets Campaign.Tick. It requires exactly the native `IsWaitActive` call, true branch, zero `_dt` field store and continuation label. Unexpected or duplicate/missing shape throws before patch installation. Only that getter call is replaced, retaining labels/blocks, Tick order and all business code. The replacement evaluates the original getter first and returns true only for the original wait state or the existing qualified world-simulation policy. It does not mutate a menu, set a clock, synthesize delta, add a tick driver or change the AI runner.

The extracted-guard fixture executes the actual native Campaign.Tick instructions from `IsWaitActive` through `_dt=0`, then invokes the actual native Campaign.LateAITickAux. Only PartiesThink is replaced with a recording leaf. It proves vanilla starvation and the adapted dispatch for both writer roles. It is deliberately **not** a full Campaign.Tick or Mission game simulation.

## Actual checks

- `red01`: before production edits, 12 cases, 9 PASS / 3 FAIL, exit 1. Both writer roles lost the positive delta and the existing adapter did not target Tick. Raw log/TRX retained.
- `green02`: same 12 cases, 12 PASS / 0 FAIL, exit 0.
- `related03`: Time, authority AI routing/execution/evidence and async-drain regressions plus six additional guard-shape/compatibility cases, 134 PASS / 0 FAIL, exit 0.
- `related04`: final code including read-only menu diagnostic and debug-command regressions, 140 PASS / 0 FAIL, exit 0.

All worker builds use `-p:ModName=`. Native dual-game Mission02, full natural day, field battle and performance: **NOT_RUN** in this worker. Mission01 stays FAIL. Root must independently review, integrate and rerun the exact Mission scenario without weakening its AI assertion.
