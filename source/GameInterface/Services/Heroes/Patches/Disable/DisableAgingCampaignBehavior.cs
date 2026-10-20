using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Clans.Extensions;
using GameInterface.Services.Heroes.Interfaces;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;

namespace GameInterface.Services.Heroes.Patches.Disable;

[HarmonyPatch(typeof(AgingCampaignBehavior))]
internal class AgingCampaignBehaviorPatches
{
    [HarmonyPatch(nameof(AgingCampaignBehavior.DailyTickHero))]
    [HarmonyPrefix]
    public static bool DailyTickHeroPrefix(AgingCampaignBehavior __instance, Hero hero)
    {
        if (!CampaignSimulationAuthorityPolicy.CanRunWorldSimulation()) return false;
        if (!ContainerProvider.TryResolve<IAgingCampaignBehaviorInterface>(out var agingBehaviorInterface)) return false;

        agingBehaviorInterface.DailyTickHero(__instance, hero);

        return false;
    }

    [HarmonyPatch(nameof(AgingCampaignBehavior.OnHeroComesOfAge))]
    [HarmonyPrefix]
    public static bool OnHeroComesOfAgePrefix(AgingCampaignBehavior __instance, Hero hero)
    {
        if (!CampaignSimulationAuthorityPolicy.CanRunWorldSimulation()) return false;
        if (!ContainerProvider.TryResolve<IAgingCampaignBehaviorInterface>(out var agingBehaviorInterface)) return false;

        agingBehaviorInterface.OnHeroComesOfAge(__instance, hero);

        return false;
    }

    [HarmonyPatch(nameof(AgingCampaignBehavior.OnHeroGrowsOutOfInfancy))]
    [HarmonyPrefix]
    public static bool OnHeroGrowsOutOfInfancyPrefix(AgingCampaignBehavior __instance, Hero hero)
    {
        return CampaignSimulationAuthorityPolicy.CanRunWorldSimulation() && hero.Clan?.IsPlayerClan() == false;
    }

    [HarmonyPatch(nameof(AgingCampaignBehavior.OnHeroReachesTeenAge))]
    [HarmonyPrefix]
    public static bool OnHeroReachesTeenAgePrefix(AgingCampaignBehavior __instance, Hero hero)
    {
        if (!CampaignSimulationAuthorityPolicy.CanRunWorldSimulation()) return false;
        if (hero.Clan?.IsPlayerClan() == true)
        {
            if (!ContainerProvider.TryResolve<IAgingCampaignBehaviorInterface>(out var agingBehaviorInterface)) return false;

            agingBehaviorInterface.OnPlayerClanHeroReachesTeenAge(__instance, hero);

            return false;
        }

        return true;
    }
}
