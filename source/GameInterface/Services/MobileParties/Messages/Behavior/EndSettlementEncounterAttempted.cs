using Common.Messaging;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.MobileParties.Messages.Behavior;

/// <summary>
/// Triggered when a player attempts to leave a settlement.
/// </summary>
public readonly struct EndSettlementEncounterAttempted : IEvent
{
    public readonly MobileParty Party;
    public readonly AuthorityLease ObservedLease;

    public EndSettlementEncounterAttempted(MobileParty party, AuthorityLease observedLease = default)
    {
        Party = party;
        ObservedLease = observedLease;
    }
}