# MODIFY Bootstrap and world callback boundary

2026-10-20. Base d2738eda. Dynamic campaign migration remains required.

## Change boundary recorded before implementation

1. Existing functionality: Campaign.RealTick/Tick and MobilePartyAi.Tick already use confirmed writer policy; local observer navigation is explicitly permitted. CallOriginalPolicy also permits received-state application and native loading. Original caravan, aging, food and governor behavior patches disable subscriptions or mix initialization with world callbacks.
2. Adapter evidence: the early CallOriginalPolicy return in MobilePartyAi.Tick treats AllowedThread replay as AI permission. Existing policy can reject forbidden execution phases at the actual native entry without modifying AI algorithms, Registry, serialization or AutoSync. Bootstrap subscription changes require separate initialization and runtime callback adapters, not rebuilding event lists.
3. Invariants: preserve one native world writer, native initialization and one-time event registration, local observer navigation, received leaf application without echo, and original AI/economy algorithms. No world AI executes from Replay, Prediction, CheckpointInstall, LocalIntent or Bootstrap callbacks. No all-thread loading bypass authorizes a world Tick.
4. Regression obligations: promoted B runs world callbacks independent of IsServer; observing A/B never runs world AI, including AllowedThread replay on the writer; local observer navigation still runs; paused/frozen leases refuse Tick; native callback registration and initial indexes remain valid; real natural day and Mission transfer require coordinator-owned native acceptance.

## Slice 1

The existing policy rejects disallowed provenance before writer/ownership lookup. Campaign.RealTick and MobilePartyAi.Tick use that decision before any receive-path bypass. No native algorithms or signatures change. This does not claim all CampaignBehavior callbacks are covered.
