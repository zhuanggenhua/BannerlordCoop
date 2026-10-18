using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Common;

[ProtoContract]
public sealed class CampaignSchedulerCheckpoint
{
    [ProtoMember(1)] public long LastGameTimeTicks { get; private set; }
    [ProtoMember(2)] public long MinimumIntervalTicks { get; private set; }
    [ProtoMember(3)] public int CurrentTickCount { get; private set; }
    [ProtoMember(4)] public CampaignPeriodicTickerState[] Tickers { get; private set; } = Array.Empty<CampaignPeriodicTickerState>();
    [ProtoMember(5)] public CampaignPeriodicEventState[] Events { get; private set; } = Array.Empty<CampaignPeriodicEventState>();

    private CampaignSchedulerCheckpoint() { }
    public CampaignSchedulerCheckpoint(long lastGameTimeTicks, long minimumIntervalTicks, int currentTickCount,
        IEnumerable<CampaignPeriodicTickerState> tickers, IEnumerable<CampaignPeriodicEventState> events)
    {
        LastGameTimeTicks = lastGameTimeTicks;
        MinimumIntervalTicks = minimumIntervalTicks;
        CurrentTickCount = currentTickCount;
        Tickers = tickers.ToArray();
        Events = events.ToArray();
    }
}

[ProtoContract]
public sealed class CampaignPeriodicTickerState
{
    [ProtoMember(1)] public string Name { get; private set; }
    [ProtoMember(2)] public int Index { get; private set; }
    [ProtoMember(3)] public double TickDebt { get; private set; }
    [ProtoMember(4)] public uint[] OrderedHandles { get; private set; } = Array.Empty<uint>();

    private CampaignPeriodicTickerState() { }
    public CampaignPeriodicTickerState(string name, int index, double tickDebt, IEnumerable<uint> orderedHandles)
    {
        Name = name;
        Index = index;
        TickDebt = tickDebt;
        OrderedHandles = orderedHandles.ToArray();
    }
}

[ProtoContract]
public sealed class CampaignPeriodicEventState
{
    [ProtoMember(1)] public string Description { get; private set; }
    [ProtoMember(2)] public long TriggerPeriodTicks { get; private set; }
    [ProtoMember(3)] public long InitialWaitTicks { get; private set; }
    [ProtoMember(4)] public long NextTriggerTicks { get; private set; }
    [ProtoMember(5)] public bool IsDeleted { get; private set; }

    private CampaignPeriodicEventState() { }
    public CampaignPeriodicEventState(string description, long triggerPeriodTicks, long initialWaitTicks,
        long nextTriggerTicks, bool isDeleted)
    {
        Description = description;
        TriggerPeriodTicks = triggerPeriodTicks;
        InitialWaitTicks = initialWaitTicks;
        NextTriggerTicks = nextTriggerTicks;
        IsDeleted = isDeleted;
    }
}
