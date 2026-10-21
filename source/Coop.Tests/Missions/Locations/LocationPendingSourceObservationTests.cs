using Common;
using Common.Messaging;
using Coop.Tests.Missions.Battles;
using GameInterface.Services.Locations;
using GameInterface.Services.Locations.Conversations;
using GameInterface.Services.MapEvents;
using GameInterface.Services.ObjectManager;
using Missions;
using Missions.Agents;
using Missions.Agents.Handlers;
using Missions.Locations;
using Missions.Messages;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Runtime.ExceptionServices;
using TaleWorlds.MountAndBlade;
using TaleWorlds.Core;
using Xunit;

namespace Coop.Tests.Missions.Locations;

[CollectionDefinition(nameof(PendingSourceMissionCollection), DisableParallelization = true)]
public class PendingSourceMissionCollection { }

[Collection(nameof(PendingSourceMissionCollection))]
public class LocationPendingSourceObservationTests : IDisposable
{
    private readonly MessageBroker broker = new();
    private readonly MissionCurrentScope mission = new();
    private readonly Mock<INetworkAgentRegistry> registry = new();
    private readonly Mock<ILocationSession> session = new();
    private readonly Mock<ILocationControllerWithdrawalState> withdrawal = new();
    private readonly Mock<IBattleAgentBudget> budget = new();
    private readonly Mock<IObjectManager> objects = new();
    private readonly LocationPuppetSpawner spawner;
    private readonly List<string> failures = new();
    private int completed;
    private bool current = true;
    private Exception sourceFailure;

    public LocationPendingSourceObservationTests()
    {
        session.SetupGet(value => value.InstanceId).Returns("location-instance");
        budget.Setup(value => value.RemainingCapacity(It.IsAny<int>())).Returns(64);
        var component = new Mock<ICoopMissionComponent>();
        component.SetupGet(value => value.AgentRegistry).Returns(registry.Object);
        var movement = new Mock<IAgentMovementHandler>();
        movement.SetupGet(value => value.Interpolator).Returns(Mock.Of<IAgentPositionInterpolator>());
        component.SetupGet(value => value.AgentMovementHandler).Returns(movement.Object);
        spawner = new LocationPuppetSpawner(broker, objects.Object, component.Object, session.Object,
            Mock.Of<ILocationConversationAgentGuard>(), Mock.Of<ILocationAgentBindingMap>(),
            Mock.Of<ILocationPartyAgentMap>(), Mock.Of<ILocationPuppetRosterBinder>(), budget.Object,
            new LocationAgentSpawnBatchCodec(), Mock.Of<ILocationAuthorityMigrator>(), withdrawal.Object);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BufferedRecordRetainsActualSourceUntilDedupeApply(bool networkThread)
    {
        var record = Record();
        if (networkThread)
        {
            var thread = new Thread(() => PublishObserved(record));
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(2)));
        }
        else OnGameThread(() => PublishObserved(record));
        OnGameThread(() =>
        {
            Assert.True(spawner.HasPendingNpcRecords);
            Assert.Equal(0, completed);
            Resolve(record.AgentId);
            spawner.NotifyMissionReady();
            spawner.DrainPendingPuppets();
            Assert.False(spawner.HasPendingNpcRecords);
            Assert.Equal(1, completed);
            Assert.Empty(failures);
        });
    }

    [Fact]
    public void BatchRetainsEachPendingRecordUntilAllHaveApplied()
    {
        OnGameThread(() =>
        {
            var first = Record();
            var second = Record();
            PublishObserved(first, second);
            Assert.Equal(0, completed);
            Resolve(first.AgentId);
            budget.Setup(value => value.RemainingCapacity(It.IsAny<int>())).Returns(0);
            spawner.NotifyMissionReady();
            spawner.DrainPendingPuppets();
            Assert.Equal(0, completed);
            Assert.True(spawner.HasPendingNpcRecords);
            spawner.DrainPendingPuppets();
            Resolve(second.AgentId);
            spawner.DrainPendingPuppets();
            Assert.False(spawner.HasPendingNpcRecords);
            Assert.Equal(1, completed);
            Assert.Empty(failures);
        });
    }

    [Fact]
    public void StaleSourceCannotApplyAndDoesNotRemoveNewerSameAgentRecord()
    {
        OnGameThread(() =>
        {
            var record = Record();
            PublishObserved(record);
            Assert.Equal(0, completed);
            current = false;
            int newerCompleted = 0;
            var newerFailures = new List<string>();
            using (MessageApplyScope.Observe(() => true, newerFailures.Add, () => newerCompleted++))
                broker.Publish(this, new NetworkSpawnLocationAgents(new[] { record }));
            Resolve(record.AgentId);
            spawner.NotifyMissionReady();
            spawner.DrainPendingPuppets();
            Assert.Equal(1, completed);
            Assert.Single(failures);
            Assert.Equal(1, newerCompleted);
            Assert.Empty(newerFailures);
            Assert.False(spawner.HasPendingNpcRecords);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DespawnOrDisposeCancelsPendingInsteadOfCertifyingApply(bool dispose)
    {
        OnGameThread(() =>
        {
            var record = Record();
            PublishObserved(record);
            Assert.Equal(0, completed);
            if (dispose) spawner.Dispose();
            else broker.Publish(this, new NetworkDespawnLocationAgents(new[] { record.AgentId }, Array.Empty<byte>(), Array.Empty<string>()));
            Assert.False(spawner.HasPendingNpcRecords);
            Assert.Equal(1, completed);
            Assert.Single(failures);
        });
    }

    [Fact]
    public void NativeUnresolvableDropIsFailureForObservedApply()
    {
        OnGameThread(() =>
        {
            PublishObserved(Record());
            spawner.NotifyMissionReady();
            spawner.DrainPendingPuppets();
            Assert.False(spawner.HasPendingNpcRecords);
            Assert.Equal(1, completed);
            Assert.Single(failures);
            Assert.Contains("unresolvable", failures[0], StringComparison.OrdinalIgnoreCase);
        });
    }

    private void PublishObserved(params LocationAgentSpawnData[] records)
    {
        using (MessageApplyScope.Observe(() => sourceFailure == null ? current : throw sourceFailure,
            failures.Add, () => completed++))
            broker.Publish(this, new NetworkSpawnLocationAgents(records));
    }

    [Fact]
    public void BudgetFailureSettlesEveryDequeuedResponsibilityAndPreservesOriginalError()
    {
        OnGameThread(() =>
        {
            PublishObserved(Record(), Record());
            var original = new InvalidOperationException("budget failed after dequeue");
            budget.Setup(value => value.CountLiveAgents(It.IsAny<Mission>())).Throws(original);
            spawner.NotifyMissionReady();
            Assert.Same(original, Assert.Throws<InvalidOperationException>(() => spawner.DrainPendingPuppets()));
            Assert.Equal(1, completed);
            Assert.Equal(2, failures.Count);
            Assert.All(failures, failure => Assert.Contains(original.Message, failure));
            Assert.False(spawner.HasPendingNpcRecords);
        });
    }

    [Fact]
    public void DisposeDuringSourceValidationCannotApplyAnAlreadyDequeuedRecord()
    {
        OnGameThread(() =>
        {
            var record = Record();
            bool cancel = false;
            using (MessageApplyScope.Observe(() =>
            {
                if (cancel) spawner.Dispose();
                return true;
            }, failures.Add, () => completed++))
                broker.Publish(this, new NetworkSpawnLocationAgents(new[] { record }));
            int nativeQueries = 0;
            var info = new CoopAgentInfo("host", "host", "host", null, record.AgentId, 0);
            registry.Setup(value => value.TryGetAgentInfo(record.AgentId, out info))
                .Callback(() => nativeQueries++).Returns(true);
            cancel = true;
            spawner.NotifyMissionReady();
            spawner.DrainPendingPuppets();
            Assert.Equal(0, nativeQueries);
            Assert.Equal(1, completed);
            Assert.Single(failures);
            Assert.Contains("disposed", failures[0], StringComparison.OrdinalIgnoreCase);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlayerWithdrawalCancelsWhileFormerHostQueueRemainsForOriginalAdoption(bool formerHost)
    {
        OnGameThread(() =>
        {
            var record = Record();
            PublishObserved(record);
            session.Setup(value => value.IsHostController("host")).Returns(formerHost);
            broker.Publish(this, new MissionPeerLeft("host", "location-instance"));
            Assert.Equal(formerHost, spawner.HasPendingNpcRecords);
            Assert.Equal(formerHost ? 0 : 1, completed);
            if (!formerHost) Assert.Single(failures);
            else
            {
                withdrawal.Verify(value => value.RetainFormerHostRecord(record.AgentId), Times.Once);
                Resolve(record.AgentId);
                spawner.NotifyMissionReady();
                spawner.DrainPendingPuppets();
                Assert.Equal(1, completed);
                Assert.Empty(failures);
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeOrSourceFailureFaultsRatherThanRequeuesForever(bool validator)
    {
        OnGameThread(() =>
        {
            var record = Record();
            PublishObserved(record);
            var original = new InvalidOperationException("retained record apply failure");
            if (validator) sourceFailure = original;
            else objects.Setup(value => value.TryGetObjectWithLogging(It.IsAny<string>(), out It.Ref<ItemObject>.IsAny)).Throws(original);
            spawner.NotifyMissionReady();
            spawner.DrainPendingPuppets();
            Assert.False(spawner.HasPendingNpcRecords);
            Assert.Equal(1, completed);
            Assert.Contains(failures, failure => failure.Contains(original.Message));
        });
    }

    [Fact]
    public void QueuedDescendantOfActualApplyKeepsTheOriginalObservationPending()
    {
        bool applied = false;
        OnGameThread(() =>
        {
            var record = Record();
            PublishObserved(record);
            var info = new CoopAgentInfo("host", "host", "host", null, record.AgentId, 0);
            registry.Setup(value => value.TryGetAgentInfo(record.AgentId, out info))
                .Callback(() => GameThread.EnqueueSafe(() => applied = true)).Returns(true);
            spawner.NotifyMissionReady();
            spawner.DrainPendingPuppets();
            Assert.Equal(0, completed);
            Assert.False(applied);
        });
        OnGameThread(() =>
        {
            Assert.True(applied);
            Assert.Equal(1, completed);
            Assert.Empty(failures);
        });
    }

    [Fact]
    public void CancellationCallbacksRunOutsideQueueLock()
    {
        OnGameThread(() =>
        {
            var record = Record();
            bool callbackReadQueue = false;
            using (MessageApplyScope.Observe(() => true, _ =>
            {
                var read = System.Threading.Tasks.Task.Run(() => spawner.HasPendingNpcRecords);
                callbackReadQueue = read.Wait(TimeSpan.FromSeconds(2));
            }, () => completed++))
                broker.Publish(this, new NetworkSpawnLocationAgents(new[] { record }));
            broker.Publish(this, new NetworkDespawnLocationAgents(new[] { record.AgentId }, Array.Empty<byte>(), Array.Empty<string>()));
            Assert.True(callbackReadQueue, "Cancellation callback must not hold the pending queue lock.");
            Assert.Equal(1, completed);
        });
    }

    [Fact]
    public void StaleCancellationDoesNotDeleteNewSameIdRecordCreatedByItsCallback()
    {
        OnGameThread(() =>
        {
            var record = Record();
            int newerCompleted = 0;
            var newerFailures = new List<string>();
            using (MessageApplyScope.Observe(() => current, _ =>
            {
                using (MessageApplyScope.Observe(() => true, newerFailures.Add, () => newerCompleted++))
                    broker.Publish(this, new NetworkSpawnLocationAgents(new[] { record }));
            }, () => completed++))
                broker.Publish(this, new NetworkSpawnLocationAgents(new[] { record }));
            current = false;
            budget.Setup(value => value.RemainingCapacity(It.IsAny<int>())).Returns(0);
            spawner.NotifyMissionReady();
            spawner.DrainPendingPuppets();
            Assert.Equal(1, completed);
            Assert.Equal(0, newerCompleted);
            Assert.True(spawner.HasPendingNpcRecords);
            Resolve(record.AgentId);
            spawner.DrainPendingPuppets();
            Assert.Equal(1, newerCompleted);
            Assert.Empty(newerFailures);
            Assert.False(spawner.HasPendingNpcRecords);
        });
    }

    [Fact]
    public void AlreadyQueuedBatchAfterDisposeFaultsWithoutCreatingRecords()
    {
        OnGameThread(() =>
        {
            var thread = new Thread(() => PublishObserved(Record()));
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(2)));
            spawner.Dispose();
        });
        OnGameThread(() =>
        {
            Assert.Equal(1, completed);
            Assert.Single(failures);
            Assert.Contains("disposed", failures[0], StringComparison.OrdinalIgnoreCase);
            Assert.False(spawner.HasPendingNpcRecords);
            budget.Verify(value => value.CountLiveAgents(It.IsAny<Mission>()), Times.Never);
        });
    }

    [Fact]
    public void NoObservationKeepsOriginalBufferAndDedupeBehavior()
    {
        OnGameThread(() =>
        {
            current = false; // This unobserved input must not acquire an unrelated source restriction.
            var record = Record();
            broker.Publish(this, new NetworkSpawnLocationAgents(new[] { record }));
            Assert.True(spawner.HasPendingNpcRecords);
            Resolve(record.AgentId);
            spawner.NotifyMissionReady();
            spawner.DrainPendingPuppets();
            Assert.False(spawner.HasPendingNpcRecords);
            Assert.Equal(0, completed);
            Assert.Empty(failures);
        });
    }

    private void Resolve(Guid id)
    {
        var info = new CoopAgentInfo("host", "host", "host", null, id, 0);
        registry.Setup(value => value.TryGetAgentInfo(id, out info)).Returns(true);
    }

    private static LocationAgentSpawnData Record() => new(Guid.NewGuid(), null, default, default, 100, "host",
        null, default, 0, 0, LocationAgentKind.Animal, "missing_item", null, 0, "host", null, null);

    public void Dispose()
    {
        OnGameThread(() =>
        {
            spawner.Dispose();
            broker.Dispose();
            mission.Dispose();
        });
    }

    private static void OnGameThread(Action action)
    {
        ExceptionDispatchInfo failure = null;
        GameThread.Run(() =>
        {
            try { action(); }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
        }, blocking: true);
        failure?.Throw();
    }
}
