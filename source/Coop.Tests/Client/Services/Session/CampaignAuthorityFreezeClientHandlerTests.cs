using GameInterface.Services.CampaignAuthority.Movement;
using Coop.Tests.Mocks;
using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Network.Coalescing;
using Common.Network.Session;
using Common.PacketHandlers;
using Common.Serialization;
using Common.Session;
using Common.Tests.Utils;
using Coop.Core.Client.Services.Session;
using GameInterface.Services.Entity;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Time.Interfaces;
using Moq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Xunit;
using Xunit.Abstractions;

namespace Coop.Tests.Client.Services.Session;

public class CampaignAuthorityFreezeClientHandlerTests
{
    [Theory]
    [InlineData("none")]
    [InlineData("send")]
    [InlineData("final-flush")]
    public void AckDiagnosticsDistinguishCallsFromDeliveryAndRetainOriginalFailureHandling(string failureStage)
    {
        const long sequence = 987661;
        var logs = new ConcurrentQueue<string>();
        Action<string> capture = logs.Enqueue;
        OutputSinkManager.AddLogCallback(capture);
        try
        {
            using var broker = new TestMessageBroker();
            var identity = new SessionIdentity();
            var lease = new AuthorityLease("diagnostic-room", "writer-lease", "writer", 5);
            Assert.True(identity.TryApply(lease, "host"));
            var controller = new ControllerIdProvider();controller.SetControllerId("writer");
            var failure = new InvalidOperationException("original-ack-transport-failure-987661");
            int sends = 0, flushes = 0;
            var network = new Mock<INetwork>();
            network.Setup(value => value.SendAll(It.IsAny<IMessage>())).Callback(() =>
            {
                Assert.True(identity.IsLocallyFrozen(lease));sends++;
                if (failureStage == "send") throw failure;
            });
            network.Setup(value => value.FlushPendingMessages()).Callback(() =>
            {
                flushes++;
                if (failureStage == "final-flush" && flushes == 2) throw failure;
            });
            var rosters = new Mock<ICampaignRosterCheckpointService>();
            var roster = new CampaignRosterCheckpoint(Array.Empty<CampaignRosterState>());
            rosters.Setup(value => value.TryCapture(out roster)).Returns(true);
            var clock = new Mock<IMapTimeTrackerInterface>();long ticks = 888;
            clock.Setup(value => value.TryGetCurrentTicks(out ticks)).Returns(true);
            var sessions = new Mock<ICampaignSessionCheckpointService>();
            var session = new CampaignSessionCheckpoint("campaign", "{}");
            sessions.Setup(value => value.TryCapture(out session)).Returns(true);
            var objects = Mock.Of<IObjectManager>(value => value.CaptureAllocationCheckpoint() ==
                new RegistryAllocationCheckpoint(444, new Dictionary<string,int>()));
            using var handler = new CampaignAuthorityFreezeClientHandler(broker, network.Object, identity, controller,
                objects, rosters.Object, clock.Object, sessions.Object, CampaignHandoffTestServices.Scheduler(),
                CampaignHandoffTestServices.Drained());
            broker.Publish(this, new NetworkCampaignAuthorityFreezeRequest(lease, "host", sequence));
            GameThread.Run(() => { }, blocking: true);
            Assert.Equal(1, sends);Assert.Equal(failureStage == "send" ? 1 : 2, flushes);
            Assert.True(identity.IsLocallyFrozen(lease));Assert.Equal(lease, identity.CurrentLease);
            var stages = logs.ToArray();
            Assert.Contains(stages, text => text.StartsWith("Freeze ACK send stage") && text.Contains("send-call") && text.Contains("987661") && text.Contains("Unavailable(non-CoopClient transport)"));
            if (failureStage == "none")
            {
                Assert.Contains(stages, text => text.StartsWith("Freeze ACK send stage") && text.Contains("flush-call-returned-delivery-unconfirmed") && text.Contains("987661"));
                Assert.DoesNotContain(stages, text => text.Contains("Failed to acknowledge campaign authority freeze 987661"));
            }
            else
            {
                Assert.Contains(stages, text => text.Contains("Failed to acknowledge campaign authority freeze 987661") && text.Contains(failure.Message));
                Assert.DoesNotContain(stages, text => text.Contains("flush-call-returned-delivery-unconfirmed") && text.Contains("987661"));
            }
        }
        finally { OutputSinkManager.RemoveLogCallback(capture); }
    }

    [Fact]
    public void EighteenIdenticalRequestsCaptureAndEnqueueOneFullAck()
    {
        using var fixture = new IdempotencyFixture();
        for (int i = 0; i < 18; i++) fixture.Request();
        fixture.Drain();
        Assert.Single(fixture.Acks);Assert.Equal(1, fixture.Captures);Assert.Equal(2, fixture.Flushes);
        Assert.True(fixture.Identity.IsLocallyFrozen(fixture.Lease));
        Assert.Equal(fixture.Lease.Epoch, fixture.Acks[0].Epoch);
        Assert.Equal(44, fixture.Acks[0].HandoffSequence);Assert.Equal("host", fixture.Acks[0].CandidateControllerId);
        Assert.NotNull(fixture.Acks[0].SchedulerCheckpoint);Assert.NotNull(fixture.Acks[0].MovementCheckpoint);
    }

    [Theory]
    [InlineData("send-before")]
    [InlineData("send-partial")]
    [InlineData("final-flush")]
    public void UncertainTransportNeverRequeuesEvenAcrossHandlerReconnection(string failure)
    {
        using var fixture = new IdempotencyFixture();fixture.Failure = failure;fixture.Request();fixture.Drain();
        Assert.Equal(1, fixture.SendCalls);Assert.True(fixture.Identity.IsLocallyFrozen(fixture.Lease));
        Assert.Equal(failure == "send-before" ? 0 : 1, fixture.Acks.Count);
        int captures = fixture.Captures,flushes = fixture.Flushes;
        fixture.Failure = null;
        for (int i = 0; i < 18; i++) fixture.Request();fixture.Drain();
        fixture.Reconnect();fixture.Request();fixture.Drain();
        Assert.Equal(1, fixture.SendCalls);Assert.Equal(captures, fixture.Captures);Assert.Equal(flushes, fixture.Flushes);
        Assert.True(fixture.Identity.IsLocallyFrozen(fixture.Lease));
        fixture.Cancel();fixture.Drain();Assert.False(fixture.Identity.IsLocallyFrozen(fixture.Lease));
        fixture.Request(45);fixture.Drain();Assert.Equal(2, fixture.SendCalls);
    }

    [Theory]
    [InlineData("roster")]
    [InlineData("clock")]
    [InlineData("session")]
    [InlineData("scheduler")]
    [InlineData("movement")]
    [InlineData("allocation")]
    [InlineData("pre-flush")]
    public void CaptureOrPreSendFlushFailureCanRetryOnlyBeforeAnyAckAttempt(string failure)
    {
        using var fixture = new IdempotencyFixture();fixture.Failure = failure;fixture.Request();fixture.Drain();
        Assert.True(fixture.Identity.IsLocallyFrozen(fixture.Lease));Assert.Equal(0, fixture.SendCalls);
        fixture.Failure = null;fixture.Request();fixture.Drain();
        Assert.Equal(1, fixture.SendCalls);Assert.Single(fixture.Acks);
        fixture.Request();fixture.Drain();Assert.Equal(1, fixture.SendCalls);
    }

    [Fact]
    public void CanceledSequenceCannotRefreezeAndCandidateCannotStealTheOriginalFence()
    {
        using var fixture = new IdempotencyFixture();fixture.Request();fixture.Drain();
        fixture.Request(44, "different-candidate");fixture.Request(45, "different-candidate");fixture.Drain();
        fixture.Cancel(44, "different-candidate");fixture.Drain();
        Assert.True(fixture.Identity.IsLocallyFrozen(fixture.Lease));Assert.Equal(1, fixture.SendCalls);
        fixture.Cancel();fixture.Drain();Assert.False(fixture.Identity.IsLocallyFrozen(fixture.Lease));
        fixture.Request();fixture.Request(43);fixture.Drain();
        Assert.False(fixture.Identity.IsLocallyFrozen(fixture.Lease));Assert.Equal(1, fixture.SendCalls);
        fixture.Request(45, "different-candidate");fixture.Drain();Assert.Equal(2, fixture.SendCalls);
        Assert.Equal("different-candidate", fixture.Acks[1].CandidateControllerId);
        fixture.Cancel();fixture.Drain();Assert.True(fixture.Identity.IsLocallyFrozen(fixture.Lease));
    }

    [Fact]
    public void RebuiltHandlerKeepsConfirmedAttemptButNewExactLeaseCanCaptureItsOwnBaseline()
    {
        using var fixture = new IdempotencyFixture();fixture.Request();fixture.Drain();fixture.Reconnect();
        fixture.Request();fixture.Drain();Assert.Equal(1, fixture.SendCalls);Assert.Equal(1, fixture.Captures);
        var old = fixture.Lease;
        fixture.Lease = new AuthorityLease(old.RoomId, "new-writer-lease", old.AuthorityControllerId, old.Epoch + 1);
        Assert.True(fixture.Identity.TryApply(fixture.Lease, "host"));fixture.Reconnect();
        fixture.Broker.Publish(this, new NetworkCampaignAuthorityFreezeRequest(old, "host", 45));fixture.Drain();
        Assert.Equal(1, fixture.SendCalls);Assert.False(fixture.Identity.IsLocallyFrozen(fixture.Lease));
        fixture.Request();fixture.Drain();Assert.Equal(2, fixture.SendCalls);Assert.Equal(fixture.Lease.Epoch, fixture.Acks[1].Epoch);
        Assert.Equal(fixture.Lease, fixture.Acks[1].MovementCheckpoint.SourceLease);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LeaseOrFenceChangedDuringCaptureCannotSendAnObsoleteAck(bool changedLease)
    {
        using var fixture = new IdempotencyFixture();
        fixture.DuringCapture = () =>
        {
            if (changedLease) Assert.True(fixture.Identity.TryApply(new AuthorityLease(fixture.Lease.RoomId, "other-authority", "host", 6)));
            else Assert.True(fixture.Identity.TryCancelLocalAuthorityFreeze(fixture.Lease, 44));
        };
        fixture.Request();fixture.Drain();Assert.Equal(0, fixture.SendCalls);Assert.Empty(fixture.Acks);
    }

    private sealed class IdempotencyFixture : IDisposable
    {
        internal readonly TestMessageBroker Broker = new();
        internal readonly SessionIdentity Identity = new();
        internal readonly ControllerIdProvider Controller = new();
        internal AuthorityLease Lease = new("idempotency-room", "writer-lease", "writer", 5);
        internal Mock<INetwork> Network = new();
        internal readonly List<NetworkCampaignAuthorityFreezeAcknowledged> Acks = new();
        internal int Captures, Flushes, SendCalls;
        internal string? Failure;
        internal Action? DuringCapture;
        internal CampaignAuthorityFreezeClientHandler Handler = null!;
        internal IdempotencyFixture()
        {
            Assert.True(Identity.TryApply(Lease, "host"));Controller.SetControllerId("writer");Reconnect();
        }
        internal void Reconnect()
        {
            Handler?.Dispose();Network = new Mock<INetwork>();
            var rosters = new Mock<ICampaignRosterCheckpointService>();
            var roster = new CampaignRosterCheckpoint(Array.Empty<CampaignRosterState>());
            rosters.Setup(value => value.TryCapture(out roster)).Callback(() => { Captures++;DuringCapture?.Invoke(); }).Returns(() => Failure != "roster");
            long ticks = 888;var clock = new Mock<IMapTimeTrackerInterface>();clock.Setup(value => value.TryGetCurrentTicks(out ticks)).Returns(() => Failure != "clock");
            var session = new CampaignSessionCheckpoint("campaign", "{}");var sessions = new Mock<ICampaignSessionCheckpointService>();
            sessions.Setup(value => value.TryCapture(out session)).Returns(() => Failure != "session");
            var checkpoint = new CampaignMovementCheckpoint(Lease, ticks, 1, new byte[] { 1 }, "movement-hash");
            var movement = new Mock<ICampaignMovementCheckpointService>();movement.Setup(value => value.TryCapture(Lease, ticks, out checkpoint)).Returns(() => Failure != "movement");
            var schedulerCheckpoint = CampaignHandoffTestServices.Checkpoint();var scheduler = new Mock<ICampaignSchedulerCheckpointService>();
            scheduler.Setup(value => value.TryCapture(out schedulerCheckpoint)).Returns(() => Failure != "scheduler");
            var objects = new Mock<IObjectManager>();objects.Setup(value => value.CaptureAllocationCheckpoint()).Returns(() =>
                Failure == "allocation" ? throw new InvalidOperationException("allocation capture failed before send") : new RegistryAllocationCheckpoint(10, new Dictionary<string,int>()));
            Network.Setup(value => value.SendAll(It.IsAny<IMessage>())).Callback<IMessage>(message =>
            {
                SendCalls++;
                if (Failure == "send-before") throw new InvalidOperationException("SendAll outcome unknown before enqueue evidence");
                Acks.Add(Assert.IsType<NetworkCampaignAuthorityFreezeAcknowledged>(message));
                if (Failure == "send-partial") throw new InvalidOperationException("SendAll partially enqueued before failure");
            });
            Network.Setup(value => value.FlushPendingMessages()).Callback(() =>
            {
                Flushes++;
                if (Failure == "pre-flush" && SendCalls == 0) throw new InvalidOperationException("pre-send flush failed");
                if (Failure == "final-flush" && SendCalls > 0) throw new InvalidOperationException("final flush outcome unknown");
            });
            Handler = new CampaignAuthorityFreezeClientHandler(Broker, Network.Object, Identity, Controller, objects.Object,
                rosters.Object, clock.Object, sessions.Object, scheduler.Object, CampaignHandoffTestServices.Drained(), movement: movement.Object);
        }
        internal void Request(long sequence = 44, string candidate = "host") => Broker.Publish(this, new NetworkCampaignAuthorityFreezeRequest(Lease, candidate, sequence));
        internal void Cancel(long sequence = 44, string candidate = "host") => Broker.Publish(this, new NetworkCampaignAuthorityFreezeRequest(Lease, candidate, sequence).ToCanceled());
        internal void Drain() => GameThread.Run(() => { }, blocking: true);
        public void Dispose() { Handler.Dispose();Broker.Dispose(); }
    }

    private readonly ITestOutputHelper output;

    public CampaignAuthorityFreezeClientHandlerTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void FailedNavigationPreparationPreventsFreezeFlushAndAck()
    {
        var broker = new TestMessageBroker();
        var identity = new SessionIdentity();
        var lease = new AuthorityLease("room", "authority", "writer", 2);
        Assert.True(identity.TryApply(lease, "host"));
        var controller = new ControllerIdProvider();
        controller.SetControllerId("writer");
        var network = new Mock<INetwork>();
        var coalescer = new Mock<ISendCoalescer>();
        var preparation = new Mock<ICampaignMovementFreezePreparation>();
        preparation.Setup(value => value.TryPrepareForFreeze(lease)).Returns(false);
        using var handler = new CampaignAuthorityFreezeClientHandler(broker, network.Object, identity, controller,
            Mock.Of<IObjectManager>(), Mock.Of<ICampaignRosterCheckpointService>(), Mock.Of<IMapTimeTrackerInterface>(),
            Mock.Of<ICampaignSessionCheckpointService>(), CampaignHandoffTestServices.Scheduler(),
            CampaignHandoffTestServices.Drained(), coalescer.Object, movementPreparation: preparation.Object);
        broker.Publish(this, new NetworkCampaignAuthorityFreezeRequest(lease, "host", 33));
        GameThread.Run(() => { }, blocking: true);
        Assert.False(identity.IsLocallyFrozen(lease));
        Assert.Equal(lease, identity.CurrentLease);
        preparation.Verify(value => value.TryPrepareForFreeze(lease), Times.Once);
        coalescer.Verify(value => value.Flush(network.Object), Times.Never);
        network.Verify(value => value.SendAll(It.IsAny<IMessage>()), Times.Never);
    }

    [Fact]
    public void CurrentAuthorityFreezesBeforeSendingOrderedAcknowledgementAndCanBeCanceled()
    {
        var evidence = new ConcurrentQueue<string>();
        var elapsed = Stopwatch.StartNew();
        void Observe(string stage)
        {
            ThreadPool.GetAvailableThreads(out int workers, out int io);
            evidence.Enqueue($"{DateTime.UtcNow:O} elapsedMs={elapsed.Elapsed.TotalMilliseconds:F3} stage={stage} thread={Thread.CurrentThread.ManagedThreadId} pool={Thread.CurrentThread.IsThreadPoolThread} gameThread={GameThread.Instance.IsGameThread} availableWorkers={workers} availableIO={io}");
        }
        Action<string> capture = message =>
        {
            if (message.Contains("Failed to acknowledge campaign authority freeze")) evidence.Enqueue("HANDLER_ERROR " + message);
        };
        OutputSinkManager.AddLogCallback(capture);
        try
        {
            var broker = new TestMessageBroker();
            var identity = new SessionIdentity();
            var lease = new AuthorityLease("room-a", "authority-b", "controller-b", 3);
            Assert.True(identity.TryApply(lease, "controller-a"));
            var controller = new ControllerIdProvider();
            controller.SetControllerId("controller-b");
            var network = new Mock<INetwork>();
            var operations = new List<string>();
            var asyncTasks = new Mock<ICampaignAsyncTaskDrain>();
            asyncTasks.Setup(value => value.TryDrain()).Callback(() =>
            {
                Observe("drain-entered");
                Assert.True(GameThread.Instance.IsGameThread);
                Assert.False(identity.IsLocallyFrozen(lease));
                // A finishing engine worker must run independently of the test runner thread pool.
                var worker = System.Threading.Tasks.Task.Factory.StartNew(() =>
                {
                    Observe("worker-started-before-lease");
                    Assert.False(Thread.CurrentThread.IsThreadPoolThread);
                    Assert.False(GameThread.Instance.IsGameThread);
                    bool result = identity.TryExecuteWithLease(lease, () => { Observe("worker-holds-lease"); operations.Add("worker"); });
                    Observe("worker-completed-lease-result=" + result);
                    return result;
                }, CancellationToken.None, System.Threading.Tasks.TaskCreationOptions.LongRunning, System.Threading.Tasks.TaskScheduler.Default);
                Observe("worker-dispatched");
                bool completed = worker.Wait(System.TimeSpan.FromSeconds(2));
                Observe("worker-wait-ended-completed=" + completed + "-status=" + worker.Status);
                Assert.True(completed, "Lease lock blocked the finishing AI worker");
                Assert.True(worker.Result);
            }).Returns(true);
            var preparation = new Mock<ICampaignMovementFreezePreparation>();
            preparation.Setup(value => value.TryPrepareForFreeze(lease)).Callback(() =>
            {
                Assert.True(GameThread.Instance.IsGameThread);
                Assert.False(identity.IsLocallyFrozen(lease));
                Assert.Contains("worker", operations);
                operations.Add("prepare");
            }).Returns(true);
            var coalescer = new Mock<ISendCoalescer>();
            coalescer.Setup(value => value.Flush(network.Object)).Callback(() =>
            {
                Observe("coalescer");
                Assert.True(GameThread.Instance.IsGameThread);
                Assert.False(identity.IsLocallyFrozen(lease));
                Assert.True(identity.TryExecuteWithLease(lease, () => operations.Add("coalescer")));
            });
            var checkpoint = new RegistryAllocationCheckpoint(444, new Dictionary<string, int>());
            var rosters = new CampaignRosterCheckpoint(new CampaignRosterState[0]);
            var rosterService = new Mock<ICampaignRosterCheckpointService>();
            rosterService.Setup(value => value.TryCapture(out rosters))
                .Callback(() => Assert.True(identity.IsLocallyFrozen(lease))).Returns(true);
            long finalTicks = 888;
            var clock = new Mock<IMapTimeTrackerInterface>();
            clock.Setup(value => value.TryGetCurrentTicks(out finalTicks))
                .Callback(() => Assert.True(identity.IsLocallyFrozen(lease))).Returns(true);
            var sessionCheckpoint = new CampaignSessionCheckpoint("campaign", "{}");
            var sessions = new Mock<ICampaignSessionCheckpointService>();
            sessions.Setup(value => value.TryCapture(out sessionCheckpoint))
                .Callback(() => Assert.True(identity.IsLocallyFrozen(lease))).Returns(true);
            var objectManager = new Mock<IObjectManager>();
            objectManager.Setup(value => value.CaptureAllocationCheckpoint())
                .Callback(() => Assert.True(identity.IsLocallyFrozen(lease)))
                .Returns(checkpoint);
            NetworkCampaignAuthorityFreezeAcknowledged? acknowledgement = null;

            network.Setup(value => value.FlushPendingMessages())
                .Callback(() => operations.Add("flush"));
            network.Setup(value => value.SendAll(It.IsAny<IMessage>()))
                .Callback<IMessage>(message =>
                {
                    Assert.True(identity.IsLocallyFrozen(lease));
                    acknowledgement = Assert.IsType<NetworkCampaignAuthorityFreezeAcknowledged>(message);
                    operations.Add("ack");
                });

            var request = new NetworkCampaignAuthorityFreezeRequest(lease, "controller-a", 44);
            using var handler = new CampaignAuthorityFreezeClientHandler(broker, network.Object, identity, controller,
                objectManager.Object, rosterService.Object, clock.Object, sessions.Object, CampaignHandoffTestServices.Scheduler(), asyncTasks.Object, coalescer.Object, movementPreparation: preparation.Object);
            broker.Publish(this, request);
            GameThread.Run(() => { }, blocking: true);

            Observe("request-drained-frozen=" + identity.IsLocallyFrozen(lease));
            Assert.True(identity.IsLocallyFrozen(lease));
            Assert.NotNull(acknowledgement);
            Assert.Equal(new[] { "worker", "prepare", "coalescer", "flush", "ack", "flush" }, operations);
            Assert.Equal(request.HandoffSequence, acknowledgement.Value.HandoffSequence);
            Assert.Equal(request.CandidateControllerId, acknowledgement.Value.CandidateControllerId);
            Assert.Same(checkpoint, acknowledgement.Value.AllocationCheckpoint);
            Assert.Same(rosters, acknowledgement.Value.RosterCheckpoint);
            Assert.Equal(888, acknowledgement.Value.ClockCheckpoint.Ticks);
            Assert.Same(sessionCheckpoint, acknowledgement.Value.SessionCheckpoint);
            Assert.NotNull(acknowledgement.Value.SchedulerCheckpoint);

            broker.Publish(this, request);
            GameThread.Run(() => { }, blocking: true);
            coalescer.Verify(value => value.Flush(network.Object), Times.Once);
            asyncTasks.Verify(value => value.TryDrain(), Times.Once);
            preparation.Verify(value => value.TryPrepareForFreeze(lease), Times.Once);
            network.Verify(value => value.SendAll(It.IsAny<NetworkCampaignAuthorityFreezeAcknowledged>()), Times.Once);

            broker.Publish(this, request.ToCanceled());
            GameThread.Run(() => { }, blocking: true);
            Assert.False(identity.IsLocallyFrozen(lease));
            Observe("canceled");
        }
        finally
        {
            OutputSinkManager.RemoveLogCallback(capture);
            foreach (string row in evidence) output.WriteLine(row);
        }
    }

    [Fact]
    public void FailedRosterCaptureKeepsOldAuthorityFrozenWithoutAcknowledgement()
    {
        var broker = new TestMessageBroker();
        var identity = new SessionIdentity();
        var lease = new AuthorityLease("room", "authority", "writer", 2);
        Assert.True(identity.TryApply(lease, "host"));
        var controller = new ControllerIdProvider();
        controller.SetControllerId("writer");
        var network = new Mock<INetwork>();
        var rosterService = new Mock<ICampaignRosterCheckpointService>();
        CampaignRosterCheckpoint checkpoint = null;
        rosterService.Setup(value => value.TryCapture(out checkpoint)).Returns(false);
        using var handler = new CampaignAuthorityFreezeClientHandler(broker, network.Object, identity, controller,
            Mock.Of<IObjectManager>(), rosterService.Object, Mock.Of<IMapTimeTrackerInterface>(), Mock.Of<ICampaignSessionCheckpointService>(), CampaignHandoffTestServices.Scheduler(), CampaignHandoffTestServices.Drained());
        var request = new NetworkCampaignAuthorityFreezeRequest(lease, "host", 33);
        broker.Publish(this, request);
        GameThread.Run(() => { }, blocking: true);

        Assert.True(identity.IsLocallyFrozen(lease));
        network.Verify(value => value.SendAll(It.IsAny<IMessage>()), Times.Never);
        broker.Publish(this, request.ToCanceled());
        GameThread.Run(() => { }, blocking: true);
        Assert.False(identity.IsLocallyFrozen(lease));
    }

    [Fact]
    public void FreezeRequestMustMatchTheExactActiveAuthorityLeaseAndLocalController()
    {
        var lease = new AuthorityLease("room-a", "authority-b", "controller-b", 3);
        var request = new NetworkCampaignAuthorityFreezeRequest(lease, "controller-a", 44);

        Assert.True(CampaignAuthorityFreezeClientHandler.Matches(request, lease, "controller-b"));
        Assert.False(CampaignAuthorityFreezeClientHandler.Matches(request, lease, "controller-a"));
        Assert.False(CampaignAuthorityFreezeClientHandler.Matches(
            new NetworkCampaignAuthorityFreezeRequest(lease, "controller-b", 44), lease, "controller-b"));
        Assert.False(CampaignAuthorityFreezeClientHandler.Matches(
            request,
            new AuthorityLease("room-a", "authority-c", "controller-b", 4),
            "controller-b"));
        Assert.False(CampaignAuthorityFreezeClientHandler.Matches(
            new NetworkCampaignAuthorityFreezeRequest(lease, "controller-a", 0),
            lease,
            "controller-b"));
    }

    [Theory]
    [InlineData(false, 888L)]
    [InlineData(true, -1L)]
    public void MissingOrInvalidFrozenClockPreventsAcknowledgement(bool clockAvailable, long ticks)
    {
        var broker = new TestMessageBroker();
        var identity = new SessionIdentity();
        var lease = new AuthorityLease("room", "authority", "writer", 2);
        Assert.True(identity.TryApply(lease, "host"));
        var controller = new ControllerIdProvider();
        controller.SetControllerId("writer");
        var network = new Mock<INetwork>();
        var rosterService = new Mock<ICampaignRosterCheckpointService>();
        var roster = new CampaignRosterCheckpoint(new CampaignRosterState[0]);
        rosterService.Setup(value => value.TryCapture(out roster)).Returns(true);
        var clock = new Mock<IMapTimeTrackerInterface>();
        clock.Setup(value => value.TryGetCurrentTicks(out ticks)).Returns(clockAvailable);
        using var handler = new CampaignAuthorityFreezeClientHandler(broker, network.Object, identity, controller,
            Mock.Of<IObjectManager>(), rosterService.Object, clock.Object, Mock.Of<ICampaignSessionCheckpointService>(), CampaignHandoffTestServices.Scheduler(), CampaignHandoffTestServices.Drained());
        broker.Publish(this, new NetworkCampaignAuthorityFreezeRequest(lease, "host", 33));
        GameThread.Run(() => { }, blocking: true);
        Assert.True(identity.IsLocallyFrozen(lease));
        network.Verify(value => value.SendAll(It.IsAny<IMessage>()), Times.Never);
    }

    [Fact]
    public void FailedSessionCaptureKeepsOldAuthorityFrozenWithoutAcknowledgement()
    {
        var broker = new TestMessageBroker();
        var identity = new SessionIdentity();
        var lease = new AuthorityLease("room", "authority", "writer", 2);
        Assert.True(identity.TryApply(lease, "host"));
        var controller = new ControllerIdProvider();
        controller.SetControllerId("writer");
        var network = new Mock<INetwork>();
        var rosters = new Mock<ICampaignRosterCheckpointService>();
        var roster = new CampaignRosterCheckpoint(new CampaignRosterState[0]);
        rosters.Setup(value => value.TryCapture(out roster)).Returns(true);
        long ticks = 888;
        var clock = new Mock<IMapTimeTrackerInterface>();
        clock.Setup(value => value.TryGetCurrentTicks(out ticks)).Returns(true);
        var sessions = new Mock<ICampaignSessionCheckpointService>();
        CampaignSessionCheckpoint checkpoint = null;
        sessions.Setup(value => value.TryCapture(out checkpoint)).Returns(false);
        using var handler = new CampaignAuthorityFreezeClientHandler(broker, network.Object, identity, controller,
            Mock.Of<IObjectManager>(), rosters.Object, clock.Object, sessions.Object, CampaignHandoffTestServices.Scheduler(), CampaignHandoffTestServices.Drained());
        var request = new NetworkCampaignAuthorityFreezeRequest(lease, "host", 33);
        broker.Publish(this, request);
        GameThread.Run(() => { }, blocking: true);
        Assert.True(identity.IsLocallyFrozen(lease));
        network.Verify(value => value.SendAll(It.IsAny<IMessage>()), Times.Never);
        broker.Publish(this, request.ToCanceled());
        GameThread.Run(() => { }, blocking: true);
        Assert.False(identity.IsLocallyFrozen(lease));
    }

    [Fact]
    public void FreezeRequestAcknowledgementAndCancellationRoundTripThroughTheMessageWireFormat()
    {
        var serializer = new ProtoBufSerializer(new SerializableTypeMapper());
        var checkpoint = new RegistryAllocationCheckpoint(555, new Dictionary<string, int> { ["type"] = 800 });
        var rosters = new CampaignRosterCheckpoint(new[] { new CampaignRosterState(88, new CampaignRosterElementState[0]) });
        var lease = new AuthorityLease("room-a", "authority-b", "controller-b", 3);
        var request = new NetworkCampaignAuthorityFreezeRequest(lease, "controller-a", 44);

        var receivedRequest = Assert.IsType<NetworkCampaignAuthorityFreezeRequest>(
            serializer.Deserialize<IMessage>(MessagePacket.Create(request, serializer).Data));
        var receivedAcknowledgement = Assert.IsType<NetworkCampaignAuthorityFreezeAcknowledged>(
            serializer.Deserialize<IMessage>(MessagePacket.Create(request.ToAcknowledged(checkpoint, rosters,
                new CampaignClockCheckpoint(888), new CampaignSessionCheckpoint("campaign", "{}"),
                CampaignHandoffTestServices.Checkpoint()), serializer).Data));
        var receivedCancellation = Assert.IsType<NetworkCampaignAuthorityFreezeCanceled>(
            serializer.Deserialize<IMessage>(MessagePacket.Create(request.ToCanceled(), serializer).Data));

        Assert.Equal(request.RoomId, receivedRequest.RoomId);
        Assert.Equal(request.AuthorityId, receivedRequest.AuthorityId);
        Assert.Equal(request.AuthorityControllerId, receivedRequest.AuthorityControllerId);
        Assert.Equal(request.Epoch, receivedRequest.Epoch);
        Assert.Equal(request.CandidateControllerId, receivedRequest.CandidateControllerId);
        Assert.Equal(request.HandoffSequence, receivedRequest.HandoffSequence);
        Assert.Equal(request.HandoffSequence, receivedAcknowledgement.HandoffSequence);
        Assert.Equal(555u, receivedAcknowledgement.AllocationCheckpoint.NextHandle);
        Assert.Equal(800, receivedAcknowledgement.AllocationCheckpoint.TypeCounters["type"]);
        Assert.Equal(88u, Assert.Single(receivedAcknowledgement.RosterCheckpoint.Rosters).RosterHandle);
        Assert.Equal(888, receivedAcknowledgement.ClockCheckpoint.Ticks);
        Assert.Equal("campaign", receivedAcknowledgement.SessionCheckpoint.CampaignId);
        Assert.Equal("{}", receivedAcknowledgement.SessionCheckpoint.SessionJson);
        Assert.Equal(50, receivedAcknowledgement.SchedulerCheckpoint.MinimumIntervalTicks);
        Assert.Equal(request.HandoffSequence, receivedCancellation.HandoffSequence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CannotAcknowledgeWithoutAsyncDrainAndSchedulerCapture(bool drainSucceeds)
    {
        var broker = new TestMessageBroker();
        var identity = new SessionIdentity();
        var lease = new AuthorityLease("room", "authority", "writer", 2);
        Assert.True(identity.TryApply(lease, "host"));
        var controller = new ControllerIdProvider();
        controller.SetControllerId("writer");
        var network = new Mock<INetwork>();
        var rosters = new Mock<ICampaignRosterCheckpointService>();
        var roster = new CampaignRosterCheckpoint(new CampaignRosterState[0]);
        rosters.Setup(value => value.TryCapture(out roster)).Returns(true);
        var clock = new Mock<IMapTimeTrackerInterface>();
        long ticks = 888;
        clock.Setup(value => value.TryGetCurrentTicks(out ticks)).Returns(true);
        var sessions = new Mock<ICampaignSessionCheckpointService>();
        var session = new CampaignSessionCheckpoint("campaign", "{}");
        sessions.Setup(value => value.TryCapture(out session)).Returns(true);
        var scheduler = new Mock<ICampaignSchedulerCheckpointService>();
        var drain = new Mock<ICampaignAsyncTaskDrain>();
        drain.Setup(value => value.TryDrain()).Returns(drainSucceeds);
        var coalescer = new Mock<ISendCoalescer>();
        using var handler = new CampaignAuthorityFreezeClientHandler(broker, network.Object, identity, controller,
            Mock.Of<IObjectManager>(), rosters.Object, clock.Object, sessions.Object, scheduler.Object, drain.Object, coalescer.Object);
        broker.Publish(this, new NetworkCampaignAuthorityFreezeRequest(lease, "host", 33));
        GameThread.Run(() => { }, blocking: true);
        Assert.Equal(drainSucceeds, identity.IsLocallyFrozen(lease));
        coalescer.Verify(value => value.Flush(network.Object), drainSucceeds ? Times.Once() : Times.Never());
        network.Verify(value => value.SendAll(It.IsAny<IMessage>()), Times.Never);
    }
}
