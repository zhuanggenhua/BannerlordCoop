using Common;
using GameInterface.Services.CampaignAuthority;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using Xunit;
using Fixture = GameInterface.Tests.Services.CampaignAuthority.CampaignSchedulerCheckpointServiceTests.Fixture;

namespace GameInterface.Tests.Services.CampaignAuthority;

[Collection(ModInformationRoleCollection.Name)]
public class CampaignSchedulerTombstoneTests
{
    static CampaignSchedulerTombstoneTests() => RuntimeHelpers.RunModuleConstructor(typeof(Coop.Tests.Mocks.TestNetwork).Module.ModuleHandle);

    [Fact]
    public void SeventeenActiveEventsReplaceNineteenWithOnlyTwoNativeDeletedEntriesWithoutInvokingCallbacks()
    {
        var source = Fixture.Create();var target = Fixture.Create();
        var sourceEvents = Timers(source, 17);var active = Timers(target, 17);
        var first = Tombstone();var second = Tombstone();
        var events = new List<MBCampaignEvent> { first, second };events.AddRange(active);
        source.Manager._mobilePartyHourlyTicker.Index = 1;source.Manager._mobilePartyHourlyTicker.TickDebt = .75;
        source.Manager._lastGameTime = new CampaignTime(5000);
        sourceEvents[0].InitialWait = new CampaignTime(70);sourceEvents[0].NextTriggerTime = new CampaignTime(6000);
        Assert.True(source.Service.TryCapture(source.Manager, 19, sourceEvents, out var checkpoint));

        Assert.True(target.Service.TryApply(checkpoint, target.Manager, events));
        Assert.Equal(active, events);Assert.DoesNotContain(first, events);Assert.DoesNotContain(second, events);
        Assert.Equal(1, target.Manager._mobilePartyHourlyTicker.Index);Assert.Equal(.75, target.Manager._mobilePartyHourlyTicker.TickDebt);
        Assert.Equal(5000, target.Manager._lastGameTime.NumTicks);
        Assert.All(events, e => { Assert.True(Counters.TryGetValue(e, out var counter));Assert.Equal(0, counter.Calls); });
        Assert.True(first.isEventDeleted);Assert.Null(first.handlers);
        Assert.True(target.Service.TryCapture(target.Manager, 19, events, out var returned));
        Assert.Equal(17, returned.Events.Length);
        Assert.Equal(checkpoint.Events.Select(e => e.CallbackIdentities.Single()), returned.Events.Select(e => e.CallbackIdentities.Single()));
        Assert.Equal(checkpoint.Events.Select(e => e.NextTriggerTicks), returned.Events.Select(e => e.NextTriggerTicks));
    }

    [Theory]
    [InlineData("extra-active")]
    [InlineData("wrong-owner")]
    [InlineData("unregistered-owner")]
    [InlineData("duplicate-active")]
    [InlineData("order")]
    [InlineData("definition")]
    [InlineData("phase")]
    [InlineData("ticker")]
    [InlineData("source-deleted")]
    [InlineData("null-event")]
    [InlineData("read-only")]
    [InlineData("clock")]
    [InlineData("interval")]
    [InlineData("source-null")]
    public void RejectedActiveGraphNeverRemovesTombstonesOrPartiallyAppliesTimerAndTickerState(string fault)
    {
        var source = Fixture.Create();var target = Fixture.Create();
        var sourceEvents = Timers(source, 17);var active = Timers(target, 17);
        var mutable = new List<MBCampaignEvent> { Tombstone() };mutable.AddRange(active);mutable.Insert(8, Tombstone());
        source.Manager._mobilePartyHourlyTicker.Index = 1;source.Manager._lastGameTime = new CampaignTime(5000);
        sourceEvents[0].InitialWait = new CampaignTime(70);sourceEvents[0].NextTriggerTime = new CampaignTime(6000);
        Assert.True(source.Service.TryCapture(source.Manager, 19, sourceEvents, out var checkpoint));
        if (fault == "extra-active") mutable.Add(new MBCampaignEvent("unrecognized active"));
        if (fault == "wrong-owner") { active[16].handlers.Clear();active[16].AddHandler(target.Register<Counter>(999).Record); }
        if (fault == "unregistered-owner") { active[16].handlers.Clear();active[16].AddHandler(new Counter().Record); }
        if (fault == "duplicate-active") mutable[18] = active[15];
        if (fault == "order") { var value = mutable[16];mutable[16] = mutable[17];mutable[17] = value; }
        if (fault == "definition") active[16].TriggerPeriod = new CampaignTime(999);
        if (fault == "null-event") mutable[16] = null;
        if (fault == "phase" || fault == "source-deleted" || fault == "source-null")
        {
            var states = checkpoint.Events.ToArray();var state = states[16];
            states[16] = new CampaignPeriodicEventState(state.Description, state.TriggerPeriodTicks,
                state.InitialWaitTicks, fault == "phase" ? -1 : state.NextTriggerTicks,
                fault == "source-deleted", state.CallbackIdentities);
            if (fault == "source-null") states[16] = null;
            checkpoint = WithEvents(checkpoint, states);
        }
        if (fault == "clock" || fault == "interval")
            checkpoint = new CampaignSchedulerCheckpoint(fault == "clock" ? -1 : checkpoint.LastGameTimeTicks,
                fault == "interval" ? 999 : checkpoint.MinimumIntervalTicks,
                checkpoint.CurrentTickCount, checkpoint.Tickers, checkpoint.Events);
        if (fault == "ticker")
        {
            var states = checkpoint.Tickers.ToArray();var state = states[19];
            states[19] = new CampaignPeriodicTickerState(state.Name, -2, state.TickDebt, state.OrderedHandles);
            checkpoint = new CampaignSchedulerCheckpoint(checkpoint.LastGameTimeTicks, checkpoint.MinimumIntervalTicks,
                checkpoint.CurrentTickCount, states, checkpoint.Events);
        }
        IList<MBCampaignEvent> events = fault == "read-only" ? Array.AsReadOnly(mutable.ToArray()) : mutable;
        var before = events.ToArray();var phases = before.Select(value =>
            (value?.InitialWait.NumTicks, value?.NextTriggerTime.NumTicks, value?.isEventDeleted)).ToArray();

        Assert.False(target.Service.TryApply(checkpoint, target.Manager, events));
        Assert.Equal(before, events);
        Assert.Equal(phases, events.Select(value =>
            (value?.InitialWait.NumTicks, value?.NextTriggerTime.NumTicks, value?.isEventDeleted)));
        Assert.Equal(-1, target.Manager._mobilePartyHourlyTicker.Index);Assert.Equal(0, target.Manager._mobilePartyHourlyTicker.TickDebt);
        Assert.Equal(0, target.Manager._lastGameTime.NumTicks);
        Assert.All(active, value => { Assert.True(Counters.TryGetValue(value, out var counter));Assert.Equal(0, counter.Calls); });
    }

    [Fact]
    public void InterspersedTombstonesPreserveNativeListAndActiveReferencesThenRunOnlyTheTransferredDeadline()
    {
        var previous = Campaign.Current;
        var campaign = (Campaign)RuntimeHelpers.GetUninitializedObject(typeof(Campaign));
        campaign.MapTimeTracker = new MapTimeTracker(new CampaignTime(1000));Campaign.Current = campaign;
        try
        {
            var source = Fixture.Create();var target = Fixture.Create();
            var sourceEvents = Timers(source, 2);var active = Timers(target, 2);
            sourceEvents[0].NextTriggerTime = new CampaignTime(2000);
            var events = new List<MBCampaignEvent> { Tombstone(), active[0], Tombstone(), active[1], Tombstone() };
            var reference = events;var firstHandler = active[0].handlers.Single();
            Assert.True(source.Service.TryCapture(source.Manager, 1, sourceEvents, out var checkpoint));
            Assert.True(target.Service.TryApply(checkpoint, target.Manager, events));
            Assert.Same(reference, events);Assert.Equal(active, events);Assert.Same(firstHandler, events[0].handlers.Single());
            Assert.True(Counters.TryGetValue(events[0], out var counter));Assert.Equal(0, counter.Calls);
            campaign.MapTimeTracker._numTicks = 1999;events[0].CheckUpdate();Assert.Equal(0, counter.Calls);
            campaign.MapTimeTracker._numTicks = 2001;events[0].CheckUpdate();events[0].CheckUpdate();
            Assert.Equal(1, counter.Calls);Assert.Equal(2100, events[0].NextTriggerTime.NumTicks);
        }
        finally { Campaign.Current = previous; }
    }

    [Fact]
    public void EqualCountRetainsOriginalDeletedStateTransferWithoutCleaningTheList()
    {
        var source = Fixture.Create();var target = Fixture.Create();
        var sourceEvents = Timers(source, 2);var events = Timers(target, 2);var before = events.ToArray();
        sourceEvents[0].isEventDeleted = true;
        Assert.True(source.Service.TryCapture(source.Manager, 1, sourceEvents, out var checkpoint));
        Assert.True(target.Service.TryApply(checkpoint, target.Manager, events));
        Assert.Equal(before, events);Assert.True(events[0].isEventDeleted);Assert.False(events[1].isEventDeleted);
    }

    [Fact]
    public void EmptyActiveCheckpointOnlyRemovesEventsAlreadyMarkedDeleted()
    {
        var source = Fixture.Create();var target = Fixture.Create();
        var deleted = new[] { Tombstone(), Tombstone() };var events = deleted.ToList();
        Assert.True(source.Service.TryCapture(source.Manager, 1, Array.Empty<MBCampaignEvent>(), out var checkpoint));
        Assert.True(target.Service.TryApply(checkpoint, target.Manager, events));
        Assert.Empty(events);Assert.All(deleted, value => { Assert.True(value.isEventDeleted);Assert.Null(value.handlers); });
    }

    private static CampaignSchedulerCheckpoint WithEvents(CampaignSchedulerCheckpoint checkpoint,
        IEnumerable<CampaignPeriodicEventState> events) => new(checkpoint.LastGameTimeTicks,
            checkpoint.MinimumIntervalTicks, checkpoint.CurrentTickCount, checkpoint.Tickers, events);

    private static readonly ConditionalWeakTable<MBCampaignEvent, Counter> Counters = new();
    private static List<MBCampaignEvent> Timers(Fixture fixture, int count)
    {
        var events = new List<MBCampaignEvent>();
        for (int i = 0; i < count; i++)
        {
            var counter = fixture.Register<Counter>((uint)(101 + i));
            var e = new MBCampaignEvent("active" + i) { TriggerPeriod = new CampaignTime(100 + i), InitialWait = new CampaignTime(50), NextTriggerTime = new CampaignTime(1000 + i) };
            e.AddHandler(counter.Record);Counters.Add(e, counter);events.Add(e);
        }
        return events;
    }
    private static MBCampaignEvent Tombstone() => new("deleted") { isEventDeleted = true, handlers = null };
    private sealed class Counter
    {
        internal int Calls;
        public void Record(MBCampaignEvent _, object[] args) => Calls++;
    }
}
