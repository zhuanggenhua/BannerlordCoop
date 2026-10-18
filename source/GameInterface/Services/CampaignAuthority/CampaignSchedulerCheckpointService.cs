using Common;
using Common.Logging;
using GameInterface.Services.ObjectManager;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace GameInterface.Services.CampaignAuthority;

public interface ICampaignSchedulerCheckpointService
{
    bool TryCapture(out CampaignSchedulerCheckpoint checkpoint);
    bool TryApply(CampaignSchedulerCheckpoint checkpoint);
}

internal sealed class CampaignSchedulerCheckpointService : ICampaignSchedulerCheckpointService
{
    private static readonly ILogger Logger = LogManager.GetLogger<CampaignSchedulerCheckpointService>();
    private readonly IObjectManager objectManager;
    public CampaignSchedulerCheckpointService(IObjectManager objectManager) { this.objectManager = objectManager; }

    public bool TryCapture(out CampaignSchedulerCheckpoint checkpoint)
    {
        checkpoint = null;
        var campaign = Campaign.Current;
        return campaign?._campaignPeriodicEventManager != null &&
            TryCapture(campaign._campaignPeriodicEventManager, campaign.CurrentTickCount,
                campaign.CustomPeriodicCampaignEvents, out checkpoint);
    }

    public bool TryApply(CampaignSchedulerCheckpoint checkpoint)
    {
        var campaign = Campaign.Current;
        if (campaign?._campaignPeriodicEventManager == null ||
            !TryApply(checkpoint, campaign._campaignPeriodicEventManager, campaign.CustomPeriodicCampaignEvents)) return false;
        campaign.CurrentTickCount = checkpoint.CurrentTickCount;
        return true;
    }

    internal bool TryCapture(CampaignPeriodicEventManager manager, int tickCount, IList<MBCampaignEvent> events,
        out CampaignSchedulerCheckpoint checkpoint)
    {
        checkpoint = null;
        if (manager == null || events == null) return false;
        var states = new List<CampaignPeriodicTickerState>();
        foreach (var ticker in Bind(manager))
        {
            if (!ticker.TryCapture(out var state))
            {
                Logger.Warning("Could not capture native campaign scheduler ticker {TickerName}", ticker.Name);
                return false;
            }
            states.Add(state);
        }
        if (events.Any(value => value == null || value.TriggerPeriod.NumTicks <= 0)) return false;
        checkpoint = new CampaignSchedulerCheckpoint(manager._lastGameTime.NumTicks,
            manager.MinimumPeriodicEventInterval.NumTicks, tickCount, states, events.Select(value =>
                new CampaignPeriodicEventState(value.description, value.TriggerPeriod.NumTicks, value.InitialWait.NumTicks,
                    value.NextTriggerTime.NumTicks, value.isEventDeleted)));
        return true;
    }

    internal bool TryApply(CampaignSchedulerCheckpoint checkpoint, CampaignPeriodicEventManager manager,
        IList<MBCampaignEvent> events)
    {
        if (checkpoint?.Tickers == null || checkpoint.Events == null || manager == null || events == null ||
            checkpoint.MinimumIntervalTicks != manager.MinimumPeriodicEventInterval.NumTicks ||
            checkpoint.LastGameTimeTicks < 0 || checkpoint.Events.Length != events.Count) return false;

        var bindings = Bind(manager).ToArray();
        if (checkpoint.Tickers.Length != bindings.Length) return false;
        var apply = new List<Action>();
        for (int i = 0; i < bindings.Length; i++)
        {
            if (!bindings[i].TryPrepare(checkpoint.Tickers[i], out var action))
            {
                Logger.Warning("Rejected campaign scheduler checkpoint at ticker {TickerName}; order, membership or cursor differs",
                    bindings[i].Name);
                return false;
            }
            apply.Add(action);
        }
        for (int i = 0; i < events.Count; i++)
        {
            var state = checkpoint.Events[i];
            var target = events[i];
            if (state == null || target == null || state.TriggerPeriodTicks <= 0 ||
                !string.Equals(state.Description, target.description, StringComparison.Ordinal) ||
                state.TriggerPeriodTicks != target.TriggerPeriod.NumTicks || state.InitialWaitTicks != target.InitialWait.NumTicks)
            {
                Logger.Warning("Rejected campaign scheduler checkpoint at periodic event {EventIndex}; event definitions differ", i);
                return false;
            }
            apply.Add(() => { target.NextTriggerTime = new CampaignTime(state.NextTriggerTicks); target.isEventDeleted = state.IsDeleted; });
        }

        // Validation precedes every mutation. Global entity lists retain their native ordering.
        foreach (var action in apply) action();
        manager._lastGameTime = new CampaignTime(checkpoint.LastGameTimeTicks);
        return true;
    }

    private IEnumerable<ITicker> Bind(CampaignPeriodicEventManager manager)
    {
        yield return Wrap(nameof(manager._mobilePartyHourlyTicker), manager._mobilePartyHourlyTicker);
        yield return Wrap(nameof(manager._mobilePartyDailyTicker), manager._mobilePartyDailyTicker);
        yield return Wrap(nameof(manager._dailyTickPartyTicker), manager._dailyTickPartyTicker);
        yield return Wrap(nameof(manager._hourlyTickMobilePartyTicker), manager._hourlyTickMobilePartyTicker);
        yield return Wrap(nameof(manager._hourlyTickSettlementTicker), manager._hourlyTickSettlementTicker);
        yield return Wrap(nameof(manager._hourlyTickClanTicker), manager._hourlyTickClanTicker);
        yield return Wrap(nameof(manager._dailyTickTownTicker), manager._dailyTickTownTicker);
        yield return Wrap(nameof(manager._dailyTickSettlementTicker), manager._dailyTickSettlementTicker);
        yield return Wrap(nameof(manager._dailyTickHeroTicker), manager._dailyTickHeroTicker);
        yield return Wrap(nameof(manager._dailyTickClanTicker), manager._dailyTickClanTicker);
        yield return Wrap(nameof(manager._quarterDailyPartyTicker), manager._quarterDailyPartyTicker);
        yield return Wrap(nameof(manager._caravanMobilePartyPartialHourlyAiEventTicker), manager._caravanMobilePartyPartialHourlyAiEventTicker);
        yield return Wrap(nameof(manager._garrisonMobilePartyPartialHourlyAiEventTicker), manager._garrisonMobilePartyPartialHourlyAiEventTicker);
        yield return Wrap(nameof(manager._militiaMobilePartyPartialHourlyAiEventTicker), manager._militiaMobilePartyPartialHourlyAiEventTicker);
        yield return Wrap(nameof(manager._villagerMobilePartyPartialHourlyAiEventTicker), manager._villagerMobilePartyPartialHourlyAiEventTicker);
        yield return Wrap(nameof(manager._customMobilePartyPartialHourlyAiEventTicker), manager._customMobilePartyPartialHourlyAiEventTicker);
        yield return Wrap(nameof(manager._banditMobilePartyPartialHourlyAiEventTicker), manager._banditMobilePartyPartialHourlyAiEventTicker);
        yield return Wrap(nameof(manager._lordMobilePartyPartialHourlyAiEventTicker), manager._lordMobilePartyPartialHourlyAiEventTicker);
        yield return Wrap(nameof(manager._partiesWithoutPartyComponentsPartialHourlyAiEventTicker), manager._partiesWithoutPartyComponentsPartialHourlyAiEventTicker);
        yield return Wrap(nameof(manager._patrolPartyPartialHourlyAiEventTicker), manager._patrolPartyPartialHourlyAiEventTicker);
    }

    private ITicker Wrap<T>(string name, CampaignPeriodicEventManager.PeriodicTicker<T> ticker) where T : class =>
        new Ticker<T>(name, ticker, objectManager);

    private interface ITicker
    {
        string Name { get; }
        bool TryCapture(out CampaignPeriodicTickerState state);
        bool TryPrepare(CampaignPeriodicTickerState state, out Action apply);
    }

    private sealed class Ticker<T> : ITicker where T : class
    {
        public string Name { get; }
        private readonly CampaignPeriodicEventManager.PeriodicTicker<T> ticker;
        private readonly IObjectManager objects;
        public Ticker(string name, CampaignPeriodicEventManager.PeriodicTicker<T> ticker, IObjectManager objects)
        { Name = name; this.ticker = ticker; this.objects = objects; }

        public bool TryCapture(out CampaignPeriodicTickerState state)
        {
            state = null;
            if (ticker?._list == null ||
                (ticker._currentFrameToTickListFlattened != null && ticker._currentFrameToTickListFlattened.Count != 0) ||
                (ticker._doParallel && ticker._currentFrameToTickListFlattened == null) ||
                !ValidCursor(ticker.Index, ticker.TickDebt)) return false;
            var handles = new List<uint>();
            foreach (var value in ticker._list)
            {
                if (value == null || !objects.TryGetHandleWithLogging(value, out var handle)) return false;
                handles.Add(handle);
            }
            if (handles.Distinct().Count() != handles.Count) return false;
            state = new CampaignPeriodicTickerState(Name, ticker.Index, ticker.TickDebt, handles);
            return true;
        }

        public bool TryPrepare(CampaignPeriodicTickerState state, out Action apply)
        {
            apply = null;
            if (state?.OrderedHandles == null || !string.Equals(state.Name, Name, StringComparison.Ordinal) ||
                !ValidCursor(state.Index, state.TickDebt) || !TryCapture(out var local)) return false;
            MBReadOnlyList<T> ordered = ticker._list;
            if (!local.OrderedHandles.SequenceEqual(state.OrderedHandles))
            {
                // Only the settlement tickers own a shuffled snapshot. Other lists are live engine
                // membership/index structures and must never be replaced or reordered here.
                if (typeof(T) != typeof(Settlement) || local.OrderedHandles.Length != state.OrderedHandles.Length ||
                    !local.OrderedHandles.OrderBy(value => value).SequenceEqual(state.OrderedHandles.OrderBy(value => value))) return false;
                var byHandle = local.OrderedHandles.Zip(ticker._list, (handle, value) => (handle, value)).ToDictionary(pair => pair.handle, pair => pair.value);
                ordered = new MBReadOnlyList<T>(state.OrderedHandles.Select(handle => byHandle[handle]));
            }
            apply = () => { ticker._list = ordered; ticker.Index = state.Index; ticker.TickDebt = state.TickDebt; };
            return true;
        }

        private static bool ValidCursor(int index, double debt) =>
            index >= -1 && !double.IsNaN(debt) && !double.IsInfinity(debt) && debt >= 0 && debt <= 1;
    }
}
