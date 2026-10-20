using Common;
using Common.Messaging;
using Common.Network;
using Coop.Tests.Mocks;
using Moq;
using Common.Network.Session;
using Common.Serialization;
using Common.Session;
using Common.Tests.Utils;
using Coop.Core.Client.Services.Session;
using Coop.Tests.Network;
using GameInterface.Services.MobileParties.Data;
using GameInterface.Services.MobileParties.Messages.Behavior;
using GameInterface.Surrogates;
using Xunit;

namespace Coop.Tests.Client.Services.Session;

public class CampaignAuthoritySyncEnvelopeClientHandlerTests
{
    [Fact]
    public void DeliversGenericObjectCreationToConcreteProductionBrokerSubscribers()
    {
        var broker = new MessageBroker();
        var serializer = new ProtoBufSerializer(new SerializableTypeMapper());
        var identity = new SessionIdentity();
        Assert.True(identity.TryApply(new AuthorityLease("room", "authority", "host", 2), "host"));
        var received = new System.Collections.Generic.List<uint>();
        broker.Subscribe<global::GameInterface.Registry.Auto.NetworkCreateInstance<TaleWorlds.CampaignSystem.MapEvents.MapEvent>>(
            payload => received.Add(payload.What.InstanceHandle));
        using var handler = new CampaignAuthoritySyncEnvelopeClientHandler(broker, identity, serializer);
        var message = new global::GameInterface.Registry.Auto.NetworkCreateInstance<TaleWorlds.CampaignSystem.MapEvents.MapEvent>("Created_1119", 45029);
        broker.Publish(this, new NetworkCampaignAuthoritySyncEnvelope("room", "authority", "host", 2, 1, serializer.Serialize(message)));
        GameThread.Run(() => { }, blocking: true);
        Assert.Equal(new[] { 45029u }, received);
    }

    [Fact]
    public void LeaseTransitionsPreserveFinalOldEpochDeltaAndFirstNewEpochDeltaInApplyOrder()
    {
        var broker = new MessageBroker();
        var serializer = new ProtoBufSerializer(new SerializableTypeMapper());
        var identity = new SessionIdentity();
        Assert.True(identity.TryApply(new AuthorityLease("room-a", "authority-a", "host", 4), "host"));
        var applied = new System.Collections.Generic.List<int>();
        broker.Subscribe<CoopNetworkBaseTests.AuthorityProbeMessage>(payload => applied.Add(payload.What.Value));
        using var envelopes = new CampaignAuthoritySyncEnvelopeClientHandler(broker, identity, serializer);
        var network = new Mock<INetwork>();var roomPeer = CampaignHandoffTestServices.AuthenticateRoomPeer(network);
        using var snapshots = new AuthorityLeaseSnapshotHandler(broker, identity, network: network.Object);
        using var entered = new System.Threading.ManualResetEventSlim();
        using var release = new System.Threading.ManualResetEventSlim();

        GameThread.EnqueueSafe(() =>
        {
            entered.Set();
            Assert.True(release.Wait(System.TimeSpan.FromSeconds(5)));
        });
        try
        {
            Assert.True(entered.Wait(System.TimeSpan.FromSeconds(5)));
            broker.Publish(this, CreateEnvelope(serializer, "authority-a", 4, 1, 41));
            broker.Publish(roomPeer, new NetworkAuthorityLeaseSnapshot(
                new AuthorityLease("room-a", "authority-paused", string.Empty, 5, isPaused: true), "host"));
            broker.Publish(roomPeer, new NetworkAuthorityLeaseSnapshot(
                new AuthorityLease("room-a", "authority-b", "host", 6), "host"));
            broker.Publish(this, CreateEnvelope(serializer, "authority-b", 6, 1, 61));
        }
        finally
        {
            release.Set();
        }
        GameThread.Run(() => { }, blocking: true);

        Assert.Equal(new[] { 41, 61 }, applied);
        Assert.Equal(6, identity.Epoch);
    }

    [Fact]
    public void AppliesRemoteAuthorityPartyBehaviorDeltaUnderConfirmedLease()
    {
        _ = new SurrogateCollection();
        var messageBroker = new MessageBroker();
        var serializer = new ProtoBufSerializer(new SerializableTypeMapper());
        var identity = new SessionIdentity();
        Assert.True(identity.TryApply(new AuthorityLease("room-a", "authority-b", "guest", 2), "host"));
        NetworkCampaignAuthorityPartyBehaviorDelta applied = default;
        object appliedSource = null;
        messageBroker.Subscribe<NetworkCampaignAuthorityPartyBehaviorDelta>(payload =>
        {
            applied = payload.What;
            appliedSource = payload.Who;
        });
        using var handler = new CampaignAuthoritySyncEnvelopeClientHandler(messageBroker, identity, serializer);
        var source = new object();
        var delta = new NetworkCampaignAuthorityPartyBehaviorDelta(
            new NetworkPartyBehaviorUpdateData { MobilePartyId = 42 });
        var envelope = new NetworkCampaignAuthoritySyncEnvelope(
            "room-a", "authority-b", "guest", 2, 1, serializer.Serialize(delta));

        messageBroker.Publish(source, envelope);
        GameThread.Run(() => { }, blocking: true);

        Assert.Equal(42u, applied.BehaviorUpdateData.MobilePartyId);
        Assert.Same(source, appliedSource);
    }

    [Fact]
    public void AppliesOnlyCurrentLeaseEnvelopesAndRejectsReplays()
    {
        var messageBroker = new MessageBroker();
        var serializer = new ProtoBufSerializer(new SerializableTypeMapper());
        var identity = new SessionIdentity();
        Assert.True(identity.TryApply(new AuthorityLease("room-a", "authority-a", "host", 4), "host"));
        var appliedValues = new System.Collections.Generic.List<int>();
        object messageSource = new object();
        object appliedSource = null;
        messageBroker.Subscribe<CoopNetworkBaseTests.AuthorityProbeMessage>(
            payload =>
            {
                appliedValues.Add(payload.What.Value);
                appliedSource = payload.Who;
            });

        using var handler = new CampaignAuthoritySyncEnvelopeClientHandler(messageBroker, identity, serializer);
        var current = CreateEnvelope(serializer, "authority-a", 4, 1, 41);
        messageBroker.Publish(messageSource, current);
        messageBroker.Publish(messageSource, current);
        messageBroker.Publish(this, CreateEnvelope(serializer, "authority-old", 3, 90, 39));
        GameThread.Run(() => { }, blocking: true);

        Assert.Equal(new[] { 41 }, appliedValues);
        Assert.Same(messageSource, appliedSource);

        Assert.True(identity.TryApply(new AuthorityLease("room-a", "authority-b", "host", 5), "host"));
        messageBroker.Publish(this, CreateEnvelope(serializer, "authority-a", 4, 2, 42));
        messageBroker.Publish(this, CreateEnvelope(serializer, "authority-b", 5, 1, 51));
        GameThread.Run(() => { }, blocking: true);

        Assert.Equal(new[] { 41, 51 }, appliedValues);
    }

    private static NetworkCampaignAuthoritySyncEnvelope CreateEnvelope(
        ICommonSerializer serializer,
        string authorityId,
        long epoch,
        long sequence,
        int value) => new NetworkCampaignAuthoritySyncEnvelope(
            "room-a",
            authorityId,
            "host",
            epoch,
            sequence,
            serializer.Serialize(new CoopNetworkBaseTests.AuthorityProbeMessage { Value = value }));
}
