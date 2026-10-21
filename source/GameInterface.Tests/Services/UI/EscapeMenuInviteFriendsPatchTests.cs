using Autofac;
using Common;
using Common.Network.Session;
using GameInterface.Services.UI.Patches;
using Moq;
using System.Collections.Generic;
using TaleWorlds.MountAndBlade.ViewModelCollection.EscapeMenu;
using Xunit;

namespace GameInterface.Tests.Services.UI;

[Collection(ModInformationRoleCollection.Name)]
public class EscapeMenuInviteFriendsPatchTests
{
    [Theory]
    [InlineData(SessionMode.ListenAuthority, true, true)]
    [InlineData(SessionMode.RemoteReplica, true, true)]
    [InlineData(SessionMode.DedicatedAuthority, true, false)]
    [InlineData(SessionMode.ListenAuthority, false, false)]
    [InlineData(SessionMode.RemoteReplica, false, false)]
    public void SteamInviteAppearsForLocalPlayersWithInviteCapability(SessionMode mode, bool canInvite, bool expected)
    {
        var oldMode = ModInformation.SessionMode;
        var hadContainer = ContainerProvider.TryGetContainer(out var oldContainer);
        var advertiser = new Mock<ISessionAdvertiser>();
        advertiser.SetupGet(value => value.CanInviteFriends).Returns(canInvite);
        var builder = new ContainerBuilder();
        builder.RegisterInstance(advertiser.Object).As<ISessionAdvertiser>();
        using var container = builder.Build();
        try
        {
            ModInformation.SessionMode = mode;
            ContainerProvider.SetContainer(container);
            var items = new List<EscapeMenuItemVM> { null! };
            EscapeMenuInviteFriendsPatch.AddInviteFriendsItem(items);
            Assert.Equal(expected ? 2 : 1, items.Count);
            if (expected) Assert.NotNull(items[1]);
            advertiser.Verify(value => value.InviteFriends(), Times.Never);
        }
        finally
        {
            ModInformation.SessionMode = oldMode;
            if (hadContainer) ContainerProvider.SetContainer(oldContainer);
            else ContainerProvider.Clear();
        }
    }
}
