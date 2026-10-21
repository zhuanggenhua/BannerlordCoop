using Common;
using Common.Logging;
using Common.Messaging;
using Common.Util;
using GameInterface.Policies;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Settlements.Messages;
using HarmonyLib;
using Serilog;
using System.Diagnostics;
using System.Threading;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace GameInterface.Services.Settlements.Patches
{
    /// <summary>
    /// Patch for <see cref="SettlementComponent.Owner"/>
    /// </summary>
    [HarmonyPatch(typeof(SettlementComponent))]
    public static class OwnerSettlementComponentPatch
    {
        private static ILogger Logger = LogManager.GetLogger<SettlementComponent>();
        private static int rejectedCallerSamples;
        [HarmonyPatch(nameof(SettlementComponent.Owner), MethodType.Setter)]
        public static bool Prefix(SettlementComponent __instance, PartyBase value, out bool __state) =>
            CanAssignNativeProperty(__instance, nameof(SettlementComponent.Owner), out __state);

        internal static bool CanAssignNativeProperty(SettlementComponent __instance, string property, out bool __state)
        {
            __state = false;
            if (CallOriginalPolicy.IsOriginalAllowed()) return true;

            string componentId = null;
            bool registered = ContainerProvider.TryResolve<IObjectManager>(out var objects) &&
                objects.TryGetId(__instance, out componentId);
            if (CampaignSimulationAuthorityPolicy.CanInitializeUnregisteredNativeObject(registered)) return true;

            if (!CampaignSimulationAuthorityPolicy.CanRunWorldSimulation())
            {
                string caller = Interlocked.Increment(ref rejectedCallerSamples) <= 4
                    ? new StackTrace(1, false).ToString() : "caller sample limit reached";
                if (caller.Length > 4096) caller = caller.Substring(0, 4096);
                ContainerProvider.TryResolve<ICampaignAuthorityContext>(out var context);
                Logger.Error("Rejected native SettlementComponent.{Property} assignment phase {Phase} component {ComponentId} authorityKnown {Known} localAuthority {LocalAuthority} caller {Caller}",
                    property, CampaignExecutionScope.Current, componentId, context?.IsAuthorityKnown,
                    context?.IsLocalCampaignAuthority, caller);
                return false;
            }

            __state = true;
            return true;
        }

        [HarmonyPatch(nameof(SettlementComponent.Owner), MethodType.Setter)]
        [HarmonyPostfix]
        public static void Postfix(SettlementComponent __instance, bool __state, bool __runOriginal)
        {
            if (!__runOriginal || !__state || !CampaignSimulationAuthorityPolicy.CanPublishAppliedWorldResult()) return;
            MessageBroker.Instance.Publish(__instance, new SettlementComponentOwnerChanged(__instance, __instance.Owner?.Id));
        }

        internal static void RunSettlementComponentOwnerChanged(SettlementComponent settlementComp, PartyBase owner)
        {
            GameThread.Run(() =>
            {
                using var replay = CampaignExecutionScope.Enter(CampaignExecutionPhase.Replay);
                using (new AllowedThread())
                {
                    settlementComp.Owner = owner;
                }
            });
        }
    }
}
