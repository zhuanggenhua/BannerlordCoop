using System;
using System.Collections.Generic;
using System.Linq;

namespace Coop.Core.Common.Session;

public enum CampaignSimulationAvailability
{
    Unknown,
    Unavailable,
    MapReady
}

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
    private readonly ISessionIdentity sessionIdentity;
    private readonly ICampaignAuthorityCandidateSelector candidateSelector;

    public CampaignAuthorityAvailabilityCoordinator(
        ISessionIdentity sessionIdentity,
        ICampaignAuthorityCandidateSelector candidateSelector)
    {
        if (sessionIdentity == null) throw new ArgumentNullException(nameof(sessionIdentity));
        if (candidateSelector == null) throw new ArgumentNullException(nameof(candidateSelector));

        this.sessionIdentity = sessionIdentity;
        this.candidateSelector = candidateSelector;
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
                if (entry.Revision == current.Revision) return entry.Equals(current);
            }

            entries[entry.ControllerId] = entry;
            return true;
        }
    }

    public bool Remove(string controllerId)
    {
        if (string.IsNullOrWhiteSpace(controllerId)) return false;
        lock (gate) return entries.Remove(controllerId);
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

    public CampaignAuthoritySelection Select() =>
        candidateSelector.Select(sessionIdentity.RoomHostControllerId, Snapshot());
}
