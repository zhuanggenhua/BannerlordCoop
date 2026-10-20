using GameInterface.Services.CampaignAuthority;
using HarmonyLib;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.MobilePartyAIs.Patches;

[HarmonyPatch(typeof(MobilePartyAi))]
internal class MobilePartyAIDisablePatches
{
    [HarmonyPatch(nameof(MobilePartyAi.Tick))]
    [HarmonyPrefix]
    private static bool TickPrefix(MobilePartyAi __instance)
    {
        return IsTickAuthority(__instance._mobileParty);
    }

    internal static bool IsTickAuthority(MobileParty party) => CampaignSimulationAuthorityPolicy.CanTickParty(party);
}
