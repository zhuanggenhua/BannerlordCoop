using Common;
using GameInterface.Policies;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Clans.Extensions;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;

namespace GameInterface.Services.Heroes.Patches;

[HarmonyPatch(typeof(HeroSpawnCampaignBehavior))]
internal class HeroSpawnCampaignBehaviorPatches
{
    [HarmonyPatch(nameof(HeroSpawnCampaignBehavior.OnNewGameCreated))]
    [HarmonyPrefix]
    public static bool OnNewGameCreatedPrefix() => 
        ModInformation.IsServer || CallOriginalPolicy.IsOriginalAllowed();

    [HarmonyPatch(nameof(HeroSpawnCampaignBehavior.OnNewGameCreatedPartialFollowUp))]
    [HarmonyPrefix]
    public static bool OnNewGameCreatedPartialFollowUpPrefix() => 
        ModInformation.IsServer || CallOriginalPolicy.IsOriginalAllowed();

    [HarmonyPatch(nameof(HeroSpawnCampaignBehavior.OnNewGameCreatedPartialFollowUpEnd))]
    [HarmonyPrefix]
    public static bool OnNewGameCreatedPartialFollowUpEndPrefix() => 
        ModInformation.IsServer || CallOriginalPolicy.IsOriginalAllowed();

    [HarmonyPatch(nameof(HeroSpawnCampaignBehavior.OnGovernorChanged))]
    [HarmonyPrefix]
    public static bool OnGovernorChangedPrefix() => 
        CampaignSimulationAuthorityPolicy.CanRunWorldSimulation();

    [HarmonyPatch(nameof(HeroSpawnCampaignBehavior.OnNonBanditClanDailyTick))]
    [HarmonyPrefix]
    public static bool OnNonBanditClanDailyTickPrefix(Clan clan) =>
        CampaignSimulationAuthorityPolicy.CanRunWorldSimulation() && !clan.IsPlayerClan();

    [HarmonyPatch(nameof(HeroSpawnCampaignBehavior.OnHeroComesOfAge))]
    [HarmonyPrefix]
    public static bool OnHeroComesOfAgePrefix() => 
        CampaignSimulationAuthorityPolicy.CanRunWorldSimulation();

    [HarmonyPatch(nameof(HeroSpawnCampaignBehavior.OnHeroDailyTick))]
    [HarmonyPrefix]
    public static bool OnHeroDailyTickPrefix() => 
        CampaignSimulationAuthorityPolicy.CanRunWorldSimulation();

    [HarmonyPatch(nameof(HeroSpawnCampaignBehavior.OnCompanionRemoved))]
    [HarmonyPrefix]
    public static bool OnCompanionRemovedPrefix() => 
        CampaignSimulationAuthorityPolicy.CanRunWorldSimulation();

    [HarmonyPatch(nameof(HeroSpawnCampaignBehavior.TrySpawnHeroesAndParties))]
    [HarmonyPrefix]
    public static bool TrySpawnHeroesAndPartiesPrefix(Clan clan) =>
        clan == null || !clan.IsPlayerClan();

    [HarmonyPatch(nameof(HeroSpawnCampaignBehavior.CanHeroMoveToAnotherSettlement))]
    [HarmonyPrefix]
    public static bool CanHeroMoveToAnotherSettlementPrefix(Hero hero) =>
        hero?.Clan == null || !hero.Clan.IsPlayerClan();
}
