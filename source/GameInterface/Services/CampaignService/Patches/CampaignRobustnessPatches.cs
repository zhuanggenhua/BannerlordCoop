using Common.Logging;
using HarmonyLib;
using Common;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using Serilog;
using System;
using TaleWorlds.CampaignSystem;

namespace GameInterface.Services.CampaignService.Patches;

[HarmonyPatch(typeof(Campaign))]
internal class CampaignRobustnessPatches
{
    private static readonly ILogger Logger = LogManager.GetLogger<CampaignRobustnessPatches>();

    [HarmonyPatch(nameof(Campaign.RealTick))]
    [HarmonyPrefix]
    internal static bool Prefix_RealTick(Campaign __instance, float realDt)
    {
        if (!CampaignSimulationAuthorityPolicy.CanEnterWorldCallback()) return false;

        bool hasAuthorityContext = global::GameInterface.ContainerProvider.TryResolve<
            global::GameInterface.Services.CampaignAuthority.ICampaignAuthorityContext>(out var authorityContext);
        if (!hasAuthorityContext)
            return true;

        bool localAuthority = authorityContext.TryGetLocalAuthorityLease(out var lease);
        bool authorityKnown = lease.IsValid;
        if (ShouldRunCampaignTick(authorityKnown, localAuthority)) return true;

        if (!lease.IsPaused && ContainerProvider.TryResolve<IControllerIdProvider>(out var controller) &&
            !string.Equals(controller.ControllerId, lease.AuthorityControllerId, StringComparison.Ordinal) &&
            ContainerProvider.TryResolve<ICampaignObserverFrame>(out var observerFrame))
            observerFrame.Tick(__instance, realDt);
        return false;
    }

    [HarmonyPatch(nameof(Campaign.Tick))]
    [HarmonyPrefix]
    internal static bool Prefix_Tick() => CampaignSimulationAuthorityPolicy.CanRunWorldSimulation();

    internal static bool ShouldRunCampaignTick(bool authorityKnown, bool localAuthority) =>
        !authorityKnown || localAuthority;

    [HarmonyPatch(nameof(Campaign.RealTick))]
    [HarmonyFinalizer]
    internal static Exception Finalizer_RealTick(Exception __exception)
    {
        if (__exception != null)
        {
#if DEBUG
            if (Common.ModInformation.IsListenAuthority)
                Time.ListenAuthorityTickEvidence.RecordFailure();
#endif
            Logger.Error(__exception, "Failed to run {Method}", $"Campaign.RealTick");
        }

        // Every current campaign authority owns a native simulation world, including a remote
        // replica promoted by lease. Preserve its failures instead of silently continuing.
        bool isLocalAuthority = !Common.ModInformation.IsListenAuthority &&
            global::GameInterface.ContainerProvider.TryResolve<
                global::GameInterface.Services.CampaignAuthority.ICampaignAuthorityContext>(out var authorityContext) &&
            authorityContext.IsLocalCampaignAuthority;
        return ShouldPreserveRealTickException(
            Common.ModInformation.IsListenAuthority,
            isLocalAuthority)
            ? __exception
            : null;
    }

    internal static bool ShouldPreserveRealTickException(bool isListenAuthority, bool isLocalAuthority) =>
        isListenAuthority || isLocalAuthority;
}
