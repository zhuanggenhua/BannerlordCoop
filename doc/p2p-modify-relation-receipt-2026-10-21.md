# Scoped hero relation receive registration

Base `26d1422314564a96407dc0c6befe5d491f1835e1`. This fixes a native handoff blocker without changing relationship business logic.

1. Existing functionality: `CharacterRelationManagerHandler` sends scoped authority results after a confirmed lease and already has a scoped receive overload. Its shared apply resolves heroes on the game thread and calls the original absolute `SetHeroRelation` under AllowedThread; this preserves original values and avoids repeated RNG/model gains or echo.
2. Evidence / adapter: native map03's final ACK is correctly refused with `NetworkCampaignAuthorityHeroRelationChanged: No subscriber`. Commit `aefdc8c844` added the scoped receive overload but omitted constructor/Dispose wiring. Registering that existing overload is the minimal adapter; no relationship, AI, receipt or transport algorithm needs rewriting.
3. Invariants: retain exact-once native absolute application, no replay echo, game-thread lookup, full lease apply receipts, sticky failures, and safe freeze on failed application. A received sequence must not become a successful applied ACK without the actual native result.
4. Regression obligations: real MessageBroker.Publish of scoped messages must apply the native relationship, disposal must unregister, queued work must retain pending completion, missing heroes must fault rather than ACK, and receive must not publish another relation result. Original legacy relation E2E remains unchanged. Native map03 FAIL and its screenshots/logs remain evidence; a fresh coordinator-owned native run is required.

The two-line subscription fix exposed a second directly related receive failure: both missing-Hero tests reported Ready rather than Faulted. `IObjectManager.TryGetObjectWithLogging` only logs, so the shared apply now marks the existing receipt failed with the missing Hero ID before returning. The existing receive closure carries Replay provenance around the unchanged lookup and absolute native setter; AllowedThread remains around that setter. This does not clear failures or weaken the final ACK.

## Actual regression evidence

- Before the production change, real-broker/native-Hero receive tests fail: scoped direct result stays 0 rather than 37; queued receipt is Faulted (No subscriber), not Pending. Original map03 remains FAIL.
- Subscription-only intermediate: scoped inline and queued application and Dispose pass; both missing-Hero cases incorrectly become Ready. That failed run is retained separately.
- Final new receive cases: 6 passed / 0 failed, including legacy direct absolute -17, scoped absolute37, no echo, queued Pending before pump and Ready after actual application, Dispose/no subscriber, and missing either Hero fault with exact ID. These use a real `MessageBroker`, the original `CharacterRelationManager.SetHeroRelation` and the existing E2E Hero fixture.
- Existing GI authority tests: 5 passed / 0 failed.
- Full relation E2E class: 6 passed / 1 failed. The untouched original `Server_SetHeroRelation_SyncsToClients` still fails42 versus0 plus unpumped fixture actions. The coordinator separately reproduced that same original case alone in a clean base26d142 checkout (0 passed / 1 failed), TRX SHA256 `5b9fbf87646124f7505fb048d7257ab33e90a49fe1c3dc77973445373b10d4d0`. It is recorded as a separate baseline issue, not silently added to the designated historical10/195 lists. The original assertion was not changed.

No native dual-process PASS is claimed by this worker. Ignored task artifacts retain all RED/intermediate/green TRX files, raw exit codes, exact baseline-name comparison, source wiring evidence and handoff hashes.

## Bounded hand-written scoped route check

Static checks covered the16 concrete hand-written types directly declaring `ICampaignAuthorityScopedMessage` in GameInterface/Coop.Core. After the relation fix, each has an explicit Subscribe and Unsubscribe. Army's four routes use ArmyHandler; five troop-roster routes use TroopRosterDeltaHandler/TroopRosterOwnerPartyHandler; four party routes use disorganization/volunteer/mercenary/behavior handlers; item lifetime and campaign item count use original lifetime/message handlers. Client campaign item count inherits the original handler's constructor through CampaignItemRosterAuthorityHandler. No additional missing registration was identified in that bounded set. This is static wiring evidence, not native validation of every handler. Original generic Registry handlers and GenericNetworkEvent/AutoSync were inspected for their existing generic registration and were not rewritten.
