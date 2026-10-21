using Common;
using Common.Messaging;
using Common.Network;
using Common.Network.Session;
using Coop.Core.Client.Messages;
using Coop.Core.Client.Services.Instances;
using Coop.Core.Client.Services.Session;
using Coop.Tests.Mocks;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Time.Interfaces;
using GameInterface.Services.Time.Messages;
using Moq;
using Serilog;
using System;
using System.Collections.Generic;
using Xunit;

namespace Coop.Tests.Client.Services.Session;

public class RoomMissionFactsBarrierTests
{
    private static RoomMissionFactsSnapshot Facts(long revision, bool occupied = false)
    {
        Assert.True(RoomMissionFactsSnapshot.TryCreate("room", revision, occupied
            ? new[] { new RoomMissionMember("A", "instance", 1) } : Array.Empty<RoomMissionMember>(), out var snapshot));
        return snapshot;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BarrierWaitsForExactActuallyInstalledMembershipAndEchoesIt(bool occupied)
    {
        using var broker = new MessageBroker();
        var network = new Mock<INetwork>();
        var peer = CampaignHandoffTestServices.AuthenticateRoomPeer(network);
        var session = new SessionIdentity();
        var lease = new AuthorityLease("room", "paused", "", 2, true);
        Assert.True(session.TryApply(lease, "A"));
        var projection = new RoomMissionMembershipProjection();
        var required = Facts(3, occupied);
        var controller = new ControllerIdProvider(); controller.SetControllerId("B");
        var objectManager = new ObjectManager(Mock.Of<ILogger>());
        uint initialHandle = objectManager.CaptureAllocationCheckpoint().NextHandle;
        var rosters = Mock.Of<ICampaignRosterCheckpointService>(value => value.TryApply(It.IsAny<CampaignRosterCheckpoint>()));
        var clock = Mock.Of<IMapTimeTrackerInterface>(value => value.TryApplyAuthorityHandoffBaseline(777));
        var shared = Mock.Of<ICampaignSessionCheckpointService>(value => value.TryApply(It.IsAny<CampaignSessionCheckpoint>()));
        using var handler = new CampaignAuthorityHandoffBarrierClientHandler(broker, network.Object, session, controller,
            objectManager, rosters, clock, shared, CampaignHandoffTestServices.Scheduler(), CampaignHandoffTestServices.Drained(),
            missionFacts: projection);
        var barrier = new NetworkCampaignAuthorityHandoffBarrier("room", "paused", 2, "B", 1,
            new RegistryAllocationCheckpoint(42, new Dictionary<string, int>()),
            new CampaignRosterCheckpoint(Array.Empty<CampaignRosterState>()), new CampaignClockCheckpoint(777),
            new CampaignSessionCheckpoint("campaign", "{}"), CampaignHandoffTestServices.Checkpoint(),
            missionFactsVersion: required.Version);
        broker.Publish(this, new ClientCampaignReady());
        broker.Publish(this, new CampaignMapSimulationReadinessChanged(true));
        broker.Publish(peer, barrier);
        GameThread.Run(() => { }, blocking: true);
        network.Verify(value => value.SendAll(It.IsAny<IMessage>()), Times.Never);
        GameThread.Run(() => Assert.True(projection.TryInstallComplete(required)), blocking: true);
        for (int i = 0; i < 4; i++) GameThread.Run(() => { }, blocking: true);
        network.Verify(value => value.SendAll(It.Is<NetworkCampaignAuthorityHandoffBarrierApplied>(ack =>
            ack.BarrierSequence == 1 && ack.MissionFactsVersion == required.Version)), occupied ? Times.Never() : Times.Once());
        Assert.Equal(occupied ? initialHandle : 42u, objectManager.CaptureAllocationCheckpoint().NextHandle);
        Assert.Equal(lease, session.CurrentLease);
    }
}
