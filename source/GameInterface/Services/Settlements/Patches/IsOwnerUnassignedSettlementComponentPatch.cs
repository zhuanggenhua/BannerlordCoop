using Common;
using Common.Logging;
using Common.Messaging;
using Common.Util;
using GameInterface.Policies;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Settlements.Messages;
using HarmonyLib;
using Serilog;
using TaleWorlds.CampaignSystem.Settlements;

namespace GameInterface.Services.Settlements.Patches
{
    /// <summary>
    /// Patch for <see cref="SettlementComponent.IsOwnerUnassigned"/>
    /// </summary>
    [HarmonyPatch(typeof(SettlementComponent))]
    public static class IsOwnerUnassignedSettlementComponentPatch
    {
        private static ILogger Logger = LogManager.GetLogger<SettlementComponent>();
        [HarmonyPatch(nameof(SettlementComponent.IsOwnerUnassigned), MethodType.Setter)]
        public static bool Prefix(SettlementComponent __instance, bool value, out bool __state) =>
            OwnerSettlementComponentPatch.CanAssignNativeProperty(__instance, nameof(SettlementComponent.IsOwnerUnassigned), out __state);

        [HarmonyPatch(nameof(SettlementComponent.IsOwnerUnassigned), MethodType.Setter)]
        [HarmonyPostfix]
        public static void Postfix(SettlementComponent __instance, bool __state, bool __runOriginal)
        {
            if (!__runOriginal || !__state || !CampaignSimulationAuthorityPolicy.CanPublishAppliedWorldResult()) return;
            MessageBroker.Instance.Publish(__instance, new SettlementComponentIsOwnerUnassignedChanged(__instance, __instance.IsOwnerUnassigned));
        }

        internal static void RunSettlementComponentIsOwnerUnassignedChanged(SettlementComponent settlementComp, bool value)
        {
            GameThread.Run(() =>
            {
                using var replay = CampaignExecutionScope.Enter(CampaignExecutionPhase.Replay);
                using (new AllowedThread())
                {
                    settlementComp.IsOwnerUnassigned = value;
                }
            });
        }
    }
}
