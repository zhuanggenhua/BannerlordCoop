using Common;
using Common.Logging;
using GameInterface.Services.CampaignAuthority;
using HarmonyLib;
using Serilog;
using TaleWorlds.CampaignSystem.Actions;

namespace GameInterface.Services.Actions.Patches;

[HarmonyPatch(typeof(GiveGoldAction))]
internal class GiveGoldActionPatches
{
    private static readonly ILogger Logger = LogManager.GetLogger<GiveGoldActionPatches>();

    [HarmonyPatch(nameof(GiveGoldAction.ApplyInternal))]
    internal static bool Prefix() => CampaignSimulationAuthorityPolicy.CanRunWorldSimulation();
}
