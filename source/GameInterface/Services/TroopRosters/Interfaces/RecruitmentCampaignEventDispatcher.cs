using Common;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;

namespace GameInterface.Services.TroopRosters.Interfaces;

public interface IRecruitmentCampaignEventDispatcher : IGameAbstraction
{
    void OnUnitRecruited(CharacterObject troop, int amount);
    void OnTroopRecruited(Hero recruiterHero, Settlement settlement, Hero troopSource, CharacterObject troop, int amount);
}

internal sealed class RecruitmentCampaignEventDispatcher : IRecruitmentCampaignEventDispatcher
{
    public void OnUnitRecruited(CharacterObject troop, int amount)
        => CampaignEventDispatcher.Instance.OnUnitRecruited(troop, amount);

    public void OnTroopRecruited(Hero recruiterHero, Settlement settlement, Hero troopSource, CharacterObject troop, int amount)
        => CampaignEventDispatcher.Instance.OnTroopRecruited(recruiterHero, settlement, troopSource, troop, amount);
}