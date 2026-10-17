using Autofac;
using Common;
using Common.Messaging;
using Common.Network;
using Common.Util;
using GameInterface;
using GameInterface.Policies;
using GameInterface.Services.Actions.Handlers;
using GameInterface.Services.Actions.Messages;
using GameInterface.Services.Actions.Patches;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using GameInterface.Services.Heroes.Patches;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using LiteNetLib;
using Moq;
using ProtoBuf.Meta;
using System.IO;
using TaleWorlds.CampaignSystem;
using Xunit;

namespace GameInterface.Tests.Services.Actions;

[Collection(ModInformationRoleCollection.Name)]
public sealed class PlayerRelationChangeHandlerTests
{
    private static readonly AuthorityLease CurrentLease =
        new("room-a", "authority-b", "controller-b", 5);

    [Fact]
    public void NonAuthorityPlayerActionBecomesSequencedServerRequest()
    {
        var source = ObjectHelper.SkipConstructor<Hero>();
        var target = ObjectHelper.SkipConstructor<Hero>();
        var objectManager = new Mock<IObjectManager>();
        objectManager.Setup(value => value.TryGetIdWithLogging(source, out It.Ref<string>.IsAny))
            .Returns((Hero _, out string id) => { id = "hero-a"; return true; });
        objectManager.Setup(value => value.TryGetIdWithLogging(target, out It.Ref<string>.IsAny))
            .Returns((Hero _, out string id) => { id = "hero-target"; return true; });

        var network = new Mock<INetwork>();
        IMessage sent = null!;
        network.Setup(value => value.SendAll(It.IsAny<IMessage>()))
            .Callback<IMessage>(message => sent = message);

        WithAuthorityContext(localAuthority: false, () =>
        {
            var previousResolvedHero = ResolvedMainHeroContext.ResolvedMainHero;
            ResolvedMainHeroContext.ResolvedMainHero = source;
            try
            {
                using var handler = new PlayerRelationChangeHandler(
                    MessageBroker.Instance,
                    network.Object,
                    objectManager.Object,
                    Mock.Of<IPlayerManager>(),
                    Mock.Of<IControllerIdProvider>(),
                    CreateAuthorityContext(localAuthority: false).Object);

                Assert.False(ChangeRelationActionPatches.ApplyPlayerRelationPrefix(
                    target,
                    relation: 6,
                    affectRelatives: true,
                    showQuickNotification: false));
            }
            finally
            {
                ResolvedMainHeroContext.ResolvedMainHero = previousResolvedHero;
            }
        });

        var request = Assert.IsType<NetworkPlayerRelationChangeRequest>(sent);
        Assert.Equal("hero-a", request.SourceHeroId);
        Assert.Equal("hero-target", request.TargetHeroId);
        Assert.Equal(6, request.Relation);
        Assert.False(request.ShowNotification);
        Assert.True(request.RequestSequence > 0);
        Assert.True(request.MatchesLease(CurrentLease));
    }

    [Fact]
    public void RoomHostValidatesPlayerAndRoutesRequestOnceToRemoteAuthority()
    {
        var requestPeer = ObjectHelper.SkipConstructor<NetPeer>();
        var authorityPeer = ObjectHelper.SkipConstructor<NetPeer>();
        var player = new Player("controller-a", "hero-a", "party-a", "clan-a", "character-a");
        var players = new Mock<IPlayerManager>();
        players.Setup(value => value.TryGetPlayer(requestPeer, out It.Ref<Player>.IsAny))
            .Returns((NetPeer _, out Player result) => { result = player; return true; });
        players.Setup(value => value.TryGetPeer("controller-a", out It.Ref<NetPeer>.IsAny))
            .Returns((string _, out NetPeer result) => { result = requestPeer; return true; });
        players.Setup(value => value.TryGetPeer("controller-b", out It.Ref<NetPeer>.IsAny))
            .Returns((string _, out NetPeer result) => { result = authorityPeer; return true; });

        var network = new Mock<INetwork>();
        using var handler = CreateHandler(CreateAuthorityContext(localAuthority: false).Object, players.Object, network.Object);
        var request = new NetworkPlayerRelationChangeRequest(
            "hero-a", "hero-target", 4, true, requestSequence: 1, CurrentLease);
        ModInformation.SessionMode = SessionMode.DedicatedAuthority;
        try
        {
            Assert.True(handler.HandleNetworkRequest(requestPeer, request));
            Assert.False(handler.HandleNetworkRequest(requestPeer, request));
        }
        finally
        {
            ModInformation.SessionMode = SessionMode.RemoteReplica;
        }

        network.Verify(value => value.Send(authorityPeer, It.Is<IMessage>(message =>
            message.GetType() == typeof(NetworkCampaignAuthorityPlayerRelationCommand) &&
            ((NetworkCampaignAuthorityPlayerRelationCommand)message).Matches(CurrentLease) &&
            ((NetworkCampaignAuthorityPlayerRelationCommand)message).Sequence == 1 &&
            ((NetworkCampaignAuthorityPlayerRelationCommand)message).OriginControllerId == "controller-a")),
            Times.Once);
    }

    [Fact]
    public void StaleLeaseRequestDoesNotConsumeCurrentRequestSequence()
    {
        var requestPeer = ObjectHelper.SkipConstructor<NetPeer>();
        var authorityPeer = ObjectHelper.SkipConstructor<NetPeer>();
        var player = new Player("controller-a", "hero-a", "party-a", "clan-a", "character-a");
        var players = CreatePlayerManager(requestPeer, authorityPeer, player);
        var network = new Mock<INetwork>();
        using var handler = CreateHandler(CreateAuthorityContext(localAuthority: false).Object, players, network.Object);
        var staleRequest = new NetworkPlayerRelationChangeRequest(
            "hero-a", "hero-target", 4, true, requestSequence: 100,
            new AuthorityLease("room-a", "authority-old", "controller-old", 4));
        var currentRequest = new NetworkPlayerRelationChangeRequest(
            "hero-a", "hero-target", 4, true, requestSequence: 1, CurrentLease);
        ModInformation.SessionMode = SessionMode.DedicatedAuthority;
        try
        {
            Assert.False(handler.HandleNetworkRequest(requestPeer, staleRequest));
            Assert.True(handler.HandleNetworkRequest(requestPeer, currentRequest));
        }
        finally
        {
            ModInformation.SessionMode = SessionMode.RemoteReplica;
        }

        network.Verify(value => value.Send(authorityPeer, It.Is<IMessage>(message =>
            message.GetType() == typeof(NetworkCampaignAuthorityPlayerRelationCommand) &&
            ((NetworkCampaignAuthorityPlayerRelationCommand)message).Sequence == 1)), Times.Once);
    }

    [Fact]
    public void AuthorityCommandRejectsDuplicateSequence()
    {
        using var handler = CreateHandler(
            CreateAuthorityContext(localAuthority: true).Object,
            Mock.Of<IPlayerManager>(),
            Mock.Of<INetwork>());

        Assert.True(handler.TryAdvanceAuthorityCommandSequence(CurrentLease, 1));
        Assert.False(handler.TryAdvanceAuthorityCommandSequence(CurrentLease, 1));
        Assert.True(handler.TryAdvanceAuthorityCommandSequence(CurrentLease, 2));
    }

    [Fact]
    public void AuthorityRequestAndCommandRoundTripWithLeaseAndOrigin()
    {
        var request = new NetworkPlayerRelationChangeRequest(
            "hero-a", "hero-target", -3, false, requestSequence: 8, CurrentLease);
        using var requestStream = new MemoryStream();
        RuntimeTypeModel.Default.Serialize(requestStream, request);
        requestStream.Position = 0;
        var restoredRequest = (NetworkPlayerRelationChangeRequest)RuntimeTypeModel.Default
            .Deserialize(requestStream, null, typeof(NetworkPlayerRelationChangeRequest));

        var command = new NetworkCampaignAuthorityPlayerRelationCommand(
            CurrentLease, sequence: 2, "controller-a", restoredRequest);
        using var commandStream = new MemoryStream();
        RuntimeTypeModel.Default.Serialize(commandStream, command);
        commandStream.Position = 0;
        var restoredCommand = (NetworkCampaignAuthorityPlayerRelationCommand)RuntimeTypeModel.Default
            .Deserialize(commandStream, null, typeof(NetworkCampaignAuthorityPlayerRelationCommand));

        Assert.True(restoredCommand.Matches(CurrentLease));
        Assert.Equal("controller-a", restoredCommand.OriginControllerId);
        Assert.Equal("hero-a", restoredCommand.Request.SourceHeroId);
        Assert.Equal(-3, restoredCommand.Request.Relation);
    }

    private static PlayerRelationChangeHandler CreateHandler(
        ICampaignAuthorityContext authorityContext,
        IPlayerManager players,
        INetwork network) => new(
            Mock.Of<IMessageBroker>(),
            network,
            Mock.Of<IObjectManager>(),
            players,
            Mock.Of<IControllerIdProvider>(),
            authorityContext);

    private static Mock<ICampaignAuthorityContext> CreateAuthorityContext(bool localAuthority)
    {
        var context = new Mock<ICampaignAuthorityContext>();
        context.SetupGet(value => value.IsAuthorityKnown).Returns(true);
        context.Setup(value => value.TryGetCurrentLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease lease) => { lease = CurrentLease; return true; });
        context.Setup(value => value.TryGetLocalAuthorityLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease lease) => { lease = CurrentLease; return localAuthority; });
        return context;
    }

    private static IPlayerManager CreatePlayerManager(NetPeer requestPeer, NetPeer authorityPeer, Player player)
    {
        var players = new Mock<IPlayerManager>();
        players.Setup(value => value.TryGetPlayer(requestPeer, out It.Ref<Player>.IsAny))
            .Returns((NetPeer _, out Player result) => { result = player; return true; });
        players.Setup(value => value.TryGetPeer("controller-a", out It.Ref<NetPeer>.IsAny))
            .Returns((string _, out NetPeer result) => { result = requestPeer; return true; });
        players.Setup(value => value.TryGetPeer("controller-b", out It.Ref<NetPeer>.IsAny))
            .Returns((string _, out NetPeer result) => { result = authorityPeer; return true; });
        return players.Object;
    }

    private static void WithAuthorityContext(bool localAuthority, System.Action assertion)
    {
        var authorityContext = CreateAuthorityContext(localAuthority);
        var builder = new ContainerBuilder();
        builder.RegisterInstance(authorityContext.Object).As<ICampaignAuthorityContext>();
        builder.RegisterInstance(Mock.Of<ISyncPolicy>(policy => policy.AllowOriginal() == false))
            .As<ISyncPolicy>();
        using var container = builder.Build();

        var previousMode = ModInformation.SessionMode;
        bool hadPreviousContainer = ContainerProvider.TryGetContainer(out var previousContainer);
        try
        {
            ModInformation.SessionMode = SessionMode.RemoteReplica;
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
