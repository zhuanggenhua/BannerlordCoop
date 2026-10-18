using Autofac;
using Common;
using GameInterface;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.MobileParties.Patches;
using Moq;
using System;
using Xunit;

namespace GameInterface.Tests.Services.MobileParties;

[Collection(ModInformationRoleCollection.Name)]
public sealed class PatrolPartyAuthorityTests
{
    private static readonly AuthorityLease CurrentLease =
        new("room-a", "authority-b", "controller-b", 4);

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, false, true, true)]
    public void PatrolPartyCallbacksFollowTheConfirmedCampaignAuthority(
        bool leaseKnown,
        bool localAuthority,
        bool legacyIsServer,
        bool expected)
    {
        var authorityContext = new Mock<ICampaignAuthorityContext>();
        authorityContext
            .Setup(context => context.TryGetLocalAuthorityLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease lease) =>
            {
                lease = leaseKnown ? CurrentLease : default;
                return localAuthority;
            });

        var builder = new ContainerBuilder();
        builder.RegisterInstance(authorityContext.Object).As<ICampaignAuthorityContext>();
        using var container = builder.Build();

        var previousMode = ModInformation.SessionMode;
        bool hadPreviousContainer = ContainerProvider.TryGetContainer(out var previousContainer);
        try
        {
            ModInformation.IsServer = legacyIsServer;
            ContainerProvider.SetContainer(container);

            Assert.Equal(expected, DisablePatrolPartiesCampaignBehavior.Prefix());
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
            if (hadPreviousContainer) ContainerProvider.SetContainer(previousContainer);
            else ContainerProvider.Clear();
        }
    }
}
