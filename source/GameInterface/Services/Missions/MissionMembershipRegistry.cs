namespace GameInterface.Services.Missions;

/// <summary>
/// Session-scoped membership for clients that have entered a mission and have not left it yet.
/// </summary>
public interface IMissionMembershipRegistry
{
    bool IsControllerInMission(string controllerId);

    bool IsInstanceOccupied(string instanceId);
}

public interface IRoomMissionFactsView : IMissionMembershipRegistry
{
    bool TryCapture(out Common.Network.Session.RoomMissionFactsSnapshot snapshot);
    bool HasApplied(Common.Network.Session.RoomMissionFactsVersion version);
    bool CanActivateCampaign(Common.Network.Session.RoomMissionFactsVersion version);
    event System.Action FactsChanged;
}
