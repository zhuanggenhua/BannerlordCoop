using Autofac;
using Common;
using Common.Util;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using GameInterface.Services.Heroes.Patches;
using GameInterface.Services.Kingdoms.Patches;
using GameInterface.Tests.Bootstrap;
using HarmonyLib;
using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Election;
using Xunit;

namespace GameInterface.Tests.Services.Bootstrap;

[Collection(ModInformationRoleCollection.Name)]
public class AdditionalWorldBehaviorBoundaryTests
{
    private static int callbackEntries;
    private static bool CallbackSpy(bool __runOriginal) { if (__runOriginal) callbackEntries++; return false; }

    [Fact]
    public void NativeSubscriptionOnceSurvivesPromotionWithoutReplaySpawningHeroes()
    {
        GameBootStrap.Initialize();
        var previousMode = ModInformation.SessionMode;
        bool hadPrevious = ContainerProvider.TryGetContainer(out var previous);
        var identity = new SessionIdentity();
        Assert.True(identity.TryApply(new AuthorityLease("room", "world", "a", 4), "a"));
        var controller = new ControllerIdProvider(); controller.SetControllerId("b");
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new CampaignAuthorityContext(identity, controller)).As<ICampaignAuthorityContext>();
        using var container = builder.Build();
        var harmony = new Harmony("Coop.Tests.AdditionalBoundary." + Guid.NewGuid().ToString("N"));
        var heroSpawn = new HeroSpawnCampaignBehavior(); var proposals = new KingdomDecisionProposalBehavior();
        try
        {
            ModInformation.SessionMode = SessionMode.RemoteReplica; ContainerProvider.SetContainer(container);
            harmony.CreateClassProcessor(typeof(HeroSpawnCampaignBehaviorPatches)).Patch();
            harmony.CreateClassProcessor(typeof(CoopKingdomDecisionProposalBehaviorPatch)).Patch();
            // Observe native event dispatch after production prefix, then avoid running AI against an incomplete managed world.
            harmony.Patch(AccessTools.Method(typeof(HeroSpawnCampaignBehavior), "OnNonBanditClanDailyTick"),
                prefix: new HarmonyMethod(typeof(AdditionalWorldBehaviorBoundaryTests), nameof(CallbackSpy)) { priority = Priority.Last });
            heroSpawn.RegisterEvents(); proposals.RegisterEvents();
            AssertSubscriptions();
            var daily = Find(CampaignEvents.DailyTickClanEvent, heroSpawn);
            callbackEntries = 0; daily(null!); Assert.Equal(0, callbackEntries);
            // Observer result application still maintains the original private duplicate index.
            var value = ObjectHelper.SkipConstructor<DeclareWarDecision>();
            proposals.OnKingdomDecisionAdded(value, false);
            Assert.Same(value, Assert.Single(proposals._kingdomDecisionsList));
            Assert.True(identity.TryApply(new AuthorityLease("room", "world-b", "b", 5), "a"));
            daily(null!); Assert.Equal(1, callbackEntries);
            Assert.True(HeroSpawnCampaignBehaviorPatches.OnGovernorChangedPrefix());
            using (new AllowedThread()) { daily(null!); AssertRuntimeBlocked(); }
            using (CampaignExecutionScope.Enter(CampaignExecutionPhase.Replay)) { daily(null!); AssertRuntimeBlocked(); }
            Assert.Equal(1, callbackEntries);
            Assert.True(identity.TryApply(new AuthorityLease("room", "world-a", "a", 6), "a"));
            daily(null!); Assert.Equal(1, callbackEntries); AssertRuntimeBlocked(); AssertSubscriptions();
        }
        finally
        {
            Campaign.Current.CampaignEvents.RemoveListeners(heroSpawn); Campaign.Current.CampaignEvents.RemoveListeners(proposals);
            harmony.UnpatchAll(harmony.Id); ModInformation.SessionMode = previousMode;
            if (hadPrevious) ContainerProvider.SetContainer(previous); else ContainerProvider.Clear();
        }
        void AssertSubscriptions()
        {
            Assert.Equal(1, Count(CampaignEvents.DailyTickClanEvent, heroSpawn));
            Assert.Equal(1, Count(CampaignEvents.DailyTickHeroEvent, heroSpawn));
            Assert.Equal(1, Count(CampaignEvents.OnNewGameCreatedEvent, heroSpawn));
            Assert.Equal(1, Count(CampaignEvents.DailyTickClanEvent, proposals));
        }
    }

    [Theory]
    [InlineData(CampaignExecutionPhase.Replay)]
    [InlineData(CampaignExecutionPhase.Prediction)]
    [InlineData(CampaignExecutionPhase.CheckpointInstall)]
    [InlineData(CampaignExecutionPhase.Bootstrap)]
    public void RestrictedProvenanceCannotRunBusinessEvenWhenLegacyServerIsTrue(CampaignExecutionPhase phase)
    {
        var previousMode = ModInformation.SessionMode;
        bool hadPrevious = ContainerProvider.TryGetContainer(out var previous);
        try
        {
            ContainerProvider.Clear(); ModInformation.SessionMode = SessionMode.ListenAuthority;
            using (CampaignExecutionScope.Enter(phase)) AssertRuntimeBlocked();
            using (new AllowedThread()) AssertRuntimeBlocked();
            // Startup has its explicit legacy contract; runtime changes do not rewrite it.
            Assert.True(HeroSpawnCampaignBehaviorPatches.OnNewGameCreatedPrefix());
        }
        finally { ModInformation.SessionMode = previousMode; if (hadPrevious) ContainerProvider.SetContainer(previous); }
    }

    private static void AssertRuntimeBlocked()
    {
        Assert.False(HeroSpawnCampaignBehaviorPatches.OnGovernorChangedPrefix());
        Assert.False(HeroSpawnCampaignBehaviorPatches.OnHeroComesOfAgePrefix());
        Assert.False(HeroSpawnCampaignBehaviorPatches.OnHeroDailyTickPrefix());
        Assert.False(HeroSpawnCampaignBehaviorPatches.OnCompanionRemovedPrefix());
        Assert.False(HeroSpawnCampaignBehaviorPatches.OnNonBanditClanDailyTickPrefix(null!));
        Assert.False(CoopKingdomDecisionProposalBehaviorPatch.DailyTickClanPrefix(null!, null!));
        Assert.False(CoopKingdomDecisionProposalBehaviorPatch.HourlyTickPrefix());
    }
    private static int Count<T>(IMbEvent<T> evt, object owner)
    {
        int count = 0;
        for (var node = ((MbEvent<T>)evt)._nonSerializedListenerList; node != null; node = node.Next)
            if (ReferenceEquals(node.Owner, owner)) count++;
        return count;
    }
    private static Action<T> Find<T>(IMbEvent<T> evt, object owner)
    {
        for (var node = ((MbEvent<T>)evt)._nonSerializedListenerList; node != null; node = node.Next)
            if (ReferenceEquals(node.Owner, owner)) return node.Action;
        throw new InvalidOperationException("Native listener was not registered");
    }
}
