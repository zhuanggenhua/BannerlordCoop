using Common;
using Common.Network;
using Common.Messaging;
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
using GameInterface.Services.Heroes.Enum;
using GameInterface.Services.Heroes.Interaces;
using GameInterface.Services.MobileParties.Data;
using GameInterface.Services.Time.Interfaces;
using GameInterface.Services.GameState.Interfaces;
using GameInterface.Services.UI.Interfaces;
using LiteNetLib;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Coop.Tests.Client.Services.MobileParties;

public class JoinCampaignBaselineHandlerTests : IDisposable
{
    private readonly TestMessageBroker messageBroker = new TestMessageBroker();
    private readonly Mock<IMapTimeTrackerInterface> mapTimeTracker = new Mock<IMapTimeTrackerInterface>();
    private readonly Mock<IMobilePartyBehaviorSnapshot> mobilePartyBehaviorSnapshot =
        new Mock<IMobilePartyBehaviorSnapshot>();
    private readonly Mock<ITimeControlInterface> timeControl = new Mock<ITimeControlInterface>();
    private readonly Mock<IPlayerPartyTroopXpBaselineApplier> troopXpBaselineApplier =
        new Mock<IPlayerPartyTroopXpBaselineApplier>();
    private readonly Mock<IPartyBehaviorWireMapper> partyBehaviorWireMapper =
        new Mock<IPartyBehaviorWireMapper>();
    private readonly Mock<INetwork> network = new Mock<INetwork>();
    private readonly Mock<ICampaignAuthorityContext> authorityContext =
        new Mock<ICampaignAuthorityContext>();
    private readonly Mock<IRoomHostPeerProvider> roomHostPeerProvider;
    private readonly NetPeer hostPeer;
    private readonly JoinCampaignBaselineHandler handler;
    private readonly List<IMessage> sentMessages = new List<IMessage>();
    private Mock<IClientLogic> clientLogic;
    private LoadingState loadingState;
    private CampaignState campaignState;
    private string expectedRequestId;

    public JoinCampaignBaselineHandlerTests(ITestOutputHelper output)
    {
        _ = output;
        hostPeer = new TestNetwork().CreatePeer();
        roomHostPeerProvider = network.As<IRoomHostPeerProvider>();
        roomHostPeerProvider.Setup(provider => provider.IsCurrentRoomHostPeer(hostPeer)).Returns(true);
        network
            .Setup(value => value.SendAll(It.IsAny<IMessage>()))
            .Callback<IMessage>(sentMessages.Add);
        troopXpBaselineApplier
            .Setup(applier => applier.TryApply(It.IsAny<TroopRosterXpBaseline[]>()))
            .Returns(true);
        PartyBehaviorUpdateData mappedBehavior = default;
        partyBehaviorWireMapper
            .Setup(mapper => mapper.TryFromNetwork(
                It.IsAny<NetworkPartyBehaviorUpdateData>(), out mappedBehavior))
            .Returns(true);
        handler = new JoinCampaignBaselineHandler(
            messageBroker,
            mapTimeTracker.Object,
            mobilePartyBehaviorSnapshot.Object,
            timeControl.Object,
            troopXpBaselineApplier.Object,
            partyBehaviorWireMapper.Object,
            network.Object,
            authorityContext.Object,
            () => clientLogic?.Object);
    }

    public void Dispose()
    {
        handler.Dispose();
        campaignState?.Dispose();
        loadingState?.Dispose();
    }

    [Fact]
    public void CompleteBaseline_AppliesAllPartyStateBeforePublishingSuccess()
    {
        var partyStates = new[] { new MobilePartyJoinState() };
        var networkPartyStates = new[] { new NetworkMobilePartyJoinState(partyStates[0], default) };
        bool timeControlApplied = false;
        bool timeApplied = false;
        bool partyStateApplied = false;
        bool troopXpApplied = false;
        var troopXpBaselines = new[]
        {
            new TroopRosterXpBaseline(1, new[]
            {
                new TroopXpBaselineEntry(2, 42),
            }),
        };
        timeControl
            .Setup(time => time.ClientSetTimeControl(TimeControlEnum.Play_2x))
            .Callback(() => timeControlApplied = true);
        mapTimeTracker
            .Setup(tracker => tracker.ApplyCampaignJoinBaseline(123456L))
            .Callback(() =>
            {
                Assert.True(timeControlApplied);
                timeApplied = true;
            });
        mobilePartyBehaviorSnapshot
            .Setup(snapshot => snapshot.TryApplyJoinBaseline(It.IsAny<MobilePartyJoinState[]>(), It.IsAny<Action>()))
            .Callback<MobilePartyJoinState[], Action>((_, beforeApply) =>
            {
                beforeApply();
                Assert.True(timeApplied);
                partyStateApplied = true;
            })
            .Returns(true);
        troopXpBaselineApplier
            .Setup(applier => applier.TryApply(troopXpBaselines))
            .Callback(() =>
            {
                Assert.True(partyStateApplied);
                troopXpApplied = true;
            })
            .Returns(true);
        messageBroker.Subscribe<JoinCampaignBaselineApplied>(payload =>
        {
            Assert.True(partyStateApplied);
            Assert.True(troopXpApplied);
            Assert.True(payload.What.Success);
        });

        var applied = Apply(new NetworkJoinCampaignBaseline(
            123456L,
            TimeControlEnum.Play_2x,
            networkPartyStates,
            troopXpBaselines: troopXpBaselines));

        timeControl.Verify(time => time.ClientSetTimeControl(TimeControlEnum.Play_2x), Times.Once);
        mapTimeTracker.Verify(tracker => tracker.ApplyCampaignJoinBaseline(123456L), Times.Once);
        mobilePartyBehaviorSnapshot.Verify(
            snapshot => snapshot.TryApplyJoinBaseline(It.IsAny<MobilePartyJoinState[]>(), It.IsAny<Action>()),
            Times.Once);
        troopXpBaselineApplier.Verify(applier => applier.TryApply(troopXpBaselines), Times.Once);
        Assert.True(applied.Success);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void IncompleteOrRejectedBaseline_PublishesFailureWithoutApplyingTime(
        bool isComplete,
        int expectedApplyAttempts)
    {
        var partyStates = Array.Empty<MobilePartyJoinState>();
        var networkPartyStates = Array.Empty<NetworkMobilePartyJoinState>();
        mobilePartyBehaviorSnapshot
            .Setup(snapshot => snapshot.TryApplyJoinBaseline(partyStates, It.IsAny<Action>()))
            .Returns(false);

        var applied = Apply(new NetworkJoinCampaignBaseline(
            123456L,
            TimeControlEnum.Play_1x,
            networkPartyStates,
            isComplete));

        mobilePartyBehaviorSnapshot.Verify(
            snapshot => snapshot.TryApplyJoinBaseline(partyStates, It.IsAny<Action>()),
            Times.Exactly(expectedApplyAttempts));
        mapTimeTracker.Verify(
            tracker => tracker.ApplyCampaignJoinBaseline(It.IsAny<long>()),
            Times.Never);
        timeControl.Verify(
            time => time.ClientSetTimeControl(It.IsAny<TimeControlEnum>()),
            Times.Never);
        Assert.False(applied.Success);
    }

    [Fact]
    public void RejectedTroopXpBaseline_PublishesFailureAfterPartyStateApplies()
    {
        var partyStates = new[] { new MobilePartyJoinState() };
        var networkPartyStates = new[] { new NetworkMobilePartyJoinState(partyStates[0], default) };
        var troopXpBaselines = Array.Empty<TroopRosterXpBaseline>();
        mobilePartyBehaviorSnapshot
            .Setup(snapshot => snapshot.TryApplyJoinBaseline(It.IsAny<MobilePartyJoinState[]>(), It.IsAny<Action>()))
            .Callback<MobilePartyJoinState[], Action>((_, beforeApply) => beforeApply())
            .Returns(true);
        troopXpBaselineApplier
            .Setup(applier => applier.TryApply(troopXpBaselines))
            .Returns(false);

        var applied = Apply(new NetworkJoinCampaignBaseline(
            123456L,
            TimeControlEnum.Play_1x,
            networkPartyStates,
            troopXpBaselines: troopXpBaselines));

        Assert.False(applied.Success);
    }

    [Fact]
    public void CompletePausedCheckpointBaseline_RequiresCurrentHostAndMatchingConfirmedLease()
    {
        SetupCampaignStateWithPendingRequest();
        var lease = new AuthorityLease("room", "authority", string.Empty, 4, isPaused: true);
        SetupConfirmedLease(lease);
        SetupSuccessfulPartyBaseline();

        var applied = Apply(CreatePausedBaseline(lease));

        Assert.True(applied.Success);
        Assert.True(applied.HasPausedCheckpointAttestation);
        Assert.Equal(lease, applied.PausedCheckpointLease);
        Assert.Same(hostPeer, applied.SourcePeer);
        Assert.Equal(expectedRequestId, applied.BaselineRequestId);
    }

    [Fact]
    public void PausedCheckpointBaseline_FromForeignPeerIsIgnoredWithoutConsumingPendingResponse()
    {
        var lease = new AuthorityLease("room", "authority", string.Empty, 4, isPaused: true);
        var foreignPeer = new TestNetwork().CreatePeer();
        SetupConfirmedLease(lease);
        roomHostPeerProvider.Setup(provider => provider.IsCurrentRoomHostPeer(foreignPeer)).Returns(false);

        messageBroker.Publish(foreignPeer, CreatePausedBaseline(lease));
        GameThread.Run(() => { }, blocking: true);

        Assert.Empty(messageBroker.GetMessagesFromType<JoinCampaignBaselineApplied>());
        mobilePartyBehaviorSnapshot.Verify(
            snapshot => snapshot.TryApplyJoinBaseline(It.IsAny<MobilePartyJoinState[]>(), It.IsAny<Action>()),
            Times.Never);
    }

    [Fact]
    public void PausedCheckpointBaseline_WithStaleLeaseIsIgnoredWithoutConsumingPendingResponse()
    {
        var stampedLease = new AuthorityLease("room", "authority", string.Empty, 4, isPaused: true);
        var currentLease = new AuthorityLease("room", "authority", string.Empty, 5, isPaused: true);
        SetupConfirmedLease(currentLease);

        messageBroker.Publish(hostPeer, CreatePausedBaseline(stampedLease));
        GameThread.Run(() => { }, blocking: true);

        Assert.Empty(messageBroker.GetMessagesFromType<JoinCampaignBaselineApplied>());
        mobilePartyBehaviorSnapshot.Verify(
            snapshot => snapshot.TryApplyJoinBaseline(It.IsAny<MobilePartyJoinState[]>(), It.IsAny<Action>()),
            Times.Never);
    }

    [Fact]
    public void PausedCheckpointBaseline_IsDroppedIfHostIsReplacedBeforeQueuedApply()
    {
        var lease = new AuthorityLease("room", "authority", string.Empty, 4, isPaused: true);
        SetupConfirmedLease(lease);
        roomHostPeerProvider.SetupSequence(provider => provider.IsCurrentRoomHostPeer(hostPeer))
            .Returns(true)
            .Returns(true)
            .Returns(false);

        messageBroker.Publish(hostPeer, CreatePausedBaseline(lease));
        GameThread.Run(() => { }, blocking: true);

        Assert.Empty(messageBroker.GetMessagesFromType<JoinCampaignBaselineApplied>());
        roomHostPeerProvider.Verify(provider => provider.IsCurrentRoomHostPeer(hostPeer), Times.Exactly(3));
        mobilePartyBehaviorSnapshot.Verify(
            snapshot => snapshot.TryApplyJoinBaseline(It.IsAny<MobilePartyJoinState[]>(), It.IsAny<Action>()),
            Times.Never);
    }

    [Fact]
    public void IncompleteBaselineFromCurrentHostPublishesRetryableFailureWithoutAttestation()
    {
        var lease = new AuthorityLease("room", "authority", string.Empty, 4, isPaused: true);
        SetupConfirmedLease(lease);
        var baseline = new NetworkJoinCampaignBaseline(
            123456L,
            TimeControlEnum.Pause,
            Array.Empty<NetworkMobilePartyJoinState>(),
            isComplete: false);

        var applied = Apply(baseline);

        Assert.False(applied.Success);
        Assert.False(applied.HasPausedCheckpointAttestation);
        mapTimeTracker.Verify(tracker => tracker.ApplyCampaignJoinBaseline(It.IsAny<long>()), Times.Never);
    }

    [Fact]
    public void DisposedHandler_DoesNotApplyAlreadyQueuedBaseline()
    {
        messageBroker.Publish(hostPeer, new NetworkJoinCampaignBaseline(
            123456L,
            TimeControlEnum.Play_1x,
            Array.Empty<NetworkMobilePartyJoinState>()));

        handler.Dispose();
        GameThread.Run(() => { }, blocking: true);

        Assert.Empty(messageBroker.GetMessagesFromType<JoinCampaignBaselineApplied>());
        mobilePartyBehaviorSnapshot.Verify(
            snapshot => snapshot.TryApplyJoinBaseline(It.IsAny<MobilePartyJoinState[]>(), It.IsAny<Action>()),
            Times.Never);
    }

    private void SetupConfirmedLease(AuthorityLease lease)
    {
        authorityContext
            .Setup(context => context.TryGetCurrentLease(out lease))
            .Returns(true);
    }

    private void SetupSuccessfulPartyBaseline()
    {
        mobilePartyBehaviorSnapshot
            .Setup(snapshot => snapshot.TryApplyJoinBaseline(It.IsAny<MobilePartyJoinState[]>(), It.IsAny<Action>()))
            .Callback<MobilePartyJoinState[], Action>((_, beforeApply) => beforeApply())
            .Returns(true);
    }

    private NetworkJoinCampaignBaseline CreatePausedBaseline(AuthorityLease lease) =>
        new(
            123456L,
            TimeControlEnum.Pause,
            Array.Empty<NetworkMobilePartyJoinState>(),
            committedPausedCheckpointLease: lease,
            baselineRequestId: expectedRequestId ?? "d4388b2b0f8b4f939c6f86ebcb9c1251");

    private void SetupCampaignStateWithPendingRequest()
    {
        clientLogic = new Mock<IClientLogic>();
        IClientState currentState = null;
        clientLogic.SetupGet(logic => logic.State).Returns(() => currentState);
        loadingState = new LoadingState(
            clientLogic.Object,
            messageBroker,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);
        currentState = loadingState;
        campaignState = new CampaignState(
            clientLogic.Object,
            messageBroker,
            network.Object,
            new Mock<ILoadingInterface>().Object,
            null!,
            null!,
            mapTimeTracker.Object);
        currentState = campaignState;
        messageBroker.Publish(this, new NetworkJoinSync(JoinSyncSignal.ReplayComplete));
        GameThread.Run(() => { }, blocking: true);
        expectedRequestId = sentMessages
            .OfType<NetworkJoinSync>()
            .Single(message => message.Signal == JoinSyncSignal.ReplayApplied)
            .BaselineRequestId;
    }

    private JoinCampaignBaselineApplied Apply(NetworkJoinCampaignBaseline baseline)
    {
        messageBroker.Publish(hostPeer, baseline);
        GameThread.Run(() => { }, blocking: true);
        return Assert.Single(messageBroker.GetMessagesFromType<JoinCampaignBaselineApplied>());
    }
}
