using GameInterface.Services.Time.Patches;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem.ViewModelCollection.Map.MapBar;
using Xunit;

namespace GameInterface.Tests.Services.Time.Patches;

public class MapTimeDisplayPatchesTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void SuspendedSimulationDisplaysPauseWithoutCallingNativeTimeInput(int selectedState)
    {
        // A constructor-free VM has no native time input callback; the display update must not need one.
        var viewModel = (MapTimeControlVM)RuntimeHelpers.GetUninitializedObject(typeof(MapTimeControlVM));
        viewModel.TimeFlowState = selectedState;
        MapTimeDisplayPatches.ApplyEffectivePause(viewModel, true);

        Assert.Equal(0, viewModel.TimeFlowState);
        Assert.False(viewModel.IsCurrentlyPausedOnMap); // Native panel visibility rules still apply.
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    public void RunningOrLegacySessionKeepsStateProducedByNativeTick(int nativeState)
    {
        var viewModel = (MapTimeControlVM)RuntimeHelpers.GetUninitializedObject(typeof(MapTimeControlVM));
        viewModel.TimeFlowState = nativeState;
        MapTimeDisplayPatches.ApplyEffectivePause(viewModel, false);

        Assert.Equal(nativeState, viewModel.TimeFlowState);
    }
}
