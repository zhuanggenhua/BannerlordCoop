using System;
using System.Collections.Generic;
using System.Linq;
using Common.Session;

namespace Coop.Core.Common.Session;

public readonly struct CampaignAvailabilityEntry : IEquatable<CampaignAvailabilityEntry>
{
    public string ControllerId { get; }
    public CampaignSimulationAvailability Availability { get; }
    public long Revision { get; }

    public CampaignAvailabilityEntry(
        string controllerId,
        CampaignSimulationAvailability availability,
        long revision)
    {
        ControllerId = controllerId ?? string.Empty;
        Availability = availability;
        Revision = revision;
    }

    public bool IsValid => !string.IsNullOrWhiteSpace(ControllerId) &&
        Enum.IsDefined(typeof(CampaignSimulationAvailability), Availability) &&
        Revision > 0;

    public bool Equals(CampaignAvailabilityEntry other) =>
        string.Equals(ControllerId, other.ControllerId, StringComparison.Ordinal) &&
        Availability == other.Availability &&
        Revision == other.Revision;

    public override bool Equals(object obj) => obj is CampaignAvailabilityEntry other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = StringComparer.Ordinal.GetHashCode(ControllerId ?? string.Empty);
            hash = (hash * 397) ^ (int)Availability;
            return (hash * 397) ^ Revision.GetHashCode();
        }
    }
}

public interface ICampaignAuthorityAvailabilityCoordinator
{
    bool Report(string controllerId, CampaignSimulationAvailability availability, long revision);
    bool Remove(string controllerId);
    CampaignAvailabilityEntry[] Snapshot();
    CampaignAuthoritySelection Select();
}

public sealed class CampaignAuthorityAvailabilityCoordinator : ICampaignAuthorityAvailabilityCoordinator
{
    private readonly object gate = new object();
    private readonly Dictionary<string, CampaignAvailabilityEntry> entries =
        new Dictionary<string, CampaignAvailabilityEntry>(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> reportTimesUtc =
        new Dictionary<string, DateTime>(StringComparer.Ordinal);
    private readonly ISessionIdentity sessionIdentity;
    private readonly ICampaignAuthorityCandidateSelector candidateSelector;
    private readonly Func<DateTime> utcNow;
    private readonly TimeSpan reportTimeout;

    public CampaignAuthorityAvailabilityCoordinator(
        ISessionIdentity sessionIdentity,
        ICampaignAuthorityCandidateSelector candidateSelector)
        : this(sessionIdentity, candidateSelector, () => DateTime.UtcNow, TimeSpan.FromSeconds(3))
    {
    }

    internal CampaignAuthorityAvailabilityCoordinator(
        ISessionIdentity sessionIdentity,
        ICampaignAuthorityCandidateSelector candidateSelector,
        Func<DateTime> utcNow,
        TimeSpan reportTimeout)
    {
        if (sessionIdentity == null) throw new ArgumentNullException(nameof(sessionIdentity));
        if (candidateSelector == null) throw new ArgumentNullException(nameof(candidateSelector));
        if (utcNow == null) throw new ArgumentNullException(nameof(utcNow));
        if (reportTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(reportTimeout));

        this.sessionIdentity = sessionIdentity;
        this.candidateSelector = candidateSelector;
        this.utcNow = utcNow;
        this.reportTimeout = reportTimeout;
    }

    public bool Report(string controllerId, CampaignSimulationAvailability availability, long revision)
    {
        var entry = new CampaignAvailabilityEntry(controllerId, availability, revision);
        if (!entry.IsValid) return false;

        lock (gate)
        {
            if (entries.TryGetValue(entry.ControllerId, out var current))
            {
                if (entry.Revision < current.Revision) return false;
                if (entry.Revision == current.Revision)
                {
                    if (!entry.Equals(current)) return false;
                    reportTimesUtc[entry.ControllerId] = utcNow();
                    return true;
                }
            }

            entries[entry.ControllerId] = entry;
            reportTimesUtc[entry.ControllerId] = utcNow();
            return true;
        }
    }

    public bool Remove(string controllerId)
    {
        if (string.IsNullOrWhiteSpace(controllerId)) return false;
        lock (gate)
        {
            reportTimesUtc.Remove(controllerId);
            return entries.Remove(controllerId);
        }
    }

    public CampaignAvailabilityEntry[] Snapshot()
    {
        lock (gate)
        {
            return entries.Values
                .OrderBy(entry => entry.ControllerId, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public CampaignAuthoritySelection Select()
    {
        var now = utcNow();
        CampaignAvailabilityEntry[] freshEntries;

        lock (gate)
        {
            freshEntries = entries.Values
                .Where(entry => reportTimesUtc.TryGetValue(entry.ControllerId, out var reportedAtUtc) &&
                    now >= reportedAtUtc && now - reportedAtUtc <= reportTimeout)
                .ToArray();
        }

        return candidateSelector.Select(sessionIdentity.RoomHostControllerId, freshEntries);
    }
}
