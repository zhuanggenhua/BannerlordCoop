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
    [ThreadStatic] private static TickerBatch current;
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var type in new[] { typeof(MobileParty), typeof(Settlement), typeof(Town), typeof(Hero), typeof(Clan) })
            yield return AccessTools.Method(typeof(CampaignPeriodicEventManager.PeriodicTicker<>).MakeGenericType(type), "PeriodicTickSome");
    }

    [HarmonyPrefix]
    private static void Prefix(object __instance, double timeUnitsElapsed, out TickerBatch __state)
    {
        __state = null;
        var campaign = Campaign.Current;
        if (campaign?._campaignPeriodicEventManager == null) return;
        var field = typeof(CampaignPeriodicEventManager).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(value => ReferenceEquals(value.GetValue(campaign._campaignPeriodicEventManager), __instance));
        if (field == null) return;
        ContainerProvider.TryResolve<IControllerIdProvider>(out var controller);
        ContainerProvider.TryResolve<ICampaignAuthorityContext>(out var authority);
        AuthorityLease lease = default;
        bool known = authority?.TryGetCurrentLease(out lease) == true;
        __state = new TickerBatch { Parent = current, Instance = __instance, Collector = CampaignCycleDispatchPatches.For(campaign),
            Audit = new CampaignTickerAudit { Ticker = field.Name,
                Node = controller?.ControllerId, AuthorityId = lease.AuthorityId, RoomId = lease.RoomId,
                Epoch = lease.Epoch, LeaseKnown = known, LocalWriter = authority?.IsLocalCampaignAuthority == true } };
        try
        {
            var ticker = Traverse.Create(__instance);
            __state.Ids = ((System.Collections.IEnumerable)ticker.Field("_list").GetValue()).Cast<MBObjectBase>().Select(value => value.StringId).ToArray();
            __state.Expected = Expected(__state.Ids, ticker.Property<int>("Index").Value, ticker.Property<double>("TickDebt").Value,
                timeUnitsElapsed, out __state.EndIndex, out __state.EndDebt);
        }
        catch (Exception error) { __state.DiagnosticError = error.ToString(); }
        current = __state;
    }

    internal static string[] Expected(string[] ids, int index, double debt, double elapsed, out int endIndex, out double endDebt)
    {
        var expected = new List<string>();
        if (ids.Length == 0) { endIndex = index; endDebt = 0; return expected.ToArray(); }
        debt += elapsed * ids.Length;
        while (debt > 1)
        {
            index++;
            if (index >= ids.Length) index = 0;
            expected.Add(ids[index]); debt--;
        }
        endIndex = index; endDebt = debt; return expected.ToArray();
    }

    internal static void Completed(object entity)
    { if (current != null && entity is MBObjectBase value) current.Completed.Add(value.StringId); }

    [HarmonyFinalizer]
    private static Exception Finalizer(Exception __exception, TickerBatch __state)
    {
        if (__state == null) return __exception;
        current = __state.Parent;
        bool matches = false;
        if (__exception == null && __state.DiagnosticError == null)
        {
            try
            {
                var ticker = Traverse.Create(__state.Instance);
                var ids = ((System.Collections.IEnumerable)ticker.Field("_list").GetValue()).Cast<MBObjectBase>().Select(value => value.StringId).ToArray();
                matches = __state.Expected.SequenceEqual(__state.Completed) && __state.Ids.SequenceEqual(ids) &&
                    ticker.Property<int>("Index").Value == __state.EndIndex && ticker.Property<double>("TickDebt").Value == __state.EndDebt;
            }
            catch (Exception error) { __state.DiagnosticError = error.ToString(); }
        }
        var audit = __state.Audit;
        audit.Expected = __state.Expected.Length; audit.Completed = __state.Completed.Count;
        audit.ExpectedHash = CampaignCycleDiagnostics.Hash(string.Join("|", __state.Expected));
        audit.CompletedHash = CampaignCycleDiagnostics.Hash(string.Join("|", __state.Completed));
        audit.Mismatches = matches ? 0 : 1;
        if (!matches) audit.FirstFailure = $"expected={string.Join(",", __state.Expected.Take(12))}; completed={string.Join(",", __state.Completed.Take(12))}; originalException={__exception}; diagnosticError={__state.DiagnosticError}";
        __state.Collector.Audit(audit);
        return __exception;
    }

    internal sealed class TickerBatch
    {
        public TickerBatch Parent;
        public object Instance;
        public CampaignCycleDiagnostics Collector;
        public string DiagnosticError;
        public string[] Ids = Array.Empty<string>(), Expected = Array.Empty<string>();
        public readonly List<string> Completed = new();
        public int EndIndex;
        public double EndDebt;
        public CampaignTickerAudit Audit;
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
    private static void Postfix(object[] __args, bool __runOriginal)
    { if (__runOriginal) CampaignTickerEvidencePatches.Completed(__args[0]); }
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
    private static void Postfix(MobileParty __instance, bool __runOriginal)
    { if (__runOriginal) CampaignTickerEvidencePatches.Completed(__instance); }
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
