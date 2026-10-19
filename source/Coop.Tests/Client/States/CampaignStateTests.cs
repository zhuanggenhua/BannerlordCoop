using Autofac;
using Common;
using Common.Messaging;
using Common.Network;
using Coop.Core.Common;
using System;
using System.Threading;
using Common.Tests.Utils;
using Coop.Core.Client;
using Coop.Core.Client.Messages;
using Coop.Core.Client.Services.MobileParties;
using Coop.Core.Client.Services.MobileParties.Handlers;
using Coop.Core.Client.States;
using Coop.Core.Server.Connections.Messages;
using Coop.Core.Server.Services.MobileParties.Messages;
using Coop.Tests.Mocks;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.GameState.Interfaces;
using GameInterface.Services.GameState.Messages;
using GameInterface.Services.Heroes.Enum;
using GameInterface.Services.Heroes.Interaces;
using GameInterface.Services.MobileParties.Data;
using GameInterface.Services.Time.Interfaces;
using GameInterface.Services.UI.Interfaces;
using GameInterface.Services.UI.Messages;
using Moq;
using LiteNetLib;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;
using LiteNetLib;

namespace Coop.Tests.Client.States;

// Drain-limit assertions observe the shared default game-thread queue.
[CollectionDefinition(nameof(CampaignStateCollection), DisableParallelization = true)]
public sealed class CampaignStateCollection
{
}

[Collection(nameof(CampaignStateCollection))]
public class CampaignStateTests : IDisposable
{
    public void Dispose() => clientLogic.State.Dispose();

    private readonly IClientLogic clientLogic;
    private readonly ClientTestComponent clientComponent;

    private TestMessageBroker TestMessageBroker => clientComponent.TestMessageBroker;
    public CampaignStateTests(ITestOutputHelper output)
    {
        clientComponent = new ClientTestComponent(output);
        var container = clientComponent.Container;
        clientLogic = container.Resolve<IClientLogic>()!;
    }

    [Fact]
    public void ReplayBatchAcknowledgement_WaitsForEarlierGameThreadApplicationAndEchoesId()
    {
        clientComponent.TestNetwork.CreatePeer();
        _ = clientLogic.SetState<LoadingState>();
        _ = clientLogic.SetState<CampaignState>();
        using var earlierReplayStarted = new ManualResetEventSlim();
        using var releaseEarlierReplay = new ManualResetEventSlim();
        bool earlierReplayApplied = false;
        GameThread.EnqueueSafe(
            () =>
            {
                earlierReplayStarted.Set();
                releaseEarlierReplay.Wait();
                earlierReplayApplied = true;
            },
            context: nameof(ReplayBatchAcknowledgement_WaitsForEarlierGameThreadApplicationAndEchoesId));

        try
        {
            Assert.True(earlierReplayStarted.Wait(TimeSpan.FromSeconds(5)));
            TestMessageBroker.Publish(
                this,
                new NetworkJoinSync(JoinSyncSignal.ReplayBatchComplete, replayBatchId: 17));

            Assert.False(earlierReplayApplied);
            Assert.Equal(0, JoinSignalCount(JoinSyncSignal.ReplayBatchApplied));
        }
        finally
        {
            releaseEarlierReplay.Set();
        }

        DrainGameThread();

        Assert.True(earlierReplayApplied);
        NetworkJoinSync acknowledgement = Assert.Single(
            clientComponent.TestNetwork
                .GetPeerMessagesFromType<NetworkJoinSync>(clientComponent.TestNetwork.Peers[0]),
            message => message.Signal == JoinSyncSignal.ReplayBatchApplied);
        Assert.Equal(17, acknowledgement.ReplayBatchId);
    }

    [Fact]
    public void FailedCampaignEntrySend_ReleasesTheFrameDrainLimit()
    {
        var container = clientComponent.Container;
        _ = clientLogic.SetState<LoadingState>();
        int previousLimitCount = GameThread.Instance.FrameDrainLimitCount;
        var network = new Mock<INetwork>();
        network
            .Setup(value => value.SendAll(It.IsAny<IMessage>()))
            .Throws<InvalidOperationException>();

        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = new CampaignState(
                clientLogic,
                container.Resolve<IMessageBroker>(),
                network.Object,
                container.Resolve<ILoadingInterface>(),
                container.Resolve<IGameStateInterface>(),
                container.Resolve<ICoopFinalizer>(),
                container.Resolve<IMapTimeTrackerInterface>());
        });

        Assert.Equal(previousLimitCount, GameThread.Instance.FrameDrainLimitCount);
    }

    [Fact]
    public void LeavingAJoiningCampaign_ReleasesTheFrameDrainLimit()
    {
        int previousLimitCount = GameThread.Instance.FrameDrainLimitCount;
        _ = clientLogic.SetState<LoadingState>();
        _ = clientLogic.SetState<CampaignState>();
        Assert.Equal(previousLimitCount + 1, GameThread.Instance.FrameDrainLimitCount);

        _ = clientLogic.SetState<MainMenuState>();

        Assert.Equal(previousLimitCount, GameThread.Instance.FrameDrainLimitCount);
    }

    [Fact]
    public void EnterMainMenu_GoesToMainMenu()
    {
        // Arrange
        var campaignState = clientLogic.SetState<CampaignState>();
        var gameStateMock = clientComponent.Container.Resolve<Mock<IGameStateInterface>>();

        // Act
        clientLogic.EnterMainMenu();

        // Assert
        gameStateMock.Verify(x => x.GoToMainMenu(), Times.Once);
    }

    [Fact]
    public void Disconnect_GoesToMainMenu()
    {
        // Arrange
        var campaignState = clientLogic.SetState<CampaignState>();
        var gameStateMock = clientComponent.Container.Resolve<Mock<IGameStateInterface>>();

        // Act
        clientLogic.Disconnect();

        // Assert
        gameStateMock.Verify(x => x.GoToMainMenu(), Times.Once);
    }

    [Fact]
    public void OtherStateMethods_DoNotAlterState()
    {
        var campaignState = clientLogic.SetState<CampaignState>();

        clientLogic.Connect();
        Assert.IsType<CampaignState>(clientLogic.State);

        clientLogic.Disconnect();
        Assert.IsType<CampaignState>(clientLogic.State);

        clientLogic.ExitGame();
        Assert.IsType<CampaignState>(clientLogic.State);

        clientLogic.LoadSavedData();
        Assert.IsType<CampaignState>(clientLogic.State);

        clientLogic.StartCharacterCreation();
        Assert.IsType<CampaignState>(clientLogic.State);

        clientLogic.EnterCampaignState();
        Assert.IsType<CampaignState>(clientLogic.State);

        clientLogic.ValidateModules();
        Assert.IsType<CampaignState>(clientLogic.State);
    }

    [Fact]
    public void EnteringCampaign_RetriesBaselinesAndWaitsForWorldReady()
    {
        clientComponent.TestNetwork.CreatePeer();
        var loadingInterface = clientComponent.Container.Resolve<Mock<ILoadingInterface>>();
        var mapTimeTracker = clientComponent.Container.Resolve<Mock<IMapTimeTrackerInterface>>();
        _ = clientLogic.SetState<LoadingState>();
        loadingInterface.Reset();
        _ = clientLogic.SetState<CampaignState>();

        Assert.Single(clientComponent.TestNetwork.GetPeerMessagesFromType<NetworkPlayerCampaignEntered>(
            clientComponent.TestNetwork.Peers[0]));
        loadingInterface.Verify(m => m.HideLoadingScreen(), Times.Never);
        mapTimeTracker.Verify(m => m.ResetForCampaignJoin(), Times.Once);
        Assert.Empty(TestMessageBroker.GetMessagesFromType<PlayerKillFeedColorResendRequested>());

        PublishJoinSignal(JoinSyncSignal.ReplayComplete);
        Assert.Equal(1, JoinSignalCount(JoinSyncSignal.ReplayApplied));

        PublishJoinSignal(JoinSyncSignal.WorldReady);
        AssertJoinStillLoading(loadingInterface);

        PublishBaseline(success: false);
        Assert.Equal(1, JoinSignalCount(JoinSyncSignal.BaselineRequested));
        PublishBaseline(success: true);
        Assert.Equal(2, JoinSignalCount(JoinSyncSignal.BaselineRequested));
        PublishBaseline(success: false);
        Assert.Equal(3, JoinSignalCount(JoinSyncSignal.BaselineRequested));
        PublishBaseline(success: true);

        PublishTimeSample(mapTimeTracker, completed: false, refreshRequired: false);
        Assert.Equal(0, JoinSignalCount(JoinSyncSignal.BaselineApplied));
        PublishTimeSample(mapTimeTracker, completed: true, refreshRequired: true);
        Assert.Equal(4, JoinSignalCount(JoinSyncSignal.BaselineRequested));

        PublishBaseline(success: true);
        PublishTimeSample(mapTimeTracker, completed: true, refreshRequired: false);
        Assert.Equal(1, JoinSignalCount(JoinSyncSignal.BaselineApplied));

        PublishJoinSignal(JoinSyncSignal.WorldReady);
        AssertJoinStillLoading(loadingInterface);

        PublishBaseline(success: false);
        Assert.Equal(5, JoinSignalCount(JoinSyncSignal.BaselineRequested));
        PublishBaseline(success: true);
        PublishTimeSample(mapTimeTracker, completed: true, refreshRequired: true);
        Assert.Equal(6, JoinSignalCount(JoinSyncSignal.BaselineRequested));
        Assert.Equal(0, JoinSignalCount(JoinSyncSignal.FinalBaselineApplied));

        PublishBaseline(success: true);
        PublishTimeSample(mapTimeTracker, completed: true, refreshRequired: false);
        Assert.Equal(1, JoinSignalCount(JoinSyncSignal.FinalBaselineApplied));
        mapTimeTracker.Verify(m => m.ResetForCampaignJoin(), Times.Exactly(8));
        AssertJoinStillLoading(loadingInterface);

        PublishJoinSignal(JoinSyncSignal.WorldReady);

        loadingInterface.Verify(m => m.HideLoadingScreen(), Times.Once);
        Assert.Single(TestMessageBroker.GetMessagesFromType<PlayerKillFeedColorResendRequested>());
        Assert.Single(TestMessageBroker.GetMessagesFromType<ClientCampaignReady>());
        Assert.Equal(1, JoinSignalCount(JoinSyncSignal.CatchUpApplied));
    }

    [Fact]
    public void EnteringCampaign_ShowsJoinPacketCountdownAndFinishingState()
    {
        clientComponent.TestNetwork.CreatePeer();
        var loadingInterface = clientComponent.Container.Resolve<Mock<ILoadingInterface>>();
        _ = clientLogic.SetState<LoadingState>();
        _ = clientLogic.SetState<CampaignState>();
        loadingInterface.Reset();

        TestMessageBroker.Publish(this, new CampaignTimeSampleReceived(12345));
        GameThread.Run(() => { }, blocking: true);

        loadingInterface.Verify(m => m.SetLoadingMessage(
            "Loading Host Campaign",
            "Catching up to the host... 12,345 packets remaining"), Times.Once);

        TestMessageBroker.Publish(this, new CampaignTimeSampleReceived(0));
        GameThread.Run(() => { }, blocking: true);

        loadingInterface.Verify(m => m.SetLoadingMessage(
            "Loading Host Campaign",
            "Finishing synchronization..."), Times.Once);
    }

    [Theory]
    [InlineData(NetworkJoinSync.CompletionPacketThreshold + 1, false)]
    [InlineData(NetworkJoinSync.CompletionPacketThreshold, true)]
    public void FinalCatchUp_UsesAcceptablePacketBacklog(int packetsRemaining, bool completes)
    {
        var mapTimeTracker = PrepareFinalCatchUp();

        PublishTimeSample(
            mapTimeTracker,
            completed: false,
            refreshRequired: false,
            packetsRemaining: packetsRemaining);

        Assert.Equal(
            completes ? 1 : 0,
            JoinSignalCount(JoinSyncSignal.FinalBaselineApplied));
    }

    [Fact]
    public void FinalCatchUp_RefreshesInvalidBaselineWithinPacketThreshold()
    {
        var mapTimeTracker = PrepareFinalCatchUp();
        int requestsBefore = JoinSignalCount(JoinSyncSignal.BaselineRequested);

        PublishTimeSample(
            mapTimeTracker,
            completed: true,
            refreshRequired: true,
            packetsRemaining: NetworkJoinSync.CompletionPacketThreshold);

        Assert.Equal(
            requestsBefore + 1,
            JoinSignalCount(JoinSyncSignal.BaselineRequested));
        Assert.Equal(0, JoinSignalCount(JoinSyncSignal.FinalBaselineApplied));
    }

    private void PublishJoinSignal(JoinSyncSignal signal)
    {
        TestMessageBroker.Publish(this, new NetworkJoinSync(signal));
        DrainGameThread();
    }

    private void PublishBaseline(bool success) =>
        TestMessageBroker.Publish(this, new JoinCampaignBaselineApplied(success));

    private void PublishTimeSample(
        Mock<IMapTimeTrackerInterface> tracker,
        bool completed,
        bool refreshRequired,
        int packetsRemaining = -1)
    {
        tracker.Setup(m => m.TryCompleteCampaignJoinCatchUp(out refreshRequired)).Returns(completed);
        TestMessageBroker.Publish(this, new CampaignTimeSampleReceived(packetsRemaining));
        DrainGameThread();
    }

    private Mock<IMapTimeTrackerInterface> PrepareFinalCatchUp()
    {
        clientComponent.TestNetwork.CreatePeer();
        var mapTimeTracker = clientComponent.Container.Resolve<Mock<IMapTimeTrackerInterface>>();
        _ = clientLogic.SetState<LoadingState>();
        _ = clientLogic.SetState<CampaignState>();

        PublishJoinSignal(JoinSyncSignal.ReplayComplete);
        PublishBaseline(success: true);
        PublishBaseline(success: true);
        PublishTimeSample(mapTimeTracker, completed: true, refreshRequired: false);
        PublishBaseline(success: true);

        return mapTimeTracker;
    }

    private int JoinSignalCount(JoinSyncSignal signal) =>
        clientComponent.TestNetwork
            .GetPeerMessagesFromType<NetworkJoinSync>(clientComponent.TestNetwork.Peers[0])
            .Count(message => message.Signal == signal);

    private void AssertJoinStillLoading(Mock<ILoadingInterface> loadingInterface)
    {
        loadingInterface.Verify(m => m.HideLoadingScreen(), Times.Never);
        Assert.Empty(TestMessageBroker.GetMessagesFromType<PlayerKillFeedColorResendRequested>());
        Assert.Empty(TestMessageBroker.GetMessagesFromType<ClientCampaignReady>());
        Assert.Equal(0, JoinSignalCount(JoinSyncSignal.CatchUpApplied));
    }

    private static void DrainGameThread() => GameThread.Run(() => { }, blocking: true);
}

public sealed class PausedCampaignStateCorrelationTests
{
    [Fact]
    public void PausedCheckpointJoin_RequiresCurrentRequestIdAndAdvancesWithoutTimeSamples()
    {
        var broker = new TestMessageBroker();
        var network = new Mock<INetwork>();
        var roomHostPeerProvider = network.As<IRoomHostPeerProvider>();
        var peer = new TestNetwork().CreatePeer();
        roomHostPeerProvider.Setup(provider => provider.IsCurrentRoomHostPeer(peer)).Returns(true);
        var sentMessages = new List<IMessage>();
        network
            .Setup(value => value.SendAll(It.IsAny<IMessage>()))
            .Callback<IMessage>(sentMessages.Add);
        var logic = new Mock<IClientLogic>();
        var loadingState = new LoadingState(
            logic.Object,
            broker,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);
        IClientState currentState = loadingState;
        logic.SetupGet(value => value.State).Returns(() => currentState);
        var loadingInterface = new Mock<ILoadingInterface>();
        var mapTimeTracker = new Mock<IMapTimeTrackerInterface>();
        var mobilePartySnapshot = new Mock<IMobilePartyBehaviorSnapshot>();
        mobilePartySnapshot
            .Setup(snapshot => snapshot.TryApplyJoinBaseline(
                It.IsAny<MobilePartyJoinState[]>(),
                It.IsAny<Action>()))
            .Callback<MobilePartyJoinState[], Action>((_, beforeApply) => beforeApply())
            .Returns(true);
        var troopXpApplier = new Mock<IPlayerPartyTroopXpBaselineApplier>();
        troopXpApplier
            .Setup(applier => applier.TryApply(It.IsAny<TroopRosterXpBaseline[]>()))
            .Returns(true);
        var behaviorMapper = new Mock<IPartyBehaviorWireMapper>();
        var timeControl = new Mock<ITimeControlInterface>();
        var authorityContext = new Mock<ICampaignAuthorityContext>();
        var lease = new AuthorityLease("room", "authority", string.Empty, 4, isPaused: true);
        authorityContext.Setup(context => context.TryGetCurrentLease(out lease)).Returns(true);
        var handler = new JoinCampaignBaselineHandler(
            broker,
            mapTimeTracker.Object,
            mobilePartySnapshot.Object,
            timeControl.Object,
            troopXpApplier.Object,
            behaviorMapper.Object,
            network.Object,
            authorityContext.Object,
            () => logic.Object);
        var campaignState = new CampaignState(
            logic.Object,
            broker,
            network.Object,
            loadingInterface.Object,
            null!,
            null!,
            mapTimeTracker.Object);
        currentState = campaignState;

        try
        {
            broker.Publish(this, new NetworkJoinSync(JoinSyncSignal.ReplayComplete));
            DrainGameThread();
            string firstRequestId = LastRequest(sentMessages, JoinSyncSignal.ReplayApplied).BaselineRequestId;

            PublishPausedBaseline(broker, peer, lease, "ffffffffffffffffffffffffffffffff");
            mobilePartySnapshot.Verify(
                snapshot => snapshot.TryApplyJoinBaseline(
                    It.IsAny<MobilePartyJoinState[]>(), It.IsAny<Action>()),
                Times.Never);
            timeControl.Verify(
                control => control.ClientSetTimeControl(It.IsAny<TimeControlEnum>()),
                Times.Never);
            mapTimeTracker.Verify(
                tracker => tracker.ApplyCampaignJoinBaseline(It.IsAny<long>()),
                Times.Never);
            troopXpApplier.Verify(
                applier => applier.TryApply(It.IsAny<TroopRosterXpBaseline[]>()),
                Times.Never);
            Assert.Equal(0, SignalCount(sentMessages, JoinSyncSignal.BaselineRequested));

            PublishPausedBaseline(broker, peer, lease, firstRequestId);
            string secondRequestId = LastRequest(sentMessages, JoinSyncSignal.BaselineRequested).BaselineRequestId;
            Assert.NotEqual(firstRequestId, secondRequestId);

            PublishPausedBaseline(broker, peer, lease, firstRequestId);
            mobilePartySnapshot.Verify(
                snapshot => snapshot.TryApplyJoinBaseline(
                    It.IsAny<MobilePartyJoinState[]>(), It.IsAny<Action>()),
                Times.Once);
            timeControl.Verify(
                control => control.ClientSetTimeControl(It.IsAny<TimeControlEnum>()),
                Times.Once);
            mapTimeTracker.Verify(
                tracker => tracker.ApplyCampaignJoinBaseline(It.IsAny<long>()),
                Times.Once);
            troopXpApplier.Verify(
                applier => applier.TryApply(It.IsAny<TroopRosterXpBaseline[]>()),
                Times.Once);
            Assert.Equal(1, SignalCount(sentMessages, JoinSyncSignal.BaselineRequested));
            Assert.Equal(secondRequestId, LastRequest(sentMessages, JoinSyncSignal.BaselineRequested).BaselineRequestId);
            Assert.Equal(0, SignalCount(sentMessages, JoinSyncSignal.FinalBaselineApplied));

            PublishPausedBaseline(broker, peer, lease, secondRequestId);
            string finalRequestId = LastRequest(sentMessages, JoinSyncSignal.BaselineApplied).BaselineRequestId;
            Assert.NotEqual(secondRequestId, finalRequestId);

            PublishPausedBaseline(broker, peer, lease, finalRequestId);

            Assert.Equal(1, SignalCount(sentMessages, JoinSyncSignal.FinalBaselineApplied));
            Assert.Equal(finalRequestId, LastRequest(sentMessages, JoinSyncSignal.FinalBaselineApplied).BaselineRequestId);
            mapTimeTracker.Verify(
                tracker => tracker.TryCompleteCampaignJoinCatchUp(out It.Ref<bool>.IsAny),
                Times.Never);

            broker.Publish(this, new NetworkJoinSync(JoinSyncSignal.WorldReady));
            DrainGameThread();

            loadingInterface.Verify(value => value.HideLoadingScreen(), Times.Once);
            Assert.Single(broker.GetMessagesFromType<ClientCampaignReady>());
            Assert.Equal(1, SignalCount(sentMessages, JoinSyncSignal.CatchUpApplied));
        }
        finally
        {
            handler.Dispose();
            campaignState.Dispose();
            loadingState.Dispose();
        }
    }

    private static void PublishPausedBaseline(
        TestMessageBroker broker,
        NetPeer peer,
        AuthorityLease lease,
        string requestId)
    {
        broker.Publish(
            peer,
            new NetworkJoinCampaignBaseline(
                12345L,
                TimeControlEnum.Pause,
                Array.Empty<NetworkMobilePartyJoinState>(),
                committedPausedCheckpointLease: lease,
                baselineRequestId: requestId));
        DrainGameThread();
    }

    private static int SignalCount(List<IMessage> sentMessages, JoinSyncSignal signal) =>
        sentMessages.OfType<NetworkJoinSync>().Count(message => message.Signal == signal);

    private static NetworkJoinSync LastRequest(List<IMessage> sentMessages, JoinSyncSignal signal) =>
        sentMessages.OfType<NetworkJoinSync>().Last(message => message.Signal == signal);

    private static void DrainGameThread() => GameThread.Run(() => { }, blocking: true);
}
