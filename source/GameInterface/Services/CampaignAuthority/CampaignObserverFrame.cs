using TaleWorlds.CampaignSystem;

using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.CampaignAuthority;

public interface ICampaignObserverFrame
{
    void Tick(Campaign campaign, float realDt);
}

/// <summary>Advances paced map presentation without campaign events, encounters, or siege simulation.</summary>
internal sealed class CampaignObserverFrame : ICampaignObserverFrame
{
    public void Tick(Campaign campaign, float realDt)
    {
        campaign.WaitAsyncTasks();
        campaign.CheckMainPartyNeedsUpdate();
        campaign.TickMapTime(realDt);
        if (!campaign.GameStarted)
        {
            campaign.GameStarted = true;
            campaign._tickData.InitializeDataCache();
        }
        campaign._tickData.RealTick(campaign._dt, realDt);
        var party = MobileParty.MainParty;
        if (party?.Ai != null && CampaignSimulationAuthorityPolicy.CanTickParty(party))
            party.Ai.Tick(campaign._dt);
    }
}
