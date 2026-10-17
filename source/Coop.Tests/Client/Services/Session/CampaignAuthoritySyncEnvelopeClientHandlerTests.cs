using Common;
using Common.Messaging;
using Common.Network.Session;
using Common.Serialization;
using Common.Session;
using Common.Tests.Utils;
using Coop.Core.Client.Services.Session;
using Coop.Tests.Network;
using Xunit;

namespace Coop.Tests.Client.Services.Session;

public class CampaignAuthoritySyncEnvelopeClientHandlerTests
{
    [Fact]
    public void AppliesOnlyCurrentLeaseEnvelopesAndRejectsReplays()
    {
        var messageBroker = new TestMessageBroker();
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
