using Common;
using Common.Network;
using Common.Network.Messages;
using Common.Tests.Utils;
using Common.Util;
using Coop.Core.Server.Connections;
using Coop.Core.Server.Connections.Messages;
using Coop.Core.Server.Services.Players.Handlers;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using GameInterface.Services.SiegeEvents.Interfaces;
using HarmonyLib;
using LiteNetLib;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using Xunit;
using Xunit.Abstractions;

namespace Coop.Tests.Server.Services.Players;

[Collection(ModInformationRoleCollection.Name)]
public sealed class PlayerPartyVisibilityLeaseTests : IDisposable
{
    private readonly SessionMode previousMode = ModInformation.SessionMode;
    private readonly Campaign? previousCampaign = Campaign.Current;
    private readonly Type sandBoxViewSubModuleType;
    private readonly object? previousSandBoxViewSubModule;
    private readonly ITestOutputHelper output;

    public PlayerPartyVisibilityLeaseTests(ITestOutputHelper output)
    {
        this.output = output;
        ModInformation.SessionMode = SessionMode.ListenAuthority;

        var campaign = (Campaign)FormatterServices.GetUninitializedObject(typeof(Campaign));
        campaign.CampaignEventDispatcher = new CampaignEventDispatcher(Array.Empty<CampaignEventReceiver>());
        Campaign.Current = campaign;

        sandBoxViewSubModuleType = Type.GetType(
            "SandBox.View.SandBoxViewSubModule, SandBox.View",
            throwOnError: true)!;
        var visualManagerType = Type.GetType(
            "SandBox.View.SandBoxViewVisualManager, SandBox.View",
            throwOnError: true)!;
        var instanceField = AccessTools.Field(sandBoxViewSubModuleType, "_instance");
        previousSandBoxViewSubModule = instanceField.GetValue(null);

        var subModule = FormatterServices.GetUninitializedObject(sandBoxViewSubModuleType);
        AccessTools.Field(sandBoxViewSubModuleType, "_sandBoxViewVisualManager")
            .SetValue(subModule, Activator.CreateInstance(visualManagerType));
        instanceField.SetValue(null, subModule);
    }

    public void Dispose()
    {
        ModInformation.SessionMode = previousMode;
        Campaign.Current = previousCampaign;
        AccessTools.Field(sandBoxViewSubModuleType, "_instance")
            .SetValue(null, previousSandBoxViewSubModule);
    }

    [Fact]
    public void DisconnectOnPausedHostClearsPeerButDoesNotParkPartyOrTouchVisualRegistration()
    {
        var peer = CreatePeer();
        var player = CreatePlayer();
        var party = CreateParty(isActive: true, isVisible: true);
        var players = new TestPlayerManager(player, peer);
        var objects = new Mock<IObjectManager>();
        objects.Setup(manager => manager.TryGetObjectWithLogging(player.MobilePartyId, out party)).Returns(true);
        var broker = new TestMessageBroker();
        using var handler = CreateHandler(broker, players, objects.Object, CreateAuthority("paused"));

        broker.Publish(this, new PlayerDisconnected(peer, default));

        Assert.Null(players.CurrentPeer);
        Assert.Single(broker.GetMessagesFromType<PlayerConnectionStateChanged>());
        GameThread.Run(() => { }, blocking: true);

        Assert.True(party.IsActive);
        Assert.True(party.IsVisible);
        AssertNoVisualRegistryWrites(objects);
    }

    [Fact]
    public void DisconnectOnCurrentWriterStillParksParty()
    {
        var peer = CreatePeer();
        var player = CreatePlayer();
        var party = CreateParty(isActive: true, isVisible: true);
        var players = new TestPlayerManager(player, peer);
        var objects = new Mock<IObjectManager>();
        objects.Setup(manager => manager.TryGetObjectWithLogging(player.MobilePartyId, out party)).Returns(true);
        var broker = new TestMessageBroker();
        using var handler = CreateHandler(broker, players, objects.Object, CreateAuthority("writer"));

        broker.Publish(this, new PlayerDisconnected(peer, default));
        GameThread.Run(() => { }, blocking: true);

        Assert.False(party.IsActive);
        Assert.False(party.IsVisible);
    }

    [Theory]
    [InlineData("paused")]
    [InlineData("observer")]
    [InlineData("frozen")]
    public void SynchronizationWithoutLocalWritableLeaseDoesNotRestorePartyOrCreateVisual(string authorityState)
    {
        var peer = CreatePeer();
        var player = CreatePlayer();
        var party = CreateParty(isActive: false, isVisible: false);
        var hero = CreateActiveHero();
        var players = new TestPlayerManager(player, peer);
        var objects = CreateCampaignObjects(player, party, hero);
        var broker = new TestMessageBroker();
        using var handler = CreateHandler(broker, players, objects.Object, CreateAuthority(authorityState));

        broker.Publish(this, new PlayerCampaignSynchronized(peer));
        GameThread.Run(() => { }, blocking: true);

        Assert.False(party.IsActive);
        Assert.False(party.IsVisible);
        AssertNoVisualRegistryWrites(objects);
    }

    [Fact]
    public void SynchronizationWithCurrentWriterPreservesExistingRestoreAction()
    {
        var peer = CreatePeer();
        var player = CreatePlayer();
        var party = CreateParty(isActive: false, isVisible: false);
        var hero = CreateActiveHero();
        var players = new TestPlayerManager(player, peer);
        var objects = CreateCampaignObjects(player, party, hero);
        var broker = new TestMessageBroker();
        using var handler = CreateHandler(broker, players, objects.Object, CreateAuthority("writer"));

        broker.Publish(this, new PlayerCampaignSynchronized(peer));
        GameThread.Run(() => { }, blocking: true);

        Assert.True(party.IsActive);
        Assert.True(party.IsVisible);
        Assert.True(party.IsInspected);
    }

    [Fact]
    public void DedicatedAuthorityWithKnownLeaseStillRunsExistingRestoreAction()
    {
        var previousMode = ModInformation.SessionMode;
        ModInformation.SessionMode = SessionMode.DedicatedAuthority;
        try
        {
            var peer = CreatePeer();
            var player = CreatePlayer();
            var party = CreateParty(isActive: false, isVisible: false);
            var hero = CreateActiveHero();
            var players = new TestPlayerManager(player, peer);
            var objects = CreateCampaignObjects(player, party, hero);
            var broker = new TestMessageBroker();
            using var handler = CreateHandler(broker, players, objects.Object, CreateAuthority("observer"));

            broker.Publish(this, new PlayerCampaignSynchronized(peer));
            GameThread.Run(() => { }, blocking: true);

            Assert.True(party.IsActive);
            Assert.True(party.IsVisible);
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
        }
    }

    [Fact]
    public void QueuedDisconnectDoesNotParkPartyAfterSameControllerReconnects()
    {
        var disconnectedPeer = CreatePeer();
        var reconnectedPeer = CreatePeer();
        var player = CreatePlayer();
        var party = CreateParty(isActive: true, isVisible: true);
        var players = new TestPlayerManager(player, disconnectedPeer);
        var objects = new Mock<IObjectManager>();
        objects.Setup(manager => manager.TryGetObjectWithLogging(player.MobilePartyId, out party)).Returns(true);
        var broker = new TestMessageBroker();
        using var handler = CreateHandler(broker, players, objects.Object, CreateAuthority("writer"));

        using var queued = new IsolatedGameQueue(output);
        broker.Publish(this, new PlayerDisconnected(disconnectedPeer, default));
        Assert.Equal(1, queued.PendingCount);
        Assert.True(party.IsActive);
        players.SetPeer(reconnectedPeer);
        queued.Drain();

        Assert.True(party.IsActive);
        Assert.True(party.IsVisible);
        AssertNoVisualRegistryWrites(objects);
    }

    [Fact]
    public void QueuedSynchronizationDoesNotRestorePartyForSupersededPeer()
    {
        var supersededPeer = CreatePeer();
        var currentPeer = CreatePeer();
        var player = CreatePlayer();
        var party = CreateParty(isActive: false, isVisible: false);
        var hero = CreateActiveHero();
        var players = new TestPlayerManager(player, supersededPeer);
        var objects = CreateCampaignObjects(player, party, hero);
        var broker = new TestMessageBroker();
        using var handler = CreateHandler(broker, players, objects.Object, CreateAuthority("writer"));

        using var queued = new IsolatedGameQueue(output);
        broker.Publish(this, new PlayerCampaignSynchronized(supersededPeer));
        Assert.Equal(1, queued.PendingCount);
        Assert.False(party.IsActive);
        Assert.Empty(objects.Invocations);
        players.SetPeer(currentPeer);
        queued.Drain();

        Assert.False(party.IsActive);
        Assert.False(party.IsVisible);
        Assert.Empty(objects.Invocations);
    }

    [Fact]
    public void QueuedSynchronizationDoesNotRunAfterHandlerDisposal()
    {
        var peer = CreatePeer();
        var player = CreatePlayer();
        var party = CreateParty(isActive: false, isVisible: false);
        var hero = CreateActiveHero();
        var players = new TestPlayerManager(player, peer);
        var objects = CreateCampaignObjects(player, party, hero);
        var broker = new TestMessageBroker();
        var handler = CreateHandler(broker, players, objects.Object, CreateAuthority("writer"));

        using var queued = new IsolatedGameQueue(output);
        broker.Publish(this, new PlayerCampaignSynchronized(peer));
        Assert.Equal(1, queued.PendingCount);
        Assert.False(party.IsActive);
        Assert.Empty(objects.Invocations);
        handler.Dispose();
        queued.Drain();

        Assert.False(party.IsActive);
        Assert.Empty(objects.Invocations);
    }

    private sealed class IsolatedGameQueue : IDisposable
    {
        private readonly Type contextType = AccessTools.Inner(typeof(GameThread), "QueueContext");
        private readonly object queue;
        private readonly ITestOutputHelper output;
        private IDisposable? active;

        public IsolatedGameQueue(ITestOutputHelper output)
        {
            Assert.False(GameThread.Instance.IsGameThread);
            this.output = output;
            queue = Activator.CreateInstance(contextType, nonPublic: true)!;
            active = Activate();
        }

        public int PendingCount => (int)AccessTools.Property(contextType, "Count").GetValue(queue)!;

        public void Drain()
        {
            Assert.Equal(1, PendingCount);
            int callerThread = Thread.CurrentThread.ManagedThreadId, executionThread = 0;
            bool ranOnGameThread = false;
            active!.Dispose();
            active = null;
            GameThread.Run(() =>
            {
                using var scope = Activate();
                executionThread = Thread.CurrentThread.ManagedThreadId;
                ranOnGameThread = GameThread.Instance.IsGameThread;
                GameThread.Instance.Update(TimeSpan.Zero);
            }, blocking: true);
            Assert.Equal(0, PendingCount);
            Assert.True(ranOnGameThread);
            Assert.NotEqual(callerThread, executionThread);
            output.WriteLine($"Private queued action: pendingBefore=1, pendingAfter={PendingCount}, callerThread={callerThread}, gameLoopThread={executionThread}, ranOnGameThread={ranOnGameThread}");
        }

        private IDisposable Activate() => (IDisposable)AccessTools.Method(typeof(GameThread), "ActivateQueue")
            .Invoke(null, new[] { queue })!;

        public void Dispose()
        {
            AccessTools.Method(typeof(GameThread), "DiscardQueuedActions", new[] { contextType })
                .Invoke(GameThread.Instance, new[] { queue });
            active?.Dispose();
        }
    }

    private static PlayerPartyVisibilityHandler CreateHandler(
        TestMessageBroker broker,
        TestPlayerManager players,
        IObjectManager objects,
        ICampaignAuthorityContext authority) => new(
            broker,
            players,
            Mock.Of<IConnectionCollection>(),
            objects,
            Mock.Of<INetwork>(),
            Mock.Of<ISiegeEventInterface>(),
            authority);

    private static Mock<IObjectManager> CreateCampaignObjects(Player player, MobileParty party, Hero hero)
    {
        var objects = new Mock<IObjectManager>();
        objects.Setup(manager => manager.TryGetObjectWithLogging(player.MobilePartyId, out party)).Returns(true);
        objects.Setup(manager => manager.TryGetObjectWithLogging(player.HeroId, out hero)).Returns(true);
        return objects;
    }

    private static ICampaignAuthorityContext CreateAuthority(string state)
    {
        var identity = new SessionIdentity();
        var lease = state switch
        {
            "paused" => new AuthorityLease("room-a", "authority-paused", string.Empty, 4, isPaused: true),
            "observer" => new AuthorityLease("room-a", "authority-b", "controller-b", 5),
            _ => new AuthorityLease("room-a", "authority-a", "controller-a", 5),
        };
        Assert.True(identity.TryApply(lease, "controller-host"));
        if (state == "frozen") Assert.True(identity.TryFreezeLocalAuthority(lease, 31));

        var controller = new ControllerIdProvider();
        controller.SetControllerId("controller-a");
        return new CampaignAuthorityContext(identity, controller);
    }

    private static void AssertNoVisualRegistryWrites(Mock<IObjectManager> objects)
    {
        Assert.DoesNotContain(objects.Invocations, invocation =>
            invocation.Method.Name is nameof(IObjectManager.AddNewObject) or
                nameof(IObjectManager.AddExisting) or nameof(IObjectManager.Remove));
    }

    private static NetPeer CreatePeer() =>
        (NetPeer)FormatterServices.GetUninitializedObject(typeof(NetPeer));

    private static Player CreatePlayer() =>
        new Player("controller-a", "Hero_One", "Party_One", "Clan_One", "Character_One");

    private static Hero CreateActiveHero()
    {
        var hero = (Hero)FormatterServices.GetUninitializedObject(typeof(Hero));
        hero._heroState = Hero.CharacterStates.Active;
        return hero;
    }

    private static MobileParty CreateParty(bool isActive, bool isVisible)
    {
        var party = (MobileParty)FormatterServices.GetUninitializedObject(typeof(MobileParty));
        party.Party = (PartyBase)FormatterServices.GetUninitializedObject(typeof(PartyBase));
        party.Party.MobileParty = party;
        party.IsActive = isActive;
        party.IsVisible = isVisible;
        return party;
    }

    private sealed class TestPlayerManager : IPlayerManager
    {
        private Player player;

        public TestPlayerManager(Player player, NetPeer peer)
        {
            this.player = player;
            CurrentPeer = peer;
        }

        public NetPeer? CurrentPeer { get; private set; }
        public IReadOnlyCollection<Player> Players => player == null ? Array.Empty<Player>() : new[] { player };

        public void SetPeer(NetPeer peer) => CurrentPeer = peer;

        public bool TryGetPlayer(NetPeer peer, out Player player)
        {
            player = ReferenceEquals(peer, CurrentPeer) ? this.player : null!;
            return player != null;
        }

        public bool TryGetPlayer(string controllerId, out Player player)
        {
            player = this.player != null && this.player.ControllerId == controllerId ? this.player : null!;
            return player != null;
        }

        public bool TryGetPeer(string controllerId, out NetPeer peer)
        {
            peer = player != null && player.ControllerId == controllerId ? CurrentPeer! : null!;
            return peer != null;
        }

        public void ClearPeer(NetPeer peer)
        {
            if (ReferenceEquals(CurrentPeer, peer)) CurrentPeer = null;
        }

        public bool IsConnected(Player player) => ReferenceEquals(this.player, player) && CurrentPeer != null;
        public bool IsReservedController(string controllerId) => false;
        public bool Contains(object obj) => false;
        public bool AddPlayer(Player player) => false;
        public bool ReplacePlayer(Player registeredPlayer, Player replacementPlayer) => false;
        public bool RemovePlayer(Player player) => false;
        public void SetPeer(string controllerId, NetPeer peer) => CurrentPeer = peer;
        public bool IsOwnerOfPartyDisconnected(MobileParty party) => false;
        public bool IsOwnerOfHeroDisconnected(Hero hero) => false;
    }
}
