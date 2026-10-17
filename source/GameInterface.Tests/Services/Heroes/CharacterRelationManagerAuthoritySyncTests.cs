using Autofac;
using Common;
using Common.Messaging;
using Common.Network;
using Common.Util;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Heroes.Handlers;
using GameInterface.Services.Heroes.Messages;
using GameInterface.Services.Heroes.Patches;
using GameInterface.Services.ObjectManager;
using GameInterface.Policies;
using Moq;
using ProtoBuf.Meta;
using System.IO;
using TaleWorlds.CampaignSystem;
using Xunit;

namespace GameInterface.Tests.Services.Heroes;

[Collection(ModInformationRoleCollection.Name)]
public sealed class CharacterRelationManagerAuthoritySyncTests
{
    private static readonly AuthorityLease Lease =
        new("room-a", "authority-b", "controller-b", 4);

    [Fact]
    public void RemoteAuthorityPublishesEpochScopedRelationResult()
    {
        var network = new Mock<INetwork>();
        var broker = new MessageBroker();
        IMessage sent = null!;
        network.Setup(value => value.SendAll(It.IsAny<IMessage>()))
            .Callback<IMessage>(message => sent = message);

        WithContext(localAuthority: true, authorityKnown: true, isServer: false, () =>
        {
            using var handler = new CharacterRelationManagerHandler(
                broker, Mock.Of<IObjectManager>(), network.Object);
            broker.Publish(null, new HeroRelationChanged("hero-1", "hero-2", 12));

            var relationChange = Assert.IsType<NetworkCampaignAuthorityHeroRelationChanged>(sent);
            Assert.Equal("hero-1", relationChange.Hero1Id);
            Assert.Equal("hero-2", relationChange.Hero2Id);
            Assert.Equal(12, relationChange.Value);
            Assert.IsAssignableFrom<ICampaignAuthorityScopedMessage>(relationChange);
        });
    }

    [Fact]
    public void NonAuthorityReplicaDoesNotPublishRelationResults()
    {
        var network = new Mock<INetwork>();
        var broker = new MessageBroker();

        WithContext(localAuthority: false, authorityKnown: true, isServer: false, () =>
        {
            using var handler = new CharacterRelationManagerHandler(
                broker, Mock.Of<IObjectManager>(), network.Object);
            broker.Publish(null, new HeroRelationChanged("hero-1", "hero-2", 12));
        });

        network.Verify(value => value.SendAll(It.IsAny<IMessage>()), Times.Never);
    }

    [Fact]
    public void RoomHostKeepsLegacyRelationBroadcastUntilLeaseIsConfirmed()
    {
        var network = new Mock<INetwork>();
        var broker = new MessageBroker();
        IMessage sent = null!;
        network.Setup(value => value.SendAll(It.IsAny<IMessage>()))
            .Callback<IMessage>(message => sent = message);

        WithContext(localAuthority: false, authorityKnown: false, isServer: true, () =>
        {
            using var handler = new CharacterRelationManagerHandler(
                broker, Mock.Of<IObjectManager>(), network.Object);
            broker.Publish(null, new HeroRelationChanged("hero-1", "hero-2", 12));
        });

        var relationChange = Assert.IsType<NetworkHeroRelationChanged>(sent);
        Assert.IsAssignableFrom<IServerToClientCommand>(relationChange);
        Assert.IsNotAssignableFrom<ICampaignAuthorityScopedMessage>(relationChange);
    }

    [Fact]
    public void AuthorityRelationDeltaRoundTripsWithItsScopedPayload()
    {
        var original = new NetworkCampaignAuthorityHeroRelationChanged("hero-1", "hero-2", -7);
        using var stream = new MemoryStream();

        RuntimeTypeModel.Default.Serialize(stream, original);
        stream.Position = 0;
        var restored = (NetworkCampaignAuthorityHeroRelationChanged)RuntimeTypeModel.Default
            .Deserialize(stream, null, typeof(NetworkCampaignAuthorityHeroRelationChanged));

        Assert.Equal("hero-1", restored.Hero1Id);
        Assert.Equal("hero-2", restored.Hero2Id);
        Assert.Equal(-7, restored.Value);
        Assert.IsAssignableFrom<ICampaignAuthorityScopedMessage>(restored);
    }

    [Fact]
    public void RemoteAuthorityPublishesNativeRelationChanges()
    {
        var source = ObjectHelper.SkipConstructor<Hero>();
        var target = ObjectHelper.SkipConstructor<Hero>();
        source.StringId = "hero-source";
        target.StringId = "hero-target";
        var published = new System.Collections.Generic.List<HeroRelationChanged>();
        System.Action<MessagePayload<HeroRelationChanged>> capture = payload => published.Add(payload.What);
        MessageBroker.Instance.Subscribe(capture);

        try
        {
            WithContext(localAuthority: true, authorityKnown: true, isServer: false, () =>
                CharacterRelationManagerPatches.Postfix_SetHeroRelation(source, target, 9));
        }
        finally
        {
            MessageBroker.Instance.Unsubscribe(capture);
        }

        var change = Assert.Single(published);
        Assert.Equal("hero-source", change.Hero1Id);
        Assert.Equal("hero-target", change.Hero2Id);
        Assert.Equal(9, change.Value);
    }

    private static void WithContext(
        bool localAuthority,
        bool authorityKnown,
        bool isServer,
        System.Action assertion)
    {
        var authorityContext = new Mock<ICampaignAuthorityContext>();
        authorityContext.SetupGet(context => context.IsAuthorityKnown).Returns(authorityKnown);
        authorityContext
            .Setup(context => context.TryGetLocalAuthorityLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease lease) =>
            {
                lease = authorityKnown ? Lease : default;
                return authorityKnown && localAuthority;
            });

        var builder = new ContainerBuilder();
        builder.RegisterInstance(authorityContext.Object).As<ICampaignAuthorityContext>();
        builder.RegisterInstance(Mock.Of<ISyncPolicy>(policy => policy.AllowOriginal() == false))
            .As<ISyncPolicy>();
        using var container = builder.Build();

        var previousMode = ModInformation.SessionMode;
        bool hadPreviousContainer = ContainerProvider.TryGetContainer(out var previousContainer);
        try
        {
            ModInformation.IsServer = isServer;
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
