using Common.Util;
using Common;
using GameInterface.Registry.Auto;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.TownMarketDatas;
using Moq;
using Serilog;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;
using Xunit;

namespace GameInterface.Tests.Services.Players;

[Collection("CampaignCurrentCollection")]
public class TownMarketRegistrationTests
{
    [Fact]
    public void CastleMarketGetsStableIdentityAlongsideTownAndSurvivesRefresh()
    {
        var previous = Campaign.Current;
        var previousMode = ModInformation.SessionMode;
        try
        {
            ModInformation.SessionMode = SessionMode.ListenAuthority;
            var town = new Town { StringId = "town_1" };
            var castle = new Town { StringId = "castle_1" };
            var campaign = ObjectHelper.SkipConstructor<Campaign>();
            campaign._towns = new MBList<Town> { town };
            campaign._castles = new MBList<Town> { castle };
            Campaign.Current = campaign;
            var objects = new ObjectManager(Mock.Of<ILogger>());
            var registry = new TownMarketDataRegistry(Mock.Of<ILogger>(), Mock.Of<IAutoRegistryFactory>(), objects);
            registry.RegisterAllObjects();
            registry.RegisterAllObjects();
            Assert.True(objects.TryGetId(town.MarketData, out var townId));
            Assert.True(objects.TryGetId(castle.MarketData, out var castleId));
            Assert.Equal("TownMarketData_town_1", townId);
            Assert.Equal("TownMarketData_castle_1", castleId);
            Assert.NotEqual(townId, castleId);
            Assert.True(objects.TryGetHandle(castle.MarketData, out var handle));
            Assert.NotEqual(0u, handle);
        }
        finally { Campaign.Current = previous; ModInformation.SessionMode = previousMode; }
    }
}
