using Common;
using Common.Logging;
using GameInterface.Services.CampaignAuthority;
using HarmonyLib;
using Serilog;
using TaleWorlds.CampaignSystem.Actions;

namespace GameInterface.Services.Actions.Patches;

[HarmonyPatch(typeof(AddHeroToPartyAction))]
internal class AddHeroToPartyActionPatches
{
    private static readonly ILogger Logger = LogManager.GetLogger<AddHeroToPartyActionPatches>();

    [HarmonyPatch(nameof(AddHeroToPartyAction.ApplyInternal))]
    internal static bool Prefix() => CampaignSimulationAuthorityPolicy.CanRunWorldSimulation();
}
