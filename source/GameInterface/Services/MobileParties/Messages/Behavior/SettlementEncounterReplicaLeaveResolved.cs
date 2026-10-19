using Common;
using Common.Messaging;
using Common.Network.MobileParties;

namespace GameInterface.Services.MobileParties.Messages.Behavior;

public enum SettlementEncounterReplicaLeaveOutcome
{
    Pending,
    Applied,
    Rejected,
}

/// <summary>
/// Local presentation signal correlating the observer leave UI with its exact authority request.
/// </summary>
public readonly struct SettlementEncounterReplicaLeaveResolved : IEvent
{
    public readonly CampaignSettlementLeaveRequest Request;
    public readonly SettlementEncounterReplicaLeaveOutcome Outcome;

    public SettlementEncounterReplicaLeaveResolved(
        CampaignSettlementLeaveRequest request,
        SettlementEncounterReplicaLeaveOutcome outcome)
    {
        Request = request;
        Outcome = outcome;
    }
}
