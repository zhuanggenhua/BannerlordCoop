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
    private CampaignNativeBehaviorCheckpoint() { }
    public CampaignNativeBehaviorCheckpoint(CampaignAgingBehaviorState aging)
    {
        Version = 1;
        Aging = aging;
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
