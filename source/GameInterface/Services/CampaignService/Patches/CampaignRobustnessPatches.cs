using Common.Logging;
using HarmonyLib;
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
    internal static bool Prefix_RealTick()
    {
        bool hasAuthorityContext = global::GameInterface.ContainerProvider.TryResolve<
            global::GameInterface.Services.CampaignAuthority.ICampaignAuthorityContext>(out var authorityContext);
        if (!hasAuthorityContext)
            return ShouldRunCampaignTick(Common.ModInformation.IsListenAuthority, authorityKnown: false, localAuthority: false);

        bool localAuthority = authorityContext.TryGetLocalAuthorityLease(out var lease);
        bool authorityKnown = lease.IsValid;
        return ShouldRunCampaignTick(Common.ModInformation.IsListenAuthority, authorityKnown, localAuthority);
    }

    internal static bool ShouldRunCampaignTick(bool isListenAuthority, bool authorityKnown, bool localAuthority)
    {
        // A ListenHost keeps the native path until its lease is confirmed, then only runs the tick while it owns it.
        return !isListenAuthority || !authorityKnown || localAuthority;
    }

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

        // A native local world must surface simulation failures instead of silently continuing.
        return Common.ModInformation.IsListenAuthority ? __exception : null;
    }
}
