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
using System.Reflection.Emit;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.ObjectSystem;
using TaleWorlds.CampaignSystem.GameComponents;
using GameInterface.Services.Clans.Interfaces;
using Common.Util;
using GameInterface.Services.Players;
using GameInterface.Services.ObjectManager;

namespace GameInterface.Services.CampaignAuthority;

internal sealed class CampaignCycleDiagnostics
{
    private readonly object gate = new();
    private readonly Dictionary<string, CampaignCycleRow> rows = new(StringComparer.Ordinal);
    private readonly int capacity;
    private readonly Dictionary<string, CampaignTickerAudit> audits = new(StringComparer.Ordinal);
    private readonly List<CampaignEconomicRow> effects = new();
    private readonly Dictionary<string, long> simulation = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CampaignAiTickEvidence> firstAiEvidence = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CampaignSimulationRoleGroup> roleGroups = new(StringComparer.Ordinal);
    private bool roleOverflowed;
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
            total.MembershipChanges += batch.MembershipChanges;
            total.MembershipChangesHash = Hash(total.MembershipChangesHash + batch.MembershipChangesHash);
            if (total.FirstMembershipChange == null) total.FirstMembershipChange = batch.FirstMembershipChange;
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
    internal CampaignSimulationSnapshot SimulationEvidenceSnapshot()
    {
        lock (gate)
        {
            var counts = new Dictionary<string, long>(simulation);
            foreach (string key in CampaignAiTickEvidence.CounterKeys)
                if (!counts.ContainsKey(key)) counts.Add(key, 0);
            foreach (string method in CampaignNativeBoundary.Methods)
            {
                foreach (string outcome in CampaignAiTickEvidence.Outcomes)
                    if (!counts.ContainsKey(method + "." + outcome)) counts.Add(method + "." + outcome, 0);
                if (!counts.ContainsKey(method + ".beforeOriginal.failed")) counts.Add(method + ".beforeOriginal.failed", 0);
            }
            return new CampaignSimulationSnapshot { Counts = counts,
                FirstEntries = firstAiEvidence.Values.Select(value => value.Copy()).ToArray(),
                RoleGroups = roleGroups.Values.Select(value => value.Copy()).ToArray(), RoleOverflowed = roleOverflowed };
        }
    }
    internal void NativeTick(string method, CampaignNativeBoundary entry, CampaignNativeBoundary exit, bool ranOriginal, Exception error)
    {
        string outcome = !ranOriginal ? "skipped" : error == null ? "completed" : "failed";
        lock (gate)
        {
            Simulation(method + "." + outcome);
            if (!ranOriginal && error != null) Simulation(method + ".beforeOriginal.failed");
            RecordRole(method, outcome, CampaignNativeBoundary.Classify(method, entry, exit, ranOriginal, error), entry, exit, error: error);
        }
    }
    private void RecordRole(string method, string outcome, string classification, CampaignNativeBoundary entry, CampaignNativeBoundary exit,
        string aiClassification = null, Exception error = null, bool aiUnexpectedWorld = false)
    {
        entry = entry ?? new CampaignNativeBoundary();exit = exit ?? new CampaignNativeBoundary();
        string key = JsonConvert.SerializeObject(new { method, outcome, classification, aiClassification, aiUnexpectedWorld, error = error?.ToString(),
            entry = entry.ContextKey(), exit = exit.ContextKey(), entryBranch = entry.BranchKey(), exitBranch = exit.BranchKey() });
        if (roleGroups.TryGetValue(key, out var group))
        { group.Count++;group.LastEntry = entry.Copy();group.LastExit = exit.Copy();return; }
        if (roleGroups.Count >= Math.Min(capacity, 256)) { roleOverflowed = Overflowed = true;return; }
        roleGroups.Add(key, new CampaignSimulationRoleGroup { Method = method, Outcome = outcome, Classification = classification,
            AiClassification = aiClassification, AiUnexpectedWorld = aiUnexpectedWorld, Error = error?.ToString(),
            Count = 1, FirstEntry = entry.Copy(), FirstExit = exit.Copy(), LastEntry = entry.Copy(), LastExit = exit.Copy() });
    }
    internal void AiTick(CampaignAiTickEvidence entry, CampaignAiTickEvidence exit, bool ranOriginal, Exception error,
        CampaignNativeBoundary nativeEntry = null, CampaignNativeBoundary nativeExit = null)
    {
        string outcome = !ranOriginal ? "skipped" : error == null ? "completed" : "failed";
        entry.RanOriginal = ranOriginal;entry.Outcome = outcome;entry.Error = error?.ToString();
        entry.FinishedUtc = DateTime.UtcNow;entry.ExitLease = exit.Lease;entry.ExitFrozen = exit.Frozen;
        entry.ExitReason = exit.Reason;entry.ExitDiagnosticError = exit.DiagnosticError;
        entry.ExitRole = exit.Role;entry.ExitPartyRole = exit.PartyRole;entry.ExitClassification = exit.Classification;
        entry.ExitPartyId = exit.PartyId;entry.ExitPartyStringId = exit.PartyStringId;
        entry.ExitObjectController = exit.ObjectController;entry.ExitAiIdentity = exit.AiIdentity;
        entry.ContextChanged = !entry.SameContext(exit);
        entry.UnexpectedWorld |= entry.ContextChanged;
        lock (gate)
        {
            Simulation("MobilePartyAi.Tick.observed");
            Simulation("MobilePartyAi.Tick." + outcome);
            Simulation("MobilePartyAi.Tick." + entry.Classification + "." + outcome);
            if (entry.UnexpectedWorld && ranOriginal) Simulation("MobilePartyAi.Tick.unexpectedWorld." + outcome);
            if (!ranOriginal && error != null) Simulation("MobilePartyAi.Tick.beforeOriginal.failed");
            string role = !ranOriginal ? "skipped" : error != null || entry.UnexpectedWorld ||
                !CampaignNativeBoundary.SameQualifiedContext(nativeEntry, nativeExit, requireWriter: false) ? "unexpected" : entry.Classification;
            RecordRole("MobilePartyAi.Tick", outcome, role, nativeEntry?.WithAi(entry), nativeExit?.WithAi(exit),
                entry.Classification, error, entry.UnexpectedWorld && ranOriginal);
            string key = entry.Classification + "|" + outcome + "|" + entry.UnexpectedWorld;
            if (!firstAiEvidence.ContainsKey(key))
            {
                if (firstAiEvidence.Count >= capacity) Overflowed = true;
                else firstAiEvidence.Add(key, entry.Copy());
            }
        }
    }
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

internal sealed class CampaignSimulationSnapshot
{
    public Dictionary<string, long> Counts;
    public CampaignAiTickEvidence[] FirstEntries;
    public CampaignSimulationRoleGroup[] RoleGroups;
    public bool RoleOverflowed;
}

internal sealed class CampaignSimulationRoleGroup
{
    public string Method, Outcome, Classification, AiClassification, Error;
    public bool AiUnexpectedWorld;
    public long Count;
    public CampaignNativeBoundary FirstEntry, FirstExit, LastEntry, LastExit;
    public CampaignSimulationRoleGroup Copy() => new() { Method = Method, Outcome = Outcome, Classification = Classification,
        AiClassification = AiClassification, AiUnexpectedWorld = AiUnexpectedWorld, Error = Error,
        Count = Count, FirstEntry = FirstEntry.Copy(), FirstExit = FirstExit.Copy(), LastEntry = LastEntry.Copy(), LastExit = LastExit.Copy() };
}

internal sealed class CampaignNativeBoundary
{
    [JsonIgnore] internal Campaign ActualCampaign;
    internal static readonly string[] Methods = { "Campaign.RealTick", "Campaign.Tick", "Campaign.LateAITickAux" };
    public string Node, CampaignId, DiagnosticError;
    public string AiPartyId, AiPartyRole, AiController;
    public int AiIdentity;
    public bool AiMainParty;
    public int LateAiCoverageVersion, LateAiPartiesThinkAttempts;
    public bool LateAiScopeMatched;
    public AuthorityLease Lease;
    public bool LeaseKnown, LocalWriter, Frozen, Restricted, AllowedThread, GameStarted;
    public CampaignExecutionPhase Phase;
    public float Dt;
    public int DtBits, CurrentTickCount, CampaignIdentity, ThreadId;
    public long WorldTicks;
    public DateTime Utc;
    public CampaignNativeBoundary Copy() => (CampaignNativeBoundary)MemberwiseClone();
    internal CampaignNativeBoundary WithAi(CampaignAiTickEvidence evidence)
    {
        var copy = Copy();copy.AiPartyId = evidence.PartyId;copy.AiPartyRole = evidence.PartyRole;
        copy.AiController = evidence.ObjectController;copy.AiIdentity = evidence.AiIdentity;copy.AiMainParty = evidence.MainParty;
        return copy;
    }
    internal object BranchKey() => new { LateAiCoverageVersion, LateAiPartiesThinkAttempts, LateAiScopeMatched };
    internal object ContextKey() => new { Node, CampaignId, CampaignIdentity, Lease, LeaseKnown, LocalWriter, Frozen,
        Restricted, AllowedThread, Phase, DiagnosticError, GameStarted };
    internal static bool SameQualifiedContext(CampaignNativeBoundary entry, CampaignNativeBoundary exit, bool requireWriter = true, bool allowPaused = false) =>
        entry != null && exit != null && entry.LeaseKnown && exit.LeaseKnown && entry.Lease.IsValid && entry.Lease == exit.Lease &&
        (allowPaused || !entry.Lease.IsPaused) && !entry.Frozen && !exit.Frozen && !entry.Restricted && !exit.Restricted &&
        !entry.AllowedThread && !exit.AllowedThread && entry.Phase == exit.Phase &&
        (entry.Phase == CampaignExecutionPhase.Unspecified || entry.Phase == CampaignExecutionPhase.WorldWrite) &&
        !string.IsNullOrWhiteSpace(entry.Node) && entry.Node == exit.Node && !string.IsNullOrWhiteSpace(entry.CampaignId) && entry.CampaignId == exit.CampaignId &&
        entry.CampaignIdentity == exit.CampaignIdentity && ReferenceEquals(entry.ActualCampaign, exit.ActualCampaign) && entry.LocalWriter == exit.LocalWriter &&
        entry.ThreadId == exit.ThreadId && entry.GameStarted == exit.GameStarted &&
        entry.LocalWriter == (entry.Node == entry.Lease.AuthorityControllerId) && (!requireWriter || entry.LocalWriter) &&
        entry.DiagnosticError == null && exit.DiagnosticError == null;
    internal static string Classify(string method, CampaignNativeBoundary entry, CampaignNativeBoundary exit, bool ranOriginal, Exception error)
    {
        if (!ranOriginal) return "skipped";
        if (method == "Campaign.LateAITickAux" && error == null &&
            SameQualifiedContext(entry, exit, requireWriter: false, allowPaused: true) && !entry.LocalWriter &&
            entry.ActualCampaign != null && entry.ThreadId > 0 &&
            (entry.Lease.IsPaused || !string.IsNullOrWhiteSpace(entry.Lease.AuthorityControllerId)) &&
            entry.GameStarted && entry.CurrentTickCount >= 3 && exit.CurrentTickCount == entry.CurrentTickCount &&
            entry.DtBits == 0 && exit.DtBits == 0 && entry.Dt == 0 && exit.Dt == 0 && entry.WorldTicks == exit.WorldTicks &&
            entry.LateAiCoverageVersion == 1 && exit.LateAiCoverageVersion == 1 &&
            entry.LateAiScopeMatched && exit.LateAiScopeMatched &&
            entry.LateAiPartiesThinkAttempts == 0 && exit.LateAiPartiesThinkAttempts == 0) return "observerLateAiNoOp";
        if (error != null || !SameQualifiedContext(entry, exit) || !entry.GameStarted || entry.CurrentTickCount < 3 ||
            float.IsNaN(entry.Dt) || float.IsInfinity(entry.Dt) || float.IsNaN(exit.Dt) || float.IsInfinity(exit.Dt) ||
            entry.Dt < 0 || exit.Dt < 0 || exit.CurrentTickCount != entry.CurrentTickCount + (method == "Campaign.Tick" ? 1 : 0)) return "unexpected";
        if (entry.Dt == 0 && exit.Dt == 0 && entry.WorldTicks == exit.WorldTicks) return "writerBookkeeping";
        if (exit.WorldTicks < entry.WorldTicks || (method != "Campaign.RealTick" && entry.Dt == 0)) return "unexpected";
        return "writerProgress";
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
    public long MembershipChanges { get; set; }
    public string MembershipChangesHash { get; set; } = "";
    public string FirstMembershipChange { get; set; }
    public CampaignTickerAudit Copy() => (CampaignTickerAudit)MemberwiseClone();
}

internal sealed class CampaignAiTickEvidence
{
    internal static readonly string[] Classes = { "writerNpc", "writerPlayer", "observerLocalNavigation", "receiveApply", "unqualified", "unknown" };
    public string Classification = "unknown", BaseClassification = "unknown", Role = "unknown", PartyRole = "unknown";
    internal static readonly string[] Outcomes = { "completed", "failed", "skipped" };
    internal static readonly string[] CounterKeys = Outcomes.SelectMany(outcome =>
        Classes.Concat(new[] { "unexpectedWorld" }).Select(kind => "MobilePartyAi.Tick." + kind + "." + outcome)
            .Concat(new[] { "MobilePartyAi.Tick." + outcome }))
        .Concat(new[] { "MobilePartyAi.Tick.observed", "MobilePartyAi.Tick.beforeOriginal.failed" }).ToArray();
    internal object ActualParty, ActualAi;
    public string Node, PartyId, PartyStringId, ObjectController, Reason, DiagnosticError, Error, Outcome;
    public string ExitReason, ExitDiagnosticError, ExitRole, ExitPartyRole, ExitClassification, ExitPartyId, ExitPartyStringId, ExitObjectController;
    public int ExitAiIdentity;
    public AuthorityLease Lease, ExitLease;
    public bool LeaseKnown, Frozen, ExitFrozen, LocalWriter, MainParty, CurrentAi, AllowedThread, UnexpectedWorld = true;
    public bool RanOriginal, ContextChanged;
    public int AiIdentity, ThreadId;
    public DateTime StartedUtc, FinishedUtc;
    public CampaignAiTickEvidence Copy() => (CampaignAiTickEvidence)MemberwiseClone();
    internal bool SameContext(CampaignAiTickEvidence other) => other != null && LeaseKnown == other.LeaseKnown &&
        Lease == other.Lease && Frozen == other.Frozen && Node == other.Node && LocalWriter == other.LocalWriter &&
        PartyId == other.PartyId && PartyStringId == other.PartyStringId && ObjectController == other.ObjectController &&
        MainParty == other.MainParty && CurrentAi == other.CurrentAi && AiIdentity == other.AiIdentity &&
        AllowedThread == other.AllowedThread && BaseClassification == other.BaseClassification &&
        ReferenceEquals(ActualParty, other.ActualParty) && ReferenceEquals(ActualAi, other.ActualAi) &&
        DiagnosticError == other.DiagnosticError && Reason == other.Reason;
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
    // CLR4 cannot build Harmony wrappers for these native closed generic methods.
    // Detour only the five nongeneric callers; keep the original generic methods intact.
    internal static IEnumerable<MethodBase> TargetMethods() => GroupTargetMethods();

    // CLR4 may have compiled the parent's direct calls before these five callers
    // were detoured. Rebuild it only after every target in this class succeeded.
    [HarmonyCleanup]
    private static Exception Cleanup(MethodBase original, Exception exception, Harmony harmony)
    {
        if (original == null && exception == null)
            harmony.Patch(AccessTools.Method(typeof(CampaignPeriodicEventManager), "TickPeriodicEvents"),
                transpiler: new HarmonyMethod(typeof(CampaignTickerEvidencePatches), nameof(ParentIdentity)));
        return exception;
    }

    private static IEnumerable<CodeInstruction> ParentIdentity(IEnumerable<CodeInstruction> instructions) => instructions;

    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var result = instructions.ToList();
        int replaced = 0;
        foreach (var instruction in result)
        {
            if ((instruction.opcode != OpCodes.Call && instruction.opcode != OpCodes.Callvirt) ||
                instruction.operand is not MethodInfo method || method.Name != "PeriodicTickSome") continue;
            var wrapper = WrapperFor(method.DeclaringType);
            if (method.ReturnType != typeof(void) || method.GetParameters().Length != 1 ||
                method.GetParameters()[0].ParameterType != typeof(double))
                throw new InvalidOperationException("Unexpected native periodic ticker signature.");
            // Keep the same receiver and double on the evaluation stack, labels and EH blocks.
            instruction.opcode = OpCodes.Call;
            instruction.operand = wrapper;
            replaced++;
        }
        if (replaced != Bindings(__originalMethod.Name).Length)
            throw new InvalidOperationException("Native periodic ticker call-site coverage changed: " + __originalMethod.Name);
        return result;
    }

    internal static MethodInfo WrapperFor(Type tickerType)
    {
        if (tickerType == null || !tickerType.IsConstructedGenericType ||
            tickerType.GetGenericTypeDefinition() != typeof(CampaignPeriodicEventManager.PeriodicTicker<>))
            throw new InvalidOperationException("Unexpected native periodic ticker type.");
        var entity = tickerType.GetGenericArguments()[0];
        string name = entity == typeof(MobileParty) ? nameof(TickMobileParty) :
            entity == typeof(Settlement) ? nameof(TickSettlement) : entity == typeof(Town) ? nameof(TickTown) :
            entity == typeof(Hero) ? nameof(TickHero) : entity == typeof(Clan) ? nameof(TickClan) :
            throw new InvalidOperationException("Unsupported native periodic ticker entity.");
        return AccessTools.Method(typeof(CampaignTickerEvidencePatches), name);
    }

    private static void TickMobileParty(CampaignPeriodicEventManager.PeriodicTicker<MobileParty> ticker, double elapsed)
    {
        var batch = BeginNativeBatch(ticker, elapsed);
        try { ticker.PeriodicTickSome(elapsed); }
        catch (Exception original) { CompleteBatch(original, true, batch); throw; }
        CompleteBatch(null, true, batch);
    }
    private static void TickSettlement(CampaignPeriodicEventManager.PeriodicTicker<Settlement> ticker, double elapsed)
    {
        var batch = BeginNativeBatch(ticker, elapsed);
        try { ticker.PeriodicTickSome(elapsed); }
        catch (Exception original) { CompleteBatch(original, true, batch); throw; }
        CompleteBatch(null, true, batch);
    }
    private static void TickTown(CampaignPeriodicEventManager.PeriodicTicker<Town> ticker, double elapsed)
    {
        var batch = BeginNativeBatch(ticker, elapsed);
        try { ticker.PeriodicTickSome(elapsed); }
        catch (Exception original) { CompleteBatch(original, true, batch); throw; }
        CompleteBatch(null, true, batch);
    }
    private static void TickHero(CampaignPeriodicEventManager.PeriodicTicker<Hero> ticker, double elapsed)
    {
        var batch = BeginNativeBatch(ticker, elapsed);
        try { ticker.PeriodicTickSome(elapsed); }
        catch (Exception original) { CompleteBatch(original, true, batch); throw; }
        CompleteBatch(null, true, batch);
    }
    private static void TickClan(CampaignPeriodicEventManager.PeriodicTicker<Clan> ticker, double elapsed)
    {
        var batch = BeginNativeBatch(ticker, elapsed);
        try { ticker.PeriodicTickSome(elapsed); }
        catch (Exception original) { CompleteBatch(original, true, batch); throw; }
        CompleteBatch(null, true, batch);
    }

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

    private static TickerBatch BeginNativeBatch(object instance, double timeUnitsElapsed)
    {
        var campaign = Campaign.Current;
        if (campaign?._campaignPeriodicEventManager == null) return null;
        // Observe only the actual Campaign's ticker instances, including repeated closed types.
        foreach (var method in GroupTargetMethods())
        foreach (var binding in Bindings(method.Name))
        {
            if (!ReferenceEquals(AccessTools.Field(typeof(CampaignPeriodicEventManager), binding.Name)
                    .GetValue(campaign._campaignPeriodicEventManager), instance)) continue;
            ContainerProvider.TryResolve<IControllerIdProvider>(out var controller);
            ContainerProvider.TryResolve<ICampaignAuthorityContext>(out var authority);
            AuthorityLease lease = default;
            bool known = authority?.TryGetCurrentLease(out lease) == true;
            var batch = BeginBatch(instance, binding.Callback, CampaignCycleDispatchPatches.For(campaign),
                new CampaignTickerAudit { Ticker = binding.Name, Node = controller?.ControllerId,
                    AuthorityId = lease.AuthorityId, RoomId = lease.RoomId, Epoch = lease.Epoch,
                    LeaseKnown = known, LocalWriter = authority?.IsLocalCampaignAuthority == true }, timeUnitsElapsed);
            EnableLiveSelection(batch);
            try { batch.Frame = TaleWorlds.Engine.Utilities.EngineFrameNo; }
            catch (Exception error) { batch.DiagnosticError = error.ToString(); }
            return batch;
        }
        return null;
    }

    internal static TickerBatch BeginBatch(object instance, string callback, CampaignCycleDiagnostics collector,
        CampaignTickerAudit audit, double elapsed)
    {
        var batch = new TickerBatch { Instance = instance, Collector = collector, Audit = audit,
            Parent = current, Depth = (current?.Depth ?? 0) + 1, StartedUtc = DateTime.UtcNow,
            ThreadId = Thread.CurrentThread.ManagedThreadId, Elapsed = elapsed, Callback = callback };
        current = batch;
        try
        {
            var ticker = Traverse.Create(instance);
            batch.InitialEntities = ReadEntities(instance);
            batch.Ids = batch.InitialEntities.Select(value => value.StringId).ToArray();
            batch.StartIndex = ticker.Property<int>("Index").Value;
            batch.StartDebt = ticker.Property<double>("TickDebt").Value;
            batch.Expected = Expected(batch.Ids, batch.StartIndex, batch.StartDebt, elapsed,
                out batch.EndIndex, out batch.EndDebt).Select(id => callback + "|" + id).ToArray();
        }
        catch (Exception error) { batch.DiagnosticError = error.ToString(); }
        return batch;
    }

    private static MBObjectBase[] ReadEntities(object instance)
    {
        var entities = ((System.Collections.IEnumerable)Traverse.Create(instance).Field("_list").GetValue())
            .Cast<MBObjectBase>().Take(EvidenceLimit + 1).ToArray();
        if (entities.Length > EvidenceLimit) throw new InvalidOperationException("Ticker evidence list exceeds complete capture limit; evidence incomplete.");
        return entities;
    }

    private static bool SameEntities(MBObjectBase[] left, MBObjectBase[] right) =>
        left.Length == right.Length && left.Zip(right, (a, b) => ReferenceEquals(a, b)).All(equal => equal);

    internal static void EnableLiveSelection(TickerBatch batch)
    {
        batch.Live = true;
        batch.ObservedEntities = batch.InitialEntities;
        batch.ObservedIds = batch.Ids;
        batch.LiveIndex = batch.StartIndex;
        batch.InitialExpectedCount = batch.Expected.Length;
        batch.InitialEndDebt = batch.EndDebt;
        // Native adds debt once using the initial count; callback mutations do not recompute it.
        batch.LiveDebt = batch.Ids.Length == 0 ? 0 : batch.StartDebt + batch.Elapsed * batch.Ids.Length;
    }

    private static void Incomplete(TickerBatch batch, string reason)
    { if (batch.DiagnosticError == null) batch.DiagnosticError = reason; }

    internal static CallbackBoundary BeginCallback(string callback, object entity)
    {
        var batch = current;
        if (batch == null) return null;
        var boundary = new CallbackBoundary { Batch = batch, Callback = callback, Entity = entity as MBObjectBase,
            Parent = batch.ActiveCallback, ThreadId = Thread.CurrentThread.ManagedThreadId, StartedUtc = DateTime.UtcNow };
        if (!batch.Live) return boundary;
        batch.ActiveCallback = boundary;
        try
        {
            if (boundary.Parent != null) throw new InvalidOperationException("Nested callback without a nested native ticker boundary.");
            if (batch.LiveExpected.Count == EvidenceLimit) throw new InvalidOperationException("Live callback selection evidence exceeds complete capture limit.");
            var entities = ReadEntities(batch.Instance);
            var ids = entities.Select(value => value.StringId).ToArray();
            if (!SameEntities(batch.ObservedEntities, entities) || !batch.ObservedIds.SequenceEqual(ids))
                throw new InvalidOperationException("Native ticker membership changed outside an observed callback.");
            if (entities.Length == 0 || batch.LiveDebt <= 1 || double.IsNaN(batch.LiveDebt) || double.IsInfinity(batch.LiveDebt))
                throw new InvalidOperationException("Native callback has no independently predicted due selection.");
            int next = batch.LiveIndex + 1;
            if (next >= entities.Length) next = 0;
            boundary.Index = next;
            boundary.BeforeIds = ids;
            boundary.BeforeEntities = entities;
            boundary.EntityId = boundary.Entity?.StringId;
            // The argument is checked against the previous complete post list, never used as its own expectation.
            batch.LiveExpected.Add(batch.Callback + "|" + batch.ObservedIds[next]);
            var ticker = Traverse.Create(batch.Instance);
            boundary.ObservedIndex = ticker.Property<int>("Index").Value;
            boundary.ObservedDebt = ticker.Property<double>("TickDebt").Value;
            batch.LiveSelectionMatches &= callback == batch.Callback && ReferenceEquals(entity, batch.ObservedEntities[next]) &&
                boundary.EntityId == batch.ObservedIds[next] && boundary.ObservedIndex == next && boundary.ObservedDebt == batch.LiveDebt;
            boundary.Ready = true;
        }
        catch (Exception error) { Incomplete(batch, error.ToString()); }
        return boundary;
    }

    internal static Exception EndCallback(Exception original, bool ranOriginal, CallbackBoundary boundary)
    {
        if (boundary == null) return original;
        var batch = boundary.Batch;
        if (!batch.Live)
        {
            if (original == null && ranOriginal) Completed(boundary.Callback, boundary.Entity);
            return original;
        }
        try
        {
            if (!ReferenceEquals(current, batch) || !ReferenceEquals(batch.ActiveCallback, boundary) ||
                boundary.ThreadId != Thread.CurrentThread.ManagedThreadId)
                throw new InvalidOperationException("Incomplete callback thread/nesting boundary.");
            if (!boundary.Ready) return original;
            var entities = ReadEntities(batch.Instance);
            var ids = entities.Select(value => value.StringId).ToArray();
            var ticker = Traverse.Create(batch.Instance);
            batch.LiveSelectionMatches &= ticker.Property<int>("Index").Value == boundary.Index &&
                ticker.Property<double>("TickDebt").Value == batch.LiveDebt;
            if (!SameEntities(boundary.BeforeEntities, entities) || !boundary.BeforeIds.SequenceEqual(ids))
            {
                string evidence = JsonConvert.SerializeObject(new { boundary.Callback, boundary.EntityId, boundary.StartedUtc,
                    finishedUtc = DateTime.UtcNow, batch.Audit.Ticker, batch.Audit.Node, batch.Audit.RoomId, batch.Audit.AuthorityId, batch.Audit.Epoch,
                    expectedIndex = boundary.Index, beforeIds = boundary.BeforeIds, afterIds = ids,
                    referenceSequenceChanged = !SameEntities(boundary.BeforeEntities, entities),
                    beforeReferenceFingerprints = boundary.BeforeEntities.Select(RuntimeHelpers.GetHashCode).ToArray(),
                    afterReferenceFingerprints = entities.Select(RuntimeHelpers.GetHashCode).ToArray(),
                    referenceFingerprintScope = "Process-local diagnostics only; matching uses ReferenceEquals, not hashes.",
                    boundary.ObservedIndex, entryDebtBits = BitConverter.DoubleToInt64Bits(boundary.ObservedDebt),
                    expectedDebtBitsBeforeNativeDecrement = BitConverter.DoubleToInt64Bits(batch.LiveDebt),
                    batch.Frame, boundary.ThreadId, batch.Depth, originalException = original?.ToString() });
                batch.Audit.MembershipChanges++;
                batch.Audit.MembershipChangesHash = CampaignCycleDiagnostics.Hash(batch.Audit.MembershipChangesHash + evidence);
                if (batch.Audit.FirstMembershipChange == null) batch.Audit.FirstMembershipChange = evidence;
            }
            batch.ObservedEntities = entities; batch.ObservedIds = ids; batch.LiveIndex = boundary.Index;
            if (!ranOriginal) Incomplete(batch, "Native callback original was skipped.");
            if (original == null)
            {
                // Finalizer runs before native delegate return/decrement; predict that one native decrement.
                batch.LiveDebt--;
                if (ranOriginal) batch.Completed.Add(boundary.Callback + "|" + boundary.EntityId);
            }
        }
        catch (Exception error) { Incomplete(batch, error.ToString()); }
        finally { batch.ActiveCallback = boundary.Parent; }
        return original;
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
        if (current.Live) Incomplete(current, "Live callback completion lacks a complete observed Prefix/Finalizer boundary.");
        if (current.Completed.Count == EvidenceLimit)
        { current.DiagnosticError = "Ticker completed callbacks exceed complete capture limit; evidence incomplete."; return; }
        current.Completed.Add(callback + "|" + value.StringId);
    }

    internal static Exception CompleteBatch(Exception __exception, bool __runOriginal, TickerBatch __state)
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
        if (batch.Live)
        {
            if (originalException == null && (batch.LiveExpected.Count != batch.InitialExpectedCount || batch.LiveDebt != batch.InitialEndDebt))
                Incomplete(batch, "Live selection count/debt differs from the independent initial native budget.");
            batch.Expected = batch.LiveExpected.ToArray(); batch.EndIndex = batch.LiveIndex; batch.EndDebt = batch.LiveDebt;
            if (batch.ActiveCallback != null) Incomplete(batch, "Native ticker returned with an unfinished callback boundary.");
            if (originalException == null && (batch.LiveDebt > 1 || double.IsNaN(batch.LiveDebt) || double.IsInfinity(batch.LiveDebt)))
                Incomplete(batch, "Native ticker returned before independently due callbacks were consumed.");
        }
        bool sequenceMatches = batch.Expected.SequenceEqual(batch.Completed), membershipMatches = false,
            indexMatches = false, debtMatches = false;
        string[] actualIds = null;
        int? actualIndex = null;
        double? actualDebt = null;
        // Still capture final state on a native exception, without replacing that exception.
        try
        {
            var ticker = Traverse.Create(batch.Instance);
            var actualEntities = ReadEntities(batch.Instance);
            actualIds = actualEntities.Select(value => value.StringId).ToArray();
            actualIndex = ticker.Property<int>("Index").Value;
            actualDebt = ticker.Property<double>("TickDebt").Value;
            membershipMatches = batch.Live ? SameEntities(batch.ObservedEntities, actualEntities) && batch.ObservedIds.SequenceEqual(actualIds)
                : batch.Ids.SequenceEqual(actualIds);
            indexMatches = actualIndex == batch.EndIndex;
            debtMatches = actualDebt == batch.EndDebt;
        }
        catch (Exception error) { batch.DiagnosticError = (batch.DiagnosticError == null ? "" : batch.DiagnosticError + "\n") + error; }
        bool matches = originalException == null && batch.DiagnosticError == null && sequenceMatches &&
            membershipMatches && indexMatches && debtMatches && (!batch.Live || batch.LiveSelectionMatches);
        var audit = batch.Audit;
        audit.Expected = batch.Expected.Length; audit.Completed = batch.Completed.Count;
        audit.ExpectedHash = CampaignCycleDiagnostics.Hash(string.Join("|", batch.Expected));
        audit.CompletedHash = CampaignCycleDiagnostics.Hash(string.Join("|", batch.Completed));
        audit.Mismatches = matches ? 0 : 1;
        if (!matches) audit.FirstFailure = JsonConvert.SerializeObject(new
        {
            sequenceMatches, membershipMatches, indexMatches, debtMatches,
            complete = batch.DiagnosticError == null, batch.Live, batch.LiveSelectionMatches,
            batch.InitialExpectedCount, initialExpectedDebtBits = BitConverter.DoubleToInt64Bits(batch.InitialEndDebt),
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
        public string Callback;
        public MBObjectBase[] InitialEntities = Array.Empty<MBObjectBase>(), ObservedEntities = Array.Empty<MBObjectBase>();
        public string[] ObservedIds = Array.Empty<string>();
        public bool Live, LiveSelectionMatches = true;
        public int LiveIndex, InitialExpectedCount;
        public double LiveDebt, InitialEndDebt;
        public readonly List<string> LiveExpected = new();
        public CallbackBoundary ActiveCallback;
        public string[] Ids = Array.Empty<string>(), Expected = Array.Empty<string>();
        public readonly List<string> Completed = new();
        public int StartIndex, EndIndex;
        public double StartDebt, EndDebt, Elapsed;
        public CampaignTickerAudit Audit;
        public TickerBatch Parent;
        public int Frame, ThreadId, Depth;
        public DateTime StartedUtc;
    }

    internal sealed class CallbackBoundary
    {
        public TickerBatch Batch;
        public CallbackBoundary Parent;
        public string Callback, EntityId;
        public MBObjectBase Entity;
        public MBObjectBase[] BeforeEntities;
        public string[] BeforeIds;
        public int Index, ThreadId, ObservedIndex;
        public double ObservedDebt;
        public DateTime StartedUtc;
        public bool Ready;
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
    [HarmonyPrefix]
    private static void Prefix(object[] __args, MethodBase __originalMethod, out CampaignTickerEvidencePatches.CallbackBoundary __state) =>
        __state = CampaignTickerEvidencePatches.BeginCallback(__originalMethod.DeclaringType.Name + "." + __originalMethod.Name, __args[0]);
    [HarmonyFinalizer]
    private static Exception Finalizer(Exception __exception, bool __runOriginal, CampaignTickerEvidencePatches.CallbackBoundary __state) =>
        CampaignTickerEvidencePatches.EndCallback(__exception, __runOriginal, __state);
}

[HarmonyPatch]
internal static class CampaignMobilePartyCallbackEvidencePatches
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(MobileParty), "HourlyTick");
        yield return AccessTools.Method(typeof(MobileParty), "DailyTick");
    }
    [HarmonyPrefix]
    private static void Prefix(MobileParty __instance, MethodBase __originalMethod, out CampaignTickerEvidencePatches.CallbackBoundary __state) =>
        __state = CampaignTickerEvidencePatches.BeginCallback("MobileParty." + __originalMethod.Name, __instance);
    [HarmonyFinalizer]
    private static Exception Finalizer(Exception __exception, bool __runOriginal, CampaignTickerEvidencePatches.CallbackBoundary __state) =>
        CampaignTickerEvidencePatches.EndCallback(__exception, __runOriginal, __state);
}

[HarmonyPatch]
internal static class CampaignSimulationEvidencePatches
{
    [ThreadStatic] private static LateAiScope currentLateAi;
    internal sealed class LateAiScope
    {
        public LateAiScope Parent;
        public Campaign Campaign;
        public int ThreadId, Attempts, CoverageVersion;
        public bool Matched = true;
    }
    private static int BranchCoverage()
    {
        var patch = AccessTools.Method(typeof(CampaignSimulationEvidencePatches), nameof(Prefix));
        var info = Harmony.GetPatchInfo(AccessTools.Method(typeof(Campaign), "PartiesThink"));
        return info?.Prefixes.Any(value => value.PatchMethod == patch) == true ? 1 : 0;
    }
    private static void BranchBoundary(CampaignNativeBoundary boundary, LateAiScope scope)
    {
        boundary.LateAiCoverageVersion = Math.Min(scope.CoverageVersion, BranchCoverage());
        boundary.LateAiPartiesThinkAttempts = scope.Attempts;
        boundary.LateAiScopeMatched = scope.Matched && ReferenceEquals(scope.Campaign, boundary.ActualCampaign) &&
            scope.ThreadId == boundary.ThreadId && ReferenceEquals(currentLateAi, scope);
    }
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (string name in new[] { "RealTick", "Tick", "LateAITickAux", "WaitAsyncTasks", "PartiesThink" }) yield return AccessTools.Method(typeof(Campaign), name);
        yield return AccessTools.Method(typeof(MobilePartyAi), "Tick");
    }
    [HarmonyCleanup]
    private static Exception Cleanup(MethodBase original, Exception exception, Harmony harmony)
    {
        if (original == null && exception == null)
            harmony.Patch(AccessTools.Method(typeof(Campaign), "RealTick"),
                transpiler: new HarmonyMethod(typeof(CampaignSimulationEvidencePatches), nameof(RealTickIdentity)));
        return exception;
    }
    private static IEnumerable<CodeInstruction> RealTickIdentity(IEnumerable<CodeInstruction> instructions) => instructions;

    internal static CampaignAiTickEvidence CaptureAi(MobilePartyAi ai)
    {
        var row = new CampaignAiTickEvidence { StartedUtc = DateTime.UtcNow, ThreadId = Thread.CurrentThread.ManagedThreadId,
            AiIdentity = ai == null ? 0 : RuntimeHelpers.GetHashCode(ai), AllowedThread = AllowedThread.IsThisThreadAllowed() };
        try
        {
            if (ai?._mobileParty == null) { row.Reason = "Missing actual AI party";return row; }
            var party = ai._mobileParty;row.ActualParty = party;row.ActualAi = ai;row.PartyStringId = party.StringId;
            row.MainParty = ReferenceEquals(party, MobileParty.MainParty);row.CurrentAi = ReferenceEquals(ai, party.Ai);
            if (!ContainerProvider.TryResolve<IControllerIdProvider>(out var controller) || string.IsNullOrWhiteSpace(controller.ControllerId) ||
                !ContainerProvider.TryResolve<ICampaignAuthorityContext>(out var authority) ||
                !ContainerProvider.TryResolve<ISessionIdentity>(out var identity) ||
                !ContainerProvider.TryResolve<IObjectManager>(out var objects) ||
                !ContainerProvider.TryResolve<IPlayerManager>(out var players))
            { row.Reason = "Missing controller/session/registry evidence";return row; }
            row.Node = controller.ControllerId;
            if (!objects.TryGetId(party, out row.PartyId) || !objects.TryGetObject<MobileParty>(row.PartyId, out var registered) ||
                !ReferenceEquals(registered, party) || !row.CurrentAi)
            { row.Reason = "Unknown party registration or stale AI instance";return row; }
            var registrations = players.Players.Where(player => player.MobilePartyId == row.PartyId).ToArray();
            bool marked = PlayerManager.TryGetControlledObjectInfo(party, out var control);
            if (registrations.Length > 1 || (registrations.Length == 0 && (marked || row.MainParty)))
            { row.Reason = "Unproven registered player identity";return row; }
            if (registrations.Length == 1)
            {
                row.ObjectController = registrations[0].ControllerId;
                if (!marked || control.ObjectControllerId != row.ObjectController || control.ControllerIdProvider?.ControllerId != row.Node)
                { row.Reason = "Player control marker disagrees with registration";return row; }
                row.PartyRole = row.ObjectController == row.Node ? "localPlayer" : "remotePlayer";
            }
            else row.PartyRole = "npc";
            row.LeaseKnown = authority.TryGetCurrentLease(out row.Lease) && row.Lease.IsValid &&
                identity.TryGetConfirmedLease(out var confirmed) && confirmed == row.Lease;
            if (!row.LeaseKnown) { row.Reason = "No coherent confirmed lease";return row; }
            row.Frozen = identity.IsLocallyFrozen(row.Lease);
            row.LocalWriter = authority.TryGetLocalAuthorityLease(out var local) && local == row.Lease;
            if (row.Frozen || row.Lease.IsPaused || row.LocalWriter != (row.Node == row.Lease.AuthorityControllerId))
            { row.Role = "unqualified";row.Classification = "unqualified";row.Reason = "Frozen, paused or contradictory local authority";return row; }
            row.Role = row.LocalWriter ? "writer" : "observer";
            if (row.LocalWriter) row.Classification = row.PartyRole == "npc" ? "writerNpc" : "writerPlayer";
            else row.Classification = row.PartyRole == "localPlayer" && row.MainParty ? "observerLocalNavigation" : "unqualified";
            row.UnexpectedWorld = row.Classification == "unqualified" || (row.AllowedThread && row.LocalWriter && row.PartyRole == "npc");
        }
        catch (Exception error) { row.Classification = "unknown";row.UnexpectedWorld = true;row.Reason = "AI evidence capture failed";row.DiagnosticError = error.ToString(); }
        finally
        {
            row.BaseClassification = row.Classification;
            if (row.AllowedThread) row.Classification = "receiveApply";
        }
        return row;
    }

    internal sealed class AiEntry
    {
        public CampaignCycleDiagnostics Collector;
        public CampaignAiTickEvidence Evidence;
        public CampaignNativeBoundary Native;
        public Campaign Campaign;
        public LateAiScope LateAi;
    }
    internal static CampaignNativeBoundary CaptureNative(Campaign campaign)
    {
        var row = new CampaignNativeBoundary { Utc = DateTime.UtcNow, ThreadId = Thread.CurrentThread.ManagedThreadId,
            Phase = CampaignExecutionScope.Current, Restricted = CampaignExecutionScope.IsWorldWriteRestricted,
            AllowedThread = AllowedThread.IsThisThreadAllowed() };
        try
        {
            if (campaign == null) { row.DiagnosticError = "Missing actual Campaign";return row; }
            row.ActualCampaign = campaign;row.CampaignIdentity = RuntimeHelpers.GetHashCode(campaign);row.CampaignId = campaign.UniqueGameId;row.GameStarted = campaign.GameStarted;
            if (!ReferenceEquals(campaign, Campaign.Current)) row.DiagnosticError = "Actual Campaign is no longer current";
            row.Dt = campaign._dt;row.DtBits = BitConverter.ToInt32(BitConverter.GetBytes(row.Dt), 0);
            row.CurrentTickCount = campaign.CurrentTickCount;row.WorldTicks = campaign.MapTimeTracker._numTicks;
            if (!ContainerProvider.TryResolve<IControllerIdProvider>(out var controller) ||
                !ContainerProvider.TryResolve<ICampaignAuthorityContext>(out var authority) ||
                !ContainerProvider.TryResolve<ISessionIdentity>(out var identity))
            { row.DiagnosticError = (row.DiagnosticError == null ? "" : row.DiagnosticError + "; ") + "Missing controller or authority identity";return row; }
            row.Node = controller.ControllerId;
            row.LeaseKnown = authority.TryGetCurrentLease(out row.Lease) && row.Lease.IsValid &&
                identity.TryGetConfirmedLease(out var confirmed) && confirmed == row.Lease;
            row.Frozen = identity.IsLocallyFrozen(row.Lease);
            row.LocalWriter = authority.TryGetLocalAuthorityLease(out var local) && local == row.Lease;
        }
        catch (Exception error) { row.DiagnosticError = error.ToString(); }
        return row;
    }
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(object __instance, MethodBase __originalMethod, out AiEntry __state)
    {
        __state = null;
        if (__originalMethod.DeclaringType == typeof(Campaign) && __originalMethod.Name == "PartiesThink")
        {
            if (currentLateAi != null)
            {
                currentLateAi.Attempts++;
                currentLateAi.Matched &= ReferenceEquals(currentLateAi.Campaign, __instance) &&
                    currentLateAi.ThreadId == Thread.CurrentThread.ManagedThreadId;
            }
            return;
        }
        var campaign = __instance as Campaign ?? Campaign.Current;
        if (campaign == null) return;
        __state = new AiEntry { Collector = CampaignCycleDispatchPatches.For(campaign), Campaign = campaign, Native = CaptureNative(campaign),
            Evidence = __originalMethod.DeclaringType == typeof(MobilePartyAi) ? CaptureAi((MobilePartyAi)__instance) : null };
        if (__originalMethod.DeclaringType == typeof(Campaign) && __originalMethod.Name == "LateAITickAux")
        {
            __state.LateAi = new LateAiScope { Parent = currentLateAi, Campaign = campaign,
                ThreadId = Thread.CurrentThread.ManagedThreadId, CoverageVersion = BranchCoverage() };
            currentLateAi = __state.LateAi;
            BranchBoundary(__state.Native, __state.LateAi);
        }
    }
    [HarmonyFinalizer]
    private static Exception Finalizer(object __instance, MethodBase __originalMethod, bool __runOriginal, Exception __exception, AiEntry __state)
    {
        if (__originalMethod.DeclaringType == typeof(Campaign) && __originalMethod.Name == "PartiesThink") return __exception;
        try { return Complete(__instance, __originalMethod, __runOriginal, __exception, __state); }
        finally { if (__state?.LateAi != null) currentLateAi = __state.LateAi.Parent; }
    }
    private static Exception Complete(object __instance, MethodBase __originalMethod, bool __runOriginal, Exception __exception, AiEntry __state)
    {
        if (__originalMethod.DeclaringType == typeof(MobilePartyAi))
        {
            if (__state != null) __state.Collector.AiTick(__state.Evidence, CaptureAi((MobilePartyAi)__instance), __runOriginal, __exception,
                __state.Native, CaptureNative(__state.Campaign));
            else if (Campaign.Current != null)
            {
                var unknown = new CampaignAiTickEvidence { Reason = "Missing AI entry boundary" };
                CampaignCycleDispatchPatches.For(Campaign.Current).AiTick(unknown, CaptureAi((MobilePartyAi)__instance), __runOriginal, __exception);
            }
            return __exception;
        }
        if (__state != null && CampaignNativeBoundary.Methods.Contains("Campaign." + __originalMethod.Name))
        {
            var exit = CaptureNative(__state.Campaign);
            if (__state.LateAi != null) BranchBoundary(exit, __state.LateAi);
            __state.Collector.NativeTick("Campaign." + __originalMethod.Name, __state.Native, exit, __runOriginal, __exception);
            if (__originalMethod.Name == "RealTick" && __runOriginal && __exception == null)
                __state.Collector.RealTickFrame(TaleWorlds.Engine.Utilities.EngineFrameNo);
        }
        else if (Campaign.Current != null && __runOriginal)
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
