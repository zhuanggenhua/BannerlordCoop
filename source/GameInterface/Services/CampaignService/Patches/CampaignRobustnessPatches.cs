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
