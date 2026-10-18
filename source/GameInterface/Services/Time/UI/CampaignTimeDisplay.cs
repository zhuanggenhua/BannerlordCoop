using GameInterface.Services.Heroes.Enum;
using GameInterface.Services.Time.Interfaces;

namespace GameInterface.Services.Time.UI;

/// <summary>Projects a confirmed pause or local handoff freeze onto UI without changing the selected speed.</summary>
internal static class CampaignTimeDisplay
{
    internal static bool IsSimulationSuspended()
    {
        ContainerProvider.TryResolve<ISessionIdentity>(out var identity);
        ContainerProvider.TryResolve<IMapTimeTrackerInterface>(out var clock);
        return IsSimulationSuspended(identity, clock);
    }

    internal static bool IsSimulationSuspended(ISessionIdentity identity, IMapTimeTrackerInterface clock = null)
    {
        return identity != null && identity.TryGetConfirmedLease(out var lease) &&
            (lease.IsPaused || identity.IsLocallyFrozen(lease) || clock?.IsObserverClockSuspended == true);
    }

    internal static TimeControlEnum GetEffectiveMode(TimeControlEnum selectedMode, ISessionIdentity identity, IMapTimeTrackerInterface clock = null)
    {
        return IsSimulationSuspended(identity, clock) ? TimeControlEnum.Pause : selectedMode;
    }
}
