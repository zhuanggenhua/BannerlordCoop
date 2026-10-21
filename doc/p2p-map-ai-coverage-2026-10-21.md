# Map handoff AI execution coverage

2026-10-21. Bounded tool-only follow-up to the finite AI continuation adapter.

1. Existing map acceptance checks exact frozen positions, actor/resources/IDs, one
   writer Tick and clock progress. A few frames satisfy progress and can miss the
   later original AI failure observed in map05. Existing native cycles already expose
   completed/failed/skipped role partitions; no C# probe or AI changes are needed.
2. Extend the map caller through that existing read-only probe, not a new runner.
   The actual frozen shape has movement.count and complete active-party rows. Its
   npcSample is bounded/filtered and cannot count all NPCs. Use twice movement.count
   as a conservative writerNpc completed-call budget, including players in that bound.
3. Preserve original Tick/clock, critical-world, actor, frozen-position and ERR checks,
   poll deadlines and default AI assertion positivity. Only an explicit baseline/idle
   observer option allows zero observed calls after complete schema validation.
4. Test positive/negative count deltas, partitions, missing/invalid evidence, resets,
   actor/lease transitions, failures, observer writes and the actual whole A-B-A caller.
   Offline rehearsals do not accept native AI or replace integrated dual-process runs.

Capture raw cycles before Play_1x after confirmed handoff. Bracket cycle reads with
actor/lease snapshots. Counters are cumulative, not epoch buckets; historical
firstEntries are not claimed as current-epoch call evidence. Require writerNpc.completed
delta at least 2*activePartyCount, no authoritative NPC/player completion on observer,
no new failures/unsafe role execution, no rollback. Legal local navigation is allowed,
not required. This is an execution-call volume bound, not each unique NPC, natural
decision cycle, complete scheduler coverage, natural day, economy or 24-hour proof.

The probe's simulation/AI counters are complete even on cycles page zero; paginated
hourly/daily rows are retained as raw context, not claimed as complete day evidence.
Before/after union includes newly appearing *.failed keys, so native batch/RealTick
failures cannot hide behind a missing baseline key. Safe observer unqualified.skipped
is allowed, never counted toward the writer's completed-call budget.

Validation: the old whole-caller fixture first failed because it did not supply the
new mandatory cycles probe (3 PASS/1 FAIL, retained). The fixture now supplies existing
fields and three complete active-party rows including both actors. Map/world targeted
tests passed 36/0; final related Node suites passed 101/0 with no skip/cancel/todo.
The actual caller rehearsal proves both directions wait past an insufficient first
read and only pass at the six-call budget. It also retains emitted raw rejected
cycles for failures and checks cycles baselines precede each Play_1x. Original actor,
world Tick, clock, critical-state and frozen-position negatives remain in the suite.
No C# compilation, deployment, MCP connection or game launch was performed.
