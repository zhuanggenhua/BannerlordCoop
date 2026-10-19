#if DEBUG
using Common;
using Common.Logging;
using GameInterface.Services.Entity;
using HarmonyLib;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.CampaignAuthority;

internal sealed class CampaignCycleDiagnostics
{
    private readonly object gate = new();
    private readonly Dictionary<string, CampaignCycleRow> rows = new(StringComparer.Ordinal);
    private readonly int capacity;
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");
    public bool Overflowed { get; private set; }

    public CampaignCycleDiagnostics(int capacity = 32768)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
    }

    public bool Record(string kind, string party, long hour, string node, bool known, bool writer, AuthorityLease lease)
    {
        long bucket = kind == "HourlyTick" ? hour : hour / 24;
        string key = string.Join("|", kind, party, bucket, node, lease.RoomId, lease.AuthorityId, lease.Epoch, known, writer);
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
            });
            return true;
        }
    }

    public CampaignCycleRow[] Snapshot()
    {
        lock (gate) return rows.Values.OrderBy(row => row.Hour).ThenBy(row => row.Kind)
            .ThenBy(row => row.Party).Select(row => row.Copy()).ToArray();
    }
}

internal sealed class CampaignCycleRow
{
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
        if (!collector.Record(kind, party, (long)Math.Floor(CampaignTime.Now.ToHours),
                controller?.ControllerId, known, authority?.IsLocalCampaignAuthority == true, lease) && !alreadyOverflowed)
            Logger.Error("Campaign cycle diagnostics exceeded its 32768 entry limit; reliability evidence is incomplete.");
    }
}
#endif
