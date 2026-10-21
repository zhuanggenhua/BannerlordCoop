# Player navigation versus autonomous initiative, M15

Source baseline e2689840ae62f9f3200b6f3497ad042c1273017a. Mission02 remains FAIL; no first MoveTargetPoint write was captured. This task proves a role inconsistency, not yet the actual first cause of that runtime coordinate mismatch.

## Four core-change reasons, recorded before production edit

1. Existing CanTickParty allows the current writer to tick all parties and observers to tick their registered local player navigation. This is necessary pathfinding/encounter work. Original SkipPlayerPartyAiThinkPatch only suppresses hourly AiPartyThinkBehavior.PartyHourlyAiTick for registered players. The native short-term GetBehaviors path separately invokes DefaultMobilePartyAIModel.ShouldPartyCheckInitiativeBehavior and can choose autonomous flee/engage.
2. Actual native model IL has a direct party==MobileParty.MainParty exemption; registered remote B is not native MainParty on A. The same controlled party therefore has different autonomous initiative eligibility by process, even under an identical confirmed world lease. Extending the existing model adapter with the original registered-player identity solves that permission mismatch. There is no need to rewrite movement/prediction or disable the full player AI/navigation tick.
3. Preserve original NPC eligibility, native MainParty rule, local navigation, player input, existing hourly exclusion, CampaignTick/Epoch/freeze/replay policy and finite continuation. Registered identity must use the mature PlayerManager table, independent of which peer currently owns Campaign; removal must restore the native NPC eligibility. No snapping, synthetic targets, stronger position assertion tolerance or business algorithm replacement.
4. Required minimal RED/GREEN: real native DefaultMobilePartyAIModel method with actual Campaign.MainParty references shows remote B incorrectly eligible; registered B must stay ineligible across A/B local roles and A-to-B-to-A lease changes; original MainParty/unregistered NPC results remain; real PlayerManager registration/removal consistent. Phase/freeze/paused checks must not widen CanTickParty or CanRunWorldSimulation. Final native rerun needs finite diagnostic of initiative selection plus actual navigation/output timeline; a model permission test cannot alone prove Mission02 coordinate cause.

## Native provenance and remaining uncertainty

Campaign DLL SHA256 must match 1f8e33e2ed73e6ec653d7629180afb70649ddc6e5bd1657a802a264efda1c3ae. Existing archived native-MobilePartyAi.GetBehaviors at483 reaches the model initiative method; TickInternal then SetAiBehavior. Actual DLL reflection token06002A32 resolves MobileParty.get_MainParty in ShouldPartyCheckInitiativeBehavior. Root provided isolated fixed ILSpy11.1.0.9782; this worker reads the same actual game DLL and records the native model source below artifacts. No tool installation or game launch by this worker.

Mission02 B is Hold with origin target/move after Epoch9, later still Hold but changed MoveTargetPoint and unequal positions. Autonomous initiative could create a transient move target before Hold returns, but the original record does not capture that first write. Native SetNavigationModeHold retains MoveTargetPoint while resetting PartyMoveMode/next target/path, so a final Hold label does not prove there was never a transient movement branch. Other input/replay/cache writes must remain alternative hypotheses until bounded diagnostics/native rerun establish the first cause.

## Confirmed dynamic-session boundary before implementation

Do not change legacy Dedicated behavior without evidence. The new restriction requires the native result to be true, the mature PlayerManager marker, an exact confirmed Context/SessionIdentity lease match, and consistent PlayerManager/Registry object-controller identity. RoomHost must itself resolve to a registered controlled player party, the existing Listen session identity feature absent from a dedicated host. IsDedicatedAuthority remains unchanged; RemoteReplica alone never identifies a dynamic Listen session. Missing/contradictory registrations or unknown session retain the original getter result. This denies autonomous player initiative only; it grants no world-write/navigation rights.

Initial native-library RED before production edits: 11 cases, 2 PASS / 9 FAIL, exit1. Same remote player remained native-eligible; original NPC and MainParty behavior passed. Raw red01 log/TRX retained. Repository-wide searches found no normal PlayerManager creation/registration assignment of DoNotMakeNewDecisions; specialized conversation/fixture protection does not prove an equivalent legacy Dedicated invariant. Therefore original Dedicated semantics are explicitly retained.

## Implemented minimal adapter

CampaignSimulationAuthorityPolicy.ShouldSuppressPlayerInitiative centralizes the confirmed-session/registered-player qualification above. The existing DefaultMobilePartyAIModelPatches postfix preserves the entire original getter and only changes a qualified true result to false. Original getter exceptions still propagate. CanTickParty, CanRunWorldSimulation, native initiative selection, navigation, prediction, object Registry and AI continuation are unchanged.

The DEBUG-only diagnostic emits [PlayerInitiativeQualification] once per observed party/lease, including party/controller, native MainParty, room/authority/writer/Epoch, paused state and execution phase. Its weak-key observation cache grants no permissions and makes no game-state writes. It demonstrates that this qualification was reached; it does not identify the first MoveTargetPoint writer. The next real run must correlate this record with the original movement timeline and leave the original Mission02 FAIL intact.

## Verification and acceptance limits

All commands use GameInterface.Tests in Debug with -p:ModName= -p:NuGetAudit=false. The final related filter covers PlayerInitiativeQualificationTests, CampaignPartyAiRoutingTests, CampaignSimulationEvidenceTests, CampaignWorldExecutionTests, NativeLifecycleRoleTests, PartyAiBatchRunnerTests, PlayerManagerTests and CampaignAiContinuationTests.

| Evidence | Actual outcome |
| --- | --- |
| red01.log / red01.trx, before production edits | 2 PASS / 9 FAIL, exit 1; original native role inconsistency reproduced |
| green02.log | compilation FAIL, exit 1; test qualification corrected to global::GameInterface; raw failure retained |
| green03.log / green03.trx | 16 PASS / 0 FAIL, exit 0 |
| related04.log / related04.trx | 106 PASS / 0 FAIL, exit 0; includes 18 new cases |
| related05.log / related05.trx, final C# source and BOM/CRLF | 106 PASS / 0 FAIL, exit 0 |

New cases exercise the actual native model getter rather than a rewritten model: real MainParty versus registered remote B, registration/removal/rejoin, NPC preservation, Dedicated preservation, missing/contradictory object graph, unknown session, A-to-B-to-A, paused/frozen/Replay/Bootstrap and AllowedThread. The latter cases verify that suppressing initiative does not grant world simulation or navigation. The null case preserves the native exception.

This worker did not launch/deploy a game: final candidate native acceptance is NOT_RUN. No full-suite green claim is made; the historical Coop/E2E failures remain the coordinator's separately compared baseline. The original Mission02 raw SHA256 and selected before/after state are retained in runtime-localization.json. The proven issue is inconsistent native player initiative eligibility across peers; attribution of the actual persistent coordinate divergence awaits the final integrated real two-process rerun. Movement, Mission and full natural-day acceptance are not granted by these managed results.
