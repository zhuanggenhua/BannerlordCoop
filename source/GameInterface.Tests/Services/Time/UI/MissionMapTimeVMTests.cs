using GameInterface.Services.Heroes.Enum;
using GameInterface.Services.Time.UI;
using Xunit;

namespace GameInterface.Tests.Services.Time.UI;

/// <summary>
/// Verifies the display text produced by "MissionMapTimeVM for campaign time control modes.
/// </summary>
public class MissionMapTimeVMTests
{
    [Theory]
    [InlineData(TimeControlEnum.Pause, "Map Time: Paused")]
    [InlineData(TimeControlEnum.Play_1x, "Map Time: Normal")]
    [InlineData(TimeControlEnum.Play_2x, "Map Time: Fast Forward")]
    public void ConstructorSetsExpectedText(TimeControlEnum mode, string expectedText)
    {
        var viewModel = new MissionMapTimeVM(mode);
        
        Assert.Equal(expectedText, viewModel.MapTimeText);
    }

    [Fact]
    public void SetTimeControlMode_UpdatesText()
    {
        var viewModel = new MissionMapTimeVM(TimeControlEnum.Pause);
        
        viewModel.SetTimeControlMode(TimeControlEnum.Play_2x);
        
        Assert.Equal("Map Time: Fast Forward", viewModel.MapTimeText);
    }
    [Theory]
    [InlineData(TimeControlEnum.Play_1x, "Map Time: Normal")]
    [InlineData(TimeControlEnum.Play_2x, "Map Time: Fast Forward")]
    public void SimulationPausePreservesSelectedModeForResume(TimeControlEnum mode, string resumedText)
    {
        var viewModel = new MissionMapTimeVM(mode);
        viewModel.SetSimulationSuspended(true);
        Assert.Equal("Map Time: Paused", viewModel.MapTimeText);

        viewModel.SetSimulationSuspended(false);
        Assert.Equal(resumedText, viewModel.MapTimeText);
    }

    [Fact]
    public void SpeedChangedWhileSuspendedIsShownOnlyAfterResume()
    {
        var viewModel = new MissionMapTimeVM(TimeControlEnum.Play_1x);
        viewModel.SetSimulationSuspended(true);
        viewModel.SetTimeControlMode(TimeControlEnum.Play_2x);
        Assert.Equal("Map Time: Paused", viewModel.MapTimeText);

        viewModel.SetSimulationSuspended(false);
        Assert.Equal("Map Time: Fast Forward", viewModel.MapTimeText);
    }

    [Fact]
    public void UserSelectedPauseRemainsPausedAfterSimulationResumes()
    {
        var viewModel = new MissionMapTimeVM(TimeControlEnum.Pause);
        viewModel.SetSimulationSuspended(true);
        viewModel.SetSimulationSuspended(false);

        Assert.Equal("Map Time: Paused", viewModel.MapTimeText);
    }
}