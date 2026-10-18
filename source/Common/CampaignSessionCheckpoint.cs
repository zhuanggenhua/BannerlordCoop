using ProtoBuf;

namespace Common;

/// <summary>Detached co-op save metadata for the same native campaign, excluding local presentation state.</summary>
[ProtoContract(SkipConstructor = true)]
public sealed class CampaignSessionCheckpoint
{
    [ProtoMember(1)] public readonly string CampaignId;
    [ProtoMember(2)] public readonly string SessionJson;
    public CampaignSessionCheckpoint(string campaignId, string sessionJson)
    {
        CampaignId = campaignId;
        SessionJson = sessionJson;
    }
}
