using Common;
using GameInterface.Services.CampaignAuthority.NativeState;
using Common.Util;
using HarmonyLib;
using Moq;
using ProtoBuf;
using Serilog;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using Xunit;
using NativeObjects = GameInterface.Services.ObjectManager.ObjectManager;

namespace GameInterface.Tests.Services.CampaignAuthority.NativeState;

[Collection(ModInformationRoleCollection.Name)]
public class CampaignCaravanCheckpointTests
{
    [Fact]
    public void NativeEmptyTownPriceSentinelRoundtripsExactBitsAndRemainsAnExistingNativePrice()
    {
        var source = new Fixture();
        var target = new Fixture();
        var previous = Campaign.Current;
        try
        {
            var campaign = ObjectHelper.SkipConstructor<Campaign>();
            campaign._towns = new MBList<Town>();
            AccessTools.Property(typeof(Campaign), "AllItemCategories").SetValue(campaign,
                new MBReadOnlyList<ItemCategory>(new List<ItemCategory> { source.Category, source.OtherCategory }));
            Campaign.Current = campaign;
            AccessTools.Method(typeof(CaravansCampaignBehavior), "CreatePriceDataCache").Invoke(source.Caravans, null);
        }
        finally { Campaign.Current = previous; }

        int nativeBits = BitConverter.SingleToInt32Bits(source.Caravans._priceDictionary[source.Category].AverageBuySellPriceIndex);
        Assert.Equal(unchecked((int)0xffc00000), nativeBits);
        Assert.Equal(1000f, source.Caravans._priceDictionary[source.Category].MinBuySellPriceIndex);
        Assert.Equal(nativeBits, BitConverter.SingleToInt32Bits(source.Caravans._coastalPriceDictionary[source.Category].AverageBuySellPriceIndex));
        Assert.True(source.Capture(out var checkpoint));
        using var stream = new MemoryStream();
        Serializer.Serialize(stream, checkpoint); stream.Position = 0;
        var copy = Serializer.Deserialize<CampaignNativeBehaviorCheckpoint>(stream);
        Assert.All(copy.Caravans.Cache.LandPrices.Concat(copy.Caravans.Cache.CoastalPrices), value =>
        {
            Assert.Equal(nativeBits, BitConverter.SingleToInt32Bits(value.Average));
            Assert.Equal(BitConverter.SingleToInt32Bits(1000f), BitConverter.SingleToInt32Bits(value.Minimum));
        });
        Assert.True(target.Service.TryPrepare(copy, target.Aging, target.Caravans,
            new[] { target.Category, target.OtherCategory }, out var install));
        install();
        foreach (var category in new[] { target.Category, target.OtherCategory })
        {
            Assert.Equal(nativeBits, BitConverter.SingleToInt32Bits(target.Caravans._priceDictionary[category].AverageBuySellPriceIndex));
            Assert.Equal(nativeBits, BitConverter.SingleToInt32Bits(target.Caravans._coastalPriceDictionary[category].AverageBuySellPriceIndex));
            var args = new object[] { category, target.Party, default(CaravansCampaignBehavior.PriceIndexData) };
            Assert.True((bool)AccessTools.Method(typeof(CaravansCampaignBehavior), "GetCategoryPriceData").Invoke(target.Caravans, args)!);
            var consumed = (CaravansCampaignBehavior.PriceIndexData)args[2];
            Assert.Equal(nativeBits, BitConverter.SingleToInt32Bits(consumed.AverageBuySellPriceIndex));
            Assert.Equal(1000f, consumed.MinBuySellPriceIndex);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MalformedPricePairsAreRejectedOnCaptureAndPreparation(bool coastal)
    {
        var malformed = new[]
        {
            (float.NaN, 999f), (float.NaN, float.NaN),
            (BitConverter.Int32BitsToSingle(0x7fc00000), 1000f),
            (BitConverter.Int32BitsToSingle(unchecked((int)0xffc00001)), 1000f),
            (float.PositiveInfinity, 1000f), (float.NegativeInfinity, 1000f),
            (-1f, 1000f), (1f, float.NaN), (1f, float.PositiveInfinity), (1f, -1f)
        };
        foreach (var (average, minimum) in malformed)
        {
            var source = new Fixture();
            var prices = coastal ? source.Caravans._coastalPriceDictionary : source.Caravans._priceDictionary;
            prices[source.Category] = new(average, minimum);
            Assert.False(source.Capture(out _));
            var target = new Fixture();
            var invalidPrices = new[] { new CampaignCategoryPriceState(20, average, minimum) };
            var cache = new CampaignCaravanCacheState(-1, -1, 0, Array.Empty<CampaignCategoryValueState>(),
                coastal ? Array.Empty<CampaignCategoryPriceState>() : invalidPrices,
                coastal ? invalidPrices : Array.Empty<CampaignCategoryPriceState>());
            var state = new CampaignNativeBehaviorCheckpoint(new CampaignAgingBehaviorState(0,
                Array.Empty<CampaignHeroIntState>(), Array.Empty<CampaignHeroIntState>()),
                new CampaignCaravanBehaviorState(Array.Empty<CampaignPartyTimeState>(), Array.Empty<CampaignPartyTimeState>(), cache));
            Assert.False(target.Service.TryPrepare(state, target.Aging, target.Caravans,
                new[] { target.Category, target.OtherCategory }, out var install));
            Assert.Null(install);
            Assert.Empty(target.Caravans._priceDictionary);
            Assert.Empty(target.Caravans._coastalPriceDictionary);
        }
    }

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
