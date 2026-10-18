using System.Collections.Generic;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Roster;

namespace GameInterface.Services.ItemRosters;

/// <summary>Settlement shop stock belongs to each local campaign replica, not the shared simulation stream.</summary>
public static class SettlementMarketRosterPolicy
{
    private static readonly ConditionalWeakTable<Campaign, MarketRosterIndex> CampaignIndexes = new();

    public static bool IsLocalMarketRoster(ItemRoster roster)
    {
        var campaign = Campaign.Current;
        var settlements = campaign?.CampaignObjectManager?.Settlements;
        if (roster == null || settlements == null)
            return false;

        var index = CampaignIndexes.GetValue(campaign, _ => new MarketRosterIndex());
        lock (index.Gate)
        {
            // Campaign settlements can be added during play. Rebuild when membership changes so their
            // newly-created shop rosters are classified without scanning the world for every inventory delta.
            if (index.SettlementCount != settlements.Count)
            {
                index.MarketRosters.Clear();
                foreach (var settlement in settlements)
                {
                    if (settlement?.ItemRoster != null)
                        index.MarketRosters.Add(settlement.ItemRoster);
                }

                index.SettlementCount = settlements.Count;
            }

            return index.MarketRosters.Contains(roster);
        }
    }

    private sealed class MarketRosterIndex
    {
        public object Gate { get; } = new();
        public int SettlementCount { get; set; } = -1;
        public HashSet<ItemRoster> MarketRosters { get; } = new();
    }
}
