# P2P diagnostic follow-up, 2026-10-19

Scope: QA log collection and read-only investigation. No game synchronization,
registry, AutoSync, Mission Owner/Puppet, engine lifecycle or transport code changed.
The source baseline for this worker is `ef2f52405874a0802a3985bc43fd401e2248c1b2`.

## Archive pagination repair

Sale28 (`6f9bb97633c549f08472f79af603cde5`) demonstrates a collector bug,
not eight native sale applications: four native application lines each occur once
in their archived file and twice in the old sidecar. Only the first reset page was
filtered; subsequent archive pages were appended to the existing live text.
The immutable `sale28-final-log-audit.json` retains call IDs, byte offsets and hashes.

`tools/mcp/scenarios/native-log-archive.mjs` now buffers a complete read transaction.
A reset is accepted only during archive collection, on its first page. The complete
archive must preserve the exact UTF-8 bytes of the prior live prefix, then it replaces
the collected text. A shorter/different prefix, live reset, second reset or exhausted
page budget fails and preserves the previously accepted text/cursor. Identical lines
within a source are retained, including identical error and application emissions.
The Sale and UDP collectors reuse this adapter and the original `read_logs` interface.
The Sale scenario also checks the final archived application ledger against all
submitted intents, so a late native duplicate cannot pass an earlier live-only check.

Each collection receipt records run/instance/raw JSONL association, page call IDs,
requested/returned cursors and their SHA256, text SHA256, byte lengths and timestamps.
The original request/response JSONL is unchanged; no old logs or failures are rewritten.
Failed receipts retain the page metadata and exception. This is a QA source adapter,
not a replacement for MCP's cursor reader or the game's logger.

Ten adapter tests cover single/multi-page copies, Unicode byte lengths, actual
repeated records, new archive tails, mismatching/short prefixes, source changes,
incomplete captures, concurrent independent sources and raw-call provenance.
All scenario Node tests: 70 PASS, 0 FAIL in the final worker run. This is offline
validation; native scenarios must be rerun by the coordinator after integration.
An intermediate editing mistake changed JavaScript newline escapes, causing four
failures; that raw log is retained. The escapes and two accidentally changed Serilog
regexes were restored, with no assertion relaxed. The final reviewed test log is
separate from both earlier logs.

## Active11 missing TroopRoster handle: unresolved

Run `378d354941f44f5d8ae369dbb965f283`, game source
`bff7aec55077f69250e313b58572895a35853964`, B PID 2852.
The archived `client1.log` contains **one** error at line 630, 12:56:42:
`Failed to get network handle for object of type ...TroopRoster`.
The old merged sidecar repeats that line twice because it includes the archive copy.
The native error remains real and blocks that run; archive duplication does not excuse it.

Adjacent rows show network-created rosters and parties, but the original error has
neither the failing object's identity nor the caller stack. This does not establish
which roster, creation path or ordering caused the miss. In particular, it cannot
prove that a nearby `Created_4420` roster is the missing object. Current
`ObjectManager.TryGetHandleWithLogging` has DEBUG identity/stack diagnostics; Active21
did not reproduce the error. No root-cause repair is claimed.

Next evidence needed is a new failing object's identity/owner, caller stack and
registration lifetime under the same fault scenario. Do not add a silent registration
fallback or suppress the error without that evidence. `TroopRosterRegistry` already
registers party-owned, dummy and loaded rosters through distinct existing paths.

## Candidate19 visual type counter: observed, cause unproven

Run `a76dabbbfa01454eb4d8236a61755865`, game source `159ccb38`,
A/B PID 13292/22048. The retained independent audit confirms that the reconnect
intermediate snapshot differs only in `MobilePartyVisual` type counter (2902/3421)
and its derived counter hash. Both `nextHandle` values are 45029; other recorded
critical state matches. The final paired snapshot is equal again, with `nextHandle`
45030 after the explicit roster probe. This is not evidence of a handle collision,
and is not proof of exact allocator equality at every phase.

Source constraints explaining why a counter hash alone is insufficient:
`AutoRegistryHandler.Handle_InstanceCreated` reserves a type ordinal before
`ObjectManager.AddExistingCore`, which increments the type counter again; receiver
creation uses `AddExisting` without that first reservation. `GetUniqueTypeId` counts
allocations, not current live registrations. `TryApplyAllocationCheckpoint` takes
`Math.Max` and never rewinds counters. `PartyVisualLifetimeHandler.TryRegisterNetworkVisual`
can retain a native player's already-registered visual and still reserve the ignored
network identity's allocation floor. These are verified code paths, **not a proven
causal trace for the Candidate19 519-counter difference**.

A reproduction must trace visual create/destroy/register/skip identities, epochs and
counter checkpoints on both processes. Preserve monotonic handles and existing
registration semantics; do not rewind counters or broaden critical world assertions
to local visual state merely to force equality. The old timeout inquiry is a separate
unaccepted UI finding, not evidence of a MainHero/MainParty switch.

## Sale20 Mission NullReference: exact failing path, unresolved lifecycle cause

Run `43e85189f6fb4f7db1d03e0acd8d671b`, game source `159ccb38`,
B PID 20204. Archived `client1.log` lines 349-359 show tavern Mission entry at
15:41:44 and DEBUG leave at 15:41:45, followed by:

`LeaveLocationCoopCommand.ProcessCommand` calls `Mission.Current.EndMission()`;
`Mission.EndMission_Patch1` calls `Mission.set_MissionEnded`, which reaches
`UsableMachine.OnMissionEnded` and throws `NullReferenceException`.

Read-only decompilation of the installed 1.4.8 `UsableMachine.OnMissionEnded`
shows enumeration of `StandingPoints`, access to each point's user and deactivation.
The stack has no source line/instance, so it cannot distinguish an uninitialized
collection, a missing point or a downstream accessor failure. Early exit during
scene initialization is plausible from the timeline, **not confirmed**.
Engine `TaleWorlds.MountAndBlade.dll` SHA256:
`19387f31557ff840d14f378f6bbdf1d58fcff406ab9ff2294dfbb3e49a50b87e`.

Later Sale preparation avoids entering a second tavern Mission when B already has
the valid town menu; that removes unnecessary QA setup, not the Mission defect.
Real Mission acceptance must record actual scene readiness/initialization before exit,
normal and immediate-exit variants, availability/lease transitions and full native
exceptions. Do not catch-and-ignore engine `EndMission` failures or call this fixed
because a map-only sale path passes.

## Retained source evidence hashes

| Evidence | SHA256 |
| --- | --- |
| Active11 native archived client log | `86fee66f9928c011cf5392b2f7377acb4a281dbfeca9d047eca187b0d5147664` |
| Sale20 native archived client log | `b369388d71b5a2f2580b2eaf6a0ad6c16c320ea395410275f1a03461e85d3500` |
| Candidate19 summary | `5db7c57af150c55b0b30183df0591e5d448151481d4fcde95841c4a4efa548ab` |
| Sale28 raw JSONL | `6b69691d68cadeecc1240f1bbbd82bf3feb59c03e1cdbc4c3dca39995a789922` |

The coordinator keeps these originals under `.mcp-local` and the audits under
`.git/codex-coordination/evidence/p2p-reliability-20261019`. This document adds
interpretation without modifying those files. None of these three old incidents is
marked root-cause-fixed or newly native-accepted by the offline work.
