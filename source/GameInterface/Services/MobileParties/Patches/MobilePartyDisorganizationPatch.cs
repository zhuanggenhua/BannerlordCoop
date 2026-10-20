using Common;
using Common.Messaging;
using GameInterface.Policies;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.MobileParties.Messages;
using HarmonyLib;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.MobileParties.Patches;

[HarmonyPatch(typeof(MobileParty))]
internal class MobilePartyDisorganizationPatch
{
    [HarmonyPatch(nameof(MobileParty.CheckIsDisorganized))]
    [HarmonyPrefix]
    private static bool CheckPrefix() => CampaignSimulationAuthorityPolicy.CanRunWorldSimulation();

    [HarmonyPatch(nameof(MobileParty.SetDisorganized))]
    [HarmonyPrefix]
    private static bool SetPrefix() => CampaignSimulationAuthorityPolicy.CanRunWorldSimulation() || CallOriginalPolicy.IsOriginalAllowed();

    [HarmonyPatch(nameof(MobileParty.SetDisorganized))]
    [HarmonyPostfix]
    private static void SetPostfix(MobileParty __instance, bool __runOriginal)
    {
        if (!__runOriginal || !CampaignSimulationAuthorityPolicy.CanRunWorldSimulation() || CallOriginalPolicy.IsOriginalAllowed()) return;

        MessageBroker.Instance.Publish(__instance,
            new MobilePartyDisorganizationChanged(__instance, __instance.IsDisorganized));
    }
}
