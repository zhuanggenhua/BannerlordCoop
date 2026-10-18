using Common;
using GameInterface.Services.CampaignAuthority;
using Moq;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;
using Xunit;
using ObjectManagerService = GameInterface.Services.ObjectManager.ObjectManager;

namespace GameInterface.Tests.Services.CampaignAuthority;

[Collection(ModInformationRoleCollection.Name)]
public class CampaignSchedulerCheckpointServiceTests
{
    static CampaignSchedulerCheckpointServiceTests()
    {
        RuntimeHelpers.RunModuleConstructor(typeof(Coop.Tests.Mocks.TestNetwork).Module.ModuleHandle);
    }

    [Fact]
    public void NextNativeCallbackContinuesAtTransferredCursorAndDebt()
    {
        var source = Fixture.Create();
        var target = Fixture.Create();
        source.Manager._mobilePartyHourlyTicker.Index = 0;
        source.Manager._mobilePartyHourlyTicker.TickDebt = .75;
        source.Manager._lastGameTime = new CampaignTime(12345);
        source.Events[0].NextTriggerTime = new CampaignTime(45678);
        Assert.True(source.Service.TryCapture(source.Manager, 117, source.Events, out var checkpoint));
        Assert.True(target.Service.TryApply(checkpoint, target.Manager, target.Events));
        Assert.Equal(12345, target.Manager._lastGameTime.NumTicks);
        Assert.Equal(45678, target.Events[0].NextTriggerTime.NumTicks);

        var calls = new List<MobileParty>();
        target.Manager._mobilePartyHourlyTicker._action = calls.Add;
        target.Manager._mobilePartyHourlyTicker.PeriodicTickSome(.2);

        Assert.Same(target.Parties[1], Assert.Single(calls));
        Assert.Equal(1, target.Manager._mobilePartyHourlyTicker.Index);
        Assert.Equal(.15, target.Manager._mobilePartyHourlyTicker.TickDebt, 9);
    }

    [Fact]
    public void SettlementShuffleIsTransferredWithoutReorderingNativeMembership()
    {
        var source = Fixture.Create();
        var target = Fixture.Create();
        target.Manager._hourlyTickSettlementTicker._list.Reverse();
        target.Manager._dailyTickSettlementTicker._list.Reverse();
        var original = target.Manager._hourlyTickSettlementTicker._list;
        var first = original[0];
        Assert.True(source.Service.TryCapture(source.Manager, 0, source.Events, out var checkpoint));
        Assert.True(target.Service.TryApply(checkpoint, target.Manager, target.Events));
        Assert.Same(first, original[0]);
        Assert.NotSame(original, target.Manager._hourlyTickSettlementTicker._list);
        Assert.Equal(target.Settlements, target.Manager._hourlyTickSettlementTicker._list);
    }

    [Fact]
    public void DerivedPartyPartitionKeepsLiveMembershipAfterOrderAndCursorTransfer()
    {
        var source = Fixture.Create();
        var target = Fixture.Create();
        source.Manager._villagerMobilePartyPartialHourlyAiEventTicker.Index = 0;
        source.Manager._villagerMobilePartyPartialHourlyAiEventTicker.TickDebt = .75;
        target.Manager._villagerMobilePartyPartialHourlyAiEventTicker._list.Reverse();
        var nativePartition = target.Manager._villagerMobilePartyPartialHourlyAiEventTicker._list;
        var globalParties = target.Manager._mobilePartyHourlyTicker._list.ToArray();
        Assert.True(source.Service.TryCapture(source.Manager, 1, source.Events, out var checkpoint));
        Assert.True(target.Service.TryApply(checkpoint, target.Manager, target.Events));
        Assert.Same(nativePartition, target.Manager._villagerMobilePartyPartialHourlyAiEventTicker._list);
        Assert.Equal(globalParties, target.Manager._mobilePartyHourlyTicker._list);
        var calls = new List<MobileParty>();
        var ticker = target.Manager._villagerMobilePartyPartialHourlyAiEventTicker;
        ticker._action = calls.Add;
        ticker.PeriodicTickSome(.2);
        Assert.Same(target.Parties[1], Assert.Single(calls));
        var added = target.Register<MobileParty>(90);
        nativePartition.Add(added);
        nativePartition.Remove(target.Parties[0]);
        Assert.Contains(added, ticker._list);
        Assert.DoesNotContain(target.Parties[0], ticker._list);
        Assert.True(target.Service.TryCapture(target.Manager, 2, target.Events, out var back));
        var returned = back.Tickers.Single(value => value.Name == "_villagerMobilePartyPartialHourlyAiEventTicker");
        Assert.Equal(new[] { 2u, 90u }, returned.OrderedHandles);
    }

    [Fact]
    public void RejectsDifferentDerivedMembershipBeforeChangingListsOrCursors()
    {
        var source = Fixture.Create();
        var target = Fixture.Create();
        source.Manager._mobilePartyHourlyTicker.Index = 1;
        target.Manager._villagerMobilePartyPartialHourlyAiEventTicker._list[0] = target.Register<MobileParty>(90);
        var original = target.Manager._villagerMobilePartyPartialHourlyAiEventTicker._list.ToArray();
        Assert.True(source.Service.TryCapture(source.Manager, 1, source.Events, out var checkpoint));
        Assert.False(target.Service.TryApply(checkpoint, target.Manager, target.Events));
        Assert.Equal(-1, target.Manager._mobilePartyHourlyTicker.Index);
        Assert.Equal(original, target.Manager._villagerMobilePartyPartialHourlyAiEventTicker._list);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsDifferentLiveEntityOrderOrMembershipBeforeChangingAnyCursor(bool missing)
    {
        var source = Fixture.Create();
        var target = Fixture.Create();
        source.Manager._mobilePartyHourlyTicker.Index = 1;
        if (missing) target.Manager._dailyTickHeroTicker._list.Clear();
        else target.Manager._dailyTickHeroTicker._list.Reverse();
        Assert.True(source.Service.TryCapture(source.Manager, 0, source.Events, out var checkpoint));
        Assert.False(target.Service.TryApply(checkpoint, target.Manager, target.Events));
        Assert.Equal(-1, target.Manager._mobilePartyHourlyTicker.Index);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    [InlineData(1.01)]
    public void RejectsInvalidDebt(double debt)
    {
        var source = Fixture.Create();
        source.Manager._mobilePartyHourlyTicker.TickDebt = debt;
        Assert.False(source.Service.TryCapture(source.Manager, 0, source.Events, out _));
    }

    [Fact]
    public void RejectsUnfinishedParallelBatch()
    {
        var source = Fixture.Create();
        source.Manager._lordMobilePartyPartialHourlyAiEventTicker._currentFrameToTickListFlattened.Add(source.Parties[0]);
        Assert.False(source.Service.TryCapture(source.Manager, 0, source.Events, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoadedTickersCanOmitUnusedSerialScratchStorageButNotParallelStorage(bool parallel)
    {
        var source = Fixture.Create();
        var restored = (CampaignPeriodicEventManager.PeriodicTicker<MobileParty>)RuntimeHelpers.GetUninitializedObject(
            typeof(CampaignPeriodicEventManager.PeriodicTicker<MobileParty>));
        restored.Initialize(new MBReadOnlyList<MobileParty>(source.Parties), _ => { }, parallel);
        source.Manager._mobilePartyHourlyTicker = restored;
        Assert.Equal(!parallel, source.Service.TryCapture(source.Manager, 0, source.Events, out _));
    }

    [Fact]
    public void RejectsDifferentEventDefinitionsBeforeChangingAnyCursor()
    {
        var source = Fixture.Create();
        var target = Fixture.Create();
        source.Manager._mobilePartyHourlyTicker.Index = 1;
        target.Events[0].TriggerPeriod = new CampaignTime(999);
        Assert.True(source.Service.TryCapture(source.Manager, 0, source.Events, out var checkpoint));
        Assert.False(target.Service.TryApply(checkpoint, target.Manager, target.Events));
        Assert.Equal(-1, target.Manager._mobilePartyHourlyTicker.Index);
    }

    [Fact]
    public void ReconnectedArmyTimerRestoresAuthorityPhaseAndRunsNextNativeCallbackExactlyOnce()
    {
        var previousCampaign = Campaign.Current;
        var campaign = (Campaign)RuntimeHelpers.GetUninitializedObject(typeof(Campaign));
        campaign.MapTimeTracker = new MapTimeTracker(new CampaignTime(1_000_000_000));
        Campaign.Current = campaign;
        try
        {
            var source = Fixture.Create();
            var target = Fixture.Create();
            // Actual run 0bc4... event 0: period agrees, while Army.OnAfterLoad rebuilt InitialWait.
            source.Events[0] = new MBCampaignEvent(new CampaignTime(36_000_000), new CampaignTime(23_583_056));
            target.Events[0] = new MBCampaignEvent(new CampaignTime(36_000_000), new CampaignTime(18_735_192));
            const long nextTrigger = 1_050_000_000;
            source.Events[0].NextTriggerTime = new CampaignTime(nextTrigger);
            var sourceCounter = source.Register<CallbackCounter>(101);
            var targetCounter = target.Register<CallbackCounter>(101);
            source.Events[0].AddHandler(sourceCounter.Record);
            target.Events[0].AddHandler(targetCounter.Record);
            Assert.True(source.Service.TryCapture(source.Manager, 117, source.Events, out var checkpoint));

            Assert.True(target.Service.TryApply(checkpoint, target.Manager, target.Events));
            Assert.Equal(23_583_056, target.Events[0].InitialWait.NumTicks);
            Assert.Equal(nextTrigger, target.Events[0].NextTriggerTime.NumTicks);

            // The stale reconnect deadline would have fired already; the authority's deadline must win.
            campaign.MapTimeTracker._numTicks = nextTrigger - 1;
            target.Events[0].CheckUpdate();
            Assert.Equal(0, targetCounter.Calls);
            campaign.MapTimeTracker._numTicks = nextTrigger + 1;
            target.Events[0].CheckUpdate();
            target.Events[0].CheckUpdate();
            Assert.Equal(1, targetCounter.Calls);
            Assert.Equal(nextTrigger + 36_000_000, target.Events[0].NextTriggerTime.NumTicks);

            Assert.True(target.Service.TryCapture(target.Manager, 118, target.Events, out var back));
            Assert.True(source.Service.TryApply(back, source.Manager, source.Events));
            Assert.Equal(target.Events[0].InitialWait.NumTicks, source.Events[0].InitialWait.NumTicks);
            Assert.Equal(target.Events[0].NextTriggerTime.NumTicks, source.Events[0].NextTriggerTime.NumTicks);
        }
        finally { Campaign.Current = previousCampaign; }
    }

    [Fact]
    public void LaterDefinitionMismatchDoesNotPartiallyApplyEarlierRebuiltPhase()
    {
        var source = Fixture.Create();
        var target = Fixture.Create();
        source.Manager._mobilePartyHourlyTicker.Index = 1;
        source.Manager._lastGameTime = new CampaignTime(5000);
        source.Events[0].InitialWait = new CampaignTime(70);
        source.Events[0].NextTriggerTime = new CampaignTime(6000);
        source.Events[0].isEventDeleted = true;
        var sourceEvents = source.Events.Concat(new[]
        {
            new MBCampaignEvent("second") { TriggerPeriod = new CampaignTime(200), InitialWait = new CampaignTime(10) },
        }).ToList();
        var targetEvents = target.Events.Concat(new[]
        {
            new MBCampaignEvent("different") { TriggerPeriod = new CampaignTime(200), InitialWait = new CampaignTime(10) },
        }).ToList();
        Assert.True(source.Service.TryCapture(source.Manager, 1, sourceEvents, out var checkpoint));

        Assert.False(target.Service.TryApply(checkpoint, target.Manager, targetEvents));
        Assert.Equal(-1, target.Manager._mobilePartyHourlyTicker.Index);
        Assert.Equal(0, target.Manager._lastGameTime.NumTicks);
        Assert.Equal(50, target.Events[0].InitialWait.NumTicks);
        Assert.Equal(0, target.Events[0].NextTriggerTime.NumTicks);
        Assert.False(target.Events[0].isEventDeleted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InvalidTransferredPhaseIsRejectedWithoutChangingCursorsOrTimer(bool invalidInitialWait)
    {
        var source = Fixture.Create();
        var target = Fixture.Create();
        source.Manager._mobilePartyHourlyTicker.Index = 1;
        Assert.True(source.Service.TryCapture(source.Manager, 1, source.Events, out var valid));
        var malformed = new CampaignSchedulerCheckpoint(valid.LastGameTimeTicks, valid.MinimumIntervalTicks,
            valid.CurrentTickCount, valid.Tickers, new[]
            {
                new CampaignPeriodicEventState("hourly", 100, invalidInitialWait ? -1 : 50,
                    invalidInitialWait ? 0 : -1, false),
            });

        Assert.False(target.Service.TryApply(malformed, target.Manager, target.Events));
        Assert.Equal(-1, target.Manager._mobilePartyHourlyTicker.Index);
        Assert.Equal(50, target.Events[0].InitialWait.NumTicks);
        Assert.Equal(0, target.Events[0].NextTriggerTime.NumTicks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SamePeriodArmyOwnerOrCallbackOrderMismatchCannotPartiallyApply(bool reverseCallbacks)
    {
        var source = Fixture.Create();
        var target = Fixture.Create();
        var firstSource = source.Register<Army>(101);
        var secondSource = source.Register<Army>(102);
        var firstTarget = target.Register<Army>(101);
        var secondTarget = target.Register<Army>(102);
        var sourceEvents = new[] { Timer(firstSource), Timer(secondSource) };
        var targetEvents = new[] { Timer(firstTarget), Timer(secondTarget) };
        if (reverseCallbacks)
        {
            sourceEvents[0].AddHandler(secondSource.HourlyTick);
            targetEvents[0].handlers.Clear();
            targetEvents[0].AddHandler(secondTarget.HourlyTick);
            targetEvents[0].AddHandler(firstTarget.HourlyTick);
        }
        else Array.Reverse(targetEvents);
        source.Manager._mobilePartyHourlyTicker.Index = 1;
        source.Manager._lastGameTime = new CampaignTime(5000);
        sourceEvents[0].InitialWait = new CampaignTime(70);
        sourceEvents[0].NextTriggerTime = new CampaignTime(6000);
        Assert.True(source.Service.TryCapture(source.Manager, 1, sourceEvents, out var checkpoint));

        Assert.False(target.Service.TryApply(checkpoint, target.Manager, targetEvents));
        Assert.Equal(-1, target.Manager._mobilePartyHourlyTicker.Index);
        Assert.Equal(0, target.Manager._lastGameTime.NumTicks);
        Assert.All(targetEvents, value =>
        {
            Assert.Equal(50, value.InitialWait.NumTicks);
            Assert.Equal(0, value.NextTriggerTime.NumTicks);
            Assert.False(value.isEventDeleted);
        });
    }

    private static MBCampaignEvent Timer(Army owner)
    {
        var timer = new MBCampaignEvent(null) { TriggerPeriod = new CampaignTime(100), InitialWait = new CampaignTime(50) };
        timer.AddHandler(owner.HourlyTick);
        return timer;
    }

    private sealed class CallbackCounter
    {
        public int Calls;
        public void Record(MBCampaignEvent _, object[] args) => Calls++;
    }

    [Fact]
    public void ReturnHandoffKeepsNewlyCreatedPartiesInLiveMembership()
    {
        var source = Fixture.Create();
        var target = Fixture.Create();
        Assert.True(source.Service.TryCapture(source.Manager, 0, source.Events, out var first));
        var live = target.Manager._mobilePartyHourlyTicker._list;
        Assert.True(target.Service.TryApply(first, target.Manager, target.Events));
        Assert.Same(live, target.Manager._mobilePartyHourlyTicker._list);
        var sourceNew = source.Register<MobileParty>(90);
        var targetNew = target.Register<MobileParty>(90);
        source.Manager._mobilePartyHourlyTicker._list.Add(sourceNew);
        live.Add(targetNew);
        target.Manager._mobilePartyHourlyTicker.Index = 2;
        target.Manager._mobilePartyHourlyTicker.TickDebt = .5;
        Assert.True(target.Service.TryCapture(target.Manager, 1, target.Events, out var back));
        Assert.True(source.Service.TryApply(back, source.Manager, source.Events));
        Assert.Same(sourceNew, source.Manager._mobilePartyHourlyTicker._list[2]);
        Assert.Equal(2, source.Manager._mobilePartyHourlyTicker.Index);
        Assert.Equal(.5, source.Manager._mobilePartyHourlyTicker.TickDebt);
    }

    private sealed class Fixture
    {
        private readonly ObjectManagerService objects = new(Mock.Of<ILogger>());
        public CampaignSchedulerCheckpointService Service { get; }
        public CampaignPeriodicEventManager Manager { get; } = new();
        public MobileParty[] Parties { get; private set; }
        public Settlement[] Settlements { get; private set; }
        public IList<MBCampaignEvent> Events { get; } = new[]
        {
            new MBCampaignEvent("hourly") { TriggerPeriod = new CampaignTime(100), InitialWait = new CampaignTime(50) },
        };
        private Fixture() { Service = new CampaignSchedulerCheckpointService(objects); }
        public T Register<T>(uint handle) where T : class
        {
            var value = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
            Assert.True(objects.AddExisting(typeof(T).Name + handle, value, handle));
            return value;
        }
        public static Fixture Create()
        {
            var value = new Fixture();
            value.Parties = new[] { value.Register<MobileParty>(1), value.Register<MobileParty>(2) };
            value.Settlements = new[] { value.Register<Settlement>(3), value.Register<Settlement>(4) };
            var clans = new[] { value.Register<Clan>(5), value.Register<Clan>(6) };
            var heroes = new[] { value.Register<Hero>(7), value.Register<Hero>(8) };
            var towns = new[] { value.Register<Town>(9), value.Register<Town>(10) };
            var m = value.Manager;
            m.MinimumPeriodicEventInterval = new CampaignTime(50);
            Init(m._mobilePartyHourlyTicker, value.Parties);
            Init(m._mobilePartyDailyTicker, value.Parties);
            Init(m._dailyTickPartyTicker, value.Parties);
            Init(m._hourlyTickMobilePartyTicker, value.Parties);
            Init(m._hourlyTickSettlementTicker, value.Settlements);
            Init(m._hourlyTickClanTicker, clans);
            Init(m._dailyTickTownTicker, towns);
            Init(m._dailyTickSettlementTicker, value.Settlements);
            Init(m._dailyTickHeroTicker, heroes);
            Init(m._dailyTickClanTicker, clans);
            Init(m._quarterDailyPartyTicker, value.Parties);
            Init(m._caravanMobilePartyPartialHourlyAiEventTicker, value.Parties);
            Init(m._garrisonMobilePartyPartialHourlyAiEventTicker, value.Parties);
            Init(m._militiaMobilePartyPartialHourlyAiEventTicker, value.Parties);
            Init(m._villagerMobilePartyPartialHourlyAiEventTicker, value.Parties);
            Init(m._customMobilePartyPartialHourlyAiEventTicker, value.Parties);
            Init(m._banditMobilePartyPartialHourlyAiEventTicker, value.Parties);
            Init(m._lordMobilePartyPartialHourlyAiEventTicker, value.Parties);
            Init(m._partiesWithoutPartyComponentsPartialHourlyAiEventTicker, value.Parties);
            Init(m._patrolPartyPartialHourlyAiEventTicker, value.Parties);
            return value;
        }
        private static void Init<T>(CampaignPeriodicEventManager.PeriodicTicker<T> ticker, IEnumerable<T> values) =>
            ticker.Initialize(new MBReadOnlyList<T>(values), _ => { }, false);
    }
}
