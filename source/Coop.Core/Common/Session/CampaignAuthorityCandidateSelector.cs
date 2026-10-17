using System;
using System.Linq;
using Common.Session;

namespace Coop.Core.Common.Session;

public interface ICampaignAuthorityCandidateSelector
{
    CampaignAuthoritySelection Select(string roomHostControllerId, CampaignAvailabilityEntry[] entries);
}

public readonly struct CampaignAuthoritySelection
{
    public string ControllerId { get; }
    public bool IsPaused { get; }

    private CampaignAuthoritySelection(string controllerId, bool isPaused)
    {
        ControllerId = controllerId ?? string.Empty;
        IsPaused = isPaused;
    }

    public static CampaignAuthoritySelection Paused => new CampaignAuthoritySelection(string.Empty, true);
    public static CampaignAuthoritySelection For(string controllerId) =>
        new CampaignAuthoritySelection(controllerId, false);
}

public sealed class CampaignAuthorityCandidateSelector : ICampaignAuthorityCandidateSelector
{
    public CampaignAuthoritySelection Select(string roomHostControllerId, CampaignAvailabilityEntry[] entries)
    {
        if (entries == null || entries.Length == 0) return CampaignAuthoritySelection.Paused;

        var readyControllers = entries
            .Where(entry => entry.IsValid && entry.Availability == CampaignSimulationAvailability.MapReady)
            .Select(entry => entry.ControllerId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(controllerId => controllerId, StringComparer.Ordinal)
            .ToArray();

        if (!string.IsNullOrEmpty(roomHostControllerId) &&
            readyControllers.Contains(roomHostControllerId, StringComparer.Ordinal))
            return CampaignAuthoritySelection.For(roomHostControllerId);

        return readyControllers.Length == 0
            ? CampaignAuthoritySelection.Paused
            : CampaignAuthoritySelection.For(readyControllers[0]);
    }
}
