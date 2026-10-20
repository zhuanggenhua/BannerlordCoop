#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.LiveTesting.MovementLatency;

public interface IMovementLatencyProbe : IDisposable
{
    MovementLatencyPage Begin(string label);
    MovementLatencyPage Read(long fromSeq, int maxRows);
    MovementLatencyPage Stop();
}

internal interface IMovementLatencyHooks
{
    void Start(MovementLatencyProbe probe);
    void Stop();
}

/// <summary>Observation only. Never applies a game command, ticks the world or allocates a game object.</summary>
internal sealed class MovementLatencyProbe : IMovementLatencyProbe
{
    public const int DefaultCapacity = 8192;
    private readonly object gate = new object();
    private readonly IMovementLatencyEnvironment environment;
    private readonly IMovementLatencyHooks hooks;
    private readonly int capacity;
    private readonly List<MovementLatencyRow> rows = new();
    private readonly HashSet<string> usedLabels = new(StringComparer.Ordinal);
    private readonly Dictionary<MobileParty, long> observations = new();
    private MovementLatencyActor[] actors = Array.Empty<MovementLatencyActor>();
    private object campaignToken;
    private string campaign, label, fault, stopReason;
    private long dropped, nextObservation;
    private long? epoch;
    private bool armed, disposed;

    public MovementLatencyProbe(IMovementLatencyEnvironment environment, IMovementLatencyHooks hooks)
        : this(environment, hooks, DefaultCapacity) { }
    internal MovementLatencyProbe(IMovementLatencyEnvironment environment, IMovementLatencyHooks hooks, int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.environment = environment;
        this.hooks = hooks;
        this.capacity = capacity;
    }

    public MovementLatencyPage Begin(string newLabel)
    {
        lock (gate)
        {
            ThrowDisposed();
            if (armed) throw new InvalidOperationException("A probe is already armed; stop and retain its pages first.");
            if (string.IsNullOrWhiteSpace(newLabel) || newLabel.Length > 80 ||
                newLabel.Any(c => !(char.IsLetterOrDigit(c) || c == '-' || c == '_')))
                throw new ArgumentException("Label must contain 1..80 letters, digits, '_' or '-'.", nameof(newLabel));
            if (usedLabels.Contains(newLabel)) throw new InvalidOperationException("A probe label cannot be reused.");
            if (usedLabels.Count >= 128) throw new InvalidOperationException("Session probe label limit reached.");
            if (!environment.TryBind(out string boundCampaign, out var boundActors, out string error))
                throw new InvalidOperationException(error);
            if (environment.CampaignToken == null || boundActors?.Length != 2 ||
                boundActors.Any(a => a?.Party == null || string.IsNullOrEmpty(a.Controller) || string.IsNullOrEmpty(a.Id) || a.Handle == 0) ||
                boundActors.Select(a => a.Party).Distinct().Count() != 2)
                throw new InvalidOperationException("Invalid two-player probe binding.");
            campaignToken = environment.CampaignToken;
            campaign = boundCampaign;
            actors = boundActors;
            label = newLabel;
            rows.Clear(); observations.Clear(); dropped = 0; nextObservation = 0; epoch = null; fault = null; stopReason = null;
            try
            {
                hooks.Start(this);
                usedLabels.Add(label);
                armed = true;
                foreach (var actor in actors) Append(actor, "begin");
            }
            catch
            {
                armed = false;
                hooks.Stop(); actors = Array.Empty<MovementLatencyActor>();
                throw;
            }
            return Page(0, 256);
        }
    }

    public MovementLatencyPage Read(long fromSeq, int maxRows)
    {
        lock (gate)
        {
            ThrowDisposed(); CheckCampaign();
            if (fromSeq < 0 || fromSeq > rows.Count) throw new ArgumentOutOfRangeException(nameof(fromSeq));
            if (maxRows < 1 || maxRows > 256) throw new ArgumentOutOfRangeException(nameof(maxRows));
            return Page(fromSeq, maxRows);
        }
    }

    public MovementLatencyPage Stop()
    {
        lock (gate) { ThrowDisposed(); StopCore("requested"); return Page(0, 256); }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            StopCore("disposed"); disposed = true;
            actors = Array.Empty<MovementLatencyActor>(); observations.Clear(); campaignToken = null;
        }
    }

    internal bool Tracks(MobileParty party)
    {
        lock (gate) return armed && actors.Any(a => ReferenceEquals(a.Party, party));
    }

    internal long InputEnter(MobileParty party, float requestedX, float requestedY)
    {
        lock (gate)
        {
            var actor = Find(party); if (actor == null) return 0;
            observations[party] = ++nextObservation;
            Append(actor, "input_enter", r => { r.RequestedX = requestedX; r.RequestedY = requestedY; });
            return nextObservation;
        }
    }

    internal void InputExit(MobileParty party, long observationId)
    {
        lock (gate)
        {
            var actor = Find(party); if (actor == null || observationId == 0) return;
            Append(actor, "input_exit", r => r.ObservationId = observationId);
        }
    }

    internal long ApplyEnter(MobileParty party, string originController, float targetX, float targetY)
    {
        lock (gate)
        {
            var actor = Find(party); if (actor == null) return 0;
            // IDs are local observations, never represented as an end-to-end wire command ID.
            long id = ++nextObservation;
            if (!observations.ContainsKey(party)) observations[party] = id;
            Append(actor, "apply_enter", r =>
            { r.ObservationId = id; r.OriginController = originController; r.RequestedX = targetX; r.RequestedY = targetY; });
            return id;
        }
    }

    internal void ApplyExit(MobileParty party, long id, bool applied)
    {
        lock (gate)
        {
            var actor = Find(party); if (actor == null || id == 0) return;
            Append(actor, "apply_exit", r => { r.ObservationId = id; r.Applied = applied; });
            if (!applied) fault ??= "snapshot_apply_rejected";
        }
    }

    internal void Observe(MobileParty party, string kind, float? visualX = null, float? visualY = null)
    {
        lock (gate)
        {
            var actor = Find(party); if (actor == null) return;
            Append(actor, kind, r => { r.VisualX = visualX; r.VisualY = visualY; });
        }
    }

    internal void Frame()
    {
        lock (gate)
        {
            if (!armed || !CheckCampaign()) return;
            foreach (var actor in actors) Append(actor, "model_frame");
        }
    }

    internal void Fail(Exception exception)
    {
        lock (gate) fault ??= exception.GetType().FullName + ": " + exception.Message;
    }

    private MovementLatencyActor Find(MobileParty party) =>
        armed && CheckCampaign() ? actors.FirstOrDefault(a => ReferenceEquals(a.Party, party)) : null;

    private bool CheckCampaign()
    {
        if (!armed) return false;
        if (ReferenceEquals(campaignToken, environment.CampaignToken)) return true;
        fault ??= "campaign_changed";
        StopCore("campaign_changed");
        actors = Array.Empty<MovementLatencyActor>(); observations.Clear(); campaignToken = null;
        return false;
    }

    private void Append(MovementLatencyActor actor, string kind, Action<MovementLatencyRow> detail = null)
    {
        if (rows.Count == capacity) { dropped++; return; }
        observations.TryGetValue(actor.Party, out long id);
        var row = environment.Sample(actor, kind, id);
        if (row.Campaign != campaign) { fault ??= "campaign_identity_changed"; return; }
        if (epoch.HasValue && epoch.Value != row.Epoch) fault ??= "epoch_changed";
        epoch ??= row.Epoch;
        if (!Finite(row.X) || !Finite(row.Y) || !Finite(row.TargetX) || !Finite(row.TargetY)) fault ??= "nonfinite_position";
        row.Seq = rows.Count;
        detail?.Invoke(row);
        if ((row.VisualX.HasValue && !Finite(row.VisualX.Value)) || (row.VisualY.HasValue && !Finite(row.VisualY.Value))) fault ??= "nonfinite_visual";
        rows.Add(row);
    }

    private MovementLatencyPage Page(long from, int max)
    {
        var page = rows.Skip((int)from).Take(max).ToArray();
        long next = from + page.Length;
        return new MovementLatencyPage
        {
            Label = label, Armed = armed, Complete = !armed && label != null && fault == null && dropped == 0,
            Overflow = dropped > 0, DroppedRows = dropped, Fault = fault, StopReason = stopReason,
            Capacity = capacity, NextSeq = rows.Count, NextFromSeq = next, HasMore = next < rows.Count,
            Clock = environment.ReadClock(), Rows = page,
        };
    }

    private void StopCore(string reason)
    {
        if (!armed) return;
        armed = false; stopReason = reason;
        hooks.Stop();
    }
    private void ThrowDisposed() { if (disposed) throw new ObjectDisposedException(nameof(MovementLatencyProbe)); }
    private static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
}
#endif
