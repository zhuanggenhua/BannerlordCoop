# MODIFY Bootstrap and world callback boundary

2026-10-20. Base d2738eda. Dynamic campaign migration remains required.

## Change boundary recorded before implementation

1. Existing functionality: Campaign.RealTick/Tick and MobilePartyAi.Tick already use confirmed writer policy; local observer navigation is explicitly permitted. CallOriginalPolicy also permits received-state application and native loading. Original caravan, aging, food and governor behavior patches disable subscriptions or mix initialization with world callbacks.
2. Adapter evidence: the early CallOriginalPolicy return in MobilePartyAi.Tick treats AllowedThread replay as AI permission. Existing policy can reject forbidden execution phases at the actual native entry without modifying AI algorithms, Registry, serialization or AutoSync. Bootstrap subscription changes require separate initialization and runtime callback adapters, not rebuilding event lists.
3. Invariants: preserve one native world writer, native initialization and one-time event registration, local observer navigation, received leaf application without echo, and original AI/economy algorithms. No world AI executes from Replay, Prediction, CheckpointInstall, LocalIntent or Bootstrap callbacks. No all-thread loading bypass authorizes a world Tick.
4. Regression obligations: promoted B runs world callbacks independent of IsServer; observing A/B never runs world AI, including AllowedThread replay on the writer; local observer navigation still runs; paused/frozen leases refuse Tick; native callback registration and initial indexes remain valid; real natural day and Mission transfer require coordinator-owned native acceptance.

## Slice 1

The existing policy rejects disallowed provenance before writer/ownership lookup. Campaign.RealTick and MobilePartyAi.Tick use that decision before any receive-path bypass. No native algorithms or signatures change. This does not claim all CampaignBehavior callbacks are covered.

## Slice 2: native registration and callback classes

Original `RegisterEvents` now runs during the normal single campaign load for Caravans, Food and Aging on every candidate. No lease-change registration is added. Governor and RecruitPrisoners already registered natively.

| Native boundary | Classification / adapter |
| --- | --- |
| Caravans OnSessionLaunched | Original dialogue, distance and local price-average caches, all candidates |
| Caravans OnGameLoadFinished | Original price cache plus local stale-entry cleanup, all candidates |
| Caravans OnMobilePartyCreated/Destroyed, OnKingdomDestroyed | Original private index maintenance, including received replay |
| Caravans daily/hourly, initial caravan spawn/trade runs, settlement trade, loot/map/siege outcomes | Current writer and permitted execution phase only |
| Food HourlyTickParty / OnSettlementEntered / BuyFoodInternal | Writer only; existing player-party exclusion retained |
| Aging load/character creation and HeroCreated/Killed/PerkOpened | Original private-index initialization and maintenance |
| Aging DailyTickHero and three age-transition callbacks | Writer only, including existing Coop player-clan adaptations |
| Governor OnSessionLaunched / OnGameLoadFinished | Original dialogue and old-save backlink repair |
| Governor DailyTickSettlement / OnHeroKilled / OnHeroChangedClan | Writer only, since they use RNG or change shared relationships/governor state |
| RecruitPrisoners hourly player loop / daily NPC callback | Writer only; existing Coop algorithm and player-party exclusions retained |

The classification was checked against the installed TaleWorlds.CampaignSystem.dll, SHA256 `1f8e33e2ed73e6ec653d7629180afb70649ddc6e5bd1657a802a264efda1c3ae`, using the existing ilspycmd. The decompiled files and commands are retained in ignored task artifacts. No native AI/economy body was copied into a new implementation. All modifications above are adapter boundaries: original functionality, evidence of static-role conflict, protected invariants and regression obligations are recorded in the preceding section.

The candidate-registration test enumerates the actual native MbEvent listener chain by exact owner and invokes the registered Aging daily callback before/after B promotion and after return to A. The local caravan index test invokes original native create/destroy under AllowedThread while Food/Governor/RecruitPrisoners callbacks are blocked. These are managed game-library tests, not live dual-process acceptance.

## Explicit remaining state / routing obligations

- Aging `_heroesYoungerThanHeroComesOfAge` is advanced/removed by writer DailyTickHero; `_extraLivesContainer` is decremented by death logic. Merely subscribing the observer does not transfer these authoritative private states. Their handoff capture/install or validated finite reconstruction remains required before full aging continuity can be claimed.
- Caravan `_caravanLastHomeTownVisitTime` is updated during authoritative caravan movement/trade, not only on creation; local event subscription does not transfer all historical timestamps. Price caches may be reconstructible, but that contract has not been accepted in a live handoff.
- Aging optional IMissionMembershipRegistry must carry real room membership to B; null means no known Mission and is not an adequate dynamic-world fact source. Existing bare NetworkUpdatePlayerIllDays and caravan-specific result messages also need the current authority result route. No fake empty registry or business rewrite is introduced here.
- Other campaign behavior families (kingdom proposal, hero spawn, etc.) are outside this bounded slice. This is not a claim of complete Bootstrap conversion.
- Release test compilation at the initial baseline fails on pre-existing DEBUG-only campaign diagnostic types. Debug is the established runnable test configuration; the failure log is retained rather than changing tests or assertions.
- Native natural-day, Mission and A-B-A acceptance is coordinator-owned and not performed in this worker.
