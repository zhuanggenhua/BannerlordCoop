using Common.Network.Session;
using GameInterface.Services.Missions;
using System;

namespace Coop.Core.Client.Services.Instances;

public sealed class RoomMissionMembershipProjection : IRoomMissionFactsView
{
    private readonly object gate = new object();
    private RoomMissionFactsSnapshot current;
    private bool faulted;

    public event Action FactsChanged;

    public bool TryInstallComplete(RoomMissionFactsSnapshot snapshot)
    {
        if (snapshot == null || !snapshot.IsValid) return false;
        lock (gate)
        {
            if (faulted) return false;
            if (current != null)
            {
                if (snapshot.Version.RoomId != current.Version.RoomId ||
                    snapshot.Version.Revision < current.Version.Revision) return false;
                if (snapshot.Version.Revision == current.Version.Revision)
                {
                    if (snapshot.Version == current.Version) return true;
                    faulted = true;
                    return false;
                }
            }
            current = new RoomMissionFactsSnapshot(snapshot.Version, snapshot.Members);
        }
        FactsChanged?.Invoke();
        return true;
    }

    public bool TryCapture(out RoomMissionFactsSnapshot snapshot)
    {
        lock (gate)
        {
            snapshot = current == null || faulted ? null : new RoomMissionFactsSnapshot(current.Version, current.Members);
            return snapshot != null;
        }
    }

    public bool HasApplied(RoomMissionFactsVersion version)
    {
        lock (gate) return !faulted && current != null && version.IsValid && current.Version == version;
    }

    public bool CanActivateCampaign(RoomMissionFactsVersion version)
    {
        lock (gate) return !faulted && current != null && version.IsValid &&
            current.Version == version && current.Members.Length == 0;
    }

    public bool IsControllerInMission(string controllerId)
    {
        lock (gate) return current != null && Array.Exists(current.Members, member => member.ControllerId == controllerId);
    }

    public bool IsInstanceOccupied(string instanceId)
    {
        lock (gate) return current != null && Array.Exists(current.Members, member => member.InstanceId == instanceId);
    }
}
