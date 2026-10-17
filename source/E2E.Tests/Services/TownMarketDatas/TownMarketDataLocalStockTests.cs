using Common.Network;
using Common.Util;
using E2E.Tests.Util;
using System.Collections.Generic;
using Common.Network.Coalescing;
using System.Linq;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using Xunit.Abstractions;

namespace E2E.Tests.Services.TownMarketDatas;

public class TownMarketDataLocalStockTests : SyncTestBase
{
    private const string MarketDataId = "e2e_town_market_data";
    private readonly string categoryId;

    public TownMarketDataLocalStockTests(ITestOutputHelper output) : base(output)
    {
        SeedMarketData(Server);
        foreach (var client in Clients)
            SeedMarketData(client);

        categoryId = TestEnvironment.CreateRegisteredObject<ItemCategory>();
    }

    [Fact]
    public void ServerStockUpdateDoesNotOverwriteReplicaLocalMarketStock()
    {
        var clients = Clients.ToArray();
        var clientStock = new ItemData[clients.Length];
        for (int index = 0; index < clients.Length; index++)
        {
            clientStock[index] = new ItemData(10f + index, 20f + index, 30 + index, 40 + index);
            var client = clients[index];
            var expected = clientStock[index];
            client.Call(() =>
            {
                Assert.True(client.ObjectManager.TryGetObject(MarketDataId, out TownMarketData marketData));
                Assert.True(client.ObjectManager.TryGetObject(categoryId, out ItemCategory category));
                marketData._itemDict[category] = expected;
            });
        }

        var serverStock = new ItemData(1f, 2f, 3, 4);
        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject(MarketDataId, out TownMarketData marketData));
            Assert.True(Server.ObjectManager.TryGetObject(categoryId, out ItemCategory category));
            marketData._itemDict[category] = serverStock;
        });
        Server.Call(() => Server.Resolve<ISendCoalescer>().Flush(Server.Resolve<INetwork>()));

        AssertMarketStock(Server, serverStock);
        for (int index = 0; index < clients.Length; index++)
            AssertMarketStock(clients[index], clientStock[index]);
    }

    private static void SeedMarketData(E2E.Tests.Environment.Instance.EnvironmentInstance instance)
    {
        var marketData = instance.CreateRegisteredObject<TownMarketData>(MarketDataId);
        marketData._itemDict = new Dictionary<ItemCategory, ItemData>();
    }

    private void AssertMarketStock(
        E2E.Tests.Environment.Instance.EnvironmentInstance instance,
        ItemData expected)
    {
        instance.Call(() =>
        {
            Assert.True(instance.ObjectManager.TryGetObject(MarketDataId, out TownMarketData marketData));
            Assert.True(instance.ObjectManager.TryGetObject(categoryId, out ItemCategory category));
            Assert.True(marketData._itemDict.TryGetValue(category, out var actual));
            Assert.Equal(expected.Supply, actual.Supply);
            Assert.Equal(expected.Demand, actual.Demand);
            Assert.Equal(expected.InStore, actual.InStore);
            Assert.Equal(expected.InStoreValue, actual.InStoreValue);
        });
    }
}
