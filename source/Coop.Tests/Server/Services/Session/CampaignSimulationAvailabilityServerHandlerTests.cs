using GameInterface.Services.CampaignAuthority.Movement;
using Common;
using Common.Messaging;
using Common.Network.Messages;
using Common.Network.Session;
using Common.Session;
using Common.Serialization;
using Common.Tests.Utils;
using Coop.Core.Common.Session;
using Coop.Core.Server.Connections;
using Coop.Core.Server.Services.Session;
using Coop.Core.Server.States.Messages;
using Coop.Tests.Mocks;
using Common.Network;
using Common.Network.Coalescing;
using GameInterface.Services.Entity;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Time.Interfaces;
using GameInterface.Services.GameState.Messages;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using GameInterface.Services.Time.Messages;
using Moq;
using Xunit;

namespace Coop.Tests.Server.Services.Session;

[Collection(ModInformationRoleCollection.Name)]
public class CampaignSimulationAvailabilityServerHandlerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompleteFreezeAckFingerprintPreservesNativeFloatBitsAndWireDuplicates(bool nativeNaN)
    {
        var ack = FingerprintAck(nativeNaN);
        using var stream = new System.IO.MemoryStream();
        ProtoBuf.Serializer.Serialize(stream, ack);
        stream.Position = 0;
        var duplicate = ProtoBuf.Serializer.Deserialize<NetworkCampaignAuthorityFreezeAcknowledged>(stream);
        int expectedBits = System.BitConverter.SingleToInt32Bits(ack.SessionCheckpoint.NativeBehaviors.Caravans.Cache.LandPrices[0].Average);
        Assert.Equal(expectedBits, System.BitConverter.SingleToInt32Bits(duplicate.SessionCheckpoint.NativeBehaviors.Caravans.Cache.LandPrices[0].Average));
        Assert.Equal(Fingerprint(ack), Fingerprint(ack));
        Assert.Equal(Fingerprint(ack), Fingerprint(duplicate));
    }

    [Theory]
    [InlineData("room")]
    [InlineData("authority")]
    [InlineData("writer")]
    [InlineData("epoch")]
    [InlineData("candidate")]
    [InlineData("sequence")]
    [InlineData("allocation")]
    [InlineData("roster")]
    [InlineData("clock")]
    [InlineData("session")]
    [InlineData("native-bits")]
    [InlineData("scheduler")]
    [InlineData("movement")]
    public void CompleteFreezeAckFingerprintIncludesLeaseAndEveryCheckpointDomain(string mutation)
    {
        Assert.NotEqual(Fingerprint(FingerprintAck(true)), Fingerprint(FingerprintAck(true, mutation)));
    }

    private static string Fingerprint(NetworkCampaignAuthorityFreezeAcknowledged ack) =>
        (string)typeof(CampaignSimulationAvailabilityServerHandler).GetMethod("FreezeAckFingerprint",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, new object[] { ack })!;

    private static CampaignNativeBehaviorCheckpoint NativeCheckpoint(float average) => new(
        new CampaignAgingBehaviorState(1, new[] { new CampaignHeroIntState(7, 2) }, System.Array.Empty<CampaignHeroIntState>()),
        new CampaignCaravanBehaviorState(System.Array.Empty<CampaignPartyTimeState>(), System.Array.Empty<CampaignPartyTimeState>(),
            new CampaignCaravanCacheState(-1, -1, 0, System.Array.Empty<CampaignCategoryValueState>(),
                new[] { new CampaignCategoryPriceState(20, average, 1000) }, System.Array.Empty<CampaignCategoryPriceState>())));

    private static NetworkCampaignAuthorityFreezeAcknowledged FingerprintAck(bool nativeNaN, string mutation = "")
    {
        var lease = new AuthorityLease("room", "authority", "guest", 3);
        var native = NativeCheckpoint(nativeNaN ? float.NaN : 1.5f);
        if (mutation == "native-bits")
            native.Caravans.Cache.LandPrices[0] = new CampaignCategoryPriceState(20,
                System.BitConverter.Int32BitsToSingle(unchecked((int)0xffc00001)), 1000);
        return new NetworkCampaignAuthorityFreezeAcknowledged(
            mutation == "room" ? "other" : lease.RoomId,
            mutation == "authority" ? "other" : lease.AuthorityId,
            mutation == "writer" ? "other" : lease.AuthorityControllerId,
            mutation == "epoch" ? 4 : lease.Epoch,
            mutation == "candidate" ? "other" : "host", mutation == "sequence" ? 12 : 11,
            new RegistryAllocationCheckpoint(mutation == "allocation" ? 501u : 500u, new System.Collections.Generic.Dictionary<string, int> { ["type"] = 3 }),
            new CampaignRosterCheckpoint(new[] { new CampaignRosterState(10, new[] { new CampaignRosterElementState(11, mutation == "roster" ? 4 : 3, 1, 50) }) }),
            new CampaignClockCheckpoint(mutation == "clock" ? 1000 : 999),
            new CampaignSessionCheckpoint("campaign", mutation == "session" ? "changed" : "{}", native),
            new CampaignSchedulerCheckpoint(999, 5, mutation == "scheduler" ? 4 : 3,
                System.Array.Empty<CampaignPeriodicTickerState>(), System.Array.Empty<CampaignPeriodicEventState>()),
            new CampaignMovementCheckpoint(lease, 999, 1, new byte[] { mutation == "movement" ? (byte)2 : (byte)1 }, "hash"));
    }

    [Theory]
    [InlineData(false, false, "peer-not-synchronized")]
    [InlineData(true, false, "peer-player-unmapped")]
    [InlineData(true, true, "no-pending-freeze")]
    public void AckDiagnosticRejectionKeepsOriginalShortCircuitAndDoesNotDrain(bool synchronized, bool mapped, string stage)
    {
        var previous = ModInformation.SessionMode;ModInformation.SessionMode = SessionMode.ListenAuthority;
        var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        System.Action<string> capture = logs.Enqueue;
        Common.Logging.OutputSinkManager.AddLogCallback(capture);
        try
        {
            using var broker = new TestMessageBroker();
            var identity = new SessionIdentity();
            var lease = new AuthorityLease("diagnostic-room", "authority", "guest", 5);
            Assert.True(identity.TryApply(lease, "host"));
            var peer = new TestNetwork().CreatePeer();
            var calls = new System.Collections.Generic.List<string>();
            var connections = new Mock<IConnectionCollection>();
            connections.Setup(value => value.HasCompletedCampaignSynchronization(peer)).Callback(() => calls.Add("synchronized")).Returns(synchronized);
            var player = new Player("guest", "hero", "party", "clan", "character");
            var players = new Mock<IPlayerManager>();
            players.Setup(value => value.TryGetPlayer(peer, out player)).Callback(() => calls.Add("player")).Returns(mapped);
            var drain = new Mock<ICampaignAsyncTaskDrain>(MockBehavior.Strict);
            var controller = new ControllerIdProvider();controller.SetControllerId("host");
            using var handler = new CampaignSimulationAvailabilityServerHandler(broker,
                new CampaignAuthorityAvailabilityCoordinator(identity, new CampaignAuthorityCandidateSelector()),
                connections.Object, players.Object, controller, Mock.Of<INetwork>(), identity, asyncTasks: drain.Object, missionFacts: CampaignHandoffTestServices.MissionFacts(identity));
            broker.Publish(peer, new NetworkCampaignAuthorityFreezeRequest(lease, "host", 987662).ToAcknowledged());
            GameThread.Run(() => { }, blocking: true);
            Assert.Equal(synchronized ? new[] { "synchronized", "player" } : new[] { "synchronized" }, calls);
            Assert.Equal(lease, identity.CurrentLease);
            drain.Verify(value => value.TryDrain(), Times.Never);
            Assert.Contains(logs, text => text.StartsWith("Freeze ACK receive stage") && text.Contains("ingress") && text.Contains("987662"));
            Assert.Contains(logs, text => text.Contains("game-thread") && text.Contains("987662"));
            Assert.Contains(logs, text => text.Contains("rejected-" + stage) && text.Contains("987662"));
            Assert.DoesNotContain(logs, text => text.Contains("qualified-current-peer") && text.Contains("987662"));
        }
        finally { Common.Logging.OutputSinkManager.RemoveLogCallback(capture);ModInformation.SessionMode = previous; }
    }

    [Fact]
    public void AckGateExceptionStillEscapesTheOriginalApplyMethodUnchanged()
    {
        var previous = ModInformation.SessionMode;ModInformation.SessionMode = SessionMode.ListenAuthority;
        try
        {
            using var broker = new TestMessageBroker();
            var identity = new SessionIdentity();
            var peer = new TestNetwork().CreatePeer();
            var failure = new System.InvalidOperationException("original-ack-gate-failure");
            var connections = new Mock<IConnectionCollection>();
            connections.Setup(value => value.HasCompletedCampaignSynchronization(peer)).Throws(failure);
            var controller = new ControllerIdProvider();controller.SetControllerId("host");
            using var handler = new CampaignSimulationAvailabilityServerHandler(broker,
                new CampaignAuthorityAvailabilityCoordinator(identity, new CampaignAuthorityCandidateSelector()),
                connections.Object, Mock.Of<IPlayerManager>(), controller, Mock.Of<INetwork>(), identity, missionFacts: CampaignHandoffTestServices.MissionFacts(identity));
            var method = typeof(CampaignSimulationAvailabilityServerHandler).GetMethod("ApplyFreezeAcknowledgement",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var thrown = Assert.Throws<System.Reflection.TargetInvocationException>(() => method.Invoke(handler,
                new object[] { peer, default(NetworkCampaignAuthorityFreezeAcknowledged) }));
            Assert.Same(failure, thrown.InnerException);
        }
        finally { ModInformation.SessionMode = previous; }
    }

    [Fact]
    public void FailedHostNavigationPreparationCannotCommitPauseOrFlush()
    {
        var previousMode = ModInformation.SessionMode;
        ModInformation.SessionMode = SessionMode.ListenAuthority;
        try
        {
            var broker = new TestMessageBroker();
            var identity = new SessionIdentity();
            var lease = new AuthorityLease("room", "authority", "host", 1);
            Assert.True(identity.TryApply(lease, "host"));
            var coordinator = new CampaignAuthorityAvailabilityCoordinator(identity, new CampaignAuthorityCandidateSelector());
            var controller = new ControllerIdProvider(); controller.SetControllerId("host");
            var host = new Player("host", "hero", "party", "clan", "character");
            var players = new Mock<IPlayerManager>();
            players.Setup(value => value.TryGetPlayer("host", out host)).Returns(true);
            var network = new Mock<INetwork>();
            var coalescer = new Mock<ISendCoalescer>();
            var preparation = new Mock<ICampaignMovementFreezePreparation>();
            preparation.Setup(value => value.TryPrepareForFreeze(lease)).Returns(false);
            using var handler = new CampaignSimulationAvailabilityServerHandler(broker, coordinator,
                Mock.Of<IConnectionCollection>(), players.Object, controller, network.Object, identity,
                sendCoalescer: coalescer.Object, asyncTasks: CampaignHandoffTestServices.Drained(),
                movementPreparation: preparation.Object, missionFacts: CampaignHandoffTestServices.MissionFacts(identity));
            broker.Publish(this, new CampaignMapSimulationReadinessChanged(true));
            broker.Publish(this, new CampaignReady());
            broker.Publish(this, new CampaignInitializationCompleted());
            GameThread.Run(() => { }, blocking: true);
            broker.Publish(this, new CampaignMapSimulationReadinessChanged(false));
            GameThread.Run(() => { }, blocking: true);
            preparation.Verify(value => value.TryPrepareForFreeze(lease), Times.AtLeastOnce);
            Assert.Equal(lease, identity.CurrentLease);
            Assert.False(identity.IsLocallyFrozen(lease));
            coalescer.Verify(value => value.Flush(network.Object), Times.Never);
            network.Verify(value => value.SendAll(It.IsAny<NetworkAuthorityLeaseSnapshot>()), Times.Never);
        }
        finally { ModInformation.SessionMode = previousMode; }
    }

    [Fact]
    public void AvailabilityTimerKeepsRestoredLeaseWritableUntilCampaignObjectsAreRegistered()
    {
        var previousMode = ModInformation.SessionMode;
        ModInformation.SessionMode = SessionMode.ListenAuthority;
        try
        {
            var broker = new TestMessageBroker();
            var identity = new SessionIdentity();
            Assert.True(identity.TryApply(new AuthorityLease("room-a", "authority-a", "host", 1), "host"));
            var coordinator = new CampaignAuthorityAvailabilityCoordinator(
                identity, new CampaignAuthorityCandidateSelector());
            var controller = new ControllerIdProvider();
            controller.SetControllerId("host");
            var host = new Player("host", "hero-host", "party-host", "clan-host", "character-host");
            var players = new Mock<IPlayerManager>();
            players.Setup(value => value.TryGetPlayer("host", out host)).Returns(true);
            var objectManager = new ObjectManager(
                Serilog.Log.ForContext<ObjectManager>(),
                new CampaignAuthorityContext(identity, controller));
            using var handler = new CampaignSimulationAvailabilityServerHandler(
                broker,
                coordinator,
                Mock.Of<IConnectionCollection>(),
                players.Object,
                controller,
                Mock.Of<INetwork>(),
                identity,
                objectManager,
                asyncTasks: CampaignHandoffTestServices.Drained(), missionFacts: CampaignHandoffTestServices.MissionFacts(identity));

            // The timer's candidate-expiry pass runs before CampaignReady; it must not pause
            // the restored writer before the InitialServerState registry pass.
            handler.OnAvailabilityTimer();
            GameThread.Run(() => { }, blocking: true);
            broker.Publish(this, new CampaignReady());
            handler.OnAvailabilityTimer();
            GameThread.Run(() => { }, blocking: true);

            var roster = new object();
            Assert.True(objectManager.AddExisting("TroopRoster_preinit", roster));
            bool hasHandle = objectManager.TryGetHandle(roster, out var handle);
            Assert.True(!identity.CurrentLease.IsPaused && hasHandle && handle != 0,
                $"Pre-initialization reconciliation fenced the restored lease or registered a roster without a handle (paused={identity.CurrentLease.IsPaused}, epoch={identity.Epoch}, hasHandle={hasHandle}, handle={handle}).");

            broker.Publish(this, new CampaignMapSimulationReadinessChanged(true));
            broker.Publish(this, new CampaignInitializationCompleted());
            GameThread.Run(() => { }, blocking: true);

            Assert.False(identity.CurrentLease.IsPaused);
            Assert.Equal(1, identity.Epoch);
            Assert.True(objectManager.TryGetHandle(roster, out handle));
            Assert.NotEqual(0u, handle);
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
        }
    }

    [Fact]
    public void DisconnectedWriterWithoutFinalCheckpointStopsObserverClockWithoutChangingLease()
    {
        var previousMode = ModInformation.SessionMode;
        ModInformation.SessionMode = SessionMode.ListenAuthority;
        try
        {
            var broker = new TestMessageBroker();
            var identity = new SessionIdentity();
            var lease = new AuthorityLease("room-a", "authority-guest", "guest", 5);
            Assert.True(identity.TryApply(lease, "host"));
            var coordinator = new CampaignAuthorityAvailabilityCoordinator(identity, new CampaignAuthorityCandidateSelector());
            var guestPeer = new TestNetwork().CreatePeer();
            var host = new Player("host", "hero-host", "party-host", "clan-host", "character-host");
            var guest = new Player("guest", "hero-guest", "party-guest", "clan-guest", "character-guest");
            var players = new Mock<IPlayerManager>();
            players.Setup(value => value.TryGetPlayer("host", out host)).Returns(true);
            players.Setup(value => value.TryGetPlayer(guestPeer, out guest)).Returns(true);
            players.Setup(value => value.TryGetPeer("guest", out It.Ref<LiteNetLib.NetPeer>.IsAny))
                .Returns((string _, out LiteNetLib.NetPeer peer) => { peer = guestPeer; return true; });
            bool connected = true;
            var connections = new Mock<IConnectionCollection>();
            connections.Setup(value => value.HasCompletedCampaignSynchronization(guestPeer)).Returns(() => connected);
            var controller = new ControllerIdProvider();
            controller.SetControllerId("host");
            var clock = new MapTimeTrackerInterface();
            var network = new Mock<INetwork>();
            using var handler = new CampaignSimulationAvailabilityServerHandler(
                broker, coordinator, connections.Object, players.Object, controller,
                network.Object, identity, mapTimeTracker: clock, asyncTasks: CampaignHandoffTestServices.Drained(), missionFacts: CampaignHandoffTestServices.MissionFacts(identity));
            broker.Publish(this, new CampaignReady());
            broker.Publish(this, new CampaignInitializationCompleted());
            broker.Publish(guestPeer, new NetworkCampaignSimulationAvailabilityReport(CampaignSimulationAvailability.MapReady, 1));
            GameThread.Run(() => { }, blocking: true);
            connected = false;
            broker.Publish(this, new PlayerDisconnected(guestPeer, default));
            broker.Publish(this, new CampaignMapSimulationReadinessChanged(true));
            GameThread.Run(() => { }, blocking: true);

            var campaign = (TaleWorlds.CampaignSystem.Campaign)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(TaleWorlds.CampaignSystem.Campaign));
            campaign.MapTimeTracker = new TaleWorlds.CampaignSystem.MapTimeTracker(new TaleWorlds.CampaignSystem.CampaignTime(1000L));
            for (int frame = 0; frame < 50; frame++)
            {
                campaign._dt = 0.025f;
                campaign.MapTimeTracker._numTicks += 10L;
                campaign.MapTimeTracker._deltaTimeInTicks = 10L;
                clock.ApplyClientSimulationTime(campaign, 0.1f);
                Assert.Equal(1000L, campaign.MapTimeTracker._numTicks);
                Assert.Equal(0L, campaign.MapTimeTracker._deltaTimeInTicks);
                Assert.Equal(0f, campaign._dt);
            }
            Assert.Equal(lease, identity.CurrentLease);
            Assert.False(identity.IsLocallyFrozen(lease));
            network.Verify(value => value.SendAll(It.IsAny<NetworkAuthorityLeaseSnapshot>()), Times.Never);
            Assert.True(clock.IsObserverClockSuspended);
            connected = true;
            broker.Publish(this, new CampaignMapSimulationReadinessChanged(false));
            broker.Publish(guestPeer, new NetworkCampaignSimulationAvailabilityReport(CampaignSimulationAvailability.MapReady, 2));
            GameThread.Run(() => { }, blocking: true);
            Assert.False(clock.IsObserverClockSuspended);
            campaign.MapTimeTracker._numTicks += 10L;
            campaign.MapTimeTracker._deltaTimeInTicks = 10L;
            campaign._dt = 0.025f;
            clock.ApplyClientSimulationTime(campaign, 0.1f);
            Assert.Equal(1010L, campaign.MapTimeTracker._numTicks);
            Assert.Equal(lease, identity.CurrentLease);
            broker.Publish(this, new CampaignMapSimulationReadinessChanged(true));
            GameThread.Run(() => { }, blocking: true);
            Assert.True(clock.IsObserverClockSuspended);
            handler.Dispose();
            Assert.False(clock.IsObserverClockSuspended);
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
        }
    }

    [Fact]
    public void StaleReadinessPausesOnlyAfterTheInFlightGameThreadWorkCompletes()
    {
        var previousMode = ModInformation.SessionMode;
        ModInformation.SessionMode = SessionMode.ListenAuthority;
        try
        {
            var now = new System.DateTime(2026, 10, 18, 0, 0, 0, System.DateTimeKind.Utc);
            var messageBroker = new TestMessageBroker();
            var identity = new SessionIdentity();
            Assert.True(identity.TryApply(new AuthorityLease("room-a", "authority-a", "host", 1), "host"));
            var coordinator = new CampaignAuthorityAvailabilityCoordinator(
                identity,
                new CampaignAuthorityCandidateSelector(),
                () => now,
                System.TimeSpan.FromSeconds(3));
            var hostPlayer = new Player("host", "hero-host", "party-host", "clan-host", "character-host");
            var playerManager = new Mock<IPlayerManager>();
            playerManager.Setup(manager => manager.TryGetPlayer("host", out hostPlayer)).Returns(true);
            var connections = new Mock<IConnectionCollection>();
            var controllerId = new ControllerIdProvider();
            controllerId.SetControllerId("host");
            var network = new Mock<INetwork>();

            using var handler = new CampaignSimulationAvailabilityServerHandler(
                messageBroker,
                coordinator,
                connections.Object,
                playerManager.Object,
                controllerId,
                network.Object,
                identity, asyncTasks: CampaignHandoffTestServices.Drained(), missionFacts: CampaignHandoffTestServices.MissionFacts(identity));

            messageBroker.Publish(this, new CampaignMapSimulationReadinessChanged(true));
            messageBroker.Publish(this, new CampaignReady());
            messageBroker.Publish(this, new CampaignInitializationCompleted());
            Assert.True(coordinator.IsFreshMapReady("host"));
            GameThread.Run(() => { }, blocking: true);

            using var gameWorkStarted = new System.Threading.ManualResetEventSlim();
            using var releaseGameWork = new System.Threading.ManualResetEventSlim();
            GameThread.EnqueueSafe(() =>
            {
                gameWorkStarted.Set();
                if (!releaseGameWork.Wait(System.TimeSpan.FromSeconds(5)))
                    throw new System.TimeoutException("The test did not release in-flight game work.");
            });
            Assert.True(gameWorkStarted.Wait(System.TimeSpan.FromSeconds(5)));
            try
            {
                now = now.AddSeconds(4);
                handler.ReconcileCurrentAvailability();
                Assert.False(identity.CurrentLease.IsPaused);
                Assert.Equal(1, identity.Epoch);
            }
            finally
            {
                releaseGameWork.Set();
            }
            GameThread.Run(() => { }, blocking: true);

            Assert.True(identity.CurrentLease.IsPaused);
            Assert.Equal(2, identity.Epoch);
            network.Verify(value => value.SendAll(It.Is<NetworkAuthorityLeaseSnapshot>(
                snapshot => snapshot.IsPaused && snapshot.Epoch == 2)), Times.Once);
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
        }
    }

    [Fact]
    public void LocalReadinessHeartbeatRenewsTheHostLeaseWhileMapReady()
    {
        var previousMode = ModInformation.SessionMode;
        ModInformation.SessionMode = SessionMode.ListenAuthority;
        try
        {
            var now = new System.DateTime(2026, 10, 18, 0, 0, 0, System.DateTimeKind.Utc);
            var messageBroker = new TestMessageBroker();
            var identity = new SessionIdentity();
            Assert.True(identity.TryApply(new AuthorityLease("room-a", "authority-a", "host", 1), "host"));
            var coordinator = new CampaignAuthorityAvailabilityCoordinator(
                identity,
                new CampaignAuthorityCandidateSelector(),
                () => now,
                System.TimeSpan.FromSeconds(3));
            var hostPlayer = new Player("host", "hero-host", "party-host", "clan-host", "character-host");
            var playerManager = new Mock<IPlayerManager>();
            playerManager.Setup(manager => manager.TryGetPlayer("host", out hostPlayer)).Returns(true);
            var controllerId = new ControllerIdProvider();
            controllerId.SetControllerId("host");

            using var handler = new CampaignSimulationAvailabilityServerHandler(
                messageBroker,
                coordinator,
                Mock.Of<IConnectionCollection>(),
                playerManager.Object,
                controllerId,
                Mock.Of<INetwork>(),
                identity, asyncTasks: CampaignHandoffTestServices.Drained(), missionFacts: CampaignHandoffTestServices.MissionFacts(identity));

            messageBroker.Publish(this, new CampaignMapSimulationReadinessChanged(true));
            messageBroker.Publish(this, new CampaignReady());
            messageBroker.Publish(this, new CampaignInitializationCompleted());
            now = now.AddSeconds(2);
            handler.RefreshLocalAvailabilityAndReconcile();
            now = now.AddSeconds(2);
            handler.RefreshLocalAvailabilityAndReconcile();

            Assert.True(coordinator.IsFreshMapReady("host"));
            Assert.False(identity.CurrentLease.IsPaused);
            Assert.Equal(1, identity.Epoch);
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
        }
    }

    private sealed record NestedCriticalApply : ICampaignAuthorityScopedMessage;
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, false)]
    [InlineData(true, true, false, true)]
    public void TransfersToPreparedGuestAndRequiresFrozenCheckpointBeforeReturnOrAllAwayPause(bool hostReturnsImmediately, bool nestedAck, bool corruptRetainedAck, bool disposeQueuedAck)
        => RunPreparedGuestTransfer(hostReturnsImmediately, nestedAck, corruptRetainedAck, disposeQueuedAck);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetainedNativeNaNCheckpointResumesOrRefusesMutationOnTheActualAckPath(bool corruptNativeCheckpoint)
        => RunPreparedGuestTransfer(true, true, false, false, true, corruptNativeCheckpoint);

    private void RunPreparedGuestTransfer(bool hostReturnsImmediately, bool nestedAck, bool corruptRetainedAck,
        bool disposeQueuedAck, bool useNativeCheckpoint = false, bool corruptNativeCheckpoint = false)
    {
        var previousMode = ModInformation.SessionMode;
        ModInformation.SessionMode = SessionMode.ListenAuthority;
        try
        {
            using var messageBroker = new MessageBroker();
            var identity = new SessionIdentity();
            Assert.True(identity.TryApply(new AuthorityLease("room-a", "authority-a", "host", 1), "host"));
            var coordinator = new CampaignAuthorityAvailabilityCoordinator(
                identity,
                new CampaignAuthorityCandidateSelector());
            var guestPeer = new TestNetwork().CreatePeer();
            var hostPlayer = new Player("host", "hero-host", "party-host", "clan-host", "character-host");
            var guestPlayer = new Player("guest", "hero-guest", "party-guest", "clan-guest", "character-guest");
            var playerManager = new Mock<IPlayerManager>();
            playerManager.Setup(manager => manager.TryGetPlayer("host", out hostPlayer)).Returns(true);
            playerManager.Setup(manager => manager.TryGetPlayer(guestPeer, out guestPlayer)).Returns(true);
            playerManager.Setup(manager => manager.TryGetPeer("guest", out It.Ref<LiteNetLib.NetPeer>.IsAny))
                .Returns((string _, out LiteNetLib.NetPeer peer) =>
                {
                    peer = guestPeer;
                    return true;
                });
            var connections = new Mock<IConnectionCollection>();
            connections.Setup(value => value.HasCompletedCampaignSynchronization(guestPeer)).Returns(true);
            var controllerId = new ControllerIdProvider();
            controllerId.SetControllerId("host");
            var network = new Mock<INetwork>();
            NetworkCampaignAuthorityHandoffBarrier appliedBarrier = default;
            bool bufferedResultsFlushed = false;
            var rosters = new CampaignRosterCheckpoint(new CampaignRosterState[0]);
            var rosterService = new Mock<ICampaignRosterCheckpointService>();
            rosterService.Setup(value => value.TryCapture(out rosters)).Returns(true);
            long finalTicks = 999;
            var clock = new Mock<IMapTimeTrackerInterface>();
            bool observerHeld = false;
            clock.Setup(value => value.SetObserverClockSuspended(It.IsAny<bool>()))
                .Callback<bool>(value => observerHeld = value);
            clock.Setup(value => value.TryGetCurrentTicks(out finalTicks)).Callback(() =>
            {
                Assert.True(GameThread.Instance.IsGameThread);
                Assert.True(identity.CurrentLease.IsPaused);
            }).Returns(true);
            var sessionCheckpoint = new CampaignSessionCheckpoint("campaign", "{}",
                useNativeCheckpoint ? NativeCheckpoint(float.NaN) : null);
            var sessions = new Mock<ICampaignSessionCheckpointService>();
            sessions.Setup(value => value.TryCapture(out sessionCheckpoint))
                .Callback(() => Assert.True(identity.CurrentLease.IsPaused)).Returns(true);
            bool drained = false;
            bool prepared = false;
            var preparation = new Mock<ICampaignMovementFreezePreparation>();
            preparation.Setup(value => value.TryPrepareForFreeze(It.IsAny<AuthorityLease>())).Callback<AuthorityLease>(value =>
            {
                Assert.True(drained);
                Assert.True(GameThread.Instance.IsGameThread);
                Assert.Equal(identity.CurrentLease, value);
                Assert.False(value.IsPaused);
                prepared = true;
            }).Returns(true);
            var drain = new Mock<ICampaignAsyncTaskDrain>();
            drain.Setup(value => value.TryDrain()).Callback(() => drained = true).Returns(true);
            var coalescer = new Mock<ISendCoalescer>();
            coalescer.Setup(value => value.Flush(network.Object)).Callback(() =>
            {
                Assert.True(GameThread.Instance.IsGameThread);
                Assert.True(prepared);
                Assert.False(identity.CurrentLease.IsPaused);
                Assert.Equal(1, identity.Epoch);
                bufferedResultsFlushed = true;
            });
            network.Setup(value => value.SendAll(It.IsAny<IMessage>())).Callback<IMessage>(message =>
            {
                if (message is NetworkAuthorityLeaseSnapshot snapshot && snapshot.IsPaused)
                    Assert.True(bufferedResultsFlushed);
            });
            var objectManager = new Mock<IObjectManager>();
            objectManager.Setup(value => value.TryApplyAllocationCheckpoint(It.IsAny<RegistryAllocationCheckpoint>())).Returns(true);
            rosterService.Setup(value => value.TryApply(rosters)).Returns(true);
            sessions.Setup(value => value.TryApply(sessionCheckpoint)).Returns(true);
            clock.Setup(value => value.TryApplyAuthorityHandoffBaseline(999)).Returns(true);
            NetworkCampaignAuthorityFreezeRequest freezeRequest = default;
            network.Setup(value => value.Send(guestPeer, It.IsAny<NetworkCampaignAuthorityFreezeRequest>()))
                .Callback<LiteNetLib.NetPeer, IMessage>((_, message) => freezeRequest = (NetworkCampaignAuthorityFreezeRequest)message);
            objectManager.Setup(value => value.CaptureAllocationCheckpoint())
                .Callback(() => Assert.True(GameThread.Instance.IsGameThread))
                .Returns(new RegistryAllocationCheckpoint(555, new System.Collections.Generic.Dictionary<string, int>()));
            network.Setup(value => value.Send(guestPeer, It.IsAny<NetworkCampaignAuthorityHandoffBarrier>()))
                .Callback<LiteNetLib.NetPeer, Common.Messaging.IMessage>((_, message) =>
                    appliedBarrier = (NetworkCampaignAuthorityHandoffBarrier)message);

            using var handler = new CampaignSimulationAvailabilityServerHandler(
                messageBroker,
                coordinator,
                connections.Object,
                playerManager.Object,
                controllerId,
                network.Object,
                identity,
                objectManager.Object,
                coalescer.Object,
                rosterService.Object,
                clock.Object,
                sessions.Object, CampaignHandoffTestServices.Scheduler(), drain.Object, movementPreparation: preparation.Object, missionFacts: CampaignHandoffTestServices.MissionFacts(identity));

            messageBroker.Publish(this, new CampaignMapSimulationReadinessChanged(true));
            messageBroker.Publish(this, new CampaignReady());
            messageBroker.Publish(this, new CampaignInitializationCompleted());
            messageBroker.Publish(guestPeer, new NetworkCampaignSimulationAvailabilityReport(
                CampaignSimulationAvailability.MapReady,
                1));
            Assert.Equal(1, identity.Epoch);

            messageBroker.Publish(this, new CampaignMapSimulationReadinessChanged(false));
            GameThread.Run(() => { }, blocking: true);

            Assert.True(identity.CurrentLease.IsPaused);
            Assert.Equal(2, identity.Epoch);
            Assert.Equal("guest", coordinator.Select().ControllerId);
            network.Verify(value => value.SendAll(It.Is<NetworkAuthorityLeaseSnapshot>(
                snapshot => snapshot.IsPaused && snapshot.Epoch == 2)), Times.Once);
            network.Verify(value => value.Send(guestPeer, It.Is<NetworkCampaignAuthorityHandoffBarrier>(
                barrier => barrier.RoomId == identity.RoomId &&
                    barrier.PausedAuthorityId == identity.AuthorityId &&
                    barrier.PausedEpoch == 2 &&
                    barrier.CandidateControllerId == "guest" &&
                    barrier.BarrierSequence > 0)), Times.Once);
            Assert.True(appliedBarrier.BarrierSequence > 0);
            Assert.Equal(555u, appliedBarrier.AllocationCheckpoint.NextHandle);
            Assert.Same(rosters, appliedBarrier.RosterCheckpoint);
            Assert.Equal(999, appliedBarrier.ClockCheckpoint.Ticks);
            Assert.Same(sessionCheckpoint, appliedBarrier.SessionCheckpoint);

            var applied = new NetworkCampaignAuthorityHandoffBarrierApplied(
                appliedBarrier.RoomId, appliedBarrier.PausedAuthorityId, appliedBarrier.PausedEpoch,
                appliedBarrier.CandidateControllerId, appliedBarrier.BarrierSequence, missionFactsVersion: CampaignHandoffTestServices.MissionFactsVersion(appliedBarrier.RoomId));
            messageBroker.Publish(guestPeer, applied);
            GameThread.Run(() => { }, blocking: true);
            Assert.False(identity.CurrentLease.IsPaused);
            Assert.Equal("guest", identity.AuthorityControllerId);
            Assert.Equal(3, identity.Epoch);
            messageBroker.Publish(guestPeer, applied);
            GameThread.Run(() => { }, blocking: true);
            Assert.Equal(3, identity.Epoch);

            if (hostReturnsImmediately)
                messageBroker.Publish(this, new CampaignMapSimulationReadinessChanged(true));
            else
                messageBroker.Publish(guestPeer, new NetworkCampaignSimulationAvailabilityReport(
                    CampaignSimulationAvailability.Unavailable, 2));

            GameThread.Run(() => { }, blocking: true);
            Assert.Equal("guest", identity.AuthorityControllerId);
            Assert.Equal(3, identity.Epoch);
            Assert.True(freezeRequest.HandoffSequence > 0);
            Assert.True(observerHeld);
            Assert.Equal("host", freezeRequest.CandidateControllerId);
            messageBroker.Publish(guestPeer, freezeRequest.ToAcknowledged());
            GameThread.Run(() => { }, blocking: true);
            Assert.Equal(3, identity.Epoch);
            Assert.Equal("guest", identity.AuthorityControllerId);
            Assert.True(observerHeld);
            bool finalDeltaApplied = false;
            GameThread.EnqueueSafe(() => finalDeltaApplied = true);
            sessions.Setup(value => value.TryApply(sessionCheckpoint))
                .Callback(() => Assert.True(finalDeltaApplied)).Returns(true);
            var finalAck = freezeRequest.ToAcknowledged(appliedBarrier.AllocationCheckpoint,
                rosters, new CampaignClockCheckpoint(999), sessionCheckpoint, CampaignHandoffTestServices.Checkpoint());
            if (nestedAck)
            {
                // True receive path: envelope dispatch queues a second-frame descendant before the ACK.
                var serializer = new Mock<ICommonSerializer>();
                serializer.Setup(value => value.Deserialize<IMessage>(It.IsAny<byte[]>())).Returns(new NestedCriticalApply());
                using var relay = new CampaignAuthoritySyncRelayServerHandler(messageBroker, connections.Object,
                    playerManager.Object, network.Object, identity, serializer.Object);
                void Apply(MessagePayload<NestedCriticalApply> _)
                {
                    GameThread.EnqueueSafe(() => GameThread.EnqueueSafe(() => finalDeltaApplied = true));
                }
                messageBroker.Subscribe<NestedCriticalApply>(Apply);
                finalDeltaApplied = false;
                GameThread.Run(() =>
                {
                    var source = identity.CurrentLease;
                    if (!disposeQueuedAck)
                    {
                        messageBroker.Publish(guestPeer, new NetworkCampaignAuthoritySyncEnvelope(source.RoomId, source.AuthorityId,
                            source.AuthorityControllerId, source.Epoch, 1, new byte[] { 1 }));
                        messageBroker.Publish(guestPeer, finalAck);
                    }
                    else
                    {
                        finalDeltaApplied = true;
                        using var reportEntered = new System.Threading.ManualResetEventSlim();
                        using var releaseReport = new System.Threading.ManualResetEventSlim();
                        int reportThread = 0;
                        connections.Setup(value => value.HasCompletedCampaignSynchronization(guestPeer)).Callback(() =>
                        {
                            if (System.Threading.Thread.CurrentThread.ManagedThreadId != reportThread) return;
                            reportEntered.Set(); Assert.True(releaseReport.Wait(System.TimeSpan.FromSeconds(5)));
                        }).Returns(true);
                        // A real already-entered receive callback may finish after Dispose clears peer mappings.
                        var inFlightReport = System.Threading.Tasks.Task.Factory.StartNew(() =>
                        {
                            reportThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
                            messageBroker.Publish(guestPeer, new NetworkCampaignSimulationAvailabilityReport(
                                CampaignSimulationAvailability.Unavailable, 3));
                        }, System.Threading.CancellationToken.None, System.Threading.Tasks.TaskCreationOptions.LongRunning,
                            System.Threading.Tasks.TaskScheduler.Default);
                        try
                        {
                            Assert.True(reportEntered.Wait(System.TimeSpan.FromSeconds(5)));
                            messageBroker.Publish(guestPeer, finalAck); // Queued while service is still active.
                            handler.Dispose();
                        }
                        finally
                        {
                            releaseReport.Set(); Assert.True(inFlightReport.Wait(System.TimeSpan.FromSeconds(5)));
                        }
                    }
                }, blocking: true);
                GameThread.Run(() => { }, blocking: true); GameThread.Run(() => { }, blocking: true);
                GameThread.Run(() => { }, blocking: true);
                Assert.True(finalDeltaApplied);
                if (disposeQueuedAck)
                {
                    Assert.Equal(3, identity.Epoch); Assert.Equal("guest", identity.AuthorityControllerId);
                    sessions.Verify(value => value.TryApply(sessionCheckpoint), Times.Never());
                    network.Verify(value => value.SendAll(It.Is<NetworkAuthorityLeaseSnapshot>(value =>
                        !value.IsPaused && value.AuthorityControllerId == "host" && value.Epoch == 5)), Times.Never());
                    return;
                }
                // No second Freeze ACK is sent. Existing reconciliation resumes the retained one.
                if (useNativeCheckpoint)
                {
                    Assert.Equal(CampaignApplyStatus.Ready,
                        CampaignApplyCompletion.For(identity).GetStatusThrough(identity.CurrentLease, out _));
                    Assert.NotNull(typeof(CampaignSimulationAvailabilityServerHandler).GetField("retainedFreezeAck",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(handler));
                }
                if (corruptRetainedAck) finalAck.AllocationCheckpoint.TypeCounters["mutated-after-receive"] = 1;
                if (corruptNativeCheckpoint)
                    finalAck.SessionCheckpoint.NativeBehaviors.Caravans.Cache.LandPrices[0] = new CampaignCategoryPriceState(20, 37, 1000);
                handler.OnAvailabilityTimer(); GameThread.Run(() => { }, blocking: true);
                if (corruptRetainedAck || corruptNativeCheckpoint)
                {
                    Assert.Equal(3, identity.Epoch); Assert.Equal("guest", identity.AuthorityControllerId);
                    Assert.Equal(CampaignApplyStatus.Faulted, CampaignApplyCompletion.For(identity).GetStatusThrough(identity.CurrentLease, out _));
                    if (corruptNativeCheckpoint)
                    {
                        CampaignApplyCompletion.For(identity).GetStatusThrough(identity.CurrentLease, out var reason);
                        Assert.Contains("Conflicting authenticated final freeze checkpoint", reason);
                    }
                    sessions.Verify(value => value.TryApply(sessionCheckpoint), Times.Never());
                    network.Verify(value => value.SendAll(It.Is<NetworkAuthorityLeaseSnapshot>(value =>
                        !value.IsPaused && value.AuthorityControllerId == "host" && value.Epoch == 5)), Times.Never());
                    return;
                }
                sessions.Verify(value => value.TryApply(sessionCheckpoint), Times.Once());
                System.GC.KeepAlive((System.Action<MessagePayload<NestedCriticalApply>>)Apply);
            }
            else
            {
                messageBroker.Publish(guestPeer, finalAck);
                GameThread.Run(() => { }, blocking: true);
            }

            if (!hostReturnsImmediately)
            {
                Assert.True(identity.CurrentLease.IsPaused);
                Assert.Equal(4, identity.Epoch);
                messageBroker.Publish(this, new CampaignMapSimulationReadinessChanged(true));
                GameThread.Run(() => { }, blocking: true);
            }

            Assert.False(identity.CurrentLease.IsPaused);
            Assert.Equal("host", identity.AuthorityControllerId);
            Assert.Equal(5, identity.Epoch);
            Assert.False(observerHeld);
            network.Verify(value => value.SendAll(It.Is<NetworkAuthorityLeaseSnapshot>(
                snapshot => !snapshot.IsPaused && snapshot.AuthorityControllerId == "host" && snapshot.Epoch == 5)), Times.Once);
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
        }
    }

    [Fact]
    public void ListenAuthorityPrefersHostThenGuestAndPausesAfterGuestDisconnects()
    {
        var previousMode = ModInformation.SessionMode;
        ModInformation.SessionMode = SessionMode.ListenAuthority;
        try
        {
            var messageBroker = new TestMessageBroker();
            var identity = new SessionIdentity();
            Assert.True(identity.TrySetRoomHostControllerId("host"));
            var coordinator = new CampaignAuthorityAvailabilityCoordinator(
                identity,
                new CampaignAuthorityCandidateSelector());
            var peer = new TestNetwork().CreatePeer();
            var localPlayer = new Player("host", "hero-host", "party-host", "clan-host", "character-host");
            var remotePlayer = new Player("guest", "hero-guest", "party-guest", "clan-guest", "character-guest");
            var playerManager = new Mock<IPlayerManager>();
            playerManager.Setup(manager => manager.TryGetPlayer("host", out localPlayer)).Returns(true);
            playerManager.Setup(manager => manager.TryGetPlayer(peer, out remotePlayer)).Returns(true);
            var connections = new Mock<IConnectionCollection>();
            connections.Setup(value => value.HasCompletedCampaignSynchronization(peer)).Returns(true);
            var controllerId = new ControllerIdProvider();
            controllerId.SetControllerId("host");

            using var handler = new CampaignSimulationAvailabilityServerHandler(
                messageBroker,
                coordinator,
                connections.Object,
                playerManager.Object,
                controllerId);

            messageBroker.Publish(this, new CampaignReady());
            messageBroker.Publish(this, new CampaignInitializationCompleted());
            messageBroker.Publish(this, new CampaignMapSimulationReadinessChanged(true));
            messageBroker.Publish(peer, new NetworkCampaignSimulationAvailabilityReport(
                CampaignSimulationAvailability.MapReady,
                1));

            Assert.Equal("host", coordinator.Select().ControllerId);

            messageBroker.Publish(this, new CampaignMapSimulationReadinessChanged(false));
            Assert.Equal("guest", coordinator.Select().ControllerId);

            messageBroker.Publish(this, new PlayerDisconnected(peer, default));
            Assert.True(coordinator.Select().IsPaused);
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
        }
    }

    [Fact]
    public void IgnoresAvailabilityReportsUntilCampaignSynchronizationCompletes()
    {
        var messageBroker = new TestMessageBroker();
        var coordinator = new CampaignAuthorityAvailabilityCoordinator(
            new SessionIdentity(),
            new CampaignAuthorityCandidateSelector());
        var peer = new TestNetwork().CreatePeer();
        var remotePlayer = new Player("guest", "hero-guest", "party-guest", "clan-guest", "character-guest");
        var playerManager = new Mock<IPlayerManager>();
        playerManager.Setup(manager => manager.TryGetPlayer(peer, out remotePlayer)).Returns(true);
        var connections = new Mock<IConnectionCollection>();
        connections.Setup(value => value.HasCompletedCampaignSynchronization(peer)).Returns(false);
        var controllerId = new ControllerIdProvider();
        using var handler = new CampaignSimulationAvailabilityServerHandler(
            messageBroker,
            coordinator,
            connections.Object,
            playerManager.Object,
            controllerId);

        messageBroker.Publish(peer, new NetworkCampaignSimulationAvailabilityReport(
            CampaignSimulationAvailability.MapReady,
            1));

        Assert.True(coordinator.Select().IsPaused);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReconnectedPeerCanRestartItsAvailabilityRevisionAfterLateDisconnect(bool reportArrivesBeforeOldDisconnect)
    {
        var messageBroker = new TestMessageBroker();
        var identity = new SessionIdentity();
        Assert.True(identity.TrySetRoomHostControllerId("host"));
        var coordinator = new CampaignAuthorityAvailabilityCoordinator(
            identity,
            new CampaignAuthorityCandidateSelector());
        var peers = new TestNetwork();
        var oldPeer = peers.CreatePeer();
        var newPeer = peers.CreatePeer();
        var oldPlayer = new Player("guest", "hero-guest", "party-guest", "clan-guest", "character-guest");
        var newPlayer = new Player("guest", "hero-guest", "party-guest", "clan-guest", "character-guest");
        var playerManager = new Mock<IPlayerManager>();
        playerManager.Setup(manager => manager.TryGetPlayer(oldPeer, out oldPlayer)).Returns(true);
        playerManager.Setup(manager => manager.TryGetPlayer(newPeer, out newPlayer)).Returns(true);
        LiteNetLib.NetPeer currentPeer = oldPeer;
        playerManager.Setup(manager => manager.TryGetPeer("guest", out It.Ref<LiteNetLib.NetPeer>.IsAny))
            .Returns((string _, out LiteNetLib.NetPeer peer) =>
            {
                peer = currentPeer;
                return true;
            });
        var connections = new Mock<IConnectionCollection>();
        connections.Setup(value => value.HasCompletedCampaignSynchronization(oldPeer)).Returns(true);
        connections.Setup(value => value.HasCompletedCampaignSynchronization(newPeer)).Returns(true);
        using var handler = new CampaignSimulationAvailabilityServerHandler(
            messageBroker,
            coordinator,
            connections.Object,
            playerManager.Object,
            new ControllerIdProvider());

        messageBroker.Publish(oldPeer, new NetworkCampaignSimulationAvailabilityReport(
            CampaignSimulationAvailability.MapReady,
            100));

        currentPeer = newPeer;
        if (reportArrivesBeforeOldDisconnect)
        {
            messageBroker.Publish(newPeer, new NetworkCampaignSimulationAvailabilityReport(
                CampaignSimulationAvailability.MapReady,
                1));
            Assert.Equal(1, Assert.Single(coordinator.Snapshot()).Revision);
            messageBroker.Publish(oldPeer, new PlayerDisconnected(oldPeer, default));
        }
        else
        {
            messageBroker.Publish(oldPeer, new PlayerDisconnected(oldPeer, default));
            messageBroker.Publish(newPeer, new NetworkCampaignSimulationAvailabilityReport(
                CampaignSimulationAvailability.MapReady,
                1));
        }

        var entry = Assert.Single(coordinator.Snapshot());
        Assert.Equal("guest", entry.ControllerId);
        Assert.Equal(1, entry.Revision);
        Assert.True(coordinator.IsFreshMapReady("guest"));
    }
}
