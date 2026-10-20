using Common;
using Common.Tests.Utils;
using Common.Util;
using Coop.Tests.Mocks;
using GameInterface.Services.Entity;
using GameInterface.Services.Locations.Hosting;
using GameInterface.Services.Missions;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using Missions.Locations;
using Missions.Messages;
using Moq;
using Serilog;
using System;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using Xunit;

namespace Coop.Tests.Missions.Locations;

[Collection(ModInformationRoleCollection.Name)]
public class LocalMissionEndpointTests
{
    [Fact]
    public void LocalReadyUsesTheOriginalElectionAndPublishesAuthorityAcquiredOnce()
    {
        using var fixture = new Fixture();
        fixture.Broker.Publish(fixture, new LocationMissionReady("town|tavern"));
        fixture.Drain();
        Assert.True(fixture.Hosts.TryGet("town|tavern", out var assignment));
        Assert.Equal("A", assignment.HostControllerId);
        Assert.Equal(1, assignment.Epoch);
        Assert.Single(fixture.Broker.Messages.GetMessages<LocationHostAuthorityAcquired>());
        fixture.Broker.Publish(fixture, new LocationMissionReady("town|tavern"));
        fixture.Drain();
        Assert.Single(fixture.Broker.Messages.GetMessages<LocationHostAuthorityAcquired>());
    }

    [Fact]
    public void UnregisteredOrWrongLocalControllerCannotElectAMissionHost()
    {
        using var fixture = new Fixture();
        fixture.Broker.Publish(new LocalPlayerEndpoint("B"), new NetworkRequestLocationHost("town|tavern", "B"));
        fixture.Broker.Publish(new LocalPlayerEndpoint("A"), new NetworkRequestLocationHost("town|tavern", "B"));
        fixture.Drain();
        Assert.False(fixture.Hosts.TryGet("town|tavern", out _));
    }

    [Fact]
    public void LocalDeparturePromotesTheRealRemoteMemberAtAHigherMissionEpoch()
    {
        using var fixture = new Fixture();
        fixture.Broker.Publish(fixture, new LocationMissionReady("town|tavern"));
        fixture.Drain();
        fixture.Broker.Publish(fixture.Remote, new NetworkRequestLocationHost("town|tavern", "B"));
        fixture.Drain();
        fixture.Broker.Publish(fixture, new MissionMemberDeparted("A", "town|tavern", true, false));
        fixture.Drain();
        Assert.True(fixture.Hosts.TryGet("town|tavern", out var assignment));
        Assert.Equal("B", assignment.HostControllerId);
        Assert.Equal(2, assignment.Epoch);
        Assert.Single(fixture.Broker.Messages.GetMessages<LocationHostMigrated>());
        Assert.Single(fixture.Broker.Messages.GetMessages<LocationHostAuthorityAcquired>());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SessionMode previous = ModInformation.SessionMode;
        private readonly LocationHostHandler handler;
        public readonly TestMessageBroker Broker = new();
        public readonly TestNetwork Network = new();
        public readonly LiteNetLib.NetPeer Remote;
        public readonly LocationHostRegistry Hosts;

        public Fixture()
        {
            ModInformation.SessionMode = SessionMode.ListenAuthority;
            var controller = new ControllerIdProvider();
            controller.SetControllerId("A");
            var objects = new ObjectManager(Mock.Of<ILogger>());
            var players = new PlayerManager(Mock.Of<ILogger>(), objects, controller);
            var town = ObjectHelper.SkipConstructor<Settlement>();
            var partyA = ObjectHelper.SkipConstructor<MobileParty>();
            var partyB = ObjectHelper.SkipConstructor<MobileParty>();
            partyA._currentSettlement = town;
            partyB._currentSettlement = town;
            Assert.True(objects.AddExisting("town", town, 1));
            Assert.True(objects.AddExisting("partyA", partyA, 2));
            Assert.True(objects.AddExisting("partyB", partyB, 3));
            Assert.True(players.AddPlayer(new Player("A", "", "partyA", "", "")));
            Assert.True(players.AddPlayer(new Player("B", "", "partyB", "", "")));
            Remote = Network.CreatePeer();
            players.SetPeer("B", Remote);
            Hosts = new LocationHostRegistry(controller);
            handler = new LocationHostHandler(Broker, Network, objects, players, Hosts, controller);
        }

        public void Drain() => GameThread.Run(() => { }, blocking: true);
        public void Dispose()
        {
            Drain();
            handler.Dispose();
            ModInformation.SessionMode = previous;
        }
    }
}
