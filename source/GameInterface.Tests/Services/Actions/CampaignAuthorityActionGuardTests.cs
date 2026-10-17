using Autofac;
using Common;
using GameInterface;
using GameInterface.Services.Actions.Patches;
using GameInterface.Services.CampaignAuthority;
using Moq;
using System;
using Xunit;

namespace GameInterface.Tests.Services.Actions;

[Collection(ModInformationRoleCollection.Name)]
public sealed class CampaignAuthorityActionGuardTests
{
    private static readonly AuthorityLease CurrentLease =
        new("room-a", "authority-b", "controller-b", 4);

    [Fact]
    public void RemoteCampaignAuthorityCanApplySharedNativeActions()
    {
        WithAuthority(localAuthority: true, legacyIsServer: false, () =>
        {
            Assert.True(GiveGoldActionPatches.Prefix());
            Assert.True(AddHeroToPartyActionPatches.Prefix());
            Assert.True(RemoveCompanionActionPatches.Prefix());
        });
    }

    [Fact]
    public void RemoteReplicaCannotApplySharedNativeActionsEvenWithLegacyServerRole()
    {
        WithAuthority(localAuthority: false, legacyIsServer: true, () =>
        {
            Assert.False(GiveGoldActionPatches.Prefix());
            Assert.False(AddHeroToPartyActionPatches.Prefix());
            Assert.False(RemoveCompanionActionPatches.Prefix());
        });
    }

    [Fact]
    public void RemoteReplicaRejectsHeroDeathBeforeReadingCampaignObjects()
    {
        WithAuthority(localAuthority: false, legacyIsServer: false, () =>
        {
            Assert.False(KillCharacterActionPatches.ApplyInternalPrefix(
                victim: null!,
                killer: null!,
                actionDetail: default,
                showNotification: false));
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
            if (hadPreviousContainer) ContainerProvider.SetContainer(previousContainer);
            else ContainerProvider.Clear();
        }
    }
}
