using Common;
using Common.Network.Session;
using Coop.Core.Client.Services.Instances;
using Coop.Core.Server.Services.Instances.Handlers;
using GameInterface.Services.Missions;
using Common.Network;
using LiteNetLib;
using GameInterface.Services.CampaignAuthority;
using Moq;
using System;

namespace Coop.Tests.Mocks;

internal static class CampaignHandoffTestServices
{
    public static RoomMissionFactsVersion MissionFactsVersion(string roomId)
    {
        if (!RoomMissionFactsSnapshot.TryCreate(roomId, 0, Array.Empty<RoomMissionMember>(), out var snapshot))
            throw new ArgumentException("An explicit room is required.", nameof(roomId));
        return snapshot.Version;
    }
    public static RoomMissionFactsVersion MissionFactsVersion(AuthorityLease lease) => MissionFactsVersion(lease.RoomId);
    public static IRoomMissionFactsView MissionFacts(ISessionIdentity identity) => MissionFacts(identity.RoomId);
    public static IRoomMissionFactsView MissionFacts(string roomId)
    {
        var projection = new RoomMissionMembershipProjection();
        RoomMissionFactsSnapshot.TryCreate(roomId, 0, Array.Empty<RoomMissionMember>(), out var snapshot);
        if (!projection.TryInstallComplete(snapshot)) throw new InvalidOperationException("Complete fixture facts required.");
        return projection;
    }
    public static IRoomMissionFactsPublisher InitialFacts(INetwork network, ISessionIdentity identity) => new InitialFactsPublisher(network, identity);
    private sealed class InitialFactsPublisher : IRoomMissionFactsPublisher
    {
        private readonly INetwork network;
        private readonly ISessionIdentity identity;
        internal InitialFactsPublisher(INetwork network, ISessionIdentity identity) { this.network = network; this.identity = identity; }
        public bool TrySendInitial(NetPeer peer, out RoomMissionFactsVersion version)
        {
            version = default;
            if (!identity.IsConfirmed || !RoomMissionFactsSnapshot.TryCreate(identity.RoomId, 0,
                Array.Empty<RoomMissionMember>(), out var snapshot)) return false;
            network.SendImmediate(peer, new NetworkRoomMissionFactsSnapshot(snapshot));
            version = snapshot.Version;
            return true;
        }
        public bool HasApplied(NetPeer peer, RoomMissionFactsVersion version) => false;
    }

    public static NetPeer AuthenticateRoomPeer(Mock<INetwork> network)
    {
        var peer = new TestNetwork().CreatePeer();
        network.As<IRoomHostPeerProvider>().Setup(value => value.IsCurrentRoomHostPeer(It.IsAny<NetPeer>()))
            .Returns((NetPeer candidate) => ReferenceEquals(candidate, peer));
        return peer;
    }

    public static CampaignSchedulerCheckpoint Checkpoint() => new(0, 50, 0,
        Array.Empty<CampaignPeriodicTickerState>(), Array.Empty<CampaignPeriodicEventState>());

    public static ICampaignSchedulerCheckpointService Scheduler()
    {
        var service = new Mock<ICampaignSchedulerCheckpointService>();
        var checkpoint = Checkpoint();
        service.Setup(value => value.TryCapture(out checkpoint)).Returns(true);
        service.Setup(value => value.TryApply(It.IsAny<CampaignSchedulerCheckpoint>())).Returns(true);
        return service.Object;
    }

    public static ICampaignAsyncTaskDrain Drained() => Mock.Of<ICampaignAsyncTaskDrain>(value => value.TryDrain());
}
