using Common;
using Common.Tests.Utils;
using Common.Util;
using Coop.Tests.Mocks;
using GameInterface.Services.Entity;
using GameInterface.Services.MapEvents;
using GameInterface.Services.MapEvents.TroopSupply;
using GameInterface.Services.MapEvents.TroopSupply.Messages;
using GameInterface.Services.Missions;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using HarmonyLib;
using Missions.Battles;
using Missions.Messages;
using Moq;
using Serilog;
using System;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;
using Xunit;

namespace Coop.Tests.Missions.Battles;

[Collection(ModInformationRoleCollection.Name)]
public class LocalMissionEndpointTests
{
    [Fact]
    public void LocalMissionReadyElectsAndReceivesOriginalReserveRepliesOnce()
    {
        using var fixture = new Fixture();
        fixture.Broker.Publish(fixture, new BattleMissionReady("battle"));
        fixture.Drain();
        Assert.True(fixture.Hosts.TryGet("battle", out var assignment));
        Assert.Equal("A", assignment.HostControllerId);
        Assert.Equal(1, assignment.Epoch);
        Assert.Single(fixture.Broker.Messages.GetMessages<BattleHostAuthorityAcquired>());
        Assert.Equal(2, fixture.Broker.Messages.GetMessageCount<NetworkBattleTroopReserve>());
        fixture.Reserves.Verify(value => value.GetOwnedReserves(fixture.Event, "A", true,
            It.IsAny<System.Collections.Generic.IReadOnlyCollection<string>>(),
            It.IsAny<System.Collections.Generic.IReadOnlyCollection<string>>()), Times.Once);
        Assert.Empty(fixture.Network.GetPeerMessagesFromType<NetworkRequestBattleHost>(fixture.Remote));
        Assert.Empty(fixture.Network.GetPeerMessagesFromType<NetworkBattleTroopReserve>(fixture.Remote));
    }

    [Fact]
    public void LocalAssignmentKeepsMissionEpochIndependentAndRejectsOlderAssignments()
    {
        using var fixture = new Fixture();
        fixture.Broker.Publish(fixture, new NetworkBattleHostAssigned("battle", "B", Array.Empty<string>(), 7));
        fixture.Drain();
        fixture.Broker.Publish(fixture, new NetworkBattleHostAssigned("battle", "A", Array.Empty<string>(), 8));
        fixture.Drain();
        fixture.Broker.Publish(fixture, new NetworkBattleHostAssigned("battle", "B", Array.Empty<string>(), 7));
        fixture.Drain();
        Assert.True(fixture.Hosts.TryGet("battle", out var assignment));
        Assert.Equal("A", assignment.HostControllerId);
        Assert.Equal(8, assignment.Epoch);
        Assert.Single(fixture.Broker.Messages.GetMessages<BattleHostAuthorityAcquired>());
        Assert.Single(fixture.Broker.Messages.GetMessages<BattleHostMigrated>());
    }

    [Fact]
    public void LocalRequestCannotUseAnotherRegisteredPlayersIdentity()
    {
        using var fixture = new Fixture();
        fixture.Broker.Publish(new LocalPlayerEndpoint("B"), new NetworkRequestBattleHost("battle", "B"));
        fixture.Drain();
        Assert.False(fixture.Hosts.TryGet("battle", out _));
        fixture.Reserves.VerifyNoOtherCalls();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SessionMode previous = ModInformation.SessionMode;
        private readonly BattleHostHandler handler;
        public readonly TestMessageBroker Broker = new();
        public readonly TestNetwork Network = new();
        public readonly LiteNetLib.NetPeer Remote;
        public readonly BattleHostRegistry Hosts;
        public readonly MapEvent Event = ObjectHelper.SkipConstructor<MapEvent>();
        public readonly Mock<IBattleTroopReserveBuilder> Reserves = new();

        public Fixture()
        {
            ModInformation.SessionMode = SessionMode.ListenAuthority;
            var controller = new ControllerIdProvider();
            controller.SetControllerId("A");
            var objects = new ObjectManager(Mock.Of<ILogger>());
            var players = new PlayerManager(Mock.Of<ILogger>(), objects, controller);
            var party = ObjectHelper.SkipConstructor<MobileParty>();
            party.Party = ObjectHelper.SkipConstructor<PartyBase>();
            party.Party.MobileParty = party;
            var participant = ObjectHelper.SkipConstructor<MapEventParty>();
            participant.Party = party.Party;
            var attack = ObjectHelper.SkipConstructor<MapEventSide>();
            var defend = ObjectHelper.SkipConstructor<MapEventSide>();
            AccessTools.Field(typeof(MapEventSide), nameof(MapEventSide._battleParties)).SetValue(attack, new MBList<MapEventParty> { participant });
            AccessTools.Field(typeof(MapEventSide), nameof(MapEventSide._battleParties)).SetValue(defend, new MBList<MapEventParty>());
            Event._sides = new[] { defend, attack };
            Event._mapEventType = MapEvent.BattleTypes.FieldBattle;
            Assert.True(objects.AddExisting("battle", Event, 1));
            Assert.True(objects.AddExisting("partyA", party, 2));
            Assert.True(players.AddPlayer(new Player("A", "", "partyA", "", "")));
            Remote = Network.CreatePeer();
            Hosts = new BattleHostRegistry(controller);
            Reserves.Setup(value => value.GetOwnedReserves(Event, "A", true,
                It.IsAny<System.Collections.Generic.IReadOnlyCollection<string>>(),
                It.IsAny<System.Collections.Generic.IReadOnlyCollection<string>>()))
                .Returns(new[] {
                    new SideReserve(BattleSideEnum.Attacker, Array.Empty<PartyReserve>()),
                    new SideReserve(BattleSideEnum.Defender, Array.Empty<PartyReserve>()) });
            handler = new BattleHostHandler(Broker, Network, objects, players, Hosts, controller,
                Reserves.Object, Mock.Of<IBattleTroopLedger>(), Mock.Of<IBattleSizeProvider>());
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
