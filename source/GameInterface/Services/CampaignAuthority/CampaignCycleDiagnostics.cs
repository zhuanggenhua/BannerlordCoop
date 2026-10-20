#if DEBUG
using Common;
using Common.Logging;
using GameInterface.Services.Entity;
using HarmonyLib;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.ObjectSystem;
using TaleWorlds.CampaignSystem.GameComponents;
using GameInterface.Services.Clans.Interfaces;

namespace GameInterface.Services.CampaignAuthority;

internal sealed class CampaignCycleDiagnostics
{
    private readonly object gate = new();
    private readonly Dictionary<string, CampaignCycleRow> rows = new(StringComparer.Ordinal);
    private readonly int capacity;
    private readonly Dictionary<string, CampaignTickerAudit> audits = new(StringComparer.Ordinal);
    private readonly List<CampaignEconomicRow> effects = new();
    private readonly Dictionary<string, long> simulation = new(StringComparer.Ordinal);
    private int? lastRealTickFrame;
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");
    public bool Overflowed { get; private set; }

    public CampaignCycleDiagnostics(int capacity = 32768)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
    }

    public bool Record(string kind, string party, long hour, string node, bool known, bool writer, AuthorityLease lease,
        long dueTicks = -1)
    {
        long bucket = kind == "HourlyTick" ? hour : hour / 24;
        string key = string.Join("|", kind, party, dueTicks >= 0 ? dueTicks : bucket, node, lease.RoomId, lease.AuthorityId, lease.Epoch, known, writer);
        lock (gate)
        {
            if (rows.TryGetValue(key, out var prior))
            {
                prior.Count++;
                return true;
            }
            if (rows.Count >= capacity)
            {
                Overflowed = true;
                return false;
            }
            rows.Add(key, new CampaignCycleRow
            {
                Kind = kind, Party = party, Day = hour / 24, Hour = hour, Node = node,
                LeaseKnown = known, LocalWriter = writer, RoomId = lease.RoomId,
                AuthorityId = lease.AuthorityId, Epoch = lease.Epoch,
                WriterController = lease.AuthorityControllerId, Count = 1,
                DueTicks = dueTicks,
            });
            return true;
        }
    }

    public void Audit(CampaignTickerAudit batch)
    {
        string key = string.Join("|", batch.Ticker, batch.Node, batch.Epoch, batch.AuthorityId);
        lock (gate)
        {
            if (!audits.TryGetValue(key, out var total)) audits.Add(key, total = new CampaignTickerAudit
            { Ticker = batch.Ticker, Node = batch.Node, Epoch = batch.Epoch, AuthorityId = batch.AuthorityId,
                RoomId = batch.RoomId, LocalWriter = batch.LocalWriter, LeaseKnown = batch.LeaseKnown });
            total.Batches++;
            total.Expected += batch.Expected;
            total.Completed += batch.Completed;
            total.Mismatches += batch.Mismatches;
            total.ExpectedHash = Hash(total.ExpectedHash + batch.ExpectedHash);
            total.CompletedHash = Hash(total.CompletedHash + batch.CompletedHash);
            if (batch.Mismatches != 0 && total.FirstFailure == null) total.FirstFailure = batch.FirstFailure;
        }
    }

    public CampaignTickerAudit[] AuditSnapshot()
    { lock (gate) return audits.Values.OrderBy(row => row.Ticker).ThenBy(row => row.Epoch).Select(row => row.Copy()).ToArray(); }

    internal static string Hash(string value)
    { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", ""); }

    public void Simulation(string name)
    { lock (gate) { simulation.TryGetValue(name, out long count); simulation[name] = count + 1; } }
    public Dictionary<string, long> SimulationSnapshot()
    { lock (gate) return new Dictionary<string, long>(simulation); }
    public void RealTickFrame(int frame)
    {
        lock (gate)
        {
            if (lastRealTickFrame == frame) Simulation("Campaign.RealTick.sameFrameDuplicate.failed");
            lastRealTickFrame = frame;
        }
    }
    public void Effect(CampaignEconomicRow row)
    { lock (gate) { if (effects.Count >= capacity) Overflowed = true; else effects.Add(row); } }
    public CampaignEconomicRow[] EffectSnapshot()
    { lock (gate) return effects.Select(row => row.Copy()).ToArray(); }

    public CampaignCycleRow[] Snapshot()
    {
        lock (gate) return rows.Values.OrderBy(row => row.Hour).ThenBy(row => row.Kind)
            .ThenBy(row => row.Party).Select(row => row.Copy()).ToArray();
    }
}

internal sealed class CampaignCycleRow
{
    public long DueTicks { get; set; } = -1;
    public string Kind { get; set; }
    public string Party { get; set; }
    public long Day { get; set; }
    public long Hour { get; set; }
    public string Node { get; set; }
    public bool LeaseKnown { get; set; }
    public bool LocalWriter { get; set; }
    public string RoomId { get; set; }
    public string AuthorityId { get; set; }
    public long Epoch { get; set; }
    public string WriterController { get; set; }
    public int Count { get; set; }
    public CampaignCycleRow Copy() => (CampaignCycleRow)MemberwiseClone();
}

internal sealed class CampaignTickerAudit
{
    public string Ticker { get; set; }
    public string Node { get; set; }
    public string AuthorityId { get; set; }
    public string RoomId { get; set; }
    public long Epoch { get; set; }
    public bool LocalWriter { get; set; }
    public bool LeaseKnown { get; set; }
    public long Batches { get; set; }
    public long Expected { get; set; }
    public long Completed { get; set; }
    public long Mismatches { get; set; }
    public string ExpectedHash { get; set; } = "";
    public string CompletedHash { get; set; } = "";
    public string FirstFailure { get; set; }
    public CampaignTickerAudit Copy() => (CampaignTickerAudit)MemberwiseClone();
}

internal sealed class CampaignEconomicRow
{
    public string Kind, Entity, Hero, Node;
    public long Epoch, Ticks;
    public int? GoldBefore, GoldAfter, Wage;
    public bool Known, LocalWriter;
    public string Error;
    public CampaignEconomicRow Copy() => (CampaignEconomicRow)MemberwiseClone();
}

[HarmonyPatch(typeof(CampaignEventDispatcher))]
internal static class CampaignCycleDispatchPatches
{
    private static readonly ConditionalWeakTable<Campaign, CampaignCycleDiagnostics> Campaigns = new();
    private static readonly ILogger Logger = LogManager.GetLogger<CampaignCycleDiagnostics>();
    internal static CampaignCycleDiagnostics For(Campaign campaign) => Campaigns.GetValue(campaign, _ => new CampaignCycleDiagnostics());

    [HarmonyPatch(nameof(CampaignEventDispatcher.HourlyTick))]
    [HarmonyPostfix]
    private static void Hourly(bool __runOriginal) { if (__runOriginal) Observe("HourlyTick", null); }

    [HarmonyPatch(nameof(CampaignEventDispatcher.DailyTick))]
    [HarmonyPostfix]
    private static void Daily(bool __runOriginal) { if (__runOriginal) Observe("DailyTick", null); }

    [HarmonyPatch(nameof(CampaignEventDispatcher.DailyTickParty))]
    [HarmonyPostfix]
    private static void DailyParty(MobileParty mobileParty, bool __runOriginal)
    { if (__runOriginal) Observe("DailyTickParty", mobileParty?.StringId); }

    private static void Observe(string kind, string party)
    {
        var campaign = Campaign.Current;
        if (campaign == null) return;
        ContainerProvider.TryResolve<IControllerIdProvider>(out var controller);
        ContainerProvider.TryResolve<ICampaignAuthorityContext>(out var authority);
        AuthorityLease lease = default;
        bool known = authority?.TryGetCurrentLease(out lease) == true;
        var collector = For(campaign);
        bool alreadyOverflowed = collector.Overflowed;
        var periodic = kind == "HourlyTick" ? campaign._hourlyTickEvent : kind == "DailyTick" ? campaign._dailyTickEvent : null;
        if (!collector.Record(kind, party, (long)Math.Floor(CampaignTime.Now.ToHours),
                controller?.ControllerId, known, authority?.IsLocalCampaignAuthority == true, lease,
                periodic?.NextTriggerTime.NumTicks ?? -1) && !alreadyOverflowed)
            Logger.Error("Campaign cycle diagnostics exceeded its 32768 entry limit; reliability evidence is incomplete.");
    }
}

[HarmonyPatch]
internal static class CampaignTickerEvidencePatches
{
    internal const int EvidenceLimit = 32768;
    [ThreadStatic] private static TickerBatch current;
    internal static IEnumerable<MethodBase> GroupTargetMethods()
    {
        foreach (string name in new[] { "MobilePartyHourlyTick", "PeriodicHourlyTick", "PeriodicDailyTick", "PeriodicQuarterDailyTick", "TickPartialHourlyAi" })
            yield return AccessTools.Method(typeof(CampaignPeriodicEventManager), name);
    }
    internal static IEnumerable<MethodBase> TargetMethods() => GroupTargetMethods()
        .SelectMany(method => Bindings(method.Name))
        .Select(binding => AccessTools.Method(AccessTools.Field(typeof(CampaignPeriodicEventManager), binding.Name).FieldType,
            "PeriodicTickSome", new[] { typeof(double) })).Distinct();

    internal static (string Name, string Callback)[] Bindings(string method)
    {
        switch (method)
        {
            case "MobilePartyHourlyTick": return new[] { ("_mobilePartyHourlyTicker", "MobileParty.HourlyTick") };
            case "PeriodicHourlyTick": return new[] { ("_hourlyTickMobilePartyTicker", "CampaignEventDispatcher.HourlyTickParty"),
                ("_hourlyTickSettlementTicker", "CampaignEventDispatcher.HourlyTickSettlement"), ("_hourlyTickClanTicker", "CampaignEventDispatcher.HourlyTickClan") };
            case "PeriodicDailyTick": return new[] { ("_dailyTickPartyTicker", "CampaignEventDispatcher.DailyTickParty"),
                ("_mobilePartyDailyTicker", "MobileParty.DailyTick"), ("_dailyTickTownTicker", "CampaignEventDispatcher.DailyTickTown"),
                ("_dailyTickSettlementTicker", "CampaignEventDispatcher.DailyTickSettlement"), ("_dailyTickHeroTicker", "CampaignEventDispatcher.DailyTickHero"),
                ("_dailyTickClanTicker", "CampaignEventDispatcher.DailyTickClan") };
            case "PeriodicQuarterDailyTick": return new[] { ("_quarterDailyPartyTicker", "CampaignEventDispatcher.QuarterDailyPartyTick") };
            case "TickPartialHourlyAi": return new[] { ("_caravanMobilePartyPartialHourlyAiEventTicker", "CampaignEventDispatcher.TickPartialHourlyAi"),
                ("_garrisonMobilePartyPartialHourlyAiEventTicker", "CampaignEventDispatcher.TickPartialHourlyAi"),
                ("_militiaMobilePartyPartialHourlyAiEventTicker", "CampaignEventDispatcher.TickPartialHourlyAi"),
                ("_villagerMobilePartyPartialHourlyAiEventTicker", "CampaignEventDispatcher.TickPartialHourlyAi"),
                ("_customMobilePartyPartialHourlyAiEventTicker", "CampaignEventDispatcher.TickPartialHourlyAi"),
                ("_patrolPartyPartialHourlyAiEventTicker", "CampaignEventDispatcher.TickPartialHourlyAi"),
                ("_banditMobilePartyPartialHourlyAiEventTicker", "CampaignEventDispatcher.TickPartialHourlyAi"),
                ("_lordMobilePartyPartialHourlyAiEventTicker", "CampaignEventDispatcher.TickPartialHourlyAi"),
                ("_partiesWithoutPartyComponentsPartialHourlyAiEventTicker", "CampaignEventDispatcher.TickPartialHourlyAi") };
            default: throw new ArgumentException("Unknown native ticker group", nameof(method));
        }
    }

    [HarmonyPrefix]
    private static void Prefix(object __instance, double timeUnitsElapsed, out TickerBatch __state)
    {
        __state = null;
        var campaign = Campaign.Current;
        if (campaign?._campaignPeriodicEventManager == null) return;
        // Resolve by the actual instance, not the generic method's reported T: reference-type
        // instantiations can share native code. Only this Campaign's twenty ticker fields count.
        foreach (var method in GroupTargetMethods())
        foreach (var binding in Bindings(method.Name))
        {
            if (!ReferenceEquals(AccessTools.Field(typeof(CampaignPeriodicEventManager), binding.Name)
                    .GetValue(campaign._campaignPeriodicEventManager), __instance)) continue;
            ContainerProvider.TryResolve<IControllerIdProvider>(out var controller);
            ContainerProvider.TryResolve<ICampaignAuthorityContext>(out var authority);
            AuthorityLease lease = default;
            bool known = authority?.TryGetCurrentLease(out lease) == true;
            __state = BeginBatch(__instance, binding.Callback, CampaignCycleDispatchPatches.For(campaign),
                new CampaignTickerAudit { Ticker = binding.Name, Node = controller?.ControllerId,
                    AuthorityId = lease.AuthorityId, RoomId = lease.RoomId, Epoch = lease.Epoch,
                    LeaseKnown = known, LocalWriter = authority?.IsLocalCampaignAuthority == true }, timeUnitsElapsed);
            try { __state.Frame = TaleWorlds.Engine.Utilities.EngineFrameNo; }
            catch (Exception error) { __state.DiagnosticError = error.ToString(); }
            return;
        }
    }

    internal static TickerBatch BeginBatch(object instance, string callback, CampaignCycleDiagnostics collector,
        CampaignTickerAudit audit, double elapsed)
    {
        var batch = new TickerBatch { Instance = instance, Collector = collector, Audit = audit,
            Parent = current, Depth = (current?.Depth ?? 0) + 1, StartedUtc = DateTime.UtcNow,
            ThreadId = Thread.CurrentThread.ManagedThreadId, Elapsed = elapsed };
        current = batch;
        try
        {
            var ticker = Traverse.Create(instance);
            batch.Ids = ReadIds(instance);
            batch.StartIndex = ticker.Property<int>("Index").Value;
            batch.StartDebt = ticker.Property<double>("TickDebt").Value;
            batch.Expected = Expected(batch.Ids, batch.StartIndex, batch.StartDebt, elapsed,
                out batch.EndIndex, out batch.EndDebt).Select(id => callback + "|" + id).ToArray();
        }
        catch (Exception error) { batch.DiagnosticError = error.ToString(); }
        return batch;
    }

    private static string[] ReadIds(object instance)
    {
        var ids = ((System.Collections.IEnumerable)Traverse.Create(instance).Field("_list").GetValue())
            .Cast<MBObjectBase>().Select(value => value.StringId).Take(EvidenceLimit + 1).ToArray();
        if (ids.Length > EvidenceLimit) throw new InvalidOperationException("Ticker evidence list exceeds complete capture limit; evidence incomplete.");
        return ids;
    }

    internal static string[] Expected(string[] ids, int index, double debt, double elapsed, out int endIndex, out double endDebt)
    {
        var expected = new List<string>();
        if (ids.Length == 0) { endIndex = index; endDebt = 0; return expected.ToArray(); }
        debt += elapsed * ids.Length;
        if (double.IsNaN(debt) || double.IsInfinity(debt)) throw new InvalidOperationException("Nonfinite ticker evidence debt.");
        while (debt > 1)
        {
            if (expected.Count == EvidenceLimit) throw new InvalidOperationException("Ticker callback prediction exceeds complete capture limit; evidence incomplete.");
            index++;
            if (index >= ids.Length) index = 0;
            expected.Add(ids[index]); debt--;
        }
        endIndex = index; endDebt = debt; return expected.ToArray();
    }

    internal static void Completed(string callback, object entity)
    {
        if (current == null || entity is not MBObjectBase value) return;
        if (current.Completed.Count == EvidenceLimit)
        { current.DiagnosticError = "Ticker completed callbacks exceed complete capture limit; evidence incomplete."; return; }
        current.Completed.Add(callback + "|" + value.StringId);
    }

    [HarmonyFinalizer]
    internal static Exception Finalizer(Exception __exception, bool __runOriginal, TickerBatch __state)
    {
        if (__state == null) return __exception;
        current = __state.Parent;
        if (!__runOriginal && __exception == null)
        {
            RecordSkipped(__state);
            return __exception;
        }
        return FinishBatch(__exception, __state);
    }

    internal static void RecordSkipped(TickerBatch batch)
    {
        batch.Collector.Simulation("CampaignTicker." + batch.Audit.Ticker + ".skipped");
        bool stillWriter = batch.Audit.LocalWriter;
        if (batch.Audit.LeaseKnown && ContainerProvider.TryResolve<ICampaignAuthorityContext>(out var context))
            stillWriter &= context.TryGetLocalAuthorityLease(out var lease) && lease.RoomId == batch.Audit.RoomId &&
                lease.AuthorityId == batch.Audit.AuthorityId && lease.Epoch == batch.Audit.Epoch;
        if (stillWriter) batch.Collector.Simulation("CampaignTicker.writerSkipped.failed");
    }

    internal static Exception FinishBatch(Exception originalException, TickerBatch batch)
    {
        bool sequenceMatches = batch.Expected.SequenceEqual(batch.Completed), membershipMatches = false,
            indexMatches = false, debtMatches = false;
        string[] actualIds = null;
        int? actualIndex = null;
        double? actualDebt = null;
        // Still capture final state on a native exception, without replacing that exception.
        try
        {
            var ticker = Traverse.Create(batch.Instance);
            actualIds = ReadIds(batch.Instance);
            actualIndex = ticker.Property<int>("Index").Value;
            actualDebt = ticker.Property<double>("TickDebt").Value;
            membershipMatches = batch.Ids.SequenceEqual(actualIds);
            indexMatches = actualIndex == batch.EndIndex;
            debtMatches = actualDebt == batch.EndDebt;
        }
        catch (Exception error) { batch.DiagnosticError = (batch.DiagnosticError == null ? "" : batch.DiagnosticError + "\n") + error; }
        bool matches = originalException == null && batch.DiagnosticError == null && sequenceMatches &&
            membershipMatches && indexMatches && debtMatches;
        var audit = batch.Audit;
        audit.Expected = batch.Expected.Length; audit.Completed = batch.Completed.Count;
        audit.ExpectedHash = CampaignCycleDiagnostics.Hash(string.Join("|", batch.Expected));
        audit.CompletedHash = CampaignCycleDiagnostics.Hash(string.Join("|", batch.Completed));
        audit.Mismatches = matches ? 0 : 1;
        if (!matches) audit.FirstFailure = JsonConvert.SerializeObject(new
        {
            sequenceMatches, membershipMatches, indexMatches, debtMatches,
            complete = batch.DiagnosticError == null,
            batch.StartedUtc, finishedUtc = DateTime.UtcNow, batch.Frame, batch.ThreadId, batch.Depth,
            finishedThreadId = Thread.CurrentThread.ManagedThreadId,
            nativeTicker = batch.Instance?.GetType().FullName,
            audit.Ticker, audit.Node, audit.RoomId, audit.AuthorityId, audit.Epoch, audit.LeaseKnown, audit.LocalWriter,
            beforeIds = batch.Ids, afterIds = actualIds, expected = batch.Expected, completed = batch.Completed,
            batch.StartIndex, expectedIndex = batch.EndIndex, actualIndex,
            startDebtBits = BitConverter.DoubleToInt64Bits(batch.StartDebt),
            elapsedBits = BitConverter.DoubleToInt64Bits(batch.Elapsed),
            expectedDebtBits = BitConverter.DoubleToInt64Bits(batch.EndDebt),
            actualDebtBits = actualDebt.HasValue ? (long?)BitConverter.DoubleToInt64Bits(actualDebt.Value) : null,
            originalException = originalException?.ToString(), diagnosticError = batch.DiagnosticError,
        });
        batch.Collector.Audit(audit);
        return originalException;
    }

    internal sealed class TickerBatch
    {
        public object Instance;
        public CampaignCycleDiagnostics Collector;
        public string DiagnosticError;
        public string[] Ids = Array.Empty<string>(), Expected = Array.Empty<string>();
        public readonly List<string> Completed = new();
        public int StartIndex, EndIndex;
        public double StartDebt, EndDebt, Elapsed;
        public CampaignTickerAudit Audit;
        public TickerBatch Parent;
        public int Frame, ThreadId, Depth;
        public DateTime StartedUtc;
    }
}

// A skipped outer group never enters its native tickers. Retain writer-skip evidence
// separately, without taking any list/cursor/debt snapshot across sibling callbacks.
[HarmonyPatch]
internal static class CampaignTickerGroupEvidencePatches
{
    internal static IEnumerable<MethodBase> TargetMethods() => CampaignTickerEvidencePatches.GroupTargetMethods();
    [HarmonyPrefix]
    private static void Prefix(CampaignPeriodicEventManager __instance, MethodBase __originalMethod,
        out CampaignTickerEvidencePatches.TickerBatch[] __state)
    {
        __state = null;
        var campaign = Campaign.Current;
        if (!ReferenceEquals(campaign?._campaignPeriodicEventManager, __instance)) return;
        ContainerProvider.TryResolve<IControllerIdProvider>(out var controller);
        ContainerProvider.TryResolve<ICampaignAuthorityContext>(out var authority);
        AuthorityLease lease = default;
        bool known = authority?.TryGetCurrentLease(out lease) == true;
        __state = CampaignTickerEvidencePatches.Bindings(__originalMethod.Name).Select(binding =>
            new CampaignTickerEvidencePatches.TickerBatch { Collector = CampaignCycleDispatchPatches.For(campaign),
                Audit = new CampaignTickerAudit { Ticker = binding.Name, Node = controller?.ControllerId,
                    RoomId = lease.RoomId, AuthorityId = lease.AuthorityId, Epoch = lease.Epoch,
                    LeaseKnown = known, LocalWriter = authority?.IsLocalCampaignAuthority == true } }).ToArray();
    }
    [HarmonyFinalizer]
    private static Exception Finalizer(Exception __exception, bool __runOriginal,
        CampaignTickerEvidencePatches.TickerBatch[] __state)
    {
        if (__state != null && !__runOriginal && __exception == null)
            foreach (var batch in __state) CampaignTickerEvidencePatches.RecordSkipped(batch);
        return __exception;
    }
}


[HarmonyPatch]
internal static class CampaignTickerCallbackEvidencePatches
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (string name in new[] { "DailyTickParty", "HourlyTickParty", "DailyTickSettlement", "HourlyTickSettlement",
            "DailyTickTown", "DailyTickHero", "DailyTickClan", "HourlyTickClan", "TickPartialHourlyAi", "QuarterDailyPartyTick" })
            yield return AccessTools.Method(typeof(CampaignEventDispatcher), name);
    }
    [HarmonyPostfix]
    private static void Postfix(object[] __args, MethodBase __originalMethod, bool __runOriginal)
    { if (__runOriginal) CampaignTickerEvidencePatches.Completed(__originalMethod.DeclaringType.Name + "." + __originalMethod.Name, __args[0]); }
}

[HarmonyPatch]
internal static class CampaignMobilePartyCallbackEvidencePatches
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(MobileParty), "HourlyTick");
        yield return AccessTools.Method(typeof(MobileParty), "DailyTick");
    }
    [HarmonyPostfix]
    private static void Postfix(MobileParty __instance, MethodBase __originalMethod, bool __runOriginal)
    { if (__runOriginal) CampaignTickerEvidencePatches.Completed("MobileParty." + __originalMethod.Name, __instance); }
}

[HarmonyPatch]
internal static class CampaignSimulationEvidencePatches
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (string name in new[] { "RealTick", "Tick", "LateAITickAux", "WaitAsyncTasks" }) yield return AccessTools.Method(typeof(Campaign), name);
        yield return AccessTools.Method(typeof(MobilePartyAi), "Tick");
    }
    [HarmonyFinalizer]
    private static Exception Finalizer(MethodBase __originalMethod, bool __runOriginal, Exception __exception)
    {
        if (Campaign.Current != null && __runOriginal)
        {
            var collector = CampaignCycleDispatchPatches.For(Campaign.Current);
            collector.Simulation(__originalMethod.DeclaringType.Name + "." + __originalMethod.Name +
                (__exception == null ? ".completed" : ".failed"));
            if (__originalMethod.DeclaringType == typeof(Campaign) && __originalMethod.Name == "RealTick" && __exception == null)
                collector.RealTickFrame(TaleWorlds.Engine.Utilities.EngineFrameNo);
        }
        return __exception;
    }
}

[HarmonyPatch(typeof(ClanVariablesCampaignBehaviorInterface), "DailyTickClan")]
internal static class CampaignEconomicEvidencePatches
{
    [HarmonyPrefix]
    internal static void Prefix(object[] __args, MethodBase __originalMethod, out CampaignEconomicRow __state)
    {
        __state = null;
        if (Campaign.Current == null) return;
        bool wage = __originalMethod.Name == "CalculatePartyWage";
        if (wage && !((bool)__args[2])) return;
        var party = wage ? (MobileParty)__args[0] : null;
        var clan = wage ? null : (Clan)__args[1];
        var hero = party?.LeaderHero ?? clan?.Leader;
        ContainerProvider.TryResolve<IControllerIdProvider>(out var controller);
        ContainerProvider.TryResolve<ICampaignAuthorityContext>(out var context);
        AuthorityLease lease = default;
        bool known = context?.TryGetCurrentLease(out lease) == true;
        __state = new CampaignEconomicRow { Kind = wage ? "Wage" : "ClanFinance", Entity = party?.StringId ?? clan?.StringId,
            Hero = hero?.StringId, GoldBefore = hero?.Gold, Ticks = CampaignTime.Now.NumTicks,
            Node = controller?.ControllerId, Epoch = lease.Epoch, Known = known, LocalWriter = context?.IsLocalCampaignAuthority == true };
    }
    [HarmonyFinalizer]
    private static Exception Finalizer(object[] __args, Exception __exception, CampaignEconomicRow __state) =>
        Complete(__args, null, __exception, __state);

    internal static Exception Complete(object[] args, int? result, Exception exception, CampaignEconomicRow row)
    {
        if (row == null) return exception;
        row.GoldAfter = (row.Kind == "Wage" ? ((MobileParty)args[0]).LeaderHero : ((Clan)args[1]).Leader)?.Gold;
        row.Wage = result;
        row.Error = exception?.ToString();
        CampaignCycleDispatchPatches.For(Campaign.Current).Effect(row);
        return exception;
    }
}

[HarmonyPatch(typeof(DefaultClanFinanceModel), "CalculatePartyWage")]
internal static class CampaignWageEvidencePatches
{
    [HarmonyPrefix]
    private static void Prefix(object[] __args, MethodBase __originalMethod, out CampaignEconomicRow __state) =>
        CampaignEconomicEvidencePatches.Prefix(__args, __originalMethod, out __state);
    [HarmonyFinalizer]
    private static Exception Finalizer(object[] __args, int __result, Exception __exception, CampaignEconomicRow __state) =>
        CampaignEconomicEvidencePatches.Complete(__args, __exception == null ? __result : (int?)null, __exception, __state);
}
#endif
