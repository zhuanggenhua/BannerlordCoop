using Common;
using Common.Logging;
using GameInterface.Services.ObjectManager;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using TaleWorlds.ObjectSystem;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace GameInterface.Services.CampaignAuthority;

public interface ICampaignSchedulerCheckpointService
{
    bool TryCapture(out CampaignSchedulerCheckpoint checkpoint);
    bool TryApply(CampaignSchedulerCheckpoint checkpoint);
    bool TryCaptureCallbacks(MBCampaignEvent campaignEvent, out string[] identities);
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

    public bool TryCaptureCallbacks(MBCampaignEvent campaignEvent, out string[] identities)
    {
        identities = null;
        if (campaignEvent?.handlers == null) return false;
        var result = new List<string>();
        foreach (var handler in campaignEvent.handlers)
        {
            if (handler == null) return false;
            foreach (var callback in handler.GetInvocationList())
            {
                var method = callback.Method;
                if (method.DeclaringType?.FullName == null ||
                    !TryGetCallbackOwnerIdentity(callback.Target, method.IsStatic, out var owner))
                    return false;
                result.Add(owner + "|" + method.DeclaringType.Assembly.GetName().Name + ":" +
                    method.DeclaringType.FullName + "." + method.Name + "(" +
                    string.Join(",", method.GetParameters().Select(parameter => parameter.ParameterType.FullName)) + ")");
            }
        }
        identities = result.ToArray();
        return true;
    }

    private bool TryGetCallbackOwnerIdentity(object owner, bool isStatic, out string identity)
    {
        identity = null;
        if (owner == null)
        {
            if (!isStatic) return false;
            identity = "static";
            return true;
        }
        if (objectManager.TryGetHandle(owner, out var handle) && handle != 0)
        {
            identity = owner.GetType().FullName + "#" + handle.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        var campaign = Campaign.Current;
        if (owner is Campaign && ReferenceEquals(owner, campaign) && !string.IsNullOrWhiteSpace(campaign.UniqueGameId))
        {
            identity = "Campaign:" + campaign.UniqueGameId;
            return true;
        }
        // These are the only unregistered singleton owners in the native periodic-event call sites.
        // A same-type object from another campaign is not the current behavior singleton.
        if ((owner is MapTracksCampaignBehavior &&
             ReferenceEquals(owner, campaign?.CampaignBehaviorManager?.GetBehavior<MapTracksCampaignBehavior>())) ||
            (owner is MapWeatherCampaignBehavior &&
             ReferenceEquals(owner, campaign?.CampaignBehaviorManager?.GetBehavior<MapWeatherCampaignBehavior>())))
        {
            identity = "CampaignBehavior:" + owner.GetType().FullName;
            return true;
        }
        return false;
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
        var eventStates = new List<CampaignPeriodicEventState>();
        foreach (var value in events)
        {
            if (value == null || value.TriggerPeriod.NumTicks <= 0 || value.InitialWait.NumTicks < 0 ||
                value.NextTriggerTime.NumTicks < 0 ||
                !TryCaptureCallbacks(value, out var callbacks))
            {
                Logger.Warning("Could not capture native periodic event {EventIndex}; timer phase or callback owner identity is unavailable",
                    eventStates.Count);
                return false;
            }
            eventStates.Add(new CampaignPeriodicEventState(value.description, value.TriggerPeriod.NumTicks,
                value.InitialWait.NumTicks, value.NextTriggerTime.NumTicks, value.isEventDeleted, callbacks));
        }
        checkpoint = new CampaignSchedulerCheckpoint(manager._lastGameTime.NumTicks,
            manager.MinimumPeriodicEventInterval.NumTicks, tickCount, states, eventStates);
        return true;
    }

    internal bool TryApply(CampaignSchedulerCheckpoint checkpoint, CampaignPeriodicEventManager manager,
        IList<MBCampaignEvent> events)
    {
        if (checkpoint?.Tickers == null || checkpoint.Events == null || manager == null || events == null) return false;
        if (checkpoint.MinimumIntervalTicks != manager.MinimumPeriodicEventInterval.NumTicks ||
            checkpoint.LastGameTimeTicks < 0)
        {
            Logger.Warning("Rejected campaign scheduler header: interval source={SourceInterval} local={LocalInterval}, last signal={LastSignal}, events source={SourceEvents} local={LocalEvents}",
                checkpoint.MinimumIntervalTicks, manager.MinimumPeriodicEventInterval.NumTicks, checkpoint.LastGameTimeTicks,
                checkpoint.Events.Length, events.Count);
            return false;
        }

        var targetEvents = events.ToArray();
        var deletedEventIndices = Array.Empty<int>();
        if (checkpoint.Events.Length != targetEvents.Length)
        {
            // The observer does not run SignalPeriodicEvents/DeleteMarkedPeriodicEvents. Only
            // native-marked deletions may explain an all-active writer's shorter event list.
            if (checkpoint.Events.Length >= targetEvents.Length || events.IsReadOnly ||
                checkpoint.Events.Any(state => state == null || state.IsDeleted) ||
                targetEvents.Any(value => value == null))
            {
                Logger.Warning("Rejected campaign scheduler event count: source={SourceEvents} local={LocalEvents}; unequal counts require an all-active source and removable native-deleted local extras",
                    checkpoint.Events.Length, targetEvents.Length);
                return false;
            }
            deletedEventIndices = targetEvents.Select((value, index) => (value, index))
                .Where(entry => entry.value.isEventDeleted).Select(entry => entry.index).ToArray();
            targetEvents = targetEvents.Where(value => !value.isEventDeleted).ToArray();
            if (checkpoint.Events.Length != targetEvents.Length)
            {
                Logger.Warning("Rejected campaign scheduler active event count: source={SourceEvents} localActive={LocalActiveEvents} localDeleted={LocalDeletedEvents}",
                    checkpoint.Events.Length, targetEvents.Length, deletedEventIndices.Length);
                return false;
            }
        }

        var bindings = Bind(manager).ToArray();
        if (checkpoint.Tickers.Length != bindings.Length)
        {
            Logger.Warning("Rejected campaign scheduler ticker count: source={SourceCount} local={LocalCount}", checkpoint.Tickers.Length, bindings.Length);
            return false;
        }
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
        for (int i = 0; i < targetEvents.Length; i++)
        {
            var state = checkpoint.Events[i];
            var target = targetEvents[i];
            if (state == null || target == null || state.TriggerPeriodTicks <= 0 ||
                state.InitialWaitTicks < 0 || state.NextTriggerTicks < 0 ||
                !string.Equals(state.Description, target.description, StringComparison.Ordinal) ||
                state.TriggerPeriodTicks != target.TriggerPeriod.NumTicks)
            {
                Logger.Warning("Rejected campaign scheduler checkpoint at periodic event {EventIndex}; event definition or phase is invalid. " +
                    "Source=({SourceDescription},{SourcePeriod},{SourceInitialWait},{SourceNextTrigger}); " +
                    "local=({LocalDescription},{LocalPeriod},{LocalInitialWait},{LocalNextTrigger})",
                    i, state?.Description, state?.TriggerPeriodTicks, state?.InitialWaitTicks, state?.NextTriggerTicks,
                    target?.description, target?.TriggerPeriod.NumTicks, target?.InitialWait.NumTicks, target?.NextTriggerTime.NumTicks);
                return false;
            }
            bool callbacksResolved = TryCaptureCallbacks(target, out var callbacks);
            if (state.CallbackIdentities == null || !callbacksResolved ||
                !state.CallbackIdentities.SequenceEqual(callbacks, StringComparer.Ordinal))
            {
                Logger.Warning("Rejected campaign scheduler checkpoint at periodic event {EventIndex}; callback owners or order differ. " +
                    "Source={SourceCallbacks}; local={LocalCallbacks}", i,
                    string.Join(";", state.CallbackIdentities ?? Array.Empty<string>()),
                    callbacksResolved ? string.Join(";", callbacks) : "<unresolved>");
                return false;
            }
            // Army.OnAfterLoad rebuilds InitialWait from the load-time clock and its saved creation
            // time. It is constructor phase state, not an event definition. Resume the writer's
            // complete timer state while preserving the native callback, period and event ordering.
            apply.Add(() =>
            {
                target.InitialWait = new CampaignTime(state.InitialWaitTicks);
                target.NextTriggerTime = new CampaignTime(state.NextTriggerTicks);
                target.isEventDeleted = state.IsDeleted;
            });
        }

        // Validation precedes every mutation. Global entity lists retain their native ordering.
        // Match native DeleteMarkedPeriodicEvents only after the full active graph is proven.
        for (int i = deletedEventIndices.Length - 1; i >= 0; i--) events.RemoveAt(deletedEventIndices[i]);
        foreach (var action in apply) action();
        manager._lastGameTime = new CampaignTime(checkpoint.LastGameTimeTicks);
        if (deletedEventIndices.Length != 0)
            Logger.Information("Removed {DeletedEvents} native-deleted scheduler events after full checkpoint validation; active events={ActiveEvents}",
                deletedEventIndices.Length, targetEvents.Length);
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
        yield return Wrap(nameof(manager._caravanMobilePartyPartialHourlyAiEventTicker), manager._caravanMobilePartyPartialHourlyAiEventTicker, true);
        yield return Wrap(nameof(manager._garrisonMobilePartyPartialHourlyAiEventTicker), manager._garrisonMobilePartyPartialHourlyAiEventTicker, true);
        yield return Wrap(nameof(manager._militiaMobilePartyPartialHourlyAiEventTicker), manager._militiaMobilePartyPartialHourlyAiEventTicker, true);
        yield return Wrap(nameof(manager._villagerMobilePartyPartialHourlyAiEventTicker), manager._villagerMobilePartyPartialHourlyAiEventTicker, true);
        yield return Wrap(nameof(manager._customMobilePartyPartialHourlyAiEventTicker), manager._customMobilePartyPartialHourlyAiEventTicker, true);
        yield return Wrap(nameof(manager._banditMobilePartyPartialHourlyAiEventTicker), manager._banditMobilePartyPartialHourlyAiEventTicker, true);
        yield return Wrap(nameof(manager._lordMobilePartyPartialHourlyAiEventTicker), manager._lordMobilePartyPartialHourlyAiEventTicker, true);
        yield return Wrap(nameof(manager._partiesWithoutPartyComponentsPartialHourlyAiEventTicker), manager._partiesWithoutPartyComponentsPartialHourlyAiEventTicker, true);
        yield return Wrap(nameof(manager._patrolPartyPartialHourlyAiEventTicker), manager._patrolPartyPartialHourlyAiEventTicker, true);
    }

    private ITicker Wrap<T>(string name, CampaignPeriodicEventManager.PeriodicTicker<T> ticker, bool derivedPartyList = false) where T : class =>
        new Ticker<T>(name, ticker, objectManager, derivedPartyList);

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
        private readonly bool derivedPartyList;
        public Ticker(string name, CampaignPeriodicEventManager.PeriodicTicker<T> ticker, IObjectManager objects, bool derivedPartyList)
        { Name = name; this.ticker = ticker; this.objects = objects; this.derivedPartyList = derivedPartyList; }

        public bool TryCapture(out CampaignPeriodicTickerState state)
        {
            state = null;
            if (ticker?._list == null) return RejectCapture("null-ticker-or-list");
            if (ticker._currentFrameToTickListFlattened != null && ticker._currentFrameToTickListFlattened.Count != 0)
                return RejectCapture("scratch-not-empty");
            if (ticker._doParallel && ticker._currentFrameToTickListFlattened == null) return RejectCapture("parallel-scratch-missing");
            if (!ValidCursor(ticker.Index, ticker.TickDebt)) return RejectCapture("cursor-or-debt");
            var handles = new List<uint>();
            var capturedValues = new List<T>();
            foreach (var value in ticker._list)
            {
                uint handle = 0;
                if (value == null || !objects.TryGetHandleWithLogging(value, out handle))
                    return RejectCapture(value == null ? "null-value" : "unregistered-value", handles.Count, value, handle);
                handles.Add(handle);capturedValues.Add(value);
            }
            if (handles.Distinct().Count() != handles.Count)
                return RejectCapture("duplicate-handles", duplicates: string.Join("|", handles.Select((handle, index) => (handle, index))
                    .GroupBy(entry => entry.handle).Where(group => group.Count() > 1).SelectMany(group => group).Take(12)
                    .Select(entry => $"handle={entry.handle};index={entry.index};{DescribeValue(capturedValues[entry.index])}")));
            state = new CampaignPeriodicTickerState(Name, ticker.Index, ticker.TickDebt, handles);
            return true;
        }

        private bool RejectCapture(string reason, int ordinal = -1, T value = null, uint? handle = null, string duplicates = null)
        {
            Logger.Warning("Scheduler ticker capture rejected {TickerName} reason {Reason} listCount {ListCount} scratchCount {ScratchCount} parallel {DoParallel} index {Index} debt {Debt} debtBits {DebtBits} valueOrdinal {ValueOrdinal} value {Value} handle {Handle} duplicatesFirst12 {Duplicates}",
                Name, reason, ticker?._list?.Count, ticker?._currentFrameToTickListFlattened?.Count, ticker?._doParallel,
                ticker?.Index, ticker?.TickDebt, ticker == null ? (long?)null : BitConverter.DoubleToInt64Bits(ticker.TickDebt),
                ordinal, DescribeValue(value), handle, duplicates);
            return false;
        }

        private static string DescribeValue(T value) => value == null ? "null" :
            $"id={(value as MBObjectBase)?.StringId};ref={RuntimeHelpers.GetHashCode(value)}";

        public bool TryPrepare(CampaignPeriodicTickerState state, out Action apply)
        {
            apply = null;
            if (state?.OrderedHandles == null || !string.Equals(state.Name, Name, StringComparison.Ordinal) ||
                !ValidCursor(state.Index, state.TickDebt) || !TryCapture(out var local)) return false;
            MBReadOnlyList<T> ordered = ticker._list;
            if (!local.OrderedHandles.SequenceEqual(state.OrderedHandles))
            {
                if ((!derivedPartyList && typeof(T) != typeof(Settlement)) ||
                    local.OrderedHandles.Length != state.OrderedHandles.Length ||
                    !local.OrderedHandles.OrderBy(value => value).SequenceEqual(state.OrderedHandles.OrderBy(value => value)))
                {
                    Logger.Warning("Scheduler ticker {TickerName} mismatch: source count={SourceCount} local count={LocalCount}, source-only={SourceOnly}, local-only={LocalOnly}",
                        Name, state.OrderedHandles.Length, local.OrderedHandles.Length,
                        string.Join(",", state.OrderedHandles.Except(local.OrderedHandles).Take(12)),
                        string.Join(",", local.OrderedHandles.Except(state.OrderedHandles).Take(12)));
                    return false;
                }
                var byHandle = local.OrderedHandles.Zip(ticker._list, (handle, value) => (handle, value)).ToDictionary(pair => pair.handle, pair => pair.value);
                ordered = new MBReadOnlyList<T>(state.OrderedHandles.Select(handle => byHandle[handle]));
            }
            apply = () =>
            {
                if (derivedPartyList && !ReferenceEquals(ordered, ticker._list))
                {
                    // Component partitions use object-based Add/Remove. Keep their native live
                    // list reference so later creation and destruction remain visible to the ticker.
                    ticker._list.Clear();
                    ticker._list.AddRange(ordered);
                }
                else ticker._list = ordered;
                ticker.Index = state.Index;
                ticker.TickDebt = state.TickDebt;
            };
            return true;
        }

        private static bool ValidCursor(int index, double debt) =>
            index >= -1 && !double.IsNaN(debt) && !double.IsInfinity(debt) && debt >= 0 && debt <= 1;
    }
}
