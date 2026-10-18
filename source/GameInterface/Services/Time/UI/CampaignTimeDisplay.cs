using GameInterface.Services.Heroes.Enum;

namespace GameInterface.Services.Time.UI;

/// <summary>Projects a confirmed pause or local handoff freeze onto UI without changing the selected speed.</summary>
internal static class CampaignTimeDisplay
{
    internal static bool IsSimulationSuspended()
    {
        return ContainerProvider.TryResolve<ISessionIdentity>(out var identity) &&
            IsSimulationSuspended(identity);
    }

    internal static bool IsSimulationSuspended(ISessionIdentity identity)
    {
        return identity != null && identity.TryGetConfirmedLease(out var lease) &&
            (lease.IsPaused || identity.IsLocallyFrozen(lease));
    }

    internal static TimeControlEnum GetEffectiveMode(TimeControlEnum selectedMode, ISessionIdentity identity)
    {
        return IsSimulationSuspended(identity) ? TimeControlEnum.Pause : selectedMode;
    }
}
