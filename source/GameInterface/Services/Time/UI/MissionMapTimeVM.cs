using GameInterface.Services.Heroes.Enum;
using System;
using TaleWorlds.Library;

namespace GameInterface.Services.Time.UI;

/// <summary>
/// Provides the campaign map time control state displayed by the mission map time overlay.
/// </summary>
public sealed class MissionMapTimeVM : ViewModel
{
    private string mapTimeText;
    private TimeControlEnum selectedMode;
    private bool simulationSuspended;

    [DataSourceProperty]
    public string MapTimeText
    {
        get =>  mapTimeText;
        private set
        {
            if (mapTimeText == value) return;
            mapTimeText = value;
            OnPropertyChanged(nameof(MapTimeText));
        }
    }

    public MissionMapTimeVM(TimeControlEnum initialMode)
    {
        SetTimeControlMode(initialMode);
    }

    public void SetTimeControlMode(TimeControlEnum mode)
    {
        if (selectedMode == mode && mapTimeText != null) return;
        selectedMode = mode;
        RefreshTimeText();
    }

    public void SetSimulationSuspended(bool suspended)
    {
        if (simulationSuspended == suspended) return;
        simulationSuspended = suspended;
        RefreshTimeText();
    }

    private void RefreshTimeText()
    {
        var displayedMode = simulationSuspended ? TimeControlEnum.Pause : selectedMode;
        MapTimeText = $"Map Time: {GetModeText(displayedMode)}";
    }
    
    internal static string GetModeText(TimeControlEnum mode)
    {
        return mode switch
        {
            TimeControlEnum.Pause => "Paused",
            TimeControlEnum.Play_1x => "Normal",
            TimeControlEnum.Play_2x => "Fast Forward",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }
}