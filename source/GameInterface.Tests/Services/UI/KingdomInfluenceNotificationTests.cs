using Autofac;
using Common;
using Common.Messaging;
using Common.Network;
using Common.Util;
using Coop.Tests.Mocks;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using GameInterface.Services.Kingdoms.Interfaces;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using GameInterface.Services.UI.Notifications.Handlers;
using GameInterface.Services.UI.Notifications.Messages;
using GameInterface.Services.UI.Notifications.Patches;
using Moq;
using Serilog;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using Xunit;

namespace GameInterface.Tests.Services.UI;

[Collection(ModInformationRoleCollection.Name)]
public sealed class KingdomInfluenceNotificationTests
{
    private static readonly AuthorityLease CurrentLease =
        new("room-a", "authority-b", "controller-b", 4);

    static KingdomInfluenceNotificationTests() =>
        RuntimeHelpers.RunModuleConstructor(typeof(TestNetwork).Module.ModuleHandle);

    [Fact]
    public void NpcPrisonerDonationWithNullHero_IsNotPublished()
    {
        var party = ObjectHelper.SkipConstructor<MobileParty>();
        var clan = ObjectHelper.SkipConstructor<Clan>();
        party._actualClan = clan;
        List<NotifyKingdomInfluenceChanged> published = null;
        WithAuthority(localAuthority: true, legacyIsServer: true, () =>
            published = CaptureLocalNotifications(() =>
                GainKingdomInfluenceActionPatch.ApplyInternalPostfix(
                    hero: null,
                    party,
                    gainedInfluence: 12f,
                    GainKingdomInfluenceAction.InfluenceGainingReason.DonatePrisoners)));

        Assert.Empty(published);
    }

    [Fact]
    public void ObserverDoesNotRepublishPlayerDonationInsideAllowedThread()
    {
        using var player = new PlayerFixture(includeParty: true, includeHero: true);

        List<NotifyKingdomInfluenceChanged> published = null;
        WithAuthority(localAuthority: false, legacyIsServer: true, () =>
        {
            var notifications = new List<NotifyKingdomInfluenceChanged>();
            Action<MessagePayload<NotifyKingdomInfluenceChanged>> capture = payload => notifications.Add(payload.What);
            MessageBroker.Instance.Subscribe(capture);
            try
            {
                RunOnGameThread(() =>
                {
                    using (new AllowedThread())
                    {
                        GainKingdomInfluenceActionPatch.ApplyInternalPostfix(
                            player.Hero,
                            player.Party,
                            gainedInfluence: 12f,
                            GainKingdomInfluenceAction.InfluenceGainingReason.DonatePrisoners);
                    }
                });
            }
            finally
            {
                MessageBroker.Instance.Unsubscribe(capture);
            }

            published = notifications;
        });

        Assert.Empty(published);
    }

    [Fact]
    public void RemoteLeaseHolderPublishesPlayerHeroNotificationDespiteClientRole()
    {
        using var player = new PlayerFixture(includeParty: false, includeHero: true);
        player.Hero._clan = player.Clan;

        List<NotifyKingdomInfluenceChanged> published = null;
        WithAuthority(localAuthority: true, legacyIsServer: false, () =>
            published = CaptureLocalNotifications(() =>
                GainKingdomInfluenceActionPatch.ApplyInternalPostfix(
                    player.Hero,
                    party: null,
                    gainedInfluence: 12f,
                    GainKingdomInfluenceAction.InfluenceGainingReason.Battle)));

        var notification = Assert.Single(published);
        Assert.Same(player.Hero, notification.Hero);
        Assert.Same(player.Clan, notification.Clan);
    }

    [Fact]
    public void PlayerPartyDonationWithNullHeroSerializesPartyAndClanWithoutHeroLookup()
    {
        using var player = new PlayerFixture(includeParty: true, includeHero: true);
        var objectManager = CreateObjectManager(player);
        var broker = new MessageBroker();
        var network = new Mock<INetwork>();
        IMessage sent = null;
        network.Setup(value => value.SendAll(It.IsAny<IMessage>()))
            .Callback<IMessage>(message => sent = message);

        WithAuthority(localAuthority: true, legacyIsServer: false, () =>
        {
            using var handler = new OtherNotificationsHandler(
                broker,
                objectManager.Object,
                network.Object,
                Mock.Of<IFactionInterface>());

            Action<MessagePayload<NotifyKingdomInfluenceChanged>> forward =
                payload => broker.Publish(this, payload.What);
            MessageBroker.Instance.Subscribe(forward);
            try
            {
                RunOnGameThread(() =>
                    GainKingdomInfluenceActionPatch.ApplyInternalPostfix(
                        hero: null,
                        player.Party,
                        gainedInfluence: 12f,
                        GainKingdomInfluenceAction.InfluenceGainingReason.DonatePrisoners));
            }
            finally
            {
                MessageBroker.Instance.Unsubscribe(forward);
            }

            var message = Assert.IsType<NetworkNotifyKingdomInfluenceChanged>(sent);
            Assert.Null(message.HeroId);
            Assert.Equal("party-id", message.MobilePartyId);
            Assert.Equal("clan-id", message.ClanId);
            Assert.Equal(12, message.GainedInfluence);
            objectManager.Verify(
                value => value.TryGetIdWithLogging<Hero>(It.IsAny<Hero>(), out It.Ref<string>.IsAny),
                Times.Never);
        });
    }

    [Fact]
    public void ReceivedPlayerPartyDonationWithNullHeroStillUsesPartyAndClanNotificationPath()
    {
        using var player = new PlayerFixture(includeParty: true, includeHero: true);
        player.Clan._influence = 100f;
        var previousCampaign = Campaign.Current;
        var campaign = ObjectHelper.SkipConstructor<Campaign>();
        campaign.MainParty = player.Party;
        Campaign.Current = campaign;
        Assert.Same(player.Party, MobileParty.MainParty);
        var objectManager = new Mock<IObjectManager>(MockBehavior.Strict);
        objectManager.Setup(value => value.TryGetObjectWithLogging<MobileParty>("party-id", out It.Ref<MobileParty>.IsAny))
            .Returns((string _, out MobileParty result) => { result = player.Party; return true; });
        objectManager.Setup(value => value.TryGetObjectWithLogging<Clan>("clan-id", out It.Ref<Clan>.IsAny))
            .Returns((string _, out Clan result) => { result = player.Clan; return true; });
        var broker = new MessageBroker();
        Assert.True(OtherNotificationsHandler.ShouldDisplayInfluenceNotification(
            detail: GainKingdomInfluenceAction.InfluenceGainingReason.DonatePrisoners,
            hero: null,
            mobileParty: player.Party,
            mainHero: null,
            mainParty: player.Party));

        try
        {
            using var handler = new OtherNotificationsHandler(
                broker,
                objectManager.Object,
                Mock.Of<INetwork>(),
                Mock.Of<IFactionInterface>());

            RunOnGameThread(() => broker.Publish(this, new NetworkNotifyKingdomInfluenceChanged(
                heroId: null,
                mobilePartyId: "party-id",
                clanId: "clan-id",
                gainedInfluence: 12,
                GainKingdomInfluenceAction.InfluenceGainingReason.DonatePrisoners)));

            objectManager.Verify(
                value => value.TryGetObjectWithLogging<Hero>(It.IsAny<string>(), out It.Ref<Hero>.IsAny),
                Times.Never);
            objectManager.Verify(
                value => value.TryGetObjectWithLogging<MobileParty>("party-id", out It.Ref<MobileParty>.IsAny),
                Times.Once);
            objectManager.Verify(
                value => value.TryGetObjectWithLogging<Clan>("clan-id", out It.Ref<Clan>.IsAny),
                Times.Once);
        }
        finally
        {
            Campaign.Current = previousCampaign;
        }
    }

    [Theory]
    [InlineData(GainKingdomInfluenceAction.InfluenceGainingReason.Battle)]
    [InlineData(GainKingdomInfluenceAction.InfluenceGainingReason.SiegeSafePassage)]
    public void MissingHeroIdDoesNotMatchMissingMainHeroForHeroOnlyNotifications(
        GainKingdomInfluenceAction.InfluenceGainingReason detail)
    {
        Assert.False(OtherNotificationsHandler.ShouldDisplayInfluenceNotification(
            detail,
            hero: null,
            mobileParty: null,
            mainHero: null,
            mainParty: null));
    }

    [Fact]
    public void MainHeroBattleAndSiegeNotificationsRemainEligible()
    {
        var playerHero = ObjectHelper.SkipConstructor<Hero>();
        Assert.True(OtherNotificationsHandler.ShouldDisplayInfluenceNotification(
            detail: GainKingdomInfluenceAction.InfluenceGainingReason.Battle,
            hero: playerHero,
            mobileParty: null,
            mainHero: playerHero,
            mainParty: null));
        Assert.True(OtherNotificationsHandler.ShouldDisplayInfluenceNotification(
            detail: GainKingdomInfluenceAction.InfluenceGainingReason.SiegeSafePassage,
            hero: playerHero,
            mobileParty: null,
            mainHero: playerHero,
            mainParty: null));
    }

    [Fact]
    public void MissingRequiredClanForPlayerDonationIsLoggedAndDoesNotSend()
    {
        using var player = new PlayerFixture(includeParty: true, includeHero: true);
        var objectManager = new Mock<IObjectManager>(MockBehavior.Strict);
        objectManager.Setup(value => value.TryGetIdWithLogging(player.Party, out It.Ref<string>.IsAny))
            .Returns((MobileParty _, out string id) => { id = "party-id"; return true; });
        objectManager.Setup(value => value.TryGetIdWithLogging<Clan>(null, out It.Ref<string>.IsAny))
            .Returns((Clan _, out string id) => { id = null; return false; });
        var network = new Mock<INetwork>(MockBehavior.Strict);
        var broker = new MessageBroker();

        WithAuthority(localAuthority: true, legacyIsServer: false, () =>
        {
            using var handler = new OtherNotificationsHandler(
                broker,
                objectManager.Object,
                network.Object,
                Mock.Of<IFactionInterface>());

            RunOnGameThread(() => broker.Publish(this, new NotifyKingdomInfluenceChanged(
                hero: null,
                player.Party,
                clan: null,
                gainedInfluence: 12,
                GainKingdomInfluenceAction.InfluenceGainingReason.DonatePrisoners)));

            objectManager.Verify(
                value => value.TryGetIdWithLogging<Clan>(null, out It.Ref<string>.IsAny),
                Times.Once);
            network.Verify(value => value.SendAll(It.IsAny<IMessage>()), Times.Never);
        });
    }

    [Fact]
    public void QueuedNotificationIsDiscardedAfterLeaseMovesToObserver()
    {
        using var player = new PlayerFixture(includeParty: true, includeHero: true);
        var broker = new MessageBroker();
        var network = new Mock<INetwork>(MockBehavior.Strict);
        var objectManager = CreateObjectManager(player);
        int localAuthority = 1;

        WithAuthority(
            () => Volatile.Read(ref localAuthority) == 1,
            legacyIsServer: false,
            () =>
            {
                using var handler = new OtherNotificationsHandler(
                    broker,
                    objectManager.Object,
                    network.Object,
                    Mock.Of<IFactionInterface>());

                RunOnGameThread(() =>
                {
                    var publisher = new Thread(() => broker.Publish(this, new NotifyKingdomInfluenceChanged(
                        player.Hero,
                        player.Party,
                        player.Clan,
                        gainedInfluence: 12,
                        GainKingdomInfluenceAction.InfluenceGainingReason.DonatePrisoners)));
                    publisher.Start();
                    Assert.True(publisher.Join(TimeSpan.FromSeconds(5)));

                    // The event handler queued its work from the publisher thread. Revoke the
                    // lease before the game-thread pump drains that queued notification.
                    Interlocked.Exchange(ref localAuthority, 0);
                });

                network.Verify(value => value.SendAll(It.IsAny<IMessage>()), Times.Never);
            });
    }

    private static List<NotifyKingdomInfluenceChanged> CaptureLocalNotifications(Action action)
    {
        var notifications = new List<NotifyKingdomInfluenceChanged>();
        Action<MessagePayload<NotifyKingdomInfluenceChanged>> capture = payload => notifications.Add(payload.What);
        MessageBroker.Instance.Subscribe(capture);
        try
        {
            RunOnGameThread(action);
        }
        finally
        {
            MessageBroker.Instance.Unsubscribe(capture);
        }

        return notifications;
    }

    private static void RunOnGameThread(Action action) => GameThread.Run(action, blocking: true);

    private static void WithAuthority(
        bool localAuthority,
        bool legacyIsServer,
        Action assertion) => WithAuthority(() => localAuthority, legacyIsServer, assertion);

    private static void WithAuthority(
        Func<bool> localAuthority,
        bool legacyIsServer,
        Action assertion)
    {
        var context = new Mock<ICampaignAuthorityContext>();
        context.Setup(value => value.TryGetLocalAuthorityLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease lease) => { lease = CurrentLease; return localAuthority(); });

        var builder = new ContainerBuilder();
        builder.RegisterInstance(context.Object).As<ICampaignAuthorityContext>();
        using var container = builder.Build();
        var previousMode = ModInformation.SessionMode;
        bool hadPreviousContainer = ContainerProvider.TryGetContainer(out var previousContainer);
        try
        {
            ModInformation.SessionMode = legacyIsServer ? SessionMode.DedicatedAuthority : SessionMode.RemoteReplica;
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

    private static Mock<IObjectManager> CreateObjectManager(PlayerFixture player)
    {
        var objectManager = new Mock<IObjectManager>(MockBehavior.Strict);
        objectManager.Setup(value => value.TryGetObjectWithLogging<MobileParty>("party-id", out It.Ref<MobileParty>.IsAny))
            .Returns((string _, out MobileParty result) => { result = player.Party; return true; });
        objectManager.Setup(value => value.TryGetObjectWithLogging<Hero>("hero-id", out It.Ref<Hero>.IsAny))
            .Returns((string _, out Hero result) => { result = player.Hero; return true; });
        objectManager.Setup(value => value.TryGetObjectWithLogging<Clan>("clan-id", out It.Ref<Clan>.IsAny))
            .Returns((string _, out Clan result) => { result = player.Clan; return true; });
        objectManager.Setup(value => value.TryGetIdWithLogging(player.Party, out It.Ref<string>.IsAny))
            .Returns((MobileParty _, out string id) => { id = "party-id"; return true; });
        objectManager.Setup(value => value.TryGetIdWithLogging(player.Clan, out It.Ref<string>.IsAny))
            .Returns((Clan _, out string id) => { id = "clan-id"; return true; });
        return objectManager;
    }

    private sealed class PlayerFixture : IDisposable
    {
        private readonly PlayerManager players;
        private readonly Player player;

        public MobileParty Party { get; } = ObjectHelper.SkipConstructor<MobileParty>();
        public Hero Hero { get; } = ObjectHelper.SkipConstructor<Hero>();
        public Clan Clan { get; } = ObjectHelper.SkipConstructor<Clan>();

        public PlayerFixture(bool includeParty, bool includeHero)
        {
            Party._actualClan = Clan;
            Hero._clan = Clan;
            var objectManager = new Mock<IObjectManager>();
            objectManager.Setup(value => value.TryGetObjectWithLogging<MobileParty>("party-id", out It.Ref<MobileParty>.IsAny))
                .Returns((string _, out MobileParty result) => { result = Party; return true; });
            objectManager.Setup(value => value.TryGetObject<MobileParty>("party-id", out It.Ref<MobileParty>.IsAny))
                .Returns((string _, out MobileParty result) => { result = Party; return true; });
            objectManager.Setup(value => value.TryGetObjectWithLogging<Hero>("hero-id", out It.Ref<Hero>.IsAny))
                .Returns((string _, out Hero result) => { result = Hero; return true; });
            objectManager.Setup(value => value.TryGetObject<Hero>("hero-id", out It.Ref<Hero>.IsAny))
                .Returns((string _, out Hero result) => { result = Hero; return true; });
            objectManager.Setup(value => value.TryGetObjectWithLogging<Clan>("clan-id", out It.Ref<Clan>.IsAny))
                .Returns((string _, out Clan result) => { result = Clan; return true; });
            objectManager.Setup(value => value.TryGetObject<Clan>("clan-id", out It.Ref<Clan>.IsAny))
                .Returns((string _, out Clan result) => { result = Clan; return true; });

            var controller = new ControllerIdProvider();
            controller.SetControllerId("controller-a");
            players = new PlayerManager(Mock.Of<ILogger>(), objectManager.Object, controller);
            player = new Player(
                "controller-a",
                includeHero ? "hero-id" : string.Empty,
                includeParty ? "party-id" : string.Empty,
                "clan-id",
                "character-id");
            RunOnGameThread(() => Assert.True(players.AddPlayer(player)));
        }

        public void Dispose() => RunOnGameThread(() => players.RemovePlayer(player));
    }
}
