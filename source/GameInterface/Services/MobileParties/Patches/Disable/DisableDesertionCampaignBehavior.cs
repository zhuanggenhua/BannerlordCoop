using Common;
using GameInterface.Services.CampaignAuthority;
using HarmonyLib;
using TaleWorlds.CampaignSystem.CampaignBehaviors;

namespace GameInterface.Services.MobileParties.Patches.Disable;

[HarmonyPatch(typeof(DesertionCampaignBehavior))]
internal class DisableDesertionCampaignBehavior
{
    [HarmonyPatch(nameof(DesertionCampaignBehavior.RegisterEvents))]
    internal static bool Prefix() => true;
}

[HarmonyPatch(typeof(DesertionCampaignBehavior))]
internal class DesertionCampaignBehaviorPatches
{
    [HarmonyPatch(nameof(DesertionCampaignBehavior.DailyTickParty))]
    [HarmonyPrefix]
    internal static bool DailyTickPartyPrefix()
        => CampaignSimulationAuthorityPolicy.CanRunWorldSimulation();
}
