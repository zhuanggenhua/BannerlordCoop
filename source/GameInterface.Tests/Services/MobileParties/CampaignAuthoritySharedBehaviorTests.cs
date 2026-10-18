using Autofac;
using Common;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.MobileParties.Patches.Disable;
using GameInterface.Tests;
using Moq;
using System;
using Xunit;

namespace GameInterface.Tests.Services.MobileParties;

[Collection(ModInformationRoleCollection.Name)]
public sealed class CampaignAuthoritySharedBehaviorTests
{
    private static readonly AuthorityLease CurrentLease =
        new("room-a", "authority-b", "controller-b", 4);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SharedBehaviorListenersRegisterOnEveryReplica(bool legacyIsServer)
    {
        WithAuthority(localAuthority: false, legacyIsServer: legacyIsServer, assertion: () =>
        {
            Assert.True(DisableFoodConsumptionBehavior.Prefix());
            Assert.True(DisablePartyUpgraderCampaignBehavior.Prefix());
            Assert.True(DisableDesertionCampaignBehavior.Prefix());
        });
    }

    [Fact]
    public void ObserverReplicaSkipsSharedFoodAndTroopUpgradeCallbacks()
    {
        WithAuthority(localAuthority: false, legacyIsServer: true, assertion: () =>
        {
            Assert.False(FoodConsumptionBehaviorPatches.OnPartyAttachedPartyPrefix(null!, null!));
            Assert.False(FoodConsumptionBehaviorPatches.OnNewGameCreatedPartialFollowUpEndPrefix());
            Assert.False(PartyUpgraderCampaignBehaviorPatches.DailyTickPartyAuthorityPrefix());
            Assert.False(PartyUpgraderCampaignBehaviorPatches.MapEventEndedAuthorityPrefix());
            Assert.False(DesertionCampaignBehaviorPatches.DailyTickPartyPrefix());
            Assert.False(FoodConsumptionBehaviorPatches.DailyTickPartyPrefix(null!, null!));
            Assert.False(FoodConsumptionBehaviorPatches.OnTickPrefix(null!, 1f));
            Assert.False(FoodConsumptionBehaviorPatches.PartyConsumeFoodPrefix(null!, null!));
            Assert.False(FoodConsumptionBehaviorPatches.CheckAnimalBreedingPrefix(null!, null!));
            Assert.False(PartyUpgraderCampaignBehaviorPatches.UpgradeReadyTroopsPrefix(null!, null!));
        });
    }

    [Fact]
    public void RemoteLeaseHolderRunsSharedWorldCallbacksDespiteClientRole()
    {
        WithAuthority(localAuthority: true, legacyIsServer: false, assertion: () =>
        {
            Assert.True(CampaignSimulationAuthorityPolicy.CanRunWorldSimulation());
            Assert.True(FoodConsumptionBehaviorPatches.OnNewGameCreatedPartialFollowUpEndPrefix());
            Assert.True(PartyUpgraderCampaignBehaviorPatches.DailyTickPartyAuthorityPrefix());
            Assert.True(PartyUpgraderCampaignBehaviorPatches.MapEventEndedAuthorityPrefix());
            Assert.True(DesertionCampaignBehaviorPatches.DailyTickPartyPrefix());
        });
    }

    private static void WithAuthority(bool localAuthority, bool legacyIsServer, Action assertion)
    {
        var authorityContext = new Mock<ICampaignAuthorityContext>();
        authorityContext
            .Setup(context => context.TryGetLocalAuthorityLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease lease) =>
            {
                lease = CurrentLease;
                return localAuthority;
            });

        var builder = new ContainerBuilder();
        builder.RegisterInstance(authorityContext.Object).As<ICampaignAuthorityContext>();
        using var container = builder.Build();

        var previousMode = ModInformation.SessionMode;
        bool previousIsServer = ModInformation.IsServer;
        bool hadPreviousContainer = ContainerProvider.TryGetContainer(out var previousContainer);
        try
        {
            ModInformation.IsServer = legacyIsServer;
            ContainerProvider.SetContainer(container);
            assertion();
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
            ModInformation.IsServer = previousIsServer;
            if (hadPreviousContainer) ContainerProvider.SetContainer(previousContainer);
            else ContainerProvider.Clear();
        }
    }
}