using Common;
using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;

namespace GameInterface.Services.CampaignAuthority.NativeState;

internal sealed partial class CampaignNativeBehaviorCheckpointService
{
    private bool TryCaptureCaravans(CaravansCampaignBehavior behavior, ItemCategory[] categories,
        out CampaignCaravanBehaviorState state)
    {
        state = null;
        if (behavior == null || categories == null) return Reject("capture.caravan.behavior/categories=null");
        if (!ValidDistance(behavior._navalCaravanVeryFarCache) || !ValidDistance(behavior._defaultCaravanVeryFarCache))
            return RejectValue($"capture.caravan.distance naval={behavior._navalCaravanVeryFarCache:R}, land={behavior._defaultCaravanVeryFarCache:R}");
        if (
            !TryCapturePartyTimes(behavior._caravanLastHomeTownVisitTime, "lastHomeVisit", out var visits) ||
            !TryCapturePartyTimes(behavior._lootedCaravans, "looted", out var looted) ||
            !TryCaptureValues(behavior._averageValuesCached, out var averages) ||
            !TryCapturePrices(behavior._priceDictionary, out var prices) ||
            !TryCapturePrices(behavior._coastalPriceDictionary, out var coastal)) return false;
        uint packCategory = 0;
        if (behavior._packAnimalCategoryIndex != -1 &&
            (behavior._packAnimalCategoryIndex < 0 || behavior._packAnimalCategoryIndex >= categories.Length ||
             !TryStableHandle(categories[behavior._packAnimalCategoryIndex], out packCategory)))
            return RejectValue($"capture.caravan.packIndex={behavior._packAnimalCategoryIndex}, categories={categories.Length}");
        state = new CampaignCaravanBehaviorState(visits, looted, new CampaignCaravanCacheState(
            behavior._navalCaravanVeryFarCache, behavior._defaultCaravanVeryFarCache, packCategory,
            averages, prices, coastal));
        return true;
    }

    private bool TryPrepareCaravans(CampaignCaravanBehaviorState state, CaravansCampaignBehavior behavior,
        ItemCategory[] categories, out Action install)
    {
        install = null;
        var cache = state?.Cache;
        if (state == null || behavior == null || categories == null || cache == null ||
            behavior._averageValuesCached == null || behavior._priceDictionary == null ||
            behavior._coastalPriceDictionary == null || behavior._totalValueOfItemsAtCategory == null)
            return Reject("prepare.caravan.header/cache/nativeDictionary=null");
        if (!ValidDistance(cache.NavalDistance) || !ValidDistance(cache.LandDistance))
            return RejectValue($"prepare.caravan.distance naval={cache.NavalDistance:R}, land={cache.LandDistance:R}");
        if (
            !TryPreparePartyTimes(state.LastHomeVisits, "lastHomeVisit", out var visits) ||
            !TryPreparePartyTimes(state.LootedCaravans, "looted", out var looted) ||
            !TryPrepareValues(cache.AverageValues, out var averages) ||
            !TryPreparePrices(cache.LandPrices, out var prices) ||
            !TryPreparePrices(cache.CoastalPrices, out var coastal)) return false;
        int packIndex = -1;
        if (cache.PackAnimalCategoryHandle != 0)
        {
            if (!objects.TryGetObject<ItemCategory>(cache.PackAnimalCategoryHandle, out var category))
                return RejectValue($"prepare.caravan.packCategory handle={cache.PackAnimalCategoryHandle} missing");
            for (int i = 0; i < categories.Length; i++)
                if (ReferenceEquals(categories[i], category))
                {
                    if (packIndex != -1) return RejectValue($"prepare.caravan.packCategory handle={cache.PackAnimalCategoryHandle} duplicate local category");
                    packIndex = i;
                }
            if (packIndex == -1) return RejectValue($"prepare.caravan.packCategory handle={cache.PackAnimalCategoryHandle} absent local category");
        }
        install = () =>
        {
            behavior._caravanLastHomeTownVisitTime = visits;
            behavior._lootedCaravans = looted;
            Replace(behavior._averageValuesCached, averages);
            Replace(behavior._priceDictionary, prices);
            Replace(behavior._coastalPriceDictionary, coastal);
            behavior._navalCaravanVeryFarCache = cache.NavalDistance;
            behavior._defaultCaravanVeryFarCache = cache.LandDistance;
            behavior._packAnimalCategoryIndex = packIndex;
            // Original Think/Buy/Sell rebuild this per-party scratch before reading it.
            behavior._totalValueOfItemsAtCategory.Clear();
        };
        return true;
    }

    private bool TryCapturePartyTimes(Dictionary<MobileParty, CampaignTime> source, string field, out CampaignPartyTimeState[] states)
    {
        states = null;
        if (source == null) return RejectValue($"capture.caravan.{field}=null");
        var result = new List<CampaignPartyTimeState>();
        var seen = new HashSet<uint>();
        foreach (var pair in source)
        {
            if (pair.Value.NumTicks < 0 || !TryStableHandle(pair.Key, out var handle))
                return RejectValue($"capture.caravan.{field} party={pair.Key?.StringId}, ticks={pair.Value.NumTicks}, missing/invalid registry handle");
            if (!seen.Add(handle)) return RejectValue($"capture.caravan.{field} duplicate handle={handle}");
            result.Add(new CampaignPartyTimeState(handle, pair.Value.NumTicks));
        }
        states = result.ToArray();
        return true;
    }

    private bool TryPreparePartyTimes(CampaignPartyTimeState[] states, string field, out Dictionary<MobileParty, CampaignTime> result)
    {
        result = new Dictionary<MobileParty, CampaignTime>();
        if (states == null) return RejectValue($"prepare.caravan.{field}=null");
        foreach (var state in states)
        {
            if (state.PartyHandle == 0 || state.Ticks < 0 ||
                !objects.TryGetObject<MobileParty>(state.PartyHandle, out var party) || result.ContainsKey(party))
                return RejectValue($"prepare.caravan.{field} handle={state.PartyHandle}, ticks={state.Ticks}, missing/invalid/duplicate reference");
            result.Add(party, new CampaignTime(state.Ticks));
        }
        return true;
    }

    private bool TryCaptureValues(Dictionary<ItemCategory, float> source, out CampaignCategoryValueState[] states)
    {
        states = null;
        if (source == null) return Reject("capture.caravan.categoryCache=null");
        var result = new List<CampaignCategoryValueState>();
        var seen = new HashSet<uint>();
        foreach (var pair in source)
        {
            if (!ValidValue(pair.Value) || !TryStableHandle(pair.Key, out var handle) || !seen.Add(handle))
                return RejectValue($"capture.caravan.average category={pair.Key?.StringId}, value={pair.Value:R}, invalid value/registry");
            result.Add(new CampaignCategoryValueState(handle, pair.Value));
        }
        states = result.ToArray();
        return true;
    }

    private bool TryPrepareValues(CampaignCategoryValueState[] states, out Dictionary<ItemCategory, float> result)
    {
        result = new Dictionary<ItemCategory, float>();
        if (states == null) return Reject("prepare.caravan.categoryCache=null");
        foreach (var state in states)
        {
            if (state.CategoryHandle == 0 || !ValidValue(state.Value) ||
                !objects.TryGetObject<ItemCategory>(state.CategoryHandle, out var category) || result.ContainsKey(category))
                return RejectValue($"prepare.caravan.average handle={state.CategoryHandle}, value={state.Value:R}, invalid/duplicate reference");
            result.Add(category, state.Value);
        }
        return true;
    }

    private bool TryCapturePrices(Dictionary<ItemCategory, CaravansCampaignBehavior.PriceIndexData> source,
        out CampaignCategoryPriceState[] states)
    {
        states = null;
        if (source == null) return Reject("capture.caravan.categoryCache=null");
        var result = new List<CampaignCategoryPriceState>();
        var seen = new HashSet<uint>();
        foreach (var pair in source)
        {
            if (!ValidValue(pair.Value.AverageBuySellPriceIndex) || !ValidValue(pair.Value.MinBuySellPriceIndex) ||
                !TryStableHandle(pair.Key, out var handle) || !seen.Add(handle))
                return RejectValue($"capture.caravan.price category={pair.Key?.StringId}, average={pair.Value.AverageBuySellPriceIndex:R}, min={pair.Value.MinBuySellPriceIndex:R}, invalid value/registry");
            result.Add(new CampaignCategoryPriceState(handle, pair.Value.AverageBuySellPriceIndex, pair.Value.MinBuySellPriceIndex));
        }
        states = result.ToArray();
        return true;
    }

    private bool TryPreparePrices(CampaignCategoryPriceState[] states,
        out Dictionary<ItemCategory, CaravansCampaignBehavior.PriceIndexData> result)
    {
        result = new Dictionary<ItemCategory, CaravansCampaignBehavior.PriceIndexData>();
        if (states == null) return Reject("prepare.caravan.categoryCache=null");
        foreach (var state in states)
        {
            if (state.CategoryHandle == 0 || !ValidValue(state.Average) || !ValidValue(state.Minimum) ||
                !objects.TryGetObject<ItemCategory>(state.CategoryHandle, out var category) || result.ContainsKey(category))
                return RejectValue($"prepare.caravan.price handle={state.CategoryHandle}, average={state.Average:R}, min={state.Minimum:R}, invalid/duplicate reference");
            result.Add(category, new CaravansCampaignBehavior.PriceIndexData(state.Average, state.Minimum));
        }
        return true;
    }

    private bool TryStableHandle<T>(T value, out uint handle) where T : class
    {
        handle = 0;
        return value != null && objects.TryGetHandle(value, out handle) && handle != 0 &&
            objects.TryGetObject<T>(handle, out var local) && ReferenceEquals(local, value);
    }
    private static bool ValidValue(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0;
    private static bool ValidDistance(float value) => value == -1f || ValidValue(value);
    private static void Replace<TKey, TValue>(Dictionary<TKey, TValue> target, Dictionary<TKey, TValue> source)
    {
        target.Clear();
        foreach (var pair in source) target.Add(pair.Key, pair.Value);
    }
}
