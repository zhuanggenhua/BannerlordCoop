using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Common;

/// <summary>Absolute roster state needed when an observer becomes the campaign writer.</summary>
[ProtoContract]
public sealed class CampaignRosterCheckpoint
{
    [ProtoMember(1)] public readonly CampaignRosterState[] Rosters;
    private CampaignRosterCheckpoint() { Rosters = Array.Empty<CampaignRosterState>(); }
    public CampaignRosterCheckpoint(IEnumerable<CampaignRosterState> rosters) { Rosters = rosters.ToArray(); }
}

[ProtoContract]
public sealed class CampaignRosterState
{
    [ProtoMember(1)] public readonly uint RosterHandle;
    [ProtoMember(2)] public readonly CampaignRosterElementState[] Elements;
    private CampaignRosterState() { Elements = Array.Empty<CampaignRosterElementState>(); }
    public CampaignRosterState(uint rosterHandle, IEnumerable<CampaignRosterElementState> elements)
    {
        RosterHandle = rosterHandle;
        Elements = elements.ToArray();
    }
}

[ProtoContract(SkipConstructor = true)]
public readonly struct CampaignRosterElementState
{
    [ProtoMember(1)] public readonly uint CharacterHandle;
    [ProtoMember(2)] public readonly int Number;
    [ProtoMember(3)] public readonly int Wounded;
    [ProtoMember(4)] public readonly int Xp;
    public CampaignRosterElementState(uint characterHandle, int number, int wounded, int xp)
    {
        CharacterHandle = characterHandle;
        Number = number;
        Wounded = wounded;
        Xp = xp;
    }
}
