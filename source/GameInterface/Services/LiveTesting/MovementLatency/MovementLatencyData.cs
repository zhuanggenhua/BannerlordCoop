#if DEBUG
using System;

namespace GameInterface.Services.LiveTesting.MovementLatency;

public sealed class MovementLatencyClock
{
    public int Pid { get; set; }
    public long StopwatchTicks { get; set; }
    public long Frequency { get; set; }
    public DateTime Utc { get; set; }
    public int EngineFrame { get; set; }
    public string Basis => "Stopwatch.GetTimestamp";
}

public sealed class MovementLatencyRow
{
    public long Seq { get; internal set; }
    public string Kind { get; set; }
    public long ObservationId { get; set; }
    public MovementLatencyClock Clock { get; set; }
    public string Campaign { get; set; }
    public long Epoch { get; set; }
    public string AuthorityController { get; set; }
    public bool Frozen { get; set; }
    public string LocalController { get; set; }
    public string ActorController { get; set; }
    public string OriginController { get; set; }
    public string PartyId { get; set; }
    public uint Handle { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float TargetX { get; set; }
    public float TargetY { get; set; }
    public float? RequestedX { get; set; }
    public float? RequestedY { get; set; }
    public float? VisualX { get; set; }
    public float? VisualY { get; set; }
    public string Mode { get; set; }
    public string Behavior { get; set; }
    public bool? Applied { get; set; }
}

public sealed class MovementLatencyPage
{
    public int SchemaVersion => 1;
    public string Label { get; internal set; }
    public bool Armed { get; internal set; }
    public bool Complete { get; internal set; }
    public bool Overflow { get; internal set; }
    public long DroppedRows { get; internal set; }
    public string Fault { get; internal set; }
    public string StopReason { get; internal set; }
    public int Capacity { get; internal set; }
    public long FirstSeq => 0;
    public long NextSeq { get; internal set; }
    public long NextFromSeq { get; internal set; }
    public bool HasMore { get; internal set; }
    // Deliberately no inferred source command identity or one-way latency.
    public string Association => "NOT_OBSERVED: local observation IDs only; origin/target/time-window correlation requires independent validation";
    public string ClockCalibration => "NOT_OBSERVED: bracket clock.stopwatchTicks with coordinator monotonic reads and retain uncertainty";
    public string VisualEvidence => "native visual frame ready, not physical display scanout";
    public MovementLatencyClock Clock { get; internal set; }
    public MovementLatencyRow[] Rows { get; internal set; }
}
#endif
