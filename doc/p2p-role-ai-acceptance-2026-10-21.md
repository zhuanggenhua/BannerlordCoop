# Role adaptation and native AI takeover acceptance, 2026-10-21

## Verdict and exact runtime

**PASS, bounded to two-player map movement and one normal A → B → A authority round.** This does not accept Mission transitions, a natural 24-hour campaign cycle, fault recovery, or overall P2P completion.

Runtime source commit: `dc1e4e84f01a728903055f805e8601bd2fd16fca`, branch `codex/p2p-orchestration`. Native run `a2e7261963e8459ca84cb3c5f81e655c`, 2026-10-21 07:11:38–07:14:26 UTC. A PID **21444**, B PID **25104**. Both loaded `0.1.7+dc1e4e84f01a728903055f805e8601bd2fd16fca`, Coop MVID `a9f534a5-dcc5-4e70-864d-ffbeeefd6469`. These are two playing game processes, one per player, with A remaining the transport room host throughout. No independent game server or loopback CoopClient was added.

The map-only availability command requests an orderly handoff. It deliberately does not simulate entry into a Mission. Raw failure runs map01–map05 remain unchanged; map06 does not retroactively convert them to PASS.

## Root cause and unified implementation

The prior map05 exposed two responsibility/state boundaries, rather than evidence that native AI should be rewritten:

1. Native object constructors, component binding, and Settlement setters still used static process server/client identity. A remote confirmed Campaign writer could therefore be refused its native lifecycle and result publication. All three old Settlement setter patches emitted the same diagnostic; the original 223 messages do **not** uniquely identify the Owner setter.
2. Final movement/navigation agreement and a ready object graph did not prove that constructor-free incremental AI objects carried the native leaf state needed for takeover. Map05 recorded four incremental bandit AI failures after B activated. A native `GetBehaviors` regression independently reproduces the missing fleeing-data failure; the saved map05 Tick stack lacks the IL location needed to prove that particular dereference was its sole cause.

The implementation extends existing responsibilities, without a second global manager:

- Existing `CampaignSimulationAuthorityPolicy` separates native callbacks, initialization, and publishing an already applied result. Network result sending requires the current confirmed writer; local publication/registration retains the original unconfirmed initialization fallback. Bootstrap/CheckpointInstall permit only the explicit unregistered initialization path. Replay is not a grant of simulation permission.
- Existing Army and AiArmyMember event/callback entry points use that policy. Native event subscriptions remain initialized; original AI decision/business bodies remain intact. Cohesion results use the existing authority envelope.
- Existing AI/component lifecycle and three Settlement results use the original Registry, message DTOs, handlers, Gold coalescer, and game-thread apply queue. Only selected original publisher/receiver services are composed into both endpoint roles. No complete ClientModule/ServerModule stacking, new wire format for these results, or duplicate native operation is introduced.
- Existing final movement checkpoint adds one versioned, finite AI continuation section. It validates party/AI handles, coverage, owner bindings and values before install; installs into existing native AI instances under the original CheckpointInstall phase; then recaptures and compares the complete payload before marking it installed. Missing/incomplete state refuses activation and retains the fence. It does not serialize the full AI object graph or claim rollback/lossless recovery.
- Existing native cycle evidence now keeps each actual new writer running until completed NPC AI invocation volume reaches at least twice the full active-party count. The observer must record zero completed authoritative AI calls, and failed/unsafe execution deltas must remain zero. Existing clock, resources, exact frozen positions, error and timeout checks are retained.

See [ADR 0003](decisions/0003-ai-authority-continuation.md), [finite continuation](p2p-ai-continuation-2026-10-21.md), [lifecycle roles](p2p-lifecycle-roles-2026-10-21.md), and [map AI coverage](p2p-map-ai-coverage-2026-10-21.md) for interfaces, four-part change rationale and regression scope. Owner/Puppet, movement prediction, LiteNetLib, Protobuf/LZ4, AutoSync and the Registry/snapshot main implementations remain in use.

## Native PASS / unaccepted checklist

| Scenario/check | Result | Actual evidence |
| --- | --- | --- |
| Join two distinct native actors | PASS | Two native PIDs; original MainHero/MainParty preserved; each player's hero/party/clan registration count remains 1 on both nodes |
| Both players move, transparent UDP | PASS for original movement contract | Native map inputs and prediction retained; transient replica position offsets permitted |
| Both players move, one-way 50 ms UDP delay | PASS for original movement contract | Existing packet proxy and native timing evidence; no loss/chaos acceptance in this run |
| A → B | PASS | Room host remains LocalA; source Epoch 3 → paused Epoch 4 → B active 5; exact frozen baseline for all 1465 active parties |
| B runs native AI; A observes | PASS | B completed NPC AI delta 3095, A delta 0, required volume 2930; native world Tick deltas A 0 / B 100 |
| B → A | PASS | Source Epoch 5 → paused Epoch 6 → A active 7; exact frozen baseline for all 1465 active parties |
| A resumes native AI; B observes | PASS | A completed NPC AI delta 3096, B delta 0, required volume 2930; native world Tick deltas A 84 / B 0 |
| Critical shared state after each window | PASS | Exact paused clock, party identities, roster counts/XP, all alive hero gold and volunteers, nextHandle; no unregistered active parties; player resources and identities equal |
| Native error logs | PASS in this run | A 0 ERR / B 0 ERR, WARN 5/3 retained; no recorded RealTick, AI batch or duplicate-ID errors; failed/unsafe simulation counter increments rejected |
| Cleanup | PASS | Both process trees stopped, cleanupComplete true; physical game-installation lock released; 14 original BMP captures retained |
| Complete natural 24h, wages/economy and AI scheduling | NOT RUN | Invocation volume is not unique-NPC, hourly/daily completeness or a full campaign-day proof |
| Mission entry/exit, all-away pause, battle settlement | NOT RUN | Availability injection is map-only |
| Fault injection, reconnect, lossless recovery | NOT RUN | Normal handoff cannot establish fault safety/lossless recovery |
| Independent machines / near-zero latency / true stop-turn continuity | NOT ACCEPTED | This is one machine with two game processes; no end-to-end CommandId or pixel scanout proof |

Both final checkpoint payloads include AI version 1 / AI count 1465. Source capture and destination prepare/install have matching full SHA256 before activation:

| Direction | Source Epoch | Campaign ticks | Movement + AI SHA256 |
| --- | --- | --- | --- |
| A → B | 3 | 78699177650273 | `2c5908c39b574db109405ed24abe428fb6bb208b8c597a315db3b40e1c63e9cf` |
| B → A | 5 | 78699222089022 | `7575262a49035ee5c080c06306b279605d0c22879daadda20e62c1d5caa0cc38` |

A remains gold 1000 / troops 1 / wounded 0; B remains gold 920 / troops 5 / wounded 0, matching on both nodes after both AI windows. After B → A simulation there are 1475 active parties on both nodes, with identical party identity and roster/XP/gold hashes and zero unregistered active parties. This establishes observed replicated creation, not exhaustive lifetime coverage.

Allocation counters require a precise qualification: **both frozen checkpoint baselines have identical complete per-type counters** and identical nextHandle (45400, then 45401). During resumed simulation the per-type counter hashes differ, while nextHandle remains equal (45401, then 45593). `AddExistingCore` also invokes `GetUniqueTypeId`; these counters reflect local registration/allocation call high-water marks. The existing takeover import applies per-type `max` floors before activation. This run neither proves that every resumed-counter difference comes from a particular temporary object nor claims that daily counters must be identical. It does not demonstrate an ID conflict, but further multi-round creation/retirement testing remains required.

Responsiveness remains a separate unresolved acceptance item. Native local target adoption observations range approximately 0.0422–3.4146 ms; this is **not** end-to-end latency. Under the 50 ms profile one B-command remote visual-state first-match interval is about **5159–5239 ms**. Preserve and investigate it with CommandId correlation and a visual sequence; do not declare near-zero responsiveness or treat first subsequent motion as completed turning. The original map movement contract explicitly leaves these results unaccepted.

Two final map screenshots were visually inspected. They show normal map rendering and separate local player HUDs, but static screenshots cannot establish continuous animation or absence of transient visual artifacts.

## Automated results and exact baselines

Managed production regression ran on `b02a8c8c32b78764dbd48c41975bd09a8c8268f9`; the only following runtime-source commit is the Node acceptance change `dc1e4e84`, with no C# changes. Final `dc1e4e84` mod build passed with `-p:ModName=` before locked deployment.

| Suite | PASS | FAIL | Baseline result |
| --- | ---: | ---: | --- |
| GameInterface | 3457 | 0 | No failure |
| Common | 445 | 0 | No failure |
| Coop | 1581 | 195 | Exact historical 195 failing test names unchanged; raw exit 1 retained |
| Designated E2E | 61 | 10 | Exact historical 10 names unchanged; raw exit 1 retained |
| Separate HeroRelation E2E | 6 | 1 | Exact original failure unchanged |
| Original AI plus new role E2E | 3 | 5 | No added failure; one investigated old fixture failure removed, new role test passes |
| Final related Node suites | 101 | 0 | No skipped/cancelled/todo cases |

Original AI baseline is 1 PASS / 6 FAIL for its original seven cases. The current eight cases are 3 PASS / 5 FAIL: original lifetime assertions now pump the existing game-thread apply queue before inspecting the result, fixing one queue-fixture failure; the new role case passes. The old failing raw TRX and messages remain preserved. This is **not** an all-green solution claim, and automatic E2E does not replace the native run.

The upgraded gate reused a verified 73/73 quick-tool run in 160 ms. Read-only native triage took 1562 ms with sampled heap high-water 25,192,192 bytes, confirmed complete archives and rechecked original file hashes. Native startup, movement, both handoffs, screenshots, final logs and cleanup took about 168 seconds total.

## Commits and reproducible evidence

Functional commits integrated this round:

- `0fc74fc2e` / `d9adc7faa`: original Army callback and Cohesion role adapter.
- `d3c031405` / `12c2c999a`: finite AI continuation and rejection/baseline diagnostics.
- `b02a8c8c3`: native lifecycle and Settlement result responsibilities.
- `dc1e4e84f`: native map AI execution coverage.
- `e43187d9b` / `e01594b58`: common architecture contract and corrected provenance/bootstrap rationale.

The full 54-file change manifest is retained in the compact acceptance evidence; `git diff --name-only 099580ac9e9504d9acf0e57a1d1c19feb6efc35b dc1e4e84f01a728903055f805e8601bd2fd16fca` reproduces it. No raw game log, binary or local installation path is added to Git.

Local immutable native originals and hashes:

| Evidence | Repository-relative path | SHA256 |
| --- | --- | --- |
| Raw calls/events | `.mcp-local/p2p-modify-map-06.jsonl` | `c3b782c0651792473877bd55918c68b01721dd9f5e4a55db8df2c7462e841907` |
| A archived native log | `.mcp-local/p2p-modify-map-06.jsonl.server.log` | `c02137a51b5748a3cfc4a6ad0e800f55977bb159433c2d22d4330731b34fb592` |
| B archived native log | `.mcp-local/p2p-modify-map-06.jsonl.client1.log` | `b1706111b7049d9d47add157f73dbd0e2614857b4c38dd14dd5c2842aa3cc4b6` |
| Compact accepted observations, full source-file manifest and screenshot hashes | `.git/codex-coordination/evidence/modify-map06-triage01/compact-acceptance.json` | `9b1600c9fbe8dd34b21494864e5a70979cbeabc730682b3935753662cef156dd` |

Native summary is `.mcp-local/p2p-modify-map-06.jsonl.summary.json`. Streamed triage and input before/after hashes are in `.git/codex-coordination/evidence/modify-map06-triage01/`. Full managed suite TRX, raw exits and per-name comparisons are in `.git/codex-coordination/evidence/modify-integrated-20261020/regression08/closing-regression.json`. Final related Node/build hashes are in `.git/codex-coordination/evidence/modify-map-ai-coverage-m9/root-integration01/report.json`.

Independent source reviews approved bounded Army, finite AI continuation, lifecycle roles and map AI evidence changes. Final independent native review is PASS_BOUNDED_MAP_A_B_A, archived at `.git/codex-coordination/evidence/modify-role-ai-review-m9/map06-final-native-review.md`, SHA256 `d4175b24a8dbfc6e978a9e064e944aa7c76c1e4e52d1d1228b45f20452ad8e76`. Submitted code and automatic task status are not substitutes for the bounded native verdict above.

## Next priority

1. Run real Mission-triggered authority transfer and all-away pause/resume using these same role/continuation contracts; preserve old Mission FAILs.
2. Verify a full natural campaign day, wages/economy and native hourly/daily AI scheduling across migration, including Army/PartyBase retirement and repeated allocation floors. Existing explicit private-behavior continuity gaps remain: Aging `_heroesYoungerThanHeroComesOfAge` / `_extraLivesContainer` and Caravans `_caravanLastHomeTownVisitTime`. The finite per-party AI section and scheduler counters do not certify those states; evaluate existing save/checkpoint adapters before any broader change.
3. Diagnose remote visual delay with end-to-end command identity, then real stop/turn continuity and independent machines. Do not rewrite the mature prediction core before obtaining the causal evidence.
4. Exercise interrupted handoff and actual network faults. Incomplete checkpoint state must keep safe freezing; lossless recovery remains unaccepted.
