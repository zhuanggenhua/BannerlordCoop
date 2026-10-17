using Common;
using Common.Logging;
using GameInterface.Services.CampaignAuthority;
using HarmonyLib;
using Serilog;
using TaleWorlds.CampaignSystem.Actions;

namespace GameInterface.Services.Actions.Patches;

[HarmonyPatch(typeof(RemoveCompanionAction))]
internal class RemoveCompanionActionPatches
{
    private static readonly ILogger Logger = LogManager.GetLogger<RemoveCompanionActionPatches>();

    [HarmonyPatch(nameof(RemoveCompanionAction.ApplyInternal))]
    internal static bool Prefix() => CampaignSimulationAuthorityPolicy.CanRunWorldSimulation();
}
