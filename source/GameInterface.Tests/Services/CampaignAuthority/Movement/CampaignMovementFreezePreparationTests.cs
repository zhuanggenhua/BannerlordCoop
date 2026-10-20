using Autofac;
using Common;
using Common.Messaging;
using Common.Logging;
using Common.Network;
using Common.Tests.Utils;
using Common.Network.Coalescing;
using Common.PacketHandlers;
using Common.Util;
using Coop.Core.Client.Services.MobileParties.Handlers;
using Coop.Core.Server.Services.MobileParties.PacketHandlers;
using Coop.Tests.Mocks;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.CampaignAuthority.Movement;
using GameInterface.Services.Entity;
using GameInterface.Services.MobileParties.Data;
using GameInterface.Services.MobileParties.Handlers;
using GameInterface.Services.MobileParties.Interfaces;
using GameInterface.Services.MobileParties.Messages.Behavior;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using GameInterface.Surrogates;
using HarmonyLib;
using Moq;
using Serilog;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Map;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;
using Xunit;

namespace GameInterface.Tests.Services.CampaignAuthority.Movement;

[Collection(nameof(ModInformationRoleCollection))]
public sealed class CampaignMovementFreezePreparationTests
{
    static CampaignMovementFreezePreparationTests() => RuntimeHelpers.RunModuleConstructor(typeof(TestNetwork).Module.ModuleHandle);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NativeNavigationPublishesOnceThroughOriginalAuthorityPath(bool hostWriter)
    {
        using var fixture = new Fixture(hostWriter);
        var party = fixture.StaleNpc();
        var originalPosition = party.Position;
        var originalDefault = party.DefaultBehavior;
        var originalShort = party.ShortTermBehavior;
        var originalTarget = party.TargetParty;
        var destination = party.MoveTargetParty.Position;
        Assert.False(fixture.Checkpoints.TryCapture(fixture.Lease, 100, out _));
        Assert.True(fixture.Prepare());
        Assert.Equal(MoveModeType.Point, party.PartyMoveMode);
        Assert.Null(party.MoveTargetParty);
        Assert.Equal(destination, party.MoveTargetPoint);
        Assert.Equal(originalPosition, party.Position);
        Assert.Equal(originalDefault, party.DefaultBehavior);
        Assert.Equal(originalShort, party.ShortTermBehavior);
        Assert.Same(originalTarget, party.TargetParty);
        Assert.True(fixture.Checkpoints.TryCapture(fixture.Lease, 100, out var capture));
        Assert.Equal(1, capture.PartyCount);
        Assert.Single(fixture.Updates);
        Assert.Single(fixture.Deltas);
        Assert.Equal(MoveModeType.Point, fixture.Deltas[0].BehaviorUpdateData.PartyMoveMode);
        Assert.Equal(0u, fixture.Deltas[0].BehaviorUpdateData.MoveTargetPartyId);
        Assert.False(fixture.Deltas[0].BehaviorUpdateData.ForcePosition);
        Assert.Equal(0, fixture.ControlledRequests);
        Assert.True(fixture.Prepare());
        Assert.Single(fixture.Updates);
        Assert.Single(fixture.Deltas);
    }

    [Theory]
    [InlineData("observer")]
    [InlineData("frozen")]
    [InlineData("epoch")]
    [InlineData("allowed")]
    [InlineData("off-thread")]
    public void IneligibleWriterNeverRepairsOrPublishes(string reason)
    {
        using var fixture = new Fixture(false);
        var party = fixture.StaleNpc();
        var target = party.MoveTargetParty;
        if (reason == "observer") fixture.Controller.SetControllerId("a");
        if (reason == "frozen") Assert.True(fixture.Identity.TryFreezeLocalAuthority(fixture.Lease, 1));
        var requested = reason == "epoch" ? new AuthorityLease("room", "old", "b", 1) : fixture.Lease;
        bool result = false;
        if (reason == "off-thread") result = fixture.Service.TryPrepareForFreeze(requested);
        else GameThread.Run(() =>
        {
            if (reason == "allowed") { using (new AllowedThread()) result = fixture.Service.TryPrepareForFreeze(requested); }
            else result = fixture.Service.TryPrepareForFreeze(requested);
        }, blocking: true);
        Assert.False(result);
        Assert.Same(target, party.MoveTargetParty);
        Assert.Equal(MoveModeType.Party, party.PartyMoveMode);
        Assert.Empty(fixture.Updates);
        Assert.Empty(fixture.Deltas);
    }

    [Theory]
    [InlineData("missing-ai")]
    [InlineData("target-party")]
    [InlineData("target-settlement")]
    [InlineData("interactable")]
    [InlineData("unknown-identity")]
    [InlineData("invalid-position")]
    [InlineData("invalid-destination")]
    public void EntireGraphMustValidateBeforeFirstNpcChanges(string reason)
    {
        using var fixture = new Fixture(true);
        var first = fixture.StaleNpc();
        var target = first.MoveTargetParty;
        var later = fixture.Party(3);
        if (reason == "missing-ai") later.Ai = null;
        if (reason == "target-party") later._targetParty = target;
        if (reason == "target-settlement") later._targetSettlement = ObjectHelper.SkipConstructor<Settlement>();
        if (reason == "interactable") later.Ai.AiBehaviorInteractable = target.Party;
        if (reason == "unknown-identity") fixture.Objects.Remove(later);
        if (reason == "invalid-position") later._position = Fixture.Point(float.NaN, 1);
        if (reason == "invalid-destination") target._position = Fixture.Point(float.NaN, 1);
        Assert.False(fixture.Prepare());
        Assert.Same(target, first.MoveTargetParty);
        Assert.Equal(MoveModeType.Party, first.PartyMoveMode);
        Assert.Empty(fixture.Updates);
        Assert.Empty(fixture.Deltas);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingNavigationTargetCannotHideOtherStaleReferences(bool behaviorTarget)
    {
        using var fixture = new Fixture(true);
        var party = fixture.StaleNpc();
        Assert.True(fixture.Objects.AddExisting("PartyBase_target", party.MoveTargetParty.Party, 50));
        if (behaviorTarget) party._targetParty = party.MoveTargetParty;
        else party.Ai.AiBehaviorInteractable = party.MoveTargetParty.Party;
        Assert.False(fixture.Prepare());
        Assert.NotNull(party.MoveTargetParty);
        Assert.Empty(fixture.Updates);
    }

    [Fact]
    public void ValidActivePartyNavigationIsPreservedWithoutPublication()
    {
        using var fixture = new Fixture(true);
        var party = fixture.Party(1);
        var target = fixture.Party(2);
        party.PartyMoveMode = MoveModeType.Party;
        party.MoveTargetParty = target;
        Assert.True(fixture.Prepare());
        Assert.Same(target, party.MoveTargetParty);
        Assert.Equal(MoveModeType.Party, party.PartyMoveMode);
        Assert.Empty(fixture.Updates);
        Assert.True(fixture.Checkpoints.TryCapture(fixture.Lease, 100, out var checkpoint));
        Assert.Equal(2, checkpoint.PartyCount);
    }

    [Fact]
    public void RegisteredPlayerStaleNavigationIsRefused()
    {
        using var fixture = new Fixture(true);
        var party = fixture.StaleNpc();
        var player = new Player("remote", null, "Created_1", null, null);
        var players = new PlayerManager(Mock.Of<ILogger>(), fixture.Objects, fixture.Controller);
        Assert.True(players.AddPlayer(player));
        try
        {
            Assert.True(players.Contains(party));
            Assert.False(fixture.Prepare());
            Assert.NotNull(party.MoveTargetParty);
            Assert.Empty(fixture.Updates);
        }
        finally { Assert.True(players.RemovePlayer(player)); }
    }

    [Fact]
    public void NativeSetterFailureIsLoggedAndNeverPublishesOrCertifies()
    {
        using var fixture = new Fixture(true);
        var party = fixture.StaleNpc();
        fixture.FailNativeSetter();
        var logs = new List<string>();
        Action<string> capture = value => logs.Add(value);
        OutputSinkManager.AddLogCallback(capture);
        try
        {
            Assert.False(fixture.Prepare());
            Assert.NotNull(party.MoveTargetParty);
            Assert.Empty(fixture.Updates);
            Assert.Contains(logs, value => value.Contains("Pre-freeze navigation failed"));
        }
        finally { OutputSinkManager.RemoveLogCallback(capture); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Campaign previous = Campaign.Current;
        private readonly SessionMode previousMode = ModInformation.SessionMode;
        private readonly ILifetimeScope previousContainer;
        private readonly IContainer container;
        private readonly Harmony harmony = new($"pre-freeze-navigation.{Guid.NewGuid():N}");
        private readonly Campaign campaign = ObjectHelper.SkipConstructor<Campaign>();
        private readonly MobilePartyBehaviorHandler behavior;
        private readonly IDisposable networkBehavior;
        internal readonly ObjectManager Objects = new(Mock.Of<ILogger>());
        internal readonly SessionIdentity Identity = new();
        internal readonly ControllerIdProvider Controller = new();
        internal readonly AuthorityLease Lease;
        internal readonly CampaignMovementFreezePreparation Service;
        internal readonly CampaignMovementCheckpointService Checkpoints;
        internal readonly List<PartyBehaviorUpdated> Updates = new();
        internal readonly List<NetworkCampaignAuthorityPartyBehaviorDelta> Deltas = new();
        internal int ControlledRequests;
        private readonly SendCoalescer coalescer = new();
        private readonly INetwork wireNetwork;

        internal Fixture(bool host)
        {
            _ = new SurrogateCollection();
            ModInformation.SessionMode = host ? SessionMode.ListenAuthority : SessionMode.RemoteReplica;
            campaign.CampaignObjectManager = new CampaignObjectManager { Settlements = new MBReadOnlyList<Settlement>(new List<Settlement>()) };
            var scene = new Mock<IMapScene>();
            scene.Setup(value => value.GetFaceIndex(in It.Ref<CampaignVec2>.IsAny)).Returns(PathFaceRecord.NullFaceRecord);
            campaign._mapSceneWrapper = scene.Object;
            Campaign.Current = campaign;
            harmony.Patch(AccessTools.PropertyGetter(typeof(MobileParty), nameof(MobileParty.NavigationCapability)),
                prefix: new HarmonyMethod(typeof(Fixture), nameof(Navigation)));
            Lease = new AuthorityLease("room", "authority", host ? "a" : "b", 2);
            Assert.True(Identity.TryApply(Lease, "a"));
            Controller.SetControllerId(Lease.AuthorityControllerId);
            var authority = new CampaignAuthorityContext(Identity, Controller);
            ContainerProvider.TryGetContainer(out previousContainer);
            var builder = new ContainerBuilder();
            builder.RegisterInstance(authority).As<ICampaignAuthorityContext>();
            container = builder.Build();
            ContainerProvider.SetContainer(container);
            var snapshot = new MobilePartyBehaviorSnapshot(Objects);
            var mapper = new PartyBehaviorWireMapper(Objects);
            var messages = new TestMessageBroker();
            messages.Subscribe<PartyBehaviorUpdated>(payload => Updates.Add(payload.What));
            messages.Subscribe<ControlledPartyBehaviorUpdated>(_ => ControlledRequests++);
            var network = new Mock<INetwork>();
            network.Setup(value => value.SendAll(It.IsAny<IMessage>())).Callback<IMessage>(message =>
            { if (message is NetworkCampaignAuthorityPartyBehaviorDelta delta) Deltas.Add(delta); });
            wireNetwork = network.Object;
            behavior = new MobilePartyBehaviorHandler(messages, Controller, Mock.Of<IMobilePartyInterface>(), mapper, Objects, snapshot);
            if (host)
            {
                var handlerType = typeof(ClientMobilePartyBehaviorHandler).Assembly.GetType(
                    "Coop.Core.Server.Services.MobileParties.PacketHandlers.RequestMobilePartyBehaviorPacketHandler", throwOnError: true);
                networkBehavior = (IDisposable)Activator.CreateInstance(handlerType, Mock.Of<IPacketManager>(), coalescer,
                    messages, network.Object, mapper, Mock.Of<IPlayerManager>(), authority);
            }
            else networkBehavior = new ClientMobilePartyBehaviorHandler(messages, network.Object, mapper, authority);
            Service = new CampaignMovementFreezePreparation(authority, snapshot, mapper, messages);
            Checkpoints = new CampaignMovementCheckpointService(snapshot, mapper);
        }

        internal void FailNativeSetter() => harmony.Patch(
            AccessTools.Method(typeof(MobileParty), "SetNavigationModePoint"),
            prefix: new HarmonyMethod(typeof(Fixture), nameof(FailedNavigation)));
        private static void FailedNavigation() => throw new InvalidOperationException("native setter review failure");

        internal bool Prepare()
        {
            bool result = false;
            GameThread.Run(() => { result = Service.TryPrepareForFreeze(Lease); coalescer.Flush(wireNetwork); }, blocking: true);
            return result;
        }

        internal MobileParty StaleNpc()
        {
            var party = Party(1);
            var target = Party(2);
            campaign.CampaignObjectManager._mobileParties.Remove(target);
            party.PartyMoveMode = MoveModeType.Party;
            party.MoveTargetParty = target;
            party._defaultBehavior = AiBehavior.PatrolAroundPoint;
            party.ShortTermBehavior = AiBehavior.GoToPoint;
            return party;
        }

        internal MobileParty Party(uint handle)
        {
            var party = ObjectHelper.SkipConstructor<MobileParty>();
            party.StringId = "npc_" + handle;
            party.IsActive = true;
            party.Ai = new MobilePartyAi(party);
            party.Party = ObjectHelper.SkipConstructor<PartyBase>();
            party.Party.MobileParty = party;
            party._position = Point(handle * 10, handle * 20);
            party.MoveTargetPoint = Point(300, 400);
            party.PartyMoveMode = MoveModeType.Point;
            party._defaultBehavior = AiBehavior.Hold;
            party.ShortTermBehavior = AiBehavior.Hold;
            campaign.CampaignObjectManager._mobileParties.Add(party);
            Assert.True(Objects.AddExisting("MobileParty_Created_" + handle, party, handle));
            return party;
        }

        internal static CampaignVec2 Point(float x, float y) => new(new Vec2(x, y), true);
        private static bool Navigation(ref MobileParty.NavigationType __result) { __result = MobileParty.NavigationType.Default; return false; }
        public void Dispose()
        {
            networkBehavior.Dispose(); behavior.Dispose(); harmony.UnpatchAll(harmony.Id);
            if (previousContainer != null) ContainerProvider.SetContainer(previousContainer); else ContainerProvider.Clear();
            container.Dispose(); Campaign.Current = previous; ModInformation.SessionMode = previousMode;
        }
    }
}
