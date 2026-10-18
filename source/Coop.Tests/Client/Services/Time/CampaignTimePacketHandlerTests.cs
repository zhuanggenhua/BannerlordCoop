using Common.PacketHandlers;
using Common.Tests.Utils;
using Coop.Core.Client.Messages;
using Coop.Core.Client.Services.Time.Handlers;
using Coop.Core.Common.Network.Packets;
using GameInterface.Services.Time.Interfaces;
using Moq;
using System;
using Xunit;

namespace Coop.Tests.Client.Services.Time;

public class CampaignTimePacketHandlerTests
{
    private static readonly AuthorityLease CurrentLease =
        new AuthorityLease("room-a", "authority-b", "controller-b", 2);

    [Fact]
    public void Dispose_RemovesPacketHandler()
    {
        var broker = new TestMessageBroker();
        var packetManager = new Mock<IPacketManager>();
        var mapTimeTracker = new Mock<IMapTimeTrackerInterface>();
        var handler = new CampaignTimePacketHandler(
            broker, packetManager.Object, mapTimeTracker.Object, CreateConfirmedIdentity());

        handler.Dispose();

        packetManager.Verify(m => m.RemovePacketHandler(handler), Times.Once);
    }

    [Fact]
    public void CampaignTimePacket_CallsSyncCampaignTime()
    {
        var broker = new TestMessageBroker();
        var packetManager = new Mock<IPacketManager>();
        var mapTimeTracker = new Mock<IMapTimeTrackerInterface>();
        var identity = CreateConfirmedIdentity();
        Func<bool> sampleStillCurrent = null;
        mapTimeTracker
            .Setup(m => m.SyncCampaignTime(It.IsAny<long>(), It.IsAny<float>(), It.IsAny<Func<bool>>()))
            .Callback<long, float, Func<bool>>((_, _, isStillCurrent) => sampleStillCurrent = isStillCurrent);
        var handler = new CampaignTimePacketHandler(
            broker, packetManager.Object, mapTimeTracker.Object, identity);
        var packet = new CampaignTimePacket(123456L, -1, CurrentLease);

        handler.HandlePacket(null, packet);

        mapTimeTracker.Verify(
            m => m.SyncCampaignTime(packet.ServerTicks, 0f, It.IsAny<Func<bool>>()),
            Times.Once);
        Assert.NotNull(sampleStillCurrent);
        Assert.True(sampleStillCurrent());
        Assert.True(identity.TryApply(new AuthorityLease("room-a", "authority-paused", string.Empty, 3, isPaused: true)));
        Assert.False(sampleStillCurrent());
        var sample = Assert.Single(broker.GetMessagesFromType<CampaignTimeSampleReceived>());
        Assert.Equal(-1, sample.JoinPacketsRemaining);
    }

    [Fact]
    public void CampaignTimePacket_WithJoinProgress_PublishesRemainingPackets()
    {
        var broker = new TestMessageBroker();
        var packetManager = new Mock<IPacketManager>();
        var mapTimeTracker = new Mock<IMapTimeTrackerInterface>();
        var handler = new CampaignTimePacketHandler(
            broker, packetManager.Object, mapTimeTracker.Object, CreateConfirmedIdentity());

        handler.HandlePacket(null, new CampaignTimePacket(123456L, 4321, CurrentLease));

        var sample = Assert.Single(broker.GetMessagesFromType<CampaignTimeSampleReceived>());
        Assert.Equal(4321, sample.JoinPacketsRemaining);
    }

    [Theory]
    [InlineData(-1, 0f)]
    [InlineData(75, 0.075f)]
    [InlineData(500, 0.25f)]
    public void CalculateOneWayLatencySeconds_ClampsNetworkLatency(int latencyMilliseconds, float expectedSeconds)
    {
        Assert.Equal(expectedSeconds, CampaignTimePacketHandler.CalculateOneWayLatencySeconds(latencyMilliseconds));
    }

    [Fact]
    public void CampaignTimePacket_RejectsPreviousAuthorityEpoch()
    {
        var broker = new TestMessageBroker();
        var packetManager = new Mock<IPacketManager>();
        var mapTimeTracker = new Mock<IMapTimeTrackerInterface>();
        var handler = new CampaignTimePacketHandler(
            broker, packetManager.Object, mapTimeTracker.Object, CreateConfirmedIdentity());
        var previousLease = new AuthorityLease("room-a", "authority-a", "controller-a", 1);

        handler.HandlePacket(null, new CampaignTimePacket(123456L, -1, previousLease));

        mapTimeTracker.Verify(
            m => m.SyncCampaignTime(It.IsAny<long>(), It.IsAny<float>(), It.IsAny<Func<bool>>()),
            Times.Never);
        Assert.Empty(broker.GetMessagesFromType<CampaignTimeSampleReceived>());
    }

    [Fact]
    public void CampaignTimePacket_RejectsEvenMatchingPausedLease()
    {
        var broker = new TestMessageBroker();
        var packetManager = new Mock<IPacketManager>();
        var mapTimeTracker = new Mock<IMapTimeTrackerInterface>();
        var pausedLease = new AuthorityLease("room-a", "authority-paused", string.Empty, 3, isPaused: true);
        var identity = new SessionIdentity();
        Assert.True(identity.TryApply(pausedLease));
        var handler = new CampaignTimePacketHandler(
            broker, packetManager.Object, mapTimeTracker.Object, identity);

        handler.HandlePacket(null, new CampaignTimePacket(123456L, -1, pausedLease));

        mapTimeTracker.Verify(
            m => m.SyncCampaignTime(It.IsAny<long>(), It.IsAny<float>(), It.IsAny<Func<bool>>()),
            Times.Never);
        Assert.Empty(broker.GetMessagesFromType<CampaignTimeSampleReceived>());
    }

    private static SessionIdentity CreateConfirmedIdentity()
    {
        var identity = new SessionIdentity();
        Assert.True(identity.TryApply(CurrentLease));
        return identity;
    }
}
