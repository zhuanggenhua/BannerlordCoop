using GameInterface.Services.CampaignAuthority;
using System.Collections.Generic;
using System.Reflection;
using GameInterface.Services.MobileParties.Extensions;
using HarmonyLib;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace GameInterface.Services.MobileParties.Patches.Disable;

[HarmonyPatch(typeof(PartiesBuyFoodCampaignBehavior))]
internal class DisablePartiesBuyFoodCampaignBehavior
{
    private static IEnumerable<MethodBase> TargetMethods() => new MethodBase[]
    {
        AccessTools.Method(typeof(PartiesBuyFoodCampaignBehavior), nameof(PartiesBuyFoodCampaignBehavior.HourlyTickParty)),
        AccessTools.Method(typeof(PartiesBuyFoodCampaignBehavior), nameof(PartiesBuyFoodCampaignBehavior.OnSettlementEntered))
    };

    internal static bool Prefix() => CampaignSimulationAuthorityPolicy.CanRunWorldSimulation();
}

[HarmonyPatch(typeof(PartiesBuyFoodCampaignBehavior))]
internal class PartiesBuyFoodCampaignBehaviorPatches
{
    [HarmonyPatch(nameof(PartiesBuyFoodCampaignBehavior.BuyFoodInternal))]
    [HarmonyPrefix]
    public static bool BuyFoodInternalPrefix(PartiesBuyFoodCampaignBehavior __instance, MobileParty mobileParty, Settlement settlement, int numberOfFoodItemsNeededToBuy)
    {
        return CampaignSimulationAuthorityPolicy.CanRunWorldSimulation() && mobileParty != null && !mobileParty.IsPlayerParty();
    }
}
