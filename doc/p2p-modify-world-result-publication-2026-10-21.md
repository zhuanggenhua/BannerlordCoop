# Applied world result publication boundary

Base `fb83c1339c551d103d0d595d95ab625ebb4ec4e0`. This is a regression fix, not permission to simulate from receive scopes.

Before editing the existing boundaries:

1. Existing functionality: `TroopRosterAddToCountsPatch.Postfix` reports an already executed, registered roster mutation when the original lower-level patch was suppressed by `AllowedThread`. The identity-keyed delta handler and vanilla roster mutators implement replication. `CampaignSimulationAuthorityPolicy` blocks AI/world callbacks from receive and restricted execution phases.
2. Concrete failure: integrated regression04 adds `Server_SubtractToZeroInAllowedThread_RemovesOnClient` (expected Count 0, actual 1). The fallback requires AllowedThread and then calls `CanRunWorldSimulation`, whose callback gate rejects AllowedThread unconditionally. A policy adapter distinguishing publication from simulation fixes this contradiction; neither roster mutation/batching nor world AI needs rewriting. The receive adapter also needs explicit Replay provenance because AllowedThread alone does not identify publication origin.
3. Invariants: a confirmed, unfrozen writer may publish one already executed result; AllowedThread grants no authority. Observers, Replay, CheckpointInstall, Prediction, LocalIntent and Bootstrap cannot publish. Native AI/Tick still reject AllowedThread. Preserve registration, CallOriginalPolicy, original batching, vanilla mutators, game-thread lookup and receive FIFO.
4. Regression obligations: preserve the original failing E2E assertion; test legacy and promoted-B publication, observer and frozen denial, restrictive nested phases, and no receive echo. Run the original roster E2E class and the exact selected baseline set; preserve raw failures and compare historical names individually. This worker does not deploy or claim native-game acceptance.

## Implemented boundary

`CampaignSimulationAuthorityPolicy.CanPublishAppliedWorldResult` uses the existing authority lease/freeze resolution and rejects restricted execution phases. `CanRunWorldSimulation` still enters through the unchanged strict callback gate. Only `TroopRosterAddToCountsPatch.Postfix` uses the publication predicate. Both game-thread receive closures in `TroopRosterDeltaHandler` enter Replay around the existing AllowedThread scope. No roster algorithm, batching, network format or native callback permission changed.

## Evidence

- Original fact RED: 0 passed / 1 failed, expected roster Count 0, actual 1 at line 808. Original test source and assertion remain unchanged.
- Managed boundary regression: 45 passed / 0 failed, including 11 new cases. The first run had two test-fixture failures because Castle cannot proxy the internal party-screen interface; concrete recording implementations fixed the fixture. That failed run is retained.
- Original `TroopRosterDeltaHandlerTests`: 20 passed / 2 failed. The former new failure passes. The two failures exactly match the original historical baseline: `Server_HeroServingInRoster_SyncsToClients` and `Server_RemoveZeroCountHero_RecalculatesTotalsOnAllInstances`. The raw process exit remains 1; the separate baseline comparison exits 0.
- Original selected 71-case E2E set: 61 passed / 10 failed. All ten failure names match the original baseline exactly; the separate comparison exits 0. Raw dotnet exit remains 1. No test source or historical failure list was changed.

Evidence is retained in the worker's ignored `artifacts/modify-result-publication-m4` directory and coordinator handoff. This does not claim native dual-process acceptance or an all-green E2E suite.
