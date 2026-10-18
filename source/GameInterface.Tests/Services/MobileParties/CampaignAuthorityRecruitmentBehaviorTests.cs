using Autofac;
using Common;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.MobileParties.Patches;
using GameInterface.Services.MobileParties.Patches.Disable;
using GameInterface.Tests;
using Moq;
using System;
using TaleWorlds.CampaignSystem.Party;
using Xunit;

namespace GameInterface.Tests.Services.MobileParties;

[Collection(ModInformationRoleCollection.Name)]
public sealed class CampaignAuthorityRecruitmentBehaviorTests
{
    private static readonly AuthorityLease CurrentLease =
        new("room-a", "authority-b", "controller-b", 4);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RecruitmentCampaignListenersRegisterOnEveryReplica(bool legacyIsServer)
    {
        var previousMode = ModInformation.SessionMode;
        bool previousIsServer = ModInformation.IsServer;
        try
        {
            ModInformation.IsServer = legacyIsServer;
            Assert.True(DisableRecruitmentCampaignBehavior.PrefixRegisterEvents());
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
            ModInformation.IsServer = previousIsServer;
        }
    }

    [Fact]
    public void ObserverReplicaSkipsRecruitmentPoolAndResultCallbacks()
    {
        WithAuthority(localAuthority: false, legacyIsServer: true, assertion: () =>
        {
            Assert.False(RecruitmentCampaignBehaviorPatch.DailyTickSettlementAuthorityPrefix());
            Assert.False(RecruitmentCampaignBehaviorPatch.OnBeforeSettlementEnteredAuthorityPrefix());
            Assert.False(RecruitmentCampaignBehaviorPatch.OnNewGameCreatedPartialFollowUpEndAuthorityPrefix());
            Assert.False(RecruitmentCampaignBehaviorPatch.OnTroopRecruitedAuthorityPrefix());
            Assert.False(RecruitmentCampaignBehaviorPatch.OnUnitRecruitedAuthorityPrefix());
            Assert.False(RecruitmentCampaignBehaviorPatch.DailyTickTownPrefix(null!, null!, out _));

            MobileParty party = null!;
            Assert.False(RecruitmentCampaignBehaviorPatch.HourlyTickPartyPrefix(ref party));
        });
    }

    [Fact]
    public void RemoteLeaseHolderRunsSharedRecruitmentCallbacksInClientProcess()
    {
        WithAuthority(localAuthority: true, legacyIsServer: false, assertion: () =>
        {
            Assert.True(RecruitmentCampaignBehaviorPatch.DailyTickSettlementAuthorityPrefix());
            Assert.True(RecruitmentCampaignBehaviorPatch.OnBeforeSettlementEnteredAuthorityPrefix());
            Assert.True(RecruitmentCampaignBehaviorPatch.OnNewGameCreatedPartialFollowUpEndAuthorityPrefix());
            Assert.True(RecruitmentCampaignBehaviorPatch.OnTroopRecruitedAuthorityPrefix());
            Assert.True(RecruitmentCampaignBehaviorPatch.OnUnitRecruitedAuthorityPrefix());
            Assert.True(RecruitmentCampaignBehaviorPatch.DailyTickTownPrefix(null!, null!, out _));
        });
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void HourlyNpcRecruitmentRunsOnlyForAuthorityAiParties(
        bool isCampaignAuthority,
        bool isPlayerParty,
        bool expected)
    {
        Assert.Equal(expected, RecruitmentCampaignBehaviorPatch.ShouldRunHourlyRecruitment(
            isCampaignAuthority,
            isPlayerParty));
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
