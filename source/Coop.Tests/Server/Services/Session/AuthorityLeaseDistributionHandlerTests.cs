using Common;
using Common.Messaging;
using Common.Network.Session;
using Common.Tests.Utils;
using Coop.Core.Client.Services.Session;
using Coop.Core.Server.Connections.Messages;
using Coop.Core.Server.Services.Session;
using Coop.Tests.Mocks;
using GameInterface.Services.Entity;
using GameInterface.Services.GameState.Messages;
using Xunit;

namespace Coop.Tests.Server.Services.Session;

[Collection(ModInformationRoleCollection.Name)]
public class AuthorityLeaseDistributionHandlerTests
{
    [Fact]
    public void ListenAuthority_SendsItsLocalControllerInTheLeaseSnapshot()
    {
        var previousMode = ModInformation.SessionMode;
        ModInformation.SessionMode = SessionMode.ListenAuthority;
        try
        {
            var messageBroker = new TestMessageBroker();
            var network = new TestNetwork();
            var peer = network.CreatePeer();
            var identity = new SessionIdentity();
            var controller = new ControllerIdProvider();
            controller.SetControllerId("controller-a");
            using var handler = new AuthorityLeaseDistributionHandler(
                messageBroker, network, identity, controller);

            messageBroker.Publish(new object(), new CampaignReady());
            messageBroker.Publish(new object(), new PlayerConnected(peer));

            var snapshot = Assert.Single(network.GetPeerMessagesFromType<NetworkAuthorityLeaseSnapshot>(peer));
            Assert.Equal(identity.RoomId, snapshot.RoomId);
            Assert.Equal(identity.AuthorityId, snapshot.AuthorityId);
            Assert.Equal("controller-a", snapshot.AuthorityControllerId);
            Assert.Equal("controller-a", snapshot.RoomHostControllerId);
            Assert.Equal(1, snapshot.Epoch);
            Assert.True(identity.IsConfirmed);
            Assert.Equal("controller-a", identity.RoomHostControllerId);
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
        }
    }

    [Fact]
    public void Client_AppliesInitialSnapshotAndRejectsStaleEpoch()
    {
        var messageBroker = new TestMessageBroker();
        var identity = new SessionIdentity();
        using var handler = new AuthorityLeaseSnapshotHandler(messageBroker, identity);
        var snapshot = new NetworkAuthorityLeaseSnapshot(
            new AuthorityLease("room-a", "authority-a", "controller-a", 1));

        messageBroker.Publish(new object(), snapshot);
        messageBroker.Publish(new object(), new NetworkAuthorityLeaseSnapshot(
            new AuthorityLease("room-a", "authority-stale", "controller-stale", 0)));

        Assert.Equal("room-a", identity.RoomId);
        Assert.Equal("authority-a", identity.AuthorityId);
        Assert.Equal("controller-a", identity.AuthorityControllerId);
        Assert.Equal(1, identity.Epoch);
    }
}
