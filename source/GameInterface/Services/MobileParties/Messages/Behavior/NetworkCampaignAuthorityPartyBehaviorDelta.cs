using Common.Messaging;
using GameInterface.Services.MobileParties.Data;
using ProtoBuf;

namespace GameInterface.Services.MobileParties.Messages.Behavior;

/// <summary>
/// Carries a campaign-authority-authored party behavior snapshot. The transport wraps this
/// payload in the current RoomId, AuthorityId, controller, epoch, and ordered sequence.
/// </summary>
[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkCampaignAuthorityPartyBehaviorDelta : IEvent, ICampaignAuthorityScopedMessage
{
    [ProtoMember(1)]
    public readonly NetworkPartyBehaviorUpdateData BehaviorUpdateData;

    public NetworkCampaignAuthorityPartyBehaviorDelta(NetworkPartyBehaviorUpdateData behaviorUpdateData)
    {
        BehaviorUpdateData = behaviorUpdateData;
    }
}
