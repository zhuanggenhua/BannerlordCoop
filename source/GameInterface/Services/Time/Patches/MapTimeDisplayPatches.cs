using GameInterface.Services.Time.UI;
using HarmonyLib;
using TaleWorlds.CampaignSystem.ViewModelCollection.Map.MapBar;

namespace GameInterface.Services.Time.Patches;

[HarmonyPatch(typeof(MapTimeControlVM), nameof(MapTimeControlVM.Tick))]
internal static class MapTimeDisplayPatches
{
    [HarmonyPostfix]
    private static void Postfix(MapTimeControlVM __instance)
    {
        ApplyEffectivePause(__instance, CampaignTimeDisplay.IsSimulationSuspended());
    }

    internal static void ApplyEffectivePause(MapTimeControlVM viewModel, bool simulationSuspended)
    {
        // Native Tick restores the selected speed each frame after the lease resumes.
        if (!simulationSuspended) return;

        viewModel.TimeFlowState = 0;
        viewModel.IsCurrentlyPausedOnMap = viewModel.IsCenterPanelEnabled &&
            !viewModel.IsEscapeMenuOpened && !viewModel._isSaving;
    }
}
