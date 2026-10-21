# Native scenario tool preflight and whole-caller rehearsal

This increment extends the existing Node tests and shared MCP scenario entry. It is not a new orchestrator, game platform, CI replacement, managed-test cache or native acceptance cache.

Before adapting the existing entry:

1. Existing behavior: the shared Mission runner validates coordinator checkout and then loads the SDK/transport; map authority delegates to it. Offline helper tests do not execute the complete map A→B→A caller. The deliberate map yield previously contradicted a strict automatic-Mission helper after a costly native launch.
2. Evidence and adapter: retain existing six quick Node suites and execute them before SDK imports. Rehearse the existing whole handoff caller with a controlled context and inject only the original movement-probe stage; the runtime default remains original exerciseMapMovement. No original game/AI/Tick algorithm needs changing.
3. Invariants: no skip/command override, no SDK/MCP/native entry on a failed gate, no original assertion or physical game lock removed. Cache identity binds actual HEAD, Node executable/version/options, exact argv, and sorted filenames plus byte hashes of every recursive scenarios .mjs (including untracked additions, renames and deletions). Revalidate after tests and before cache reuse. Source links are refused rather than silently omitted. Keep successful quick-tool evidence distinct from game build, managed and native acceptance.
4. Regressions: actual cold/warm runs; valid cache reuse; edited/deleted logs, failed receipts, changed Node metadata, HEAD/source changes, additions/renames/deletions, changes during tests, and actual shared run forced failure before SDK import. Whole A→B→A caller must preserve expected yield/auto flags, writer, Tick/clock/epoch, actor and critical/frozen position/resource checks. Keep Mission default false/false. Node rehearsal does not cover native navigation-state installation, AI correctness or game performance.

## Fixed quick command

`node --test --test-reporter=tap` runs these six existing suites:

- p2p-mission-lifecycle.test.mjs
- p2p-map-authority-contracts.test.mjs (includes whole-caller rehearsal)
- frozen-movement-evidence.test.mjs
- map-responsiveness-evidence.test.mjs
- native-log-archive.test.mjs
- native-jsonl-writer.test.mjs

Native-scenario-preflight.test.mjs validates the gate separately and is not recursively included in its own quick subprocess. The subprocess clears inherited NODE_TEST_* and NODE_OPTIONS only for the quick-test child, so nested tests produce an actual independent TAP run. No global environment or Windows security setting changes.

## Receipt contract

The existing Git common-dir owns `codex-coordination/preflight/<input-sha>/<timestamp-uuid>/`. Each real attempt writes test.log and result.json once; durations are monotonic milliseconds, with UTC start/end. Success requires actual child exit0, no signal, positive TAP test count with every test passed, zero failures/cancellations/skips/todos, plus unchanged inputs. Failed or changed-input attempts remain evidence and throw before SDK/MCP launch.

A warm hit requires a matching complete input and actual successful receipt, existing raw log with verified SHA256 and valid TAP summary. Only the newest real-test attempt is eligible. A newer failed, missing, malformed or tampered attempt is recorded as rejected and rerun; it never falls back to an older success. Every hit writes its own immutable reuse receipt with source attempt/hash, reason and elapsed time; it does not pretend tests were executed again. Old attempts are never removed by the helper. Scope is native-scenario-tool-smoke-v1, and native summaries store it as toolPreflight to avoid confusing it with the MCP memory/build preflight.

The helper does not acquire or replace the existing physical game lock. Coordinator execution still requires with-game-lock.mjs, and runtime PID/MVID/log/screenshot/final-state checks remain unchanged. No old native FAIL becomes PASS because a quick-tool cache exists.

## Whole-caller boundary

The controlled rehearsal executes exerciseMapAuthorityContracts itself through both yield and auto stages, including critical snapshots, committed epochs, frozen samples, player comparisons, single-writer progress and final critical comparisons. It confirms a→b→a, epochs3→5→7, and exact yield expectations true/false then false/false. Negative cases alter the actual caller's observations to prove rejection of wrong flags, writer/observer Tick, clock, lease, actor, resources/IDs/registration and frozen positions/completeness.

This is an offline contract fixture, not a fake game acceptance. Native navigation correctness remains NOT_COVERED here: original CampaignMovementCheckpointService.MatchesInstalled and its actual source-epoch/ticks/SHA logs are still the native proof. No new synthetic navigation assertion or relaxed production policy is added. All existing critical ERR diagnostics remain active.

Actual commands, raw logs, failed development attempt, immutable fixture receipts, forced-failure SDK marker evidence and final-HEAD cold/warm timings are retained under the worker artifact handoff. Measurements are observations, not promised latency targets. Original map05 FAIL and separate runtime exceptions remain unaccepted.
