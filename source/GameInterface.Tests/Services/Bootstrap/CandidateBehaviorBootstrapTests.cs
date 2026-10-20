using Autofac;
using Common;
using Common.Util;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Caravans.Patches;
using GameInterface.Services.Entity;
using GameInterface.Services.Heroes.Interfaces;
using GameInterface.Services.Heroes.Patches;
using GameInterface.Services.Heroes.Patches.Disable;
using GameInterface.Services.MobileParties.Patches.Disable;
using GameInterface.Tests.Bootstrap;
using HarmonyLib;
using Moq;
using System;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using Xunit;

namespace GameInterface.Tests.Services.Bootstrap;

[Collection(ModInformationRoleCollection.Name)]
public class CandidateBehaviorBootstrapTests
{
    [Fact]
    public void ReplicaRegistersNativeBusinessListenersOnceAndUsesThemAfterLeaseChanges()
    {
        GameBootStrap.Initialize();
        WithPatchedBehaviors((identity, aging) =>
        {
            var food = new PartiesBuyFoodCampaignBehavior();
            var caravans = new CaravansCampaignBehavior();
            var age = new AgingCampaignBehavior();
            var governor = new GovernorCampaignBehavior();
            var prisoners = new RecruitPrisonersCampaignBehavior();
            var behaviors = new CampaignBehaviorBase[] { food, caravans, age, governor, prisoners };
            try
            {
                foreach (var behavior in behaviors) behavior.RegisterEvents();
                AssertSubscriptions();
                var tick = FindListener(CampaignEvents.DailyTickHeroEvent, age);
                tick(null!);
                aging.Verify(x => x.DailyTickHero(age, It.IsAny<Hero>()), Times.Never());

                Assert.True(identity.TryApply(new AuthorityLease("room", "world-b", "b", 5), "a"));
                tick(null!);
                aging.Verify(x => x.DailyTickHero(age, null!), Times.Once());
                using (new AllowedThread()) tick(null!);
                using (CampaignExecutionScope.Enter(CampaignExecutionPhase.Replay)) tick(null!);
                aging.Verify(x => x.DailyTickHero(age, It.IsAny<Hero>()), Times.Once());

                Assert.True(identity.TryApply(new AuthorityLease("room", "world-a", "a", 6), "a"));
                tick(null!);
                aging.Verify(x => x.DailyTickHero(age, It.IsAny<Hero>()), Times.Once());
                AssertSubscriptions();
            }
            finally
            {
                foreach (var behavior in behaviors) Campaign.Current.CampaignEvents.RemoveListeners(behavior);
            }

            void AssertSubscriptions()
            {
                Assert.Equal(1, CountListeners(CampaignEvents.HourlyTickPartyEvent, food));
                Assert.Equal(1, CountListeners(CampaignEvents.HourlyTickPartyEvent, caravans));
                Assert.Equal(1, CountListeners(CampaignEvents.DailyTickHeroEvent, caravans));
                Assert.Equal(1, CountListeners(CampaignEvents.OnSessionLaunchedEvent, caravans));
                Assert.Equal(1, CountListeners(CampaignEvents.DailyTickHeroEvent, age));
                Assert.Equal(1, CountListeners(CampaignEvents.OnGameLoadedEvent, age));
                Assert.Equal(1, CountListeners(CampaignEvents.DailyTickSettlementEvent, governor));
                Assert.Equal(1, CountListeners(CampaignEvents.DailyTickPartyEvent, prisoners));
            }
        });
    }

    [Fact]
    public void ReplayMaintainsCaravanLocalIndexWithoutRunningEconomyOrPrisonerRng()
    {
        GameBootStrap.Initialize();
        WithPatchedBehaviors((identity, aging) =>
        {
            var caravans = new CaravansCampaignBehavior();
            var party = (MobileParty)RuntimeHelpers.GetUninitializedObject(typeof(MobileParty));
            party._partyComponent = (CaravanPartyComponent)RuntimeHelpers.GetUninitializedObject(typeof(CaravanPartyComponent));
            party.UpdatePartyComponentFlags();
            using (new AllowedThread())
            {
                caravans.OnMobilePartyCreated(party);
                Assert.True(caravans._caravanLastHomeTownVisitTime.ContainsKey(party));
                caravans.OnMobilePartyDestroyed(party, null!);
                Assert.False(caravans._caravanLastHomeTownVisitTime.ContainsKey(party));
                AssertSimulationBlocked();
                new PartiesBuyFoodCampaignBehavior().HourlyTickParty(null!);
                new GovernorCampaignBehavior().DailyTickSettlement(null!);
                new RecruitPrisonersCampaignBehavior().HourlyTickMainParty();
            }
            AssertSimulationBlocked();
            Assert.True(identity.TryApply(new AuthorityLease("room", "world-b", "b", 5), "a"));
            Assert.True(DisableCaravansCampaignBehaviorPatch.Prefix());
            Assert.True(DisablePartiesBuyFoodCampaignBehavior.Prefix());
            Assert.True(GovernorCampaignBehaviorPatches.Prefix());
            using (new AllowedThread()) AssertSimulationBlocked();
        });
    }

    private static void AssertSimulationBlocked()
    {
        Assert.False(DisableCaravansCampaignBehaviorPatch.Prefix());
        Assert.False(DisablePartiesBuyFoodCampaignBehavior.Prefix());
        Assert.False(GovernorCampaignBehaviorPatches.Prefix());
        Assert.False(RecruitPrisonersCampaignBehaviorPatches.DailyTickAIMobilePartyPrefix(null!));
        Assert.False(RecruitPrisonersCampaignBehaviorPatches.HourlyTickMainPartyPrefix(null!));
        Assert.False(PartiesBuyFoodCampaignBehaviorPatches.BuyFoodInternalPrefix(null!, null!, null!, 1));
    }

    private static int CountListeners<T>(IMbEvent<T> evt, object owner)
    {
        int count = 0;
        for (var node = ((MbEvent<T>)evt)._nonSerializedListenerList; node != null; node = node.Next)
            if (ReferenceEquals(node.Owner, owner)) count++;
        return count;
    }

    private static Action<T> FindListener<T>(IMbEvent<T> evt, object owner)
    {
        for (var node = ((MbEvent<T>)evt)._nonSerializedListenerList; node != null; node = node.Next)
            if (ReferenceEquals(node.Owner, owner)) return node.Action;
        throw new InvalidOperationException("Native listener was not registered.");
    }

    private static void WithPatchedBehaviors(Action<SessionIdentity, Mock<IAgingCampaignBehaviorInterface>> action)
    {
        var identity = new SessionIdentity();
        Assert.True(identity.TryApply(new AuthorityLease("room", "world", "a", 4), "a"));
        var controller = new ControllerIdProvider();
        controller.SetControllerId("b");
        var aging = new Mock<IAgingCampaignBehaviorInterface>();
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new CampaignAuthorityContext(identity, controller)).As<ICampaignAuthorityContext>();
        builder.RegisterInstance(aging.Object).As<IAgingCampaignBehaviorInterface>();
        using var container = builder.Build();
        var harmony = new Harmony("Coop.Tests.CandidateBootstrap." + Guid.NewGuid().ToString("N"));
        var patches = new[] { typeof(DisableCaravansCampaignBehaviorPatch), typeof(CaravansCampaignBehaviorPatches),
            typeof(DisablePartiesBuyFoodCampaignBehavior), typeof(PartiesBuyFoodCampaignBehaviorPatches),
            typeof(AgingCampaignBehaviorPatches), typeof(GovernorCampaignBehaviorPatches), typeof(RecruitPrisonersCampaignBehaviorPatches) };
        bool hadPrevious = ContainerProvider.TryGetContainer(out var previous);
        var previousMode = ModInformation.SessionMode;
        try
        {
            ModInformation.SessionMode = SessionMode.RemoteReplica;
            ContainerProvider.SetContainer(container);
            foreach (var patch in patches) harmony.CreateClassProcessor(patch).Patch();
            action(identity, aging);
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            ModInformation.SessionMode = previousMode;
            if (hadPrevious) ContainerProvider.SetContainer(previous);
            else ContainerProvider.Clear();
        }
    }
}
