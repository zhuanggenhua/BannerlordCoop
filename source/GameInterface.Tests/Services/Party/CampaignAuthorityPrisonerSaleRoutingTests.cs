using Common;
using Common.Messaging;
using Common.Network;
using Common.PacketHandlers;
using Common.Serialization;
using Common.Tests.Utils;
using Common.Util;
using Coop.Tests.Mocks;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using GameInterface.Services.GameDebug.Messages;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Party;
using GameInterface.Services.Party.Handlers;
using GameInterface.Services.Party.Messages;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using GameInterface.Services.TroopRosters.Data;
using GameInterface.Services.TroopRosters.Interfaces;
using LiteNetLib;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using Xunit;

namespace GameInterface.Tests.Services.Party;

[Collection(ModInformationRoleCollection.Name)]
public sealed class CampaignAuthorityPrisonerSaleRoutingTests
{
    private static readonly AuthorityLease Lease = new("room-a", "world-a", "controller-b", 7);

    static CampaignAuthorityPrisonerSaleRoutingTests()
    {
        RuntimeHelpers.RunModuleConstructor(typeof(TestNetwork).Module.ModuleHandle);
    }

    [Fact]
    public void ObserverDoesNotApplyAReceivedSaleRequestEvenUnderAllowedThread()
    {
        var previousMode = ModInformation.SessionMode;
        try
        {
            ModInformation.SessionMode = SessionMode.RemoteReplica;
            using var network = new TestNetwork();
            var peer = network.CreatePeer();
            var world = new SaleWorld();
            var processor = new RecordingPrisonerSaleProcessor();
            var broker = new TestMessageBroker();
            using var handler = CreateHandler(
                broker,
                world,
                network,
                new Mock<ITroopRosterInterface>().Object,
                processor,
                null,
                null,
                null,
                null,
                null);
            var request = new SellPrisoners(42, EmptyRoster());

            GameThread.Run(() =>
            {
                using (new AllowedThread())
                    broker.Publish(peer, request);
            }, blocking: true);
            GameThread.Run(() => { }, blocking: true);

            Assert.Equal(0, processor.CallCount);
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
        }
    }

    [Fact]
    public void ListenAuthorityObserverRoutesLocalSaleToTheCurrentWriter()
    {
        var previousMode = ModInformation.SessionMode;
        try
        {
            ModInformation.SessionMode = SessionMode.ListenAuthority;
            using var network = new TestNetwork();
            var writerPeer = network.CreatePeer();
            var world = new SaleWorld();
            var origin = new Player("controller-a", "hero-a", "party-a", "clan-a", "character-a");
            var players = new PlayerDirectory();
            players.Add(origin);
            players.SetPeer("controller-b", writerPeer);
            var controller = Controller("controller-a");
            var context = Authority(Lease, controller, out var identity);
            var rosterPort = new Mock<ITroopRosterInterface>();
            rosterPort.Setup(value => value.PackTroopRosterData(It.IsAny<TroopRoster>())).Returns(EmptyRoster());
            var processor = new RecordingPrisonerSaleProcessor();
            var broker = new TestMessageBroker();
            using var handler = CreateHandler(broker, world, network, rosterPort.Object, processor,
                context, players.Manager.Object, controller, identity);

            GameThread.Run(() => broker.Publish(this, new PrisonersSold(world.Party, EmptyNativeRoster())), blocking: true);

            var command = Assert.Single(network.GetPeerMessagesFromType<NetworkCampaignAuthoritySellPrisonersCommand>(writerPeer));
            Assert.True(command.Matches(Lease));
            Assert.Equal("controller-a", command.OriginControllerId);
            Assert.Equal(42u, command.Request.SellingPartyId);
            Assert.Equal(0, processor.CallCount);
            Assert.Empty(network.GetPeerMessagesFromType<SellPrisoners>(writerPeer));
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
        }
    }

    [Fact]
    public void LocalCurrentWriterProcessesItsPlayerSaleOnceWithoutRoutingItself()
    {
        var previousMode = ModInformation.SessionMode;
        try
        {
            ModInformation.SessionMode = SessionMode.ListenAuthority;
            using var network = new TestNetwork();
            var world = new SaleWorld();
            var origin = new Player("controller-b", "hero-b", "party-a", "clan-b", "character-b");
            var players = new PlayerDirectory();
            players.Add(origin);
            var controller = Controller("controller-b");
            var context = Authority(Lease, controller, out var identity);
            var processor = new RecordingPrisonerSaleProcessor();
            var broker = new TestMessageBroker();
            using var handler = CreateHandler(broker, world, network, RosterPort(EmptyRoster(), () => { }).Object,
                processor, context, players.Manager.Object, controller, identity);

            GameThread.Run(() => broker.Publish(this, new PrisonersSold(world.Party, EmptyNativeRoster())), blocking: true);

            Assert.Equal(1, processor.CallCount);
            Assert.Empty(network.SentNetworkMessages);
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
        }
    }

    [Fact]
    public void CurrentWriterAppliesARelayedSaleOnceRejectsDuplicateIntentAndIgnoresReorderedCommand()
    {
        var previousMode = ModInformation.SessionMode;
        try
        {
            ModInformation.SessionMode = SessionMode.RemoteReplica;
            var hostPeer = new TestNetwork().CreatePeer();
            var world = new SaleWorld();
            var players = new PlayerDirectory();
            players.Add(new Player("controller-a", "hero-a", "party-a", "clan-a", "character-a"), hostPeer);
            var controller = Controller("controller-b");
            var context = Authority(Lease, controller, out var identity);
            var broadcasts = new List<IMessage>();
            var network = CreateRoomNetwork(peer => ReferenceEquals(peer, hostPeer), broadcasts);
            int rosterUpdates = 0;
            var rosterPort = RosterPort(EmptyRoster(), () => rosterUpdates++);
            var processor = new RecordingPrisonerSaleProcessor();
            var broker = new TestMessageBroker();
            using var handler = CreateHandler(broker, world, network.Object, rosterPort.Object,
                processor, context, players.Manager.Object, controller, identity);
            var request = Request(Lease, "controller-a", "stream-a", 1);

            PublishOnGameThread(broker, hostPeer, new NetworkCampaignAuthoritySellPrisonersCommand(Lease, 1, request));
            PublishOnGameThread(broker, hostPeer, new NetworkCampaignAuthoritySellPrisonersCommand(Lease, 2, request));
            PublishOnGameThread(broker, hostPeer, new NetworkCampaignAuthoritySellPrisonersCommand(
                Lease, 4, Request(Lease, "controller-a", "stream-a", 2)));
            PublishOnGameThread(broker, hostPeer, new NetworkCampaignAuthoritySellPrisonersCommand(
                Lease, 3, Request(Lease, "controller-a", "stream-a", 3)));

            Assert.Equal(2, processor.CallCount);
            Assert.Equal(2, rosterUpdates);
            Assert.Equal(2, broadcasts.OfType<NetworkCampaignAuthoritySellPrisonersResult>().Count());
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
        }
    }

    [Fact]
    public void ReplacedRoomHostPeerIsRecheckedBeforeCommandSequenceIsConsumed()
    {
        var previousMode = ModInformation.SessionMode;
        try
        {
            ModInformation.SessionMode = SessionMode.RemoteReplica;
            using var peers = new TestNetwork();
            var oldHostPeer = peers.CreatePeer();
            var replacementHostPeer = peers.CreatePeer();
            NetPeer currentHostPeer = oldHostPeer;
            var world = new SaleWorld();
            var players = new PlayerDirectory();
            players.Add(new Player("controller-a", "hero-a", "party-a", "clan-a", "character-a"));
            var controller = Controller("controller-b");
            var context = Authority(Lease, controller, out var identity);
            var network = CreateRoomNetwork(peer => ReferenceEquals(peer, currentHostPeer), new List<IMessage>());
            var processor = new RecordingPrisonerSaleProcessor();
            var broker = new TestMessageBroker();
            using var handler = CreateHandler(broker, world, network.Object, RosterPort(EmptyRoster(), () => { }).Object,
                processor, context, players.Manager.Object, controller, identity);
            var command = new NetworkCampaignAuthoritySellPrisonersCommand(Lease, 1,
                Request(Lease, "controller-a", "stream-peer", 1));

            GameThread.Run(() =>
            {
                Task.Run(() => broker.Publish(oldHostPeer, command)).GetAwaiter().GetResult();
                currentHostPeer = replacementHostPeer;
            }, blocking: true);
            GameThread.Run(() => { }, blocking: true);
            Assert.Equal(0, processor.CallCount);

            PublishOnGameThread(broker, replacementHostPeer, command);

            Assert.Equal(1, processor.CallCount);
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
        }
    }

    [Fact]
    public void QueuedAuthorityCommandDoesNothingAfterHandlerDisposal()
    {
        var previousMode = ModInformation.SessionMode;
        try
        {
            ModInformation.SessionMode = SessionMode.RemoteReplica;
            using var network = new TestNetwork();
            var hostPeer = network.CreatePeer();
            var world = new SaleWorld();
            var players = new PlayerDirectory();
            players.Add(new Player("controller-a", "hero-a", "party-a", "clan-a", "character-a"), hostPeer);
            var controller = Controller("controller-b");
            var context = Authority(Lease, controller, out var identity);
            var broadcasts = new List<IMessage>();
            var networkPort = CreateRoomNetwork(peer => ReferenceEquals(peer, hostPeer), broadcasts);
            int rosterUpdates = 0;
            var processor = new RecordingPrisonerSaleProcessor();
            var broker = new TestMessageBroker();
            var handler = CreateHandler(broker, world, networkPort.Object,
                RosterPort(EmptyRoster(), () => rosterUpdates++).Object,
                processor, context, players.Manager.Object, controller, identity);
            var command = new NetworkCampaignAuthoritySellPrisonersCommand(
                Lease, 1, Request(Lease, "controller-a", "stream-dispose", 1));

            GameThread.Run(() =>
            {
                Task.Run(() => broker.Publish(hostPeer, command)).GetAwaiter().GetResult();
                handler.Dispose();
            }, blocking: true);
            GameThread.Run(() => { }, blocking: true);

            handler.Dispose();
            Assert.Equal(0, processor.CallCount);
            Assert.Equal(0, rosterUpdates);
            Assert.Empty(broadcasts);
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
        }
    }

    [Fact]
    public void RequestStreamWatermarkCapRejectsNewStreamsAndRetainsExistingWatermarks()
    {
        var previousMode = ModInformation.SessionMode;
        try
        {
            ModInformation.SessionMode = SessionMode.ListenAuthority;
            using var network = new TestNetwork();
            var requester = network.CreatePeer();
            var writer = network.CreatePeer();
            var world = new SaleWorld();
            var origin = new Player("controller-a", "hero-a", "party-a", "clan-a", "character-a");
            var players = new PlayerDirectory();
            players.Add(origin, requester);
            players.Add(new Player("controller-b", "hero-b", "party-b", "clan-b", "character-b"), writer);
            var controller = Controller("controller-a");
            var context = Authority(Lease, controller, out var identity);
            var processor = new RecordingPrisonerSaleProcessor();
            var broker = new TestMessageBroker();
            using var handler = CreateHandler(broker, world, network, RosterPort(EmptyRoster(), () => { }).Object,
                processor, context, players.Manager.Object, controller, identity);

            var requests = Enumerable.Range(0, 256)
                .Select(index => Request(Lease, "controller-a", $"stream-cap-{index}", 1))
                .ToArray();
            GameThread.Run(() =>
            {
                foreach (var request in requests)
                    broker.Publish(requester, request);
            }, blocking: true);

            var relayed = network.GetPeerMessagesFromType<NetworkCampaignAuthoritySellPrisonersCommand>(writer).ToList();
            Assert.Equal(256, relayed.Count);

            // An existing stream remains usable at capacity, while its earlier sequence stays rejected.
            PublishOnGameThread(broker, requester,
                Request(Lease, "controller-a", "stream-cap-0", 2));
            int afterExistingStreamAdvance = network.GetPeerMessagesFromType<NetworkCampaignAuthoritySellPrisonersCommand>(writer).Count();
            Assert.Equal(257, afterExistingStreamAdvance);

            PublishOnGameThread(broker, requester, requests[0]);
            PublishOnGameThread(broker, requester,
                Request(Lease, "controller-a", "stream-cap-overflow", 1));

            Assert.Equal(afterExistingStreamAdvance,
                network.GetPeerMessagesFromType<NetworkCampaignAuthoritySellPrisonersCommand>(writer).Count());
            Assert.Equal(0, processor.CallCount);
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
        }
    }

    [Fact]
    public void ExecutedStreamWatermarkCapRejectsNewStreamsAndRetainsReplayWatermarks()
    {
        var previousMode = ModInformation.SessionMode;
        try
        {
            ModInformation.SessionMode = SessionMode.RemoteReplica;
            using var network = new TestNetwork();
            var hostPeer = network.CreatePeer();
            var world = new SaleWorld();
            var players = new PlayerDirectory();
            players.Add(new Player("controller-a", "hero-a", "party-a", "clan-a", "character-a"));
            var controller = Controller("controller-b");
            var context = Authority(Lease, controller, out var identity);
            var broadcasts = new List<IMessage>();
            var networkPort = CreateRoomNetwork(peer => ReferenceEquals(peer, hostPeer), broadcasts);
            var processor = new RecordingPrisonerSaleProcessor();
            var broker = new TestMessageBroker();
            using var handler = CreateHandler(broker, world, networkPort.Object,
                RosterPort(EmptyRoster(), () => { }).Object,
                processor, context, players.Manager.Object, controller, identity);
            var requests = Enumerable.Range(0, 256)
                .Select(index => Request(Lease, "controller-a", $"executed-cap-{index}", 1))
                .ToArray();

            GameThread.Run(() =>
            {
                for (int index = 0; index < requests.Length; index++)
                    broker.Publish(hostPeer, new NetworkCampaignAuthoritySellPrisonersCommand(Lease, index + 1, requests[index]));
            }, blocking: true);
            Assert.Equal(256, processor.CallCount);

            PublishOnGameThread(broker, hostPeer,
                new NetworkCampaignAuthoritySellPrisonersCommand(Lease, 257,
                    Request(Lease, "controller-a", "executed-cap-0", 2)));
            Assert.Equal(257, processor.CallCount);

            PublishOnGameThread(broker, hostPeer,
                new NetworkCampaignAuthoritySellPrisonersCommand(Lease, 258,
                    Request(Lease, "controller-a", "executed-cap-0", 1)));
            PublishOnGameThread(broker, hostPeer,
                new NetworkCampaignAuthoritySellPrisonersCommand(Lease, 259,
                    Request(Lease, "controller-a", "executed-cap-overflow", 1)));

            Assert.Equal(257, processor.CallCount);
            Assert.Equal(257, broadcasts.OfType<NetworkCampaignAuthoritySellPrisonersResult>().Count());
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
        }
    }

    [Fact]
    public void WrongPartyOwnerDoesNotConsumeTheRelaySequence()
    {
        var previousMode = ModInformation.SessionMode;
        try
        {
            ModInformation.SessionMode = SessionMode.RemoteReplica;
            var hostPeer = new TestNetwork().CreatePeer();
            var world = new SaleWorld();
            var origin = new Player("controller-a", "hero-a", "party-other", "clan-a", "character-a");
            var otherParty = world.AddParty("party-other", 43);
            var players = new PlayerDirectory();
            players.Add(origin, hostPeer);
            var controller = Controller("controller-b");
            var context = Authority(Lease, controller, out var identity);
            var network = CreateRoomNetwork(peer => ReferenceEquals(peer, hostPeer), new List<IMessage>());
            var processor = new RecordingPrisonerSaleProcessor();
            var broker = new TestMessageBroker();
            using var handler = CreateHandler(broker, world, network.Object, RosterPort(EmptyRoster(), () => { }).Object,
                processor, context, players.Manager.Object, controller, identity);
            var command = new NetworkCampaignAuthoritySellPrisonersCommand(Lease, 1,
                Request(Lease, "controller-a", "stream-owner", 1));

            PublishOnGameThread(broker, hostPeer, command);
            Assert.Equal(0, processor.CallCount);

            otherParty.Party = world.Party;
            PublishOnGameThread(broker, hostPeer, command);

            Assert.Equal(1, processor.CallCount);
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
        }
    }

    [Fact]
    public void DirectClientRequestIsRevalidatedAgainstTheCurrentPeerBeforeApplying()
    {
        var previousMode = ModInformation.SessionMode;
        try
        {
            ModInformation.SessionMode = SessionMode.ListenAuthority;
            using var peers = new TestNetwork();
            var oldPeer = peers.CreatePeer();
            var currentPeer = peers.CreatePeer();
            var world = new SaleWorld();
            var origin = new Player("controller-b", "hero-b", "party-a", "clan-b", "character-b");
            var players = new PlayerDirectory();
            players.Add(origin, oldPeer);
            players.Add(origin, currentPeer);
            var controller = Controller("controller-a");
            var currentWriterLease = new AuthorityLease("room-a", "world-a", "controller-a", 7);
            var context = Authority(currentWriterLease, controller, out var identity);
            var processor = new RecordingPrisonerSaleProcessor();
            var broker = new TestMessageBroker();
            using var handler = CreateHandler(broker, world, peers, RosterPort(EmptyRoster(), () => { }).Object,
                processor, context, players.Manager.Object, controller, identity);
            var request = Request(currentWriterLease, "controller-b", "stream-reconnect", 1);

            PublishOnGameThread(broker, oldPeer, request);
            Assert.Equal(0, processor.CallCount);
            Assert.Empty(peers.SentNetworkMessages);

            PublishOnGameThread(broker, currentPeer, request);
            PublishOnGameThread(broker, currentPeer, request);

            Assert.Equal(1, processor.CallCount);
            Assert.Empty(peers.SentNetworkMessages);
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
        }
    }

    [Fact]
    public void StaleEpochAndFrozenWriterCommandsDoNotReachTheProcessor()
    {
        var previousMode = ModInformation.SessionMode;
        try
        {
            ModInformation.SessionMode = SessionMode.RemoteReplica;
            using var network = new TestNetwork();
            var hostPeer = network.CreatePeer();
            var world = new SaleWorld();
            var players = new PlayerDirectory();
            players.Add(new Player("controller-a", "hero-a", "party-a", "clan-a", "character-a"), hostPeer);
            var controller = Controller("controller-b");
            var context = Authority(Lease, controller, out var identity);
            Assert.True(identity.TryFreezeLocalAuthority(Lease, 9));
            var processor = new RecordingPrisonerSaleProcessor();
            var broker = new TestMessageBroker();
            using var handler = CreateHandler(broker, world, network, RosterPort(EmptyRoster(), () => { }).Object,
                processor, context, players.Manager.Object, controller, identity);
            var priorEpoch = new AuthorityLease(Lease.RoomId, Lease.AuthorityId, Lease.AuthorityControllerId, Lease.Epoch - 1);
            var stale = new NetworkCampaignAuthoritySellPrisonersCommand(
                Lease, 1, Request(priorEpoch, "controller-a", "stream-stale", 1));
            var frozen = new NetworkCampaignAuthoritySellPrisonersCommand(
                Lease, 2, Request(Lease, "controller-a", "stream-frozen", 2));

            PublishOnGameThread(broker, hostPeer, stale);
            PublishOnGameThread(broker, hostPeer, frozen);

            Assert.Equal(0, processor.CallCount);
            Assert.Empty(network.SentNetworkMessages);
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
        }
    }

    [Fact]
    public void RefusalResultRequiresTheCurrentWriterPeerAndOnlyNotifiesTheOrigin()
    {
        var previousMode = ModInformation.SessionMode;
        try
        {
            ModInformation.SessionMode = SessionMode.ListenAuthority;
            using var peers = new TestNetwork();
            var oldWriterPeer = peers.CreatePeer();
            var currentWriterPeer = peers.CreatePeer();
            var world = new SaleWorld();
            var origin = new Player("controller-a", "hero-a", "party-a", "clan-a", "character-a");
            var writer = new Player("controller-b", "hero-b", "party-b", "clan-b", "character-b");
            var players = new PlayerDirectory();
            players.Add(origin);
            players.Add(writer, oldWriterPeer);
            var controller = Controller("controller-a");
            var context = Authority(Lease, controller, out var identity);
            var processor = new RecordingPrisonerSaleProcessor();
            var broker = new TestMessageBroker();
            using var handler = CreateHandler(broker, world, peers, RosterPort(EmptyRoster(), () => { }).Object,
                processor, context, players.Manager.Object, controller, identity);

            GameThread.Run(() => broker.Publish(this, new PrisonersSold(world.Party, EmptyNativeRoster())), blocking: true);
            var request = Assert.Single(peers.GetPeerMessagesFromType<NetworkCampaignAuthoritySellPrisonersCommand>(oldWriterPeer)).Request;
            var refusal = new NetworkCampaignAuthoritySellPrisonersResult(
                Lease, request, false, "Selling co-op player prisoners is unavailable.");
            players.Add(writer, currentWriterPeer);

            PublishOnGameThread(broker, oldWriterPeer, refusal);
            Assert.Empty(broker.GetMessagesFromType<SendInformationMessage>());

            PublishOnGameThread(broker, currentWriterPeer, refusal);
            PublishOnGameThread(broker, currentWriterPeer, refusal);

            Assert.Single(broker.GetMessagesFromType<SendInformationMessage>());
            Assert.Equal(0, processor.CallCount);
            Assert.Empty(peers.SentNetworkMessages.GetValueOrDefault(currentWriterPeer.Id, new List<IMessage>()));
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
        }
    }

    [Fact]
    public void RemoteAuthorityRefusesMixedPlayerAndRegularPrisonersBeforeRosterUpdate()
    {
        var previousMode = ModInformation.SessionMode;
        try
        {
            ModInformation.SessionMode = SessionMode.RemoteReplica;
            var hostPeer = new TestNetwork().CreatePeer();
            var world = new SaleWorld();
            var playerHero = new Hero();
            var playerCharacter = ObjectHelper.SkipConstructor<CharacterObject>();
            playerCharacter.HeroObject = playerHero;
            var regularCharacter = ObjectHelper.SkipConstructor<CharacterObject>();
            world.AddCharacter(101, playerCharacter);
            world.AddCharacter(102, regularCharacter);
            world.Party.PrisonRoster.data = new[]
            {
                new TroopRosterElement(playerCharacter) { Number = 1, WoundedNumber = 0 },
                new TroopRosterElement(regularCharacter) { Number = 2, WoundedNumber = 0 },
            };
            world.Party.PrisonRoster._count = 2;
            var origin = new Player("controller-a", "hero-a", "party-a", "clan-a", "character-a");
            var players = new PlayerDirectory();
            players.Add(origin, hostPeer);
            players.AddControlledObject(playerHero);
            var controller = Controller("controller-b");
            var context = Authority(Lease, controller, out var identity);
            var broadcasts = new List<IMessage>();
            var network = CreateRoomNetwork(peer => ReferenceEquals(peer, hostPeer), broadcasts);
            int rosterUpdates = 0;
            var processor = new RecordingPrisonerSaleProcessor();
            var broker = new TestMessageBroker();
            using var handler = CreateHandler(broker, world, network.Object, RosterPort(EmptyRoster(), () => rosterUpdates++).Object,
                processor, context, players.Manager.Object, controller, identity);
            var packed = new TroopRosterData(new[]
            {
                new TroopRosterElementData(101, 1, 0, 0),
                new TroopRosterElementData(102, 2, 0, 0),
            });
            var request = Request(Lease, "controller-a", "stream-mixed", 1, packed);
            int handleCount = world.HandleMap.Count;
            int playerPrisoners = world.Party.PrisonRoster.GetTroopCount(playerCharacter);
            int regularPrisoners = world.Party.PrisonRoster.GetTroopCount(regularCharacter);

            PublishOnGameThread(broker, hostPeer, new NetworkCampaignAuthoritySellPrisonersCommand(Lease, 1, request));

            var result = Assert.Single(broadcasts.OfType<NetworkCampaignAuthoritySellPrisonersResult>());
            Assert.False(result.Applied);
            Assert.Contains("co-op player prisoners", result.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, processor.CallCount);
            Assert.Equal(0, rosterUpdates);
            Assert.Equal(handleCount, world.HandleMap.Count);
            Assert.Equal(playerPrisoners, world.Party.PrisonRoster.GetTroopCount(playerCharacter));
            Assert.Equal(regularPrisoners, world.Party.PrisonRoster.GetTroopCount(regularCharacter));
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
        }
    }

    [Fact]
    public void LegacyTwoFieldRequestRemainsLimitedToDedicatedBootstrap()
    {
        var previousMode = ModInformation.SessionMode;
        try
        {
            ModInformation.SessionMode = SessionMode.DedicatedAuthority;
            using var network = new TestNetwork();
            var peer = network.CreatePeer();
            var world = new SaleWorld();
            var players = new PlayerDirectory();
            players.Add(new Player("controller-a", "hero-a", "party-a", "clan-a", "character-a"), peer);
            var processor = new RecordingPrisonerSaleProcessor();
            var broker = new TestMessageBroker();
            using var handler = CreateHandler(broker, world, network, RosterPort(EmptyRoster(), () => { }).Object,
                processor, null, players.Manager.Object, null, null, null);

            Assert.True(players.Manager.Object.TryGetPlayer(peer, out var owner));
            Assert.True(players.Manager.Object.TryGetPeer(owner.ControllerId, out var associatedPeer));
            Assert.Same(peer, associatedPeer);
            Assert.True(players.Manager.Object.ValidateCurrentPartySender(peer, owner.MobilePartyId));
            Assert.True(world.Objects.Object.TryGetObjectWithLogging<PartyBase>(42, out _));

            PublishOnGameThread(broker, peer, new SellPrisoners(42, EmptyRoster()));

            Assert.Equal(1, processor.CallCount);
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
        }
    }

    [Fact]
    public void LegacyTwoFieldRequestIsRejectedByListenAuthority()
    {
        var previousMode = ModInformation.SessionMode;
        try
        {
            ModInformation.SessionMode = SessionMode.ListenAuthority;
            using var network = new TestNetwork();
            var peer = network.CreatePeer();
            var world = new SaleWorld();
            var players = new PlayerDirectory();
            players.Add(new Player("controller-a", "hero-a", "party-a", "clan-a", "character-a"), peer);
            var processor = new RecordingPrisonerSaleProcessor();
            var broker = new TestMessageBroker();
            using var handler = CreateHandler(broker, world, network, RosterPort(EmptyRoster(), () => { }).Object,
                processor, null, players.Manager.Object, null, null, null);

            PublishOnGameThread(broker, peer, new SellPrisoners(42, EmptyRoster()));

            Assert.Equal(0, processor.CallCount);
            Assert.Single(network.GetPeerMessagesFromType<SendInformationMessage>(peer));
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
        }
    }

    [Fact]
    public void AppendedLeaseAndRequestFieldsRoundTripWithoutChangingLegacyFields()
    {
        var serializer = new ProtoBufSerializer(new SerializableTypeMapper());
        var request = Request(Lease, "controller-a", "stream-wire", 19);

        var received = Assert.IsType<SellPrisoners>(
            serializer.Deserialize<IMessage>(MessagePacket.Create(request, serializer).Data));

        Assert.Equal(42u, received.SellingPartyId);
        Assert.True(received.MatchesLease(Lease));
        Assert.Equal("controller-a", received.OriginControllerId);
        Assert.Equal(request.RequestStreamId, received.RequestStreamId);
        Assert.Equal(19, received.RequestSequence);
        Assert.True(new SellPrisoners(42, EmptyRoster()).IsLegacy);
        Assert.False(new SellPrisoners(42, EmptyRoster(), Lease, "controller-a", new string('x', 4096), 1)
            .MatchesLease(Lease));

        var authorityCommand = new NetworkCampaignAuthoritySellPrisonersCommand(Lease, 23, request);
        var receivedCommand = Assert.IsType<NetworkCampaignAuthoritySellPrisonersCommand>(
            serializer.Deserialize<IMessage>(MessagePacket.Create(authorityCommand, serializer).Data));
        Assert.True(receivedCommand.Matches(Lease));
        Assert.Equal(23, receivedCommand.Sequence);
        Assert.Equal(request.RequestSequence, receivedCommand.Request.RequestSequence);

        var authorityResult = new NetworkCampaignAuthoritySellPrisonersResult(
            Lease, request, false, "unsupported player ransom");
        var receivedResult = Assert.IsType<NetworkCampaignAuthoritySellPrisonersResult>(
            serializer.Deserialize<IMessage>(MessagePacket.Create(authorityResult, serializer).Data));
        Assert.True(receivedResult.Matches(Lease));
        Assert.Equal(request.RequestStreamId, receivedResult.RequestStreamId);
        Assert.Equal(request.RequestSequence, receivedResult.RequestSequence);
        Assert.False(receivedResult.Applied);
        Assert.Equal("unsupported player ransom", receivedResult.Reason);
    }

    private static SellPrisonersHandler CreateHandler(
        IMessageBroker broker,
        SaleWorld world,
        INetwork network,
        ITroopRosterInterface rosterPort,
        IPrisonerSaleProcessor processor,
        ICampaignAuthorityContext authority = null,
        IPlayerManager players = null,
        IControllerIdProvider controller = null,
        ISessionIdentity identity = null,
        Common.Network.Coalescing.ISendCoalescer coalescer = null) =>
        new(broker, world.Objects.Object, network, rosterPort, processor, coalescer,
            authority, players, controller, identity);

    private static CampaignAuthorityContext Authority(
        AuthorityLease lease,
        ControllerIdProvider controller,
        out SessionIdentity identity)
    {
        identity = new SessionIdentity();
        Assert.True(identity.TryApply(lease, "controller-a"));
        return new CampaignAuthorityContext(identity, controller);
    }

    private static ControllerIdProvider Controller(string controllerId)
    {
        var controller = new ControllerIdProvider();
        controller.SetControllerId(controllerId);
        return controller;
    }

    private static Mock<ITroopRosterInterface> RosterPort(TroopRosterData packed, Action updated)
    {
        var rosterPort = new Mock<ITroopRosterInterface>();
        rosterPort.Setup(value => value.PackTroopRosterData(It.IsAny<TroopRoster>())).Returns(packed);
        rosterPort.Setup(value => value.UpdateWithData(
                It.IsAny<TroopRoster>(),
                It.IsAny<TroopRosterData>(),
                It.IsAny<Hero>()))
            .Callback<TroopRoster, TroopRosterData, Hero>((_, _, _) => updated());
        return rosterPort;
    }

    private static Mock<INetwork> CreateRoomNetwork(Func<NetPeer, bool> isCurrentHost, List<IMessage> broadcasts)
    {
        var network = new Mock<INetwork>();
        network.As<IRoomHostPeerProvider>()
            .Setup(value => value.IsCurrentRoomHostPeer(It.IsAny<NetPeer>()))
            .Returns<NetPeer>(peer => isCurrentHost(peer));
        network.Setup(value => value.SendAll(It.IsAny<IMessage>()))
            .Callback<IMessage>(message => broadcasts.Add(message));
        return network;
    }

    private static void PublishOnGameThread<T>(TestMessageBroker broker, NetPeer peer, T message)
        where T : IMessage =>
        GameThread.Run(() => broker.Publish(peer, message), blocking: true);

    private static SellPrisoners Request(
        AuthorityLease lease,
        string origin,
        string stream,
        long sequence,
        TroopRosterData? packed = null) =>
        new(42, packed ?? EmptyRoster(), lease, origin, NormalizeStreamId(stream), sequence);

    private static string NormalizeStreamId(string stream) =>
        new Guid(MD5.HashData(Encoding.UTF8.GetBytes(stream))).ToString("N");

    private static TroopRosterData EmptyRoster() => new(Array.Empty<TroopRosterElementData>());

    private static TroopRoster EmptyNativeRoster() => ObjectHelper.SkipConstructor<TroopRoster>();

    private sealed class RecordingPrisonerSaleProcessor : IPrisonerSaleProcessor
    {
        public int CallCount { get; private set; }

        public void Sell(PartyBase sellingParty, TroopRoster requestedPrisoners) => CallCount++;
    }

    private sealed class PlayerDirectory
    {
        private readonly Dictionary<string, Player> byController = new();
        private readonly Dictionary<string, NetPeer> byPeerId = new();
        private readonly List<(NetPeer peer, Player player)> byPeer = new();
        private readonly HashSet<object> controlledObjects = new();

        public Mock<IPlayerManager> Manager { get; } = new();

        public PlayerDirectory()
        {
            Manager.Setup(value => value.TryGetPlayer(It.IsAny<string>(), out It.Ref<Player>.IsAny))
                .Returns((string id, out Player player) => byController.TryGetValue(id, out player));
            Manager.Setup(value => value.TryGetPeer(It.IsAny<string>(), out It.Ref<NetPeer>.IsAny))
                .Returns((string id, out NetPeer peer) => byPeerId.TryGetValue(id, out peer));
            Manager.Setup(value => value.TryGetPlayer(It.IsAny<NetPeer>(), out It.Ref<Player>.IsAny))
                .Returns((NetPeer peer, out Player player) =>
                {
                    foreach (var entry in byPeer)
                    {
                        if (ReferenceEquals(entry.peer, peer))
                        {
                            player = entry.player;
                            return true;
                        }
                    }

                    player = null;
                    return false;
                });
            Manager.Setup(value => value.Contains(It.IsAny<object>()))
                .Returns<object>(value => controlledObjects.Contains(value));
            Manager.Setup(value => value.IsReservedController(It.IsAny<string>())).Returns(false);
        }

        public void Add(Player player, NetPeer peer = null)
        {
            byController[player.ControllerId] = player;
            if (peer != null)
            {
                byPeerId[player.ControllerId] = peer;
                byPeer.Add((peer, player));
            }
        }

        public void SetPeer(string controllerId, NetPeer peer) => byPeerId[controllerId] = peer;

        public void AddControlledObject(object controlledObject) => controlledObjects.Add(controlledObject);
    }

    private sealed class SaleWorld
    {
        private readonly Dictionary<uint, PartyBase> partiesByHandle = new();
        private readonly Dictionary<string, MobileParty> mobilePartiesById = new();
        private readonly Dictionary<uint, CharacterObject> charactersByHandle = new();
        private readonly Dictionary<object, uint> handlesByObject = new();
        private readonly Dictionary<string, uint> handleMap = new();

        public Mock<IObjectManager> Objects { get; } = new();
        public PartyBase Party { get; }
        public MobileParty MobileParty { get; }
        public Dictionary<string, uint> HandleMap => handleMap;

        public SaleWorld()
        {
            Party = ObjectHelper.SkipConstructor<PartyBase>();
            MobileParty = ObjectHelper.SkipConstructor<MobileParty>();
            MobileParty.Party = Party;
            Party.PrisonRoster = new TroopRoster();
            AddParty("party-a", 42, Party, MobileParty);

            Objects.Setup(value => value.TryGetObject<PartyBase>(It.IsAny<uint>(), out It.Ref<PartyBase>.IsAny))
                .Returns((uint handle, out PartyBase party) => partiesByHandle.TryGetValue(handle, out party));
            Objects.Setup(value => value.TryGetObjectWithLogging<PartyBase>(It.IsAny<uint>(), out It.Ref<PartyBase>.IsAny))
                .Returns((uint handle, out PartyBase party) => partiesByHandle.TryGetValue(handle, out party));
            Objects.Setup(value => value.TryGetObject<MobileParty>(It.IsAny<string>(), out It.Ref<MobileParty>.IsAny))
                .Returns((string id, out MobileParty party) => mobilePartiesById.TryGetValue(id, out party));
            Objects.Setup(value => value.TryGetObject<CharacterObject>(It.IsAny<uint>(), out It.Ref<CharacterObject>.IsAny))
                .Returns((uint handle, out CharacterObject character) => charactersByHandle.TryGetValue(handle, out character));
            Objects.Setup(value => value.TryGetHandle(It.IsAny<object>(), out It.Ref<uint>.IsAny))
                .Returns((object value, out uint handle) => handlesByObject.TryGetValue(value, out handle));
            Objects.Setup(value => value.GetHandleMap()).Returns(handleMap);
            Objects.Setup(value => value.TryGetId(It.IsAny<Hero>(), out It.Ref<string>.IsAny))
                .Returns((Hero hero, out string id) =>
                {
                    id = "hero-a";
                    return hero != null;
                });
        }

        public MobileParty AddParty(string id, uint handle) =>
            AddParty(id, handle, ObjectHelper.SkipConstructor<PartyBase>(), ObjectHelper.SkipConstructor<MobileParty>());

        private MobileParty AddParty(string id, uint handle, PartyBase party, MobileParty mobileParty)
        {
            mobileParty.Party = party;
            party.PrisonRoster ??= new TroopRoster();
            partiesByHandle[handle] = party;
            mobilePartiesById[id] = mobileParty;
            handlesByObject[party] = handle;
            handleMap[id] = handle;
            return mobileParty;
        }

        public void AddCharacter(uint handle, CharacterObject character)
        {
            charactersByHandle[handle] = character;
            handleMap["character-" + handle] = handle;
        }
    }
}
