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
            network.Verify(value => value.SendAll(It.IsAny<NetworkCampaignAuthorityFreezeAcknowledged>()), Times.Exactly(2));

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
