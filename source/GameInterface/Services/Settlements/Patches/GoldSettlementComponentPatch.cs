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
    /// Patch for <see cref="SettlementComponent.Gold"/>
    /// </summary>
    [HarmonyPatch(typeof(SettlementComponent))]
    public static class GoldSettlementComponentPatch
    {
        private static ILogger Logger = LogManager.GetLogger<SettlementComponent>();
        [HarmonyPatch(nameof(SettlementComponent.Gold), MethodType.Setter)]
        public static bool Prefix(SettlementComponent __instance, int value, out bool __state) =>
            OwnerSettlementComponentPatch.CanAssignNativeProperty(__instance, nameof(SettlementComponent.Gold), out __state);

        [HarmonyPatch(nameof(SettlementComponent.Gold), MethodType.Setter)]
        [HarmonyPostfix]
        public static void Postfix(SettlementComponent __instance, bool __state, bool __runOriginal)
        {
            if (!__runOriginal || !__state || !CampaignSimulationAuthorityPolicy.CanPublishAppliedWorldResult()) return;
            MessageBroker.Instance.Publish(__instance, new SettlementComponentGoldChanged(__instance, __instance.Gold));
        }

        internal static void RunSettlementComponentGoldChanged(SettlementComponent settlementComp, int gold)
        {
            GameThread.Run(() =>
            {
                using var replay = CampaignExecutionScope.Enter(CampaignExecutionPhase.Replay);
                using (new AllowedThread())
                {
                    settlementComp.Gold = gold;
                }
            });
        }
    }
}
