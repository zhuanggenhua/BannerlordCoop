# Typed campaign lifetime completion

This is an implementation record, not a natural-day or dual-process acceptance claim. Baseline: a9a984a3ffb2afb6d468e2419424bb442ceebd42. Preserve the original Natural04 and Active22 failures.

## Four pre-change points

1. The original generic registries already allocate identities, publish InstanceDestroyed, perform typed server/client callbacks and send NetworkDestroyInstance. Army setters retain their native movement, membership, Kingdom and tracking effects. MobileParty owns its roster/PartyBase cleanup. MapEvent finalization owns contributions, battle callbacks, rewards and terminal graph cleanup.
2. Natural04 successfully removed Army160 inside the first DisperseInternal, before the outer native MobileParty.Army setter continued FinishArmyObjective and a second DisperseInternal. The same run removed garrison PartyBase32519 during FinalizeEventAux before subsequent original LeaderParty references. Receive ordering cannot resolve an already removed identity. A typed logical-completion adapter suffices; no generic registry, ObjectManager, AutoSync generator or transport rewrite is justified.
3. Invariants: preserve original operations, movement/rewards and exceptions; resolve the existing identity through the last ordered field write; retire exactly once without resurrection; preserve standalone Disperse's original AiBehaviorObject; only the current confirmed unfrozen lease may certify completion; pending/faulted scopes refuse capture/drain/ACK.
4. Automated coverage must exercise the actual installed native setter/removal/disperse/finalize paths, original generic destroy/wire pipeline, nested versus standalone completion, duplicate suppression, late ordinary postfix and subscribers, exact original exception, session/Epoch change, unknown identities and handoff refusal. Root must still run natural24h on both writers, original siege/Army cases, client terminal state and faulted handoff scenarios.

## Adapter

CampaignLifetimeCompletion is a session-container service. Its Army scope nests across MobileParty.Army, Army.OnRemovePartyInternal and Army.DisperseInternal. Only a successful terminal native disperse certifies a duplicate in the same still-current scope. The outermost Harmony finalizer runs after every ordinary postfix and publishes the unchanged InstanceDestroyed once. ArmyRegistry only removes its eager generic destroy binding. Constructors and typed cleanup remain intact. Standalone disperse does not acquire an invented null objective; native FinishArmyObjective still moves and clears the objective in the outer removal case, with the original field message before destroy.

The MapEvent scope captures an actual first native finalization's participant identities. MobilePartyRegistry defers only the already-registered PartyBase of a garrison in that exact graph. It keeps the original roster flush/destruction and all native local destruction. The MapEvent finalizer releases held PartyBases through the original handler only after the complete original FinalizeEventAux and its ordinary postfix/subscribers. This does not make generic graph destruction own PartyBase or manufacture missing identities. A thrown original/postfix, incomplete original, changed lease or failed release leaves a faulted session drain.

CampaignAsyncTaskDrain checks the completion service both before and after native WaitAsyncTasks. Every existing caller of that drain therefore refuses unresolved lifetime state. The root integrator must register CampaignLifetimeCompletionModule in GameInterfaceModule before deployment. The production service must not silently be absent; direct legacy test constructors retain optional arguments.

## Actual installed engine evidence

The DLL inspected is TaleWorlds.CampaignSystem.dll SHA256 1f8e33e2ed73e6ec653d7629180afb70649ddc6e5bd1657a802a264efda1c3ae. The earlier retained native-Army/native-MobileParty/native-MapEvent/native-MapEventSide decompilations are the source cited by the prior lifecycle audit. Fresh local MBCampaignEvent and DisbandArmyAction decompilations confirm DeletePeriodicEvent's flag and the original DisbandArmyAction forwarding calls. Neither an invented time jump nor a fake native-day count is used.

The controlled actual-engine tests run through the existing TestGameLoopPump and GameThread.Run, without retagging the game-thread singleton. Army tests leave the installed MobileParty.Army setter, OnRemovePartyInternal, DisbandArmyAction, DisperseInternal, FinishArmyObjective, Kingdom/AiBehaviorObject setters and periodic deletion intact. Fixture-only shims cover initiative/navigation rendering dependencies. The old eager hook reproduces two native OnArmyDispersed calls and a missing handle at the final objective write; the adapter produces one native disperse, one original movement call and one original destroy after the last resolvable field write.

The controlled actual FinalizeEventAux test runs the installed body through its final Clear(), the existing MapEventPatches postfix, ordinary subscribers, an additional last ordinary postfix, and the completion finalizer. Its nested HandleMapEventEnd participant-accounting dependency is explicitly shimmed; this test does not certify siege rewards, wages, economic accounting, real 24h or dual-process terminal equivalence. An injected ordinary-postfix exception remains the exact exception and holds the original identity.

Worker evidence is retained under artifacts/typed-lifetime-r3. Initial compile/fixture failures and the native-path03 fixture NRE/test-host abort are retained separately from later passes. The fixture's MainParty.Party was initialized; test-thread forwarding reports an unexpected exception without crashing the shared test pump. No production exception catch was added to silence these failures.

## Required integration/native checks

- Register the module once per session; verify the injected MobilePartyRegistry and CampaignAsyncTaskDrain reference the same service.
- Repeat the original Army160 and garrison32519 chain. Require the original final field messages before the single destroy and no missing-ID ERR.
- Compare A/B terminal Army objective/Kingdom/membership, transient LeaderParty references, world identities and absence of held PartyBase orphans.
- Inject an exception/pending scope during freeze and final barrier: safe refusal, never a successful ACK from unresolved state.
- Complete natural24h per writer and A→B→A with periodic events, real wages/economy/XP. These are NOT_RUN in worker tests and must not be marked passed from compilation or the controlled fixtures.

Known broader limits: existing MessageBroker logs and contains subscriber exceptions; this adapter does not rewrite that generic broker into a transaction. The native finalizer detects thrown original/ordinary-postfix errors and verifies actual final identity removal. Arbitrary asynchronous work spawned by unrelated subscribers is not certified by a synchronous method boundary; native task drain and the root's separate graph-readiness barriers remain required. Full lossless authority recovery remains outside this change.
