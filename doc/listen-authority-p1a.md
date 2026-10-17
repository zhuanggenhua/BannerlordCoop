# P1-A: single-process listen authority

This opt-in milestone keeps one native Campaign and its native MainHero/MainParty in the authoritative player's process. It does not implement a second local client, remote-player admission, authority migration, or mission replication changes.

## Starting a session

Use a matching DEBUG deployment. Add `/listenhost` to the game launch arguments. `/autoconnect` starts a new native campaign; `/coopsave <exact_save_basename>` loads a native campaign instead. DEBUG automated runs use `/platformId LocalA` to give the local actor a stable test identity. Normal launches without that override use the native platform identity.

For example, from the game executable directory:

```text
Bannerlord.exe /singleplayer /listenhost /autoconnect /platformId LocalA _MODULES_*Native*SandBoxCore*SandBox*StoryMode*Coop*_MODULES_
```

The repository MCP launch profile has a new `listenAuthority: true` option. Use `start_run` with `client_count: 0`. Its `server` instance is the playable authority, not an additional dedicated process. Always finish with `stop_run` and confirm both process and process tree cleanup.

The DEBUG new-character fast path still creates a native campaign character. Its culture lookup now uses `empire`, not the localized display name. Release runtime and manual character customization have not been validated by this milestone.

## Composition and identities

- `SessionMode.ListenAuthority` retains the server synchronization policy and the existing authority/network services. `ClientModule` is not loaded into the authority.
- `SessionIdentity` supplies separate room and authority identifiers. The local actor has a separate controller identity through `IControllerIdProvider`; it is not `Server` and has no loopback NetPeer.
- `LocalPlayerContext.RegisterNativePlayer` binds the already-registered native hero, party, clan and character. Existing matching registrations are reused; conflicting registrations fail instead of creating a second actor.
- Local presence participates in unpause/offline policies without manufacturing a peer. Dedicated cleanup, player removal, stale-menu clearing and forced visibility do not apply to listen authority.
- The native map, input, camera and menu states remain in the same process. `GameUIDisable` must allow listen authority to push native player screens.
- Already-applied movement publishes the authoritative result. It does not enter the remote-client request handler. Inventory preview/commit continues to use the existing transaction path; no second local client handler is installed.

Listen authority uses native menu/mission scheduling rather than the existing forced background map driver. No new world timer was added. Continuing the world while the authority is in a Mission remains outside this milestone.

`CampaignRobustnessPatches.Finalizer_RealTick` logs and preserves the original exception in ListenAuthority, including Release builds. Dedicated recovery retains its existing behavior. A failed native simulation is not silently swallowed; regression tests cover both policies.

## Verification on 2026-10-18, game 1.4.8.119303

Local evidence is retained under `artifacts/p1a/` and `.mcp-local/runs/`; these are ignored artifacts, not distributable game files. The live binaries were built before the feature commit, so their build version records the earlier session-mode commit. Run records contain the actual loaded assembly MVIDs. Do not infer the tested source solely from the version string.

Observed in real Bannerlord processes:

- One Bannerlord.exe per run, zero remote clients, no secondary server/client game process.
- Native new campaign reaches MapState after correcting the DEBUG localization failure.
- Native inventory opens, its real Confirm button closes it, and native ClanState opens and closes using its real Done button.
- The real map-click handler accepts the local player's destination; movement reaches the requested point with authority patches active.
- The local party travels normally to Danustica (`town_ES1`) and enters the native `town` menu.
- `P1A_ListenAuthority_Test` writes native save plus session sidecar; cold reload retains the actor, position and time, with exactly one local registration and no peer.
- A separate native-new-world run writes `P1A_Native_Town_Test` from inside the town. Cold reload restores `town`, the exact position and campaign time, one registered/connected local actor, and no peer. `town-reload-state.json`, `town-reload-ready.json` and `town-reloaded.png` record this check.
- In the longer map run, campaign time advances from 2185856.99908978 to 2185894.48504067 hours (37.486 hours). The AI batch counter rises from 100 to 111900. This is a batch invocation counter, not an assertion about every NPC decision.
- Sampled native new-world actor counts are one hero/party/clan registration each. Rendered map captures have normal fog and no observed nameplate trails. Only 2–6 of more than 1100 parties are visible in the samples.
- Sampled `duplicateRealTicks` and `failedRealTicks` remain zero. The diagnostics do not drive ticks. Absence of duplicate/failing ticks is bounded to these observed runs, not every possible scene or mod combination.

The failed new-game run is retained too. Its local crash dump shows `Enumerable.First -> DebugCharacterCreationInterface.SkipCharacterCreationInternal -> CharacterCreationIntroPatch.OnActivate`: the old code searched for English `Empire` in a Chinese game. The ID-based lookup and localized-culture regression tests address that failure; it is not evidence of an engine restriction on listen authority.

After the final exception-policy change, deployed-build run `5ba29f6593824e4ea38e84a8d8182898` (PID 8728) cold-loads the town save, opens/closes native inventory, and returns to the rendered town map. It records 1999 Campaign ticks / 1999 RealTicks, zero duplicate/failing RealTicks, one local actor graph and no peer. `final-processes.json` records exactly one Bannerlord.exe; `final-run.json` records loaded GameInterface MVID `1732d12a-a607-4090-ae6c-c5bd248f0391`. `final-cleanup.json` confirms process and tree termination. All owned live-test runs have been stopped.

The five-axis self-review checked native lifetime and error propagation, mode composition, bounded DEBUG automation, no new local network loop, and the absence of a second tick driver. No blocking issue remains within the observed P1-A scope. Release/manual character creation, all possible menu transactions and Mission background simulation are not validated by these checks.

## Automated results and baseline limitations

| Check | Result |
| --- | --- |
| Fresh development GameInterface baseline | 2547 passed |
| Final GameInterface suite | 2563 passed |
| Common suite | 396 passed |
| Coop suite | 1270 passed, 195 failed |
| MCP launcher tests | 7 passed |
| DEBUG mod build | Passed, existing warnings remain |

The 195 Coop failures match the failure-name set in the existing local baseline report exactly. This comparison is in `core-baseline-comparison.json`; it is not a claim that the entire suite passes. One recurring baseline problem is the test container missing `ICoopCommandArgsFactory` while activating `AgentHitSoundFixtureHandler`. The new isolated listen startup test passes.

There are 21 new automated cases: 2 Common, 1 Coop startup, 16 GameInterface and 2 launcher cases. The MCP result above is the focused launcher suite, not a rerun of every MCP-server test.

The default restore also encounters existing Scriban vulnerability advisories promoted to errors. Local comparison builds use `NuGetAudit=false`; that flag does not remediate the dependency advisories. No dependency upgrade is part of P1-A. A diagnostic-command DI regression encountered during implementation was corrected before the final passing GameInterface run.

## P1-B work still required

1. Implement and verify remote actor ownership checks, save transfer, queue barriers and B's local-player switch while preserving A. P1-A deliberately rejects remote admissions.
2. Resolve pre-registration ID diagnostics and runtime `TownMarketData` network-handle errors before claiming complete world replication. A running locally does not prove B receives all changes.
3. Audit enabled local UI operations and remote command paths for correct actor attribution, non-idempotent command execution, prediction and replay suppression. Movement coverage does not prove every transaction.
4. Add the two-process A/B live profile and state comparisons. Steam discovery is not required for the first direct-IP test.
5. Investigate background Mission scheduling, including the map-screen late-AI task and shared encounter state. No authority migration or thousand-agent battle synchronization change is included here.

## Changed files relative to development

```text
AGENTS.md
doc/listen-authority-p1a.md
source/Common.Tests/ListenAuthorityModeTests.cs
source/Common/ModInformation.cs
source/Common/SessionMode.cs
source/Coop.Core/Common/CommonModule.cs
source/Coop.Core/CoopartiveMultiplayerExperience.cs
source/Coop.Core/Server/CoopServer.cs
source/Coop.Core/Server/ServerLogic.cs
source/Coop.Core/Server/ServerModule.cs
source/Coop.Core/Server/States/InitialServerState.cs
source/Coop.Core/Server/States/ServerContext.cs
source/Coop.Tests/Server/States/ListenAuthorityStartupTests.cs
source/Coop/CoopMod.cs
source/Coop/LiveTesting/LiveTestControlServer.cs
source/GameInterface.Tests/Services/MobileParties/MobilePartyAIDisablePatchesTests.cs
source/GameInterface.Tests/Services/Players/ListenAuthorityCharacterCreationTests.cs
source/GameInterface.Tests/Services/Players/ListenAuthorityOperationTests.cs
source/GameInterface.Tests/Services/Players/ListenAuthorityPlayerTests.cs
source/GameInterface/GameInterfaceModule.cs
source/GameInterface/Services/CampaignService/Patches/CampaignRobustnessPatches.cs
source/GameInterface/Services/GameDebug/Commands/UiDebugCommands.cs
source/GameInterface/Services/GameDebug/Interfaces/DebugCharacterCreationInterface.cs
source/GameInterface/Services/GameState/Patches/HostSettlementMenuLoadPatch.cs
source/GameInterface/Services/MobileParties/Extensions/MobilePartyExtensions.cs
source/GameInterface/Services/MobileParties/Patches/PartyVisibilityServerPatches.cs
source/GameInterface/Services/MobilePartyAIs/PartyAiBatchRunner.cs
source/GameInterface/Services/Party/Commands/PartyCommands.cs
source/GameInterface/Services/Players/ListenAuthorityMenuCommand.cs
source/GameInterface/Services/Players/LocalPlayerContext.cs
source/GameInterface/Services/Players/PlayerManager.cs
source/GameInterface/Services/Time/ListenAuthorityTickEvidence.cs
source/GameInterface/Services/Time/Patches/GameStateManagerPatches.cs
source/GameInterface/Services/UI/Patches/GameUIDisable.cs
tools/CoopMcpServer.Tests/InGameProcessLauncherTests.cs
tools/CoopMcpServer/CoopMcpServerSettings.cs
tools/CoopMcpServer/InGameProcessLauncher.cs
```
