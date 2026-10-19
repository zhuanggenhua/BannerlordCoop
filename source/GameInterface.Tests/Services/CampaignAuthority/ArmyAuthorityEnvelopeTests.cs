using Common;
using Common.Messaging;
using Common.Network;
using Common.Network.Session;
using Common.PacketHandlers;
using Common.Serialization;
using Coop.Core.Common.Network;
using Coop.Core.Server.Connections;
using Coop.Core.Server.Services.Session;
using Coop.Tests.Mocks;
using GameInterface.Services.Armies.Messages;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using Moq;
using System;
using System.Runtime.CompilerServices;
using Xunit;

namespace GameInterface.Tests.Services.CampaignAuthority;

public class ArmyAuthorityEnvelopeTests
{
    static ArmyAuthorityEnvelopeTests() =>
        RuntimeHelpers.RunModuleConstructor(typeof(TestNetwork).Module.ModuleHandle);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void OriginalSenderPacketAndRelayEnforceArmyEpochAndSequence(int kind)
    {
        var serializer = new ProtoBufSerializer(new SerializableTypeMapper());
        var identity = new SessionIdentity();
        var lease = new AuthorityLease("room", "writer-B", "B", 2);
        Assert.True(identity.TryApply(lease, "A"));
        var controller = new ControllerIdProvider();
        controller.SetControllerId("B");
        var sender = new CampaignAuthorityMessageSender(serializer, new CampaignAuthorityContext(identity, controller));
        IMessage message = kind switch
        {
            0 => new NetworkAddMobilePartyInArmy("Army160", "party", false),
            1 => new NetworkRemovePartyInArmy("Army160", "party", null),
            2 => new NetworkSetArmyKingdom("Army160", null),
            _ => new NetworkSetArmyAiBehaviorObject("Army160", null, false)
        };
        Assert.IsAssignableFrom<ICampaignAuthorityScopedMessage>(message);
        MessagePacket packet = default;
        sender.Send(message, value => packet = value);
        var envelope = serializer.Deserialize<NetworkCampaignAuthoritySyncEnvelope>(packet.Data);
        Assert.Equal(2, envelope.Epoch);
        Assert.Equal(1, envelope.Sequence);
        Assert.Equal(message.GetType(), serializer.Deserialize<IMessage>(envelope.Payload).GetType());

        using var broker = new MessageBroker();
        var peer = new TestNetwork().CreatePeer();
        var player = new Player("B", "hero-B", "party-B", "clan-B", "character-B");
        var players = new Mock<IPlayerManager>();
        players.Setup(value => value.TryGetPlayer(peer, out player)).Returns(true);
        var connections = new Mock<IConnectionCollection>();
        connections.Setup(value => value.HasCompletedCampaignSynchronization(peer)).Returns(true);
        var network = new Mock<INetwork>();
        int applied = 0;
        Action<MessagePayload<NetworkAddMobilePartyInArmy>> add = _ => applied++;
        Action<MessagePayload<NetworkRemovePartyInArmy>> remove = _ => applied++;
        Action<MessagePayload<NetworkSetArmyKingdom>> kingdom = _ => applied++;
        Action<MessagePayload<NetworkSetArmyAiBehaviorObject>> objective = _ => applied++;
        broker.Subscribe(add); broker.Subscribe(remove); broker.Subscribe(kingdom); broker.Subscribe(objective);
        using var relay = new CampaignAuthoritySyncRelayServerHandler(broker, connections.Object, players.Object, network.Object, identity, serializer);
        broker.Publish(peer, envelope);
        GameThread.Run(() => { }, blocking: true);
        Assert.Equal(1, applied);
        // Duplicate, stale Epoch and wrong writer never reach the original Army apply subscription.
        broker.Publish(peer, envelope);
        broker.Publish(peer, new NetworkCampaignAuthoritySyncEnvelope("room", "writer-old", "B", 1, 99, envelope.Payload));
        broker.Publish(peer, new NetworkCampaignAuthoritySyncEnvelope("room", "writer-B", "other", 2, 99, envelope.Payload));
        GameThread.Run(() => { }, blocking: true);
        Assert.Equal(1, applied);
        network.Verify(value => value.SendAllBut(peer, It.Is<NetworkCampaignAuthoritySyncEnvelope>(item => item.Epoch == 2 && item.Sequence == 1)), Times.Once);
    }
}
