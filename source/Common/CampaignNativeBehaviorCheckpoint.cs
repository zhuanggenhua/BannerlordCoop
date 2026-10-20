using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Common;

/// <summary>Versioned finite native behavior data, not a complete Campaign memory image.</summary>
[ProtoContract]
public sealed class CampaignNativeBehaviorCheckpoint
{
    [ProtoMember(1)] public int Version { get; private set; }
    [ProtoMember(2)] public CampaignAgingBehaviorState Aging { get; private set; }
    [ProtoMember(3)] public CampaignCaravanBehaviorState Caravans { get; private set; }
    private CampaignNativeBehaviorCheckpoint() { }
    public CampaignNativeBehaviorCheckpoint(CampaignAgingBehaviorState aging, CampaignCaravanBehaviorState caravans = null)
    {
        Version = 2;
        Aging = aging;
        Caravans = caravans;
    }
}

[ProtoContract]
public sealed class CampaignAgingBehaviorState
{
    [ProtoMember(1)] public int GameStartDay { get; private set; }
    [ProtoMember(2)] public CampaignHeroIntState[] ExtraLives { get; private set; }
    [ProtoMember(3)] public CampaignHeroIntState[] YoungHeroes { get; private set; }
    private CampaignAgingBehaviorState()
    {
        ExtraLives = Array.Empty<CampaignHeroIntState>();
        YoungHeroes = Array.Empty<CampaignHeroIntState>();
    }
    public CampaignAgingBehaviorState(int gameStartDay, IEnumerable<CampaignHeroIntState> extraLives,
        IEnumerable<CampaignHeroIntState> youngHeroes)
    {
        GameStartDay = gameStartDay;
        ExtraLives = extraLives?.ToArray();
        YoungHeroes = youngHeroes?.ToArray();
    }
}

[ProtoContract(SkipConstructor = true)]
public readonly struct CampaignHeroIntState
{
    [ProtoMember(1)] public readonly uint HeroHandle;
    [ProtoMember(2)] public readonly int Value;
    public CampaignHeroIntState(uint heroHandle, int value) { HeroHandle = heroHandle; Value = value; }
}

/// <summary>World AI history and its finite caches; per-player UI/history stays local.</summary>
[ProtoContract]
public sealed class CampaignCaravanBehaviorState
{
    [ProtoMember(1)] public CampaignPartyTimeState[] LastHomeVisits { get; private set; }
    [ProtoMember(2)] public CampaignPartyTimeState[] LootedCaravans { get; private set; }
    [ProtoMember(3)] public CampaignCaravanCacheState Cache { get; private set; }
    private CampaignCaravanBehaviorState()
    {
        LastHomeVisits = Array.Empty<CampaignPartyTimeState>();
        LootedCaravans = Array.Empty<CampaignPartyTimeState>();
    }
    public CampaignCaravanBehaviorState(IEnumerable<CampaignPartyTimeState> lastHomeVisits,
        IEnumerable<CampaignPartyTimeState> lootedCaravans, CampaignCaravanCacheState cache)
    {
        LastHomeVisits = lastHomeVisits?.ToArray();
        LootedCaravans = lootedCaravans?.ToArray();
        Cache = cache;
    }
}

[ProtoContract(SkipConstructor = true)]
public readonly struct CampaignPartyTimeState
{
    [ProtoMember(1)] public readonly uint PartyHandle;
    [ProtoMember(2)] public readonly long Ticks;
    public CampaignPartyTimeState(uint partyHandle, long ticks) { PartyHandle = partyHandle; Ticks = ticks; }
}

[ProtoContract]
public sealed class CampaignCaravanCacheState
{
    [ProtoMember(1)] public float NavalDistance { get; private set; }
    [ProtoMember(2)] public float LandDistance { get; private set; }
    [ProtoMember(3)] public uint PackAnimalCategoryHandle { get; private set; }
    [ProtoMember(4)] public CampaignCategoryValueState[] AverageValues { get; private set; }
    [ProtoMember(5)] public CampaignCategoryPriceState[] LandPrices { get; private set; }
    [ProtoMember(6)] public CampaignCategoryPriceState[] CoastalPrices { get; private set; }
    private CampaignCaravanCacheState()
    {
        AverageValues = Array.Empty<CampaignCategoryValueState>();
        LandPrices = Array.Empty<CampaignCategoryPriceState>();
        CoastalPrices = Array.Empty<CampaignCategoryPriceState>();
    }
    public CampaignCaravanCacheState(float navalDistance, float landDistance, uint packAnimalCategoryHandle,
        IEnumerable<CampaignCategoryValueState> averageValues, IEnumerable<CampaignCategoryPriceState> landPrices,
        IEnumerable<CampaignCategoryPriceState> coastalPrices)
    {
        NavalDistance = navalDistance;
        LandDistance = landDistance;
        PackAnimalCategoryHandle = packAnimalCategoryHandle;
        AverageValues = averageValues?.ToArray();
        LandPrices = landPrices?.ToArray();
        CoastalPrices = coastalPrices?.ToArray();
    }
}

[ProtoContract(SkipConstructor = true)]
public readonly struct CampaignCategoryValueState
{
    [ProtoMember(1)] public readonly uint CategoryHandle;
    [ProtoMember(2)] public readonly float Value;
    public CampaignCategoryValueState(uint categoryHandle, float value) { CategoryHandle = categoryHandle; Value = value; }
}

[ProtoContract(SkipConstructor = true)]
public readonly struct CampaignCategoryPriceState
{
    [ProtoMember(1)] public readonly uint CategoryHandle;
    [ProtoMember(2)] public readonly float Average;
    [ProtoMember(3)] public readonly float Minimum;
    public CampaignCategoryPriceState(uint categoryHandle, float average, float minimum)
    { CategoryHandle = categoryHandle; Average = average; Minimum = minimum; }
}
