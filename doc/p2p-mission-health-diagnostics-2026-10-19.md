# Mission health setter diagnostic adapter

Base `9d38062f3f3aabb8b573a24d82c1e1a67453c5ce`. This is DEBUG evidence collection, not a health permission fix or native Mission acceptance.

## Existing behavior and change boundary

1. Existing `HeroHitPointsRequestPatch` forwards changed client-owned health through `HeroHitPointsHandler`; server apply uses existing HP AutoSync. Battle `CoopAgentOrigin` already reports owned final health with one-shot finalization. Native tavern `PartyAgentOrigin.OnAgentRemoved` uses its original setter. All remain unchanged.
2. Mission02 run `be22677a12a6473f8fad6c1ac60bb2bf` game53e169/harness7809, B PID4032, emitted one real managed HP write ERR at18:50:00 while paused epoch6, before MapReady and active B epoch7. The original stack identifies native Agent removal, but lacks Hero identity and old/requested/post values. A readonly adapter supplies this evidence; no generator, handler, ownership, damage or transport rewrite is justified.
3. The new nongeneric Harmony setter Prefix observes only while `Mission.Current != null`; its void signature never gates original, and no ref argument changes value. A finalizer reads actual post HP and `__runOriginal`, then returns exactly the original exception object. It neither assigns HP nor publishes any request. Diagnostic failures use existing process counters and `CampaignLifecycleProbe.observation.failed` / `.logging.failed`; unavailable evidence must fail acceptance. Release contains no new patch or helper.
4. Required regressions cover equal and changed values, non-main Hero, active/paused lease, receive allowance, skipped original, entry/final argument distinction, partial/post read and logger/exception formatter failures, original exception identity/stack, independent observations, and real nongeneric Harmony binding. Existing health E2E and actual dual-game Mission02 must still be rerun by coordinator with original failure evidence retained.

## Receipt interpretation

`[MissionHealthProbe] before` records native StringId/reference identity, Hero==MainHero, old HP, requested argument, local controller, confirmed lease epoch/paused/authority controller, local-writer flag, CallOriginalPolicy allowance and AllowedThread allowance. Original allowance alone does not prove receive apply; it can come from sync policy or explicit original scopes. Unknown lease fields remain null instead of being guessed.

The Prefix uses First priority to read before other normal prefixes. `requestedHP` describes this observation point, not a guarantee that another prefix leaves the argument unchanged. `[MissionHealthProbe] after` records `finalArgumentHP`, actual post HP, originalRan, originalException and any observation failure. This distinguishes unchanged writes, other-prefix argument changes, skipped originals and failures without making a permission decision. Identity/role/allowance fields describe entry time, not a new lease sample during finalization.

An ERR remains an ERR even if a future receipt demonstrates equal HP. A void return from existing request/generated prefix does not establish that native setter was blocked. Neither the old Mission02 log nor this adapter alone proves a health request was sent, received or applied. The raw request type is non-authority-scoped on the traced route; handler role checks alone do not prove a dynamic-authority-safe health result protocol. Do not infer an unobserved network outcome from a setter receipt.

## Coordinator native verification

Deploy only the reviewed integrated commit under the installation lock. Record actual game assembly MVID/build, both PIDs and run token, and execute original Mission scenario: A Mission/B map writer, both Missions/world exact pause with Mission and network progress, B return and automatic resume, then A return. For each HP ERR retain the surrounding before/after receipts and exact Hero/old/requested/final/post fields, lease and exception. Missing receipt, diagnostic failed counters, original ERR, divergence or native exception remains FAIL. Do not change the health request, generator, timeout or oracle to make the observation pass.

Worker validation and native acceptance results are recorded separately in the task handoff. Desktop CLR binding validates Harmony signatures against real game assemblies without starting a game; it does not replace the native scenario.

## Worker verification

New13 plus existing25 diagnostic cases passed38/38. Windows PowerShell5 Desktop .NET Framework4.8 bound11 nongeneric patch classes and32 real targets, including the new Hero setter. Debug assembly SHA256 `4aaf9fa0ac56afdf710cf77849637e202b4999abdc48179b557b0ad1a925dde9`. Release compile succeeded and Desktop reflection confirmed both new DEBUG types absent. All builds passed empty `-p:ModName=` via Node argument arrays; no deployment or game launch occurred.

The first compile failed for a missing Entity namespace import, corrected without product behavior change. The second focused run passed12/13; its fixture compared the Harmony generated replacement MethodInfo with the original setter. The final fixture uses Harmony.GetPatchedMethods to verify the real original target and retains generated replacement count separately. Both failed logs remain in ignored task evidence. Native Mission03 and full integrated regression remain coordinator responsibilities; existing Coop195/selectedE2E10 failures are not reclassified.
