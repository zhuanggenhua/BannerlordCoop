using GameInterface.Services.MobileParties.Extensions;
using HarmonyLib;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.MobileParties.Patches;

[HarmonyPatch(typeof(MobileParty), nameof(MobileParty.CheckExitingSettlementParallel))]
internal class PlayerPartySettlementExitPatch
{
    // Extend the native MainParty exemption to remote and parked players; explicit leave actions still run.
    [HarmonyPrefix]
    private static bool Prefix(MobileParty __instance) => !__instance.IsPlayerParty();
}