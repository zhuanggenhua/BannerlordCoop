using GameInterface.Services.Settlements.Interfaces;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.MobileParties;

public interface ICampaignSettlementLeave : IGameAbstraction
{
    bool Apply(MobileParty party);
}

internal sealed class CampaignSettlementLeave : ICampaignSettlementLeave
{
    private readonly ISettlementInterface settlements;

    public CampaignSettlementLeave(ISettlementInterface settlements) => this.settlements = settlements;

    public bool Apply(MobileParty party)
    {
        if (party.CurrentSettlement == null || party.Party?.MapEvent != null) return false;
        party.Position = party.CurrentSettlement.GatePosition;
        party.SetMoveModeHold();
        settlements.PartyLeaveSettlement(party);
        party.SetMoveModeHold();
        return party.CurrentSettlement == null;
    }
}
