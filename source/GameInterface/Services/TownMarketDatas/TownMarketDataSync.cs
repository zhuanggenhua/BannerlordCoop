using GameInterface.AutoSync;
using HarmonyLib;
using TaleWorlds.CampaignSystem.Settlements;

namespace GameInterface.Services.TownMarketDatas
{
    internal class TownMarketDataSync : IAutoSync
    {
        public TownMarketDataSync(AutoSyncRegistry autoSyncBuilder)
        {
            // Merchant stock is replica-local; keep only the market's town association synchronized.
            autoSyncBuilder.AddField(AccessTools.Field(typeof(TownMarketData), nameof(TownMarketData._town)));
        }
    }
}
