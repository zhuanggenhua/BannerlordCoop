using GameInterface.AutoSync;
using GameInterface.Services.TownMarketDatas;
using System.Linq;
using TaleWorlds.CampaignSystem.Settlements;
using Xunit;

namespace GameInterface.Tests.Services.TownMarketDatas;

public class TownMarketDataSyncTests
{
    [Fact]
    public void MerchantStockIsNotRegisteredForAutoSync()
    {
        var registry = new AutoSyncRegistry();
        _ = new TownMarketDataSync(registry);

        var registration = registry.Registrations[typeof(TownMarketData)];
        var registeredNames = registration.Fields.Select(field => field.Value.Name).ToArray();

        Assert.Contains(nameof(TownMarketData._town), registeredNames);
        Assert.DoesNotContain(nameof(TownMarketData._itemDict), registeredNames);
    }
}
