using Common;
using GameInterface.Services.CampaignAuthority.NativeState;
using Moq;
using ProtoBuf;
using Serilog;
using System;
using System.IO;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using Xunit;
using NativeObjects = GameInterface.Services.ObjectManager.ObjectManager;

namespace GameInterface.Tests.Services.CampaignAuthority.NativeState;

public class CampaignCaravanCheckpointTests
{
    [Fact]
    public void CompositeRoundtripTransfersAiHistoryAndCachesWithoutReplacingLocalHistory()
    {
        var source = new Fixture();
        var target = new Fixture();
        source.Caravans._caravanLastHomeTownVisitTime[source.Party] = new CampaignTime(9001);
        source.Caravans._lootedCaravans[source.Party] = new CampaignTime(8002);
        source.Caravans._navalCaravanVeryFarCache = 100.25f;
        source.Caravans._defaultCaravanVeryFarCache = 75.5f;
        source.Caravans._packAnimalCategoryIndex = 0;
        source.Caravans._averageValuesCached[source.Category] = 1.25f;
        source.Caravans._priceDictionary[source.Category] = new(3.25f, 2.125f);
        source.Caravans._coastalPriceDictionary[source.Category] = new(5.25f, 4.125f);
        source.Aging._extraLivesContainer[source.Hero] = 3;
        var localRumors = target.Caravans._tradeRumorTakenCaravans;
        localRumors[target.Party] = new CampaignTime(15);
        var localInteractions = target.Caravans._interactedCaravans;
        localInteractions[target.Party] = CaravansCampaignBehavior.PlayerInteraction.Friendly;
        var localProhibited = target.Caravans._prohibitedKingdomsForPlayerCaravans;
        var localTradeLogs = target.Caravans._tradeActionLogs;
        localTradeLogs[target.Party] = new();
        target.Caravans._totalValueOfItemsAtCategory[target.Category] = 999;
        Assert.True(source.Capture(out var checkpoint));
        using var stream = new MemoryStream();
        Serializer.Serialize(stream, checkpoint); stream.Position = 0;
        var copy = Serializer.Deserialize<CampaignNativeBehaviorCheckpoint>(stream);
        Assert.True(target.Service.TryPrepare(copy, target.Aging, target.Caravans,
            new[] { target.OtherCategory, target.Category }, out var install));
        Assert.Empty(target.Caravans._caravanLastHomeTownVisitTime);
        install();
        Assert.Equal(9001, target.Caravans._caravanLastHomeTownVisitTime[target.Party].NumTicks);
        Assert.Equal(8002, target.Caravans._lootedCaravans[target.Party].NumTicks);
        Assert.Equal(100.25f, target.Caravans._navalCaravanVeryFarCache);
        Assert.Equal(75.5f, target.Caravans._defaultCaravanVeryFarCache);
        Assert.Equal(1, target.Caravans._packAnimalCategoryIndex);
        Assert.Equal(1.25f, target.Caravans._averageValuesCached[target.Category]);
        Assert.Equal(3.25f, target.Caravans._priceDictionary[target.Category].AverageBuySellPriceIndex);
        Assert.Equal(2.125f, target.Caravans._priceDictionary[target.Category].MinBuySellPriceIndex);
        Assert.Equal(5.25f, target.Caravans._coastalPriceDictionary[target.Category].AverageBuySellPriceIndex);
        Assert.Equal(4.125f, target.Caravans._coastalPriceDictionary[target.Category].MinBuySellPriceIndex);
        Assert.Equal(3, target.Aging._extraLivesContainer[target.Hero]);
        Assert.Same(localRumors, target.Caravans._tradeRumorTakenCaravans);
        Assert.Equal(15, localRumors[target.Party].NumTicks);
        Assert.Same(localInteractions, target.Caravans._interactedCaravans);
        Assert.Same(localProhibited, target.Caravans._prohibitedKingdomsForPlayerCaravans);
        Assert.Same(localTradeLogs, target.Caravans._tradeActionLogs);
        Assert.Empty(target.Caravans._totalValueOfItemsAtCategory);
        Assert.False(target.Caravans._caravanLastHomeTownVisitTime.ContainsKey(source.Party));
    }

    [Theory]
    [InlineData("party")]
    [InlineData("duplicate")]
    [InlineData("ticks")]
    [InlineData("category")]
    [InlineData("nan")]
    [InlineData("infinity")]
    [InlineData("pack")]
    [InlineData("missing-domain")]
    public void CompositePreparationRejectsInvalidCaravanStateBeforeAgingOrCaravanMutation(string failure)
    {
        var fixture = new Fixture();
        var visits = failure == "duplicate"
            ? new[] { new CampaignPartyTimeState(10, 1), new CampaignPartyTimeState(10, 2) }
            : new[] { new CampaignPartyTimeState(failure == "party" ? 99u : 10u, failure == "ticks" ? -1 : 1) };
        var cache = new CampaignCaravanCacheState(failure == "nan" ? float.NaN : 10f,
            failure == "infinity" ? float.PositiveInfinity : 20f, failure == "pack" ? 99u : 20u,
            new[] { new CampaignCategoryValueState(failure == "category" ? 99u : 20u, 1) },
            Array.Empty<CampaignCategoryPriceState>(), Array.Empty<CampaignCategoryPriceState>());
        var caravans = new CampaignCaravanBehaviorState(visits, Array.Empty<CampaignPartyTimeState>(), cache);
        var checkpoint = new CampaignNativeBehaviorCheckpoint(new CampaignAgingBehaviorState(17,
            new[] { new CampaignHeroIntState(7, 8) }, Array.Empty<CampaignHeroIntState>()),
            failure == "missing-domain" ? null : caravans);
        fixture.Aging._gameStartDay = 50;
        fixture.Aging._extraLivesContainer[fixture.Hero] = 9;
        Assert.False(fixture.Service.TryPrepare(checkpoint, fixture.Aging, fixture.Caravans,
            new[] { fixture.Category, fixture.OtherCategory }, out var install));
        Assert.Null(install);
        Assert.Equal(50, fixture.Aging._gameStartDay);
        Assert.Equal(9, fixture.Aging._extraLivesContainer[fixture.Hero]);
        Assert.Empty(fixture.Caravans._caravanLastHomeTownVisitTime);
        Assert.Empty(fixture.Caravans._averageValuesCached);
    }

    [Fact]
    public void UnregisteredLootHistoryRefusesCaptureRatherThanDroppingOrResurrectingItsParty()
    {
        var fixture = new Fixture();
        fixture.Caravans._lootedCaravans[fixture.Party] = new CampaignTime(100);
        Assert.True(fixture.Objects.Remove(fixture.Party));
        Assert.False(fixture.Capture(out var checkpoint));
        Assert.Null(checkpoint);
        Assert.Single(fixture.Caravans._lootedCaravans);
    }

    [Fact]
    public void EmptyNativeCaravanStateAndDefaultDistanceSentinelsSurviveRoundtrip()
    {
        var fixture = new Fixture();
        Assert.True(fixture.Capture(out var checkpoint));
        using var stream = new MemoryStream(); Serializer.Serialize(stream, checkpoint); stream.Position = 0;
        var copy = Serializer.Deserialize<CampaignNativeBehaviorCheckpoint>(stream);
        Assert.True(fixture.Service.TryPrepare(copy, fixture.Aging, fixture.Caravans,
            new[] { fixture.Category, fixture.OtherCategory }, out var install));
        install();
        Assert.Equal(-1f, fixture.Caravans._navalCaravanVeryFarCache);
        Assert.Equal(-1, fixture.Caravans._packAnimalCategoryIndex);
    }

    private sealed class Fixture
    {
        internal readonly NativeObjects Objects = new(Mock.Of<ILogger>());
        internal readonly Hero Hero = Empty<Hero>();
        internal readonly MobileParty Party = Empty<MobileParty>();
        internal readonly ItemCategory Category = Empty<ItemCategory>();
        internal readonly ItemCategory OtherCategory = Empty<ItemCategory>();
        internal readonly AgingCampaignBehavior Aging = new();
        internal readonly CaravansCampaignBehavior Caravans = new();
        internal readonly CampaignNativeBehaviorCheckpointService Service;
        internal Fixture()
        {
            Assert.True(Objects.AddExisting("Hero_native", Hero, 7));
            Assert.True(Objects.AddExisting("MobileParty_native", Party, 10));
            Assert.True(Objects.AddExisting("ItemCategory_pack", Category, 20));
            Assert.True(Objects.AddExisting("ItemCategory_other", OtherCategory, 21));
            Service = new CampaignNativeBehaviorCheckpointService(Objects);
        }
        internal bool Capture(out CampaignNativeBehaviorCheckpoint checkpoint) =>
            Service.TryCapture(Aging, Caravans, new[] { Category, OtherCategory }, out checkpoint);
        private static T Empty<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    }
}
