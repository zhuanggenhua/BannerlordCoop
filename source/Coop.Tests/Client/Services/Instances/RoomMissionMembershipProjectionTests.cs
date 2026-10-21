using Common.Network.Session;
using Coop.Core.Client.Services.Instances;
using System;
using Xunit;

namespace Coop.Tests.Client.Services.Instances;

public class RoomMissionMembershipProjectionTests
{
    private static RoomMissionFactsSnapshot Snapshot(long revision, params RoomMissionMember[] members)
    {
        Assert.True(RoomMissionFactsSnapshot.TryCreate("room", revision, members, out var snapshot));
        return snapshot;
    }

    [Fact]
    public void NeverInitializedCannotActivateButExplicitEmptyCan()
    {
        var projection = new RoomMissionMembershipProjection();
        Assert.False(projection.TryCapture(out _));
        Assert.False(projection.CanActivateCampaign(default));
        var empty = Snapshot(0);
        Assert.True(projection.TryInstallComplete(empty));
        Assert.True(projection.HasApplied(empty.Version));
        Assert.True(projection.CanActivateCampaign(empty.Version));
    }

    [Fact]
    public void CompleteReplacementPreservesQueriesWithoutClaimingMissingProtection()
    {
        var projection = new RoomMissionMembershipProjection();
        var entered = Snapshot(1, new RoomMissionMember("A", "battle", 1));
        Assert.True(projection.TryInstallComplete(entered));
        Assert.True(projection.IsControllerInMission("A"));
        Assert.True(projection.IsInstanceOccupied("battle"));
        Assert.True(projection.HasApplied(entered.Version));
        Assert.False(projection.CanActivateCampaign(entered.Version));
        var left = Snapshot(3);
        Assert.True(projection.TryInstallComplete(left));
        Assert.False(projection.IsControllerInMission("A"));
        Assert.False(projection.IsInstanceOccupied("battle"));
        Assert.False(projection.HasApplied(entered.Version));
        Assert.True(projection.CanActivateCampaign(left.Version));
        Assert.False(projection.TryInstallComplete(entered));
        Assert.True(projection.HasApplied(left.Version));
    }

    [Fact]
    public void DuplicateIsIdempotentButConflictingRevisionSticksAsFault()
    {
        var projection = new RoomMissionMembershipProjection();
        int changes = 0;
        projection.FactsChanged += () => changes++;
        var snapshot = Snapshot(1);
        Assert.True(projection.TryInstallComplete(snapshot));
        Assert.True(projection.TryInstallComplete(snapshot));
        Assert.Equal(1, changes);
        Assert.False(projection.TryInstallComplete(Snapshot(1, new RoomMissionMember("A", "town", 1))));
        Assert.False(projection.HasApplied(snapshot.Version));
        Assert.False(projection.TryCapture(out _));
        Assert.False(projection.TryInstallComplete(Snapshot(2)));
    }

    [Fact]
    public void DetachedCaptureCannotMutateInstalledFactsAndAnotherRoomCannotReplaceThem()
    {
        var projection = new RoomMissionMembershipProjection();
        var entered = Snapshot(1, new RoomMissionMember("A", "town", 1));
        Assert.True(projection.TryInstallComplete(entered));
        entered.Members[0] = new RoomMissionMember("B", "town", 2);
        Assert.True(projection.IsControllerInMission("A"));
        Assert.True(projection.TryCapture(out var detached));
        detached.Members[0] = new RoomMissionMember("C", "town", 3);
        Assert.True(projection.IsControllerInMission("A"));
        Assert.True(RoomMissionFactsSnapshot.TryCreate("other", 2, Array.Empty<RoomMissionMember>(), out var other));
        Assert.False(projection.TryInstallComplete(other));
        Assert.True(projection.IsControllerInMission("A"));
    }
}
