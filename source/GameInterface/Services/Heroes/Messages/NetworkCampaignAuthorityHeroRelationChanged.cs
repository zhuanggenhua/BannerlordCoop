using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.Heroes.Messages;

/// <summary>A hero relation result published by the current campaign authority.</summary>
[ProtoContract(SkipConstructor = true)]
internal sealed class NetworkCampaignAuthorityHeroRelationChanged : ICampaignAuthorityScopedMessage
{
    [ProtoMember(1)] public string Hero1Id { get; }
    [ProtoMember(2)] public string Hero2Id { get; }
    [ProtoMember(3)] public int Value { get; }

    public NetworkCampaignAuthorityHeroRelationChanged(string hero1Id, string hero2Id, int value)
    {
        Hero1Id = hero1Id;
        Hero2Id = hero2Id;
        Value = value;
    }
}
