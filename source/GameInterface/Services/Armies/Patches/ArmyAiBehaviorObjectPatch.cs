using Common;
using Common.Messaging;
using GameInterface.Policies;
using GameInterface.Services.Armies.Messages;
using GameInterface.Services.CampaignAuthority;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Map;

namespace GameInterface.Services.Armies.Patches;

[HarmonyPatch(typeof(Army), "set_AiBehaviorObject")]
internal class ArmyAiBehaviorObjectPatch
{
    [HarmonyPrefix]
    static bool Prefix(Army __instance, IMapPoint value)
    {
        if (CallOriginalPolicy.IsOriginalAllowed()) return true;
        if (!CampaignSimulationAuthorityPolicy.CanRunWorldSimulation()) return false;

        var message = new ArmyAiBehaviorObjectChanged(__instance, value);
        MessageBroker.Instance.Publish(__instance, message);

        return true;
    }
}
