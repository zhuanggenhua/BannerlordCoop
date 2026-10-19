using Common;
using Common.Messaging;
using Common.Network;
using Autofac;
using Common.Session;
using Coop.Core.Common;
using Coop.Core.Common.Services.Connection.Messages;
using Coop.Core.Common.Session;
using Coop.Core.Server.Connections;
using Coop.Core.Server.Services.Session;
using Coop.Core.Server.Services.Shutdown;
using Coop.Core.Server.Services.Save;
using Coop.Core.Server.Services.Save.Handlers;
using GameInterface.CoopSessionData;
using GameInterface.CoopSessionData.Save.Data;
using GameInterface.Services.Entity;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.GameState.Messages;
using GameInterface.Services.GameDebug.Messages;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using GameInterface.Services.Save.Messages;
using GameInterface.Services.Heroes.Messages;
using GameInterface.Services.Time.Messages;
using GameInterface.Services.UI.Interfaces;
using Moq;
using System;
using Xunit;

namespace Coop.Tests.Server.Services.Save;

[Collection(ModInformationRoleCollection.Name)]
public sealed class ColdBootAuthorityFenceTests
{
    [Fact]
    public void SavedLocalHostGetsFreshBootFenceWithoutChangingPlayerOrWorldIdentity()
    {
        using var fixture = new Fixture();
        fixture.Broker.Publish(this, new GameLoaded("Cold_A"));
        var restored = fixture.Identity.CurrentLease;
        Assert.NotEqual(fixture.Saved.RoomId, restored.RoomId);
        Assert.Equal("a", restored.AuthorityControllerId);
        Assert.Equal(fixture.Saved.Epoch + 1, restored.Epoch);
        Assert.NotEqual(fixture.Saved.AuthorityId, restored.AuthorityId);
        Assert.True(fixture.Identity.IsConfirmed);
        Assert.Equal("a", fixture.Identity.RoomHostControllerId);
        Assert.Equal("World_Original", fixture.Provider.Object.CoopSession.UniqueGameId);
        Assert.Same(fixture.Session.Players, fixture.Provider.Object.CoopSession.Players);
        Assert.Same(fixture.Session.CraftingPlayerData, fixture.Provider.Object.CoopSession.CraftingPlayerData);
        Assert.Equal(restored.AuthorityId, fixture.Provider.Object.CoopSession.AuthorityId);
        Assert.Equal(restored.RoomId, fixture.Provider.Object.CoopSession.RoomId);
        Assert.Equal(restored.Epoch, fixture.Provider.Object.CoopSession.AuthorityEpoch);
        Assert.False(fixture.Identity.TryApply(fixture.Saved, "a"));
        Assert.False(fixture.Identity.TryApply(new AuthorityLease(fixture.Saved.RoomId, "delayed-future-authority", "a", 100), "a"));
    }

    [Fact]
    public void DuplicateLoadedEventDoesNotMintAnotherFence()
    {
        using var fixture = new Fixture();
        fixture.Broker.Publish(this, new GameLoaded("Cold_A"));
        var first = fixture.Identity.CurrentLease;
        Assert.NotEqual(fixture.Saved, first);
        fixture.Broker.Publish(this, new GameLoaded("Cold_A"));
        Assert.Equal(first, fixture.Identity.CurrentLease);
    }

    [Fact]
    public void NextNativeSaveWritesBootFenceRatherThanStaleSidecarLease()
    {
        using var fixture = new Fixture();
        fixture.Broker.Publish(this, new GameLoaded("Cold_A"));
        var restored = fixture.Identity.CurrentLease;
        fixture.Broker.Publish(this, new GameSaved("Cold_A_Next"));
        Assert.Equal(fixture.Saved.Epoch + 1, fixture.Manager.Written!.AuthorityEpoch);
        Assert.Equal(restored.AuthorityId, fixture.Manager.Written.AuthorityId);
        Assert.Equal("a", fixture.Manager.Written.RoomHostControllerId);
    }

    [Fact]
    public void DedicatedAuthorityKeepsExistingRestoreSemantics()
    {
        using var fixture = new Fixture(mode: SessionMode.DedicatedAuthority);
        fixture.Broker.Publish(this, new GameLoaded("Cold_A"));
        Assert.Equal(fixture.Saved, fixture.Identity.CurrentLease);
    }

    [Theory]
    [InlineData("remote")]
    [InlineData("paused")]
    [InlineData("exhausted")]
    [InlineData("incomplete")]
    [InlineData("duplicate")]
    [InlineData("malformed")]
    [InlineData("epoch_zero")]
    [InlineData("wrong_host")]
    [InlineData("missing_local")]
    public void UnsupportedColdBootEndsOwnedSessionBeforeLaterReadinessCanPromote(string scenario)
    {
        var broker = new MessageBroker();
        var finalizer = new CoopFinalizer(broker, Mock.Of<ILoadingInterface>());
        var lease = scenario switch
        {
            "remote" => new AuthorityLease("room", "old", "b", 7),
            "paused" => new AuthorityLease("room", "old", "", 7, true),
            "exhausted" => new AuthorityLease("room", "old", "a", long.MaxValue),
            "malformed" => new AuthorityLease(" ", "old", "a", 7),
            "epoch_zero" => new AuthorityLease("room", "old", "a", 0),
            _ => new AuthorityLease("room", "old", "a", 7),
        };
        Player[]? registrations = scenario switch
        {
            "incomplete" => new[] { new Player("a", "Hero_A", "Party_A", "Clan_A", "") },
            "duplicate" => new[] { new Player("a", "Hero_A", "Party_A", "Clan_A", "Character_A"), new Player("a", "Hero_B", "Party_B", "Clan_B", "Character_B") },
            _ => null,
        };
        using var fixture = new Fixture(savedLease: lease, savedHost: scenario == "wrong_host" ? "other" : "a",
            savedPlayers: registrations, broker: broker, finalizer: finalizer, localId: scenario == "missing_local" ? "" : "a");
        var network = new Mock<INetwork>();
        var distribution = new AuthorityLeaseDistributionHandler(broker, network.Object, fixture.Identity, fixture.Controller);
        var coordinator = new CampaignAuthorityAvailabilityCoordinator(fixture.Identity, new CampaignAuthorityCandidateSelector());
        var availability = new CampaignSimulationAvailabilityServerHandler(broker, coordinator,
            Mock.Of<IConnectionCollection>(), Mock.Of<IPlayerManager>(), fixture.Controller, network.Object, fixture.Identity);
        var builder = new ContainerBuilder();
        builder.RegisterInstance(distribution);
        builder.RegisterInstance(availability);
        builder.RegisterInstance(fixture.Handler);
        using var owner = builder.Build();
        bool ended = false;
        string? notice = null;
        Action<MessagePayload<SendPopupMessage>> show = payload => notice = payload.What.Text;
        Action<MessagePayload<EndCoopMode>> end = _ => { owner.Dispose(); ended = true; };
        broker.Subscribe(show);
        broker.Subscribe(end);
        GameThread.Run(() => broker.Publish(this, new GameLoaded("Unsupported")), blocking: true);
        GameThread.Run(() =>
        {
            broker.Publish(this, new CampaignReady());
            broker.Publish(this, new CampaignMapSimulationReadinessChanged(true));
            broker.Publish(this, new CampaignMapSimulationReadinessChanged(false));
            broker.Publish(this, new GameSaved("Unsupported_Followup"));
        }, blocking: true);
        Assert.False(fixture.Identity.IsConfirmed);
        var context = new CampaignAuthorityContext(fixture.Identity, fixture.Controller);
        Assert.False(context.IsLocalCampaignAuthority);
        Assert.True(ended);
        Assert.False(fixture.Gate.IsOpen);
        Assert.False(string.IsNullOrWhiteSpace(notice));
        if (scenario == "exhausted") Assert.Contains("exhausted", notice, StringComparison.OrdinalIgnoreCase);
        Assert.Null(fixture.Manager.Written);
        Assert.Empty(coordinator.Snapshot());
        network.Verify(value => value.SendAll(It.IsAny<Common.Messaging.IMessage>()), Times.Never);
        broker.Unsubscribe(show);
        broker.Unsubscribe(end);
    }

    [Fact]
    public void LegacySidecarWithoutAuthorityMetadataRetainsFreshHostingPath()
    {
        using var fixture = new Fixture(savedLease: new AuthorityLease(null!, null!, null!, 0));
        var before = fixture.Identity.CurrentLease;
        fixture.Broker.Publish(this, new GameLoaded("Legacy"));
        using var distribution = new AuthorityLeaseDistributionHandler(fixture.Broker, Mock.Of<INetwork>(), fixture.Identity, fixture.Controller);
        fixture.Broker.Publish(this, new CampaignReady());
        Assert.True(fixture.Gate.IsOpen);
        Assert.True(fixture.Identity.IsConfirmed);
        Assert.Equal(before.RoomId, fixture.Identity.RoomId);
        Assert.Equal("a", fixture.Identity.AuthorityControllerId);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("other")]
    public void AlreadyConfirmedSessionDoesNotRotateItsCurrentBootIdentity(string savedHost)
    {
        using var fixture = new Fixture(savedHost: savedHost);
        var current = new AuthorityLease("current-room", "current-authority", "a", 20);
        Assert.True(fixture.Identity.TryApply(current, "a"));
        fixture.Broker.Publish(this, new GameLoaded("Cold_A"));
        Assert.Equal(current, fixture.Identity.CurrentLease);
        Assert.Equal(current.RoomId, fixture.Provider.Object.CoopSession.RoomId);
        Assert.Equal("a", fixture.Provider.Object.CoopSession.RoomHostControllerId);
        Assert.True(fixture.Gate.IsOpen);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SessionMode previous = ModInformation.SessionMode;
        public readonly MessageBroker Broker;
        public readonly SessionIdentity Identity = new();
        public readonly AuthorityLease Saved;
        public readonly CoopSession Session;
        public readonly Mock<ICoopSessionProvider> Provider = new();
        public readonly SaveStub Manager;
        public readonly SaveGameHandler Handler;
        public readonly ControllerIdProvider Controller = new();
        public readonly ServerAdmissionGate Gate = new();
        public Fixture(SessionMode mode = SessionMode.ListenAuthority, AuthorityLease? savedLease = null,
            string savedHost = "a", Player[]? savedPlayers = null, MessageBroker? broker = null, ICoopFinalizer? finalizer = null,
            string localId = "a")
        {
            Broker = broker ?? new MessageBroker();
            Saved = savedLease ?? new AuthorityLease("room", "old-authority", "a", 7);
            ModInformation.SessionMode = mode;
            var empty = CoopSession.Empty;
            var a = new Player("a", "Hero_A", "MobileParty_A", "Clan_A", "Character_A");
            var b = new Player("b", "Hero_B", "MobileParty_B", "Clan_B", "Character_B");
            Session = new CoopSession("World_Original", savedPlayers ?? new[] { a, b }, empty.CraftingPlayerData,
                empty.WorkshopPlayerData, empty.CaravansPlayerData, empty.AlleyPlayerData,
                empty.InteractionsPlayerData, empty.TradePlayerData, empty.InventoryPlayerData,
                empty.HeroMeetingData, empty.AgingPlayerData, roomId: Saved.RoomId,
                authorityId: Saved.AuthorityId, authorityControllerId: Saved.AuthorityControllerId,
                authorityEpoch: Saved.Epoch, roomHostControllerId: savedHost, authorityPaused: Saved.IsPaused);
            Manager = new SaveStub(Session);
            Provider.SetupProperty(value => value.CoopSession, empty);
            Controller.SetControllerId(localId);
            var players = new Mock<IPlayerManager>();
            players.SetupGet(value => value.Players).Returns(Session.Players);
            Handler = new SaveGameHandler(Broker, Manager, Provider.Object, players.Object,
                Mock.Of<IPlayerPartyRestorer>(), Mock.Of<INetwork>(), Mock.Of<IObjectManager>(), Identity, Controller, Gate, finalizer);
        }
        public void Dispose() { Handler.Dispose(); ModInformation.SessionMode = previous; }
    }

    private sealed class SaveStub : ICoopSaveManager
    {
        private readonly ICoopSession saved;
        public ICoopSession? Written;
        public SaveStub(ICoopSession saved) { this.saved = saved; }
        public string DefaultPath => string.Empty;
        public string FileType => ".json";
        public ICoopSession LoadCoopSession(string name) => saved;
        public void SaveCoopSession(string name, ICoopSession value) => Written = value;
    }
}

