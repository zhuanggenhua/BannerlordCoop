# Native Mission and campaign pause acceptance, 2026-10-19

Status: **NOT_RUN on the new integrated version**. This worker prepares a repeatable
scenario; Node tests and a compiled diagnostic command do not accept native gameplay.
The coordinator owns deployment, the physical-installation lock and the two game processes.

## Reused implementation and bounded changes

The original location command opens Danustica (`town_ES1`) through
`ISettlementInterface.StartSettlementEncounter`, then
`PlayerEncounter.LocationEncounter.CreateAndOpenMissionController` for
`Location_town_ES1_tavern`. Its leave command calls `Mission.Current.EndMission()`.
Automatic campaign simulation availability already transfers the confirmed lease
when a local player enters or returns from Mission. The earlier Mission Run01/02
acceptance recorded this at its original build; that does not accept a later build.

No Mission coordinator, Owner/Puppet, spawning, movement, damage, transport,
registry, time advancement or checkpoint code changes here. The new scenario uses
those existing commands and the existing native screenshot/read-log APIs. It does
not force availability overrides, manually advance campaign time, register new
periodic listeners, or create an extra server/local loopback client.

The one production-source edit is an additive DEBUG diagnostic field on
`CampaignAuthorityDebugCommands.StateCommand`: `mission` is null outside Mission,
otherwise contains native `CurrentTime` as `time`, `MissionEnded` as `ended`,
`IsMissionEnding` as `ending`, `SceneName` as `scene` and `Mode` as `mode`.

The mandatory four-point change review is:

1. Existing `state` reports lease, actor, campaign time/ticks and scheduler state;
   screenshots report native engine frames, but neither proves that B's Mission
   logical clock continues while the campaign is paused.
2. A read-only diagnostic adapter is sufficient. The added sampling is exactly that
   adapter, with no change to native Mission behavior or its owners.
3. It must preserve every existing state field, not advance `Mission.CurrentTime`,
   replace a Mission, change its mode, set pause, or move objects. Sampling is on the
   existing game-thread command path.
4. Validate null-Mission JSON, existing command tests, offline negative lifecycle
   tests and, on the integrated build, increasing native Mission time and engine
   frames for the same PID while world time/ticks remain fixed.

## Scenario and exact evidence

Run `tools/mcp/scenarios/p2p-mission-lifecycle.mjs --help` for coordinator arguments.
Use a new `--record` path for each attempt and a direct-IP ListenAuthority profile.
Run the entire scenario under `tools/coordination/with-game-lock.mjs`; no worker
deploys or launches the game. The scenario has five independently recorded stages:

1. Join A/B, record OS PIDs, loaded assembly MVIDs/build versions, identity and
   single registrations, paused critical world hashes and allocation floor.
2. A enters the actual tavern Mission; B automatically acquires the world lease.
   A's authoritative `worldTicks` must stop, B's ticks and hours must grow. Compare
   the critical world after pausing the native clock without yielding authority.
3. B enters the actual tavern Mission. A/B must agree on a paused lease with zero
   local writers. Across the observation window, exact world hashes, handles,
   player resources, cycle dispatcher history, world ticks and hours remain fixed.
   Both native Mission logical times and screenshot engine frames must grow.
4. B returns to actual `MapState` while A remains in Mission. B resumes alone;
   A's world ticks stay fixed. Compare critical world/identities after convergence.
5. A returns to actual `MapState`, regains the preferred lease, and advances the
   world alone. Final A/B critical world, gold, counts/wounds/XP, volunteers,
   actor registrations and allocation checkpoint floors must agree.

The world comparison reuses the existing critical-state oracle. Noncritical shop
inventory is not added to it. Existing type allocation counters are not required
to be byte-identical across writer and replica; each must remain above the last
confirmed writer checkpoint, and shared object handles and critical world hashes
must agree. A Mission observation is not a full natural-day/AI-cycle acceptance;
the script records complete paginated dispatcher evidence but does not reinterpret
same-clock catch-up calls as automatic duplicate settlements.

All requests/responses retain unique call IDs, UTC timestamps and raw MCP data.
`native-log-archive.mjs` preserves the exact live byte prefix when switching to the
complete archive and links pages to their raw call IDs, cursors and SHA256. No line
deduplication is used. Native error lines, failed RealTick, duplicate IDs and AI
batch errors fail acceptance. A failed scenario remains failed after cleanup.
Each stage records two BMP receipts and their SHA256; screenshot pixel sanity
does not assert semantic correctness. A reviewer must inspect the actual images.
Successful MCP commands and advancing engine/Mission clocks establish control and
simulation liveness only. This direct-IP scenario does not measure fresh game P2P
messages during the all-away interval; that requirement remains NOT_OBSERVED until
separate transport or Mission message evidence is captured. It must not be inferred
from stable lease fields or registered-player counts.

`stop_run` runs in `finally`; both process trees must be confirmed exited before
the outer installation lock can be released. Archive or cleanup failure is part
of the final failure, not an ignored diagnostic warning. Prior raw evidence files
are never overwritten.

## Sale20 investigation and reproduction boundary

Original run `43e85189f6fb4f7db1d03e0acd8d671b`, source
`159ccb38915678d9f970fe430e10041a7f4cdbda`, B PID **20204**:
at 15:41:44 its log records native tavern Mission entry; at 15:41:45 the leave
command fails with `NullReferenceException` at `UsableMachine.OnMissionEnded`,
`Mission.set_MissionEnded`, `Mission.EndMission_Patch1`, then
`LocationDebugCommand.LeaveLocationCoopCommand.ProcessCommand`.
Location companion spawn result records follow that error. This temporal ordering
does not identify the null engine object or prove the lifecycle root cause.

The old preparation routine reopened a tavern for a player already in the town,
then immediately left after `readyForMissionTests`. That ready flag only requires
the campaign-test readiness and a non-null Mission pointer. It does not establish
that an actual Mission is running or that teardown is safe. Avoiding that unnecessary
preparation path did not repair Mission core behavior.

The new actual Mission test opens once, except for a separate second phase allowed
only by the exact `Started a local encounter with 'town_ES1'.` response. An uncertain
open result is never retried. It requires actual `MissionState`, stable native BMPs
across engine frames and increasing logical Mission time, not a longer timeout or
sleep as a substitute for readiness. Exit is requested once, then it waits for
actual `MapState`; it never assumes a cleared Mission pointer means exit completed.
Even these observations do not prove that every engine teardown dependency is
ready. Any repeat NRE must fail, retain both logs/state/screenshots and stop further
Mission expansion pending a grounded minimal reproduction and engine analysis.

Historical sidecar SHA256 (unchanged, including its old archive-copy duplication):

- Sale20 client log: `c1930e63bba3f9900b12a27499c18cc8212d978dbf15980e4d15f2c1bb98cf8a`.
- Sale20 summary: `e3a548534adc5cb803ec62e07689d44cb51b10cee7ec0f71539e272d190a5837`.

The raw failure remains a failure. This change does not claim to fix
`UsableMachine.OnMissionEnded` or accept battle Mission lifecycle/results.
