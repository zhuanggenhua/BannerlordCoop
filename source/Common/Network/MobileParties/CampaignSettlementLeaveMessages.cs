using Common.Messaging;
using ProtoBuf;

namespace Common.Network.MobileParties;

[ProtoContract(SkipConstructor = true)]
public sealed class CampaignSettlementLeaveRequest : ICommand
{
    [ProtoMember(1)] public readonly string RoomId;
    [ProtoMember(2)] public readonly string AuthorityId;
    [ProtoMember(3)] public readonly string AuthorityControllerId;
    [ProtoMember(4)] public readonly long Epoch;
    [ProtoMember(5)] public readonly string ControllerId;
    [ProtoMember(6)] public readonly long Sequence;
    [ProtoMember(7)] public readonly uint PartyId;
    [ProtoMember(8)] public readonly uint SettlementId;
    [ProtoMember(9)] public readonly string RequestStreamId;

    public CampaignSettlementLeaveRequest(AuthorityLease lease, string controllerId,
        long sequence, uint partyId, uint settlementId, string requestStreamId)
    {
        RoomId = lease.RoomId;
        AuthorityId = lease.AuthorityId;
        AuthorityControllerId = lease.AuthorityControllerId;
        Epoch = lease.Epoch;
        ControllerId = controllerId;
        Sequence = sequence;
        PartyId = partyId;
        SettlementId = settlementId;
        RequestStreamId = requestStreamId;
    }

    public bool Matches(AuthorityLease lease) => lease.IsValid && !lease.IsPaused &&
        RoomId == lease.RoomId && AuthorityId == lease.AuthorityId &&
        AuthorityControllerId == lease.AuthorityControllerId && Epoch == lease.Epoch &&
        !string.IsNullOrWhiteSpace(ControllerId) && !string.IsNullOrWhiteSpace(RequestStreamId) && Sequence > 0 && PartyId != 0 && SettlementId != 0;
}

[ProtoContract(SkipConstructor = true)]
public sealed class CampaignSettlementLeaveCommand : IServerToClientCommand
{
    [ProtoMember(1)] public readonly CampaignSettlementLeaveRequest Request;
    public CampaignSettlementLeaveCommand(CampaignSettlementLeaveRequest request) => Request = request;
}

[ProtoContract(SkipConstructor = true)]
public sealed class CampaignSettlementLeaveApplied : ICommand, ICampaignAuthorityScopedMessage
{
    [ProtoMember(1)] public readonly CampaignSettlementLeaveRequest Request;
    public CampaignSettlementLeaveApplied(CampaignSettlementLeaveRequest request) => Request = request;
}

[ProtoContract(SkipConstructor = true)]
public sealed class CampaignSettlementLeaveRejected : ICommand, ICampaignAuthorityScopedMessage
{
    [ProtoMember(1)] public readonly CampaignSettlementLeaveRequest Request;
    public CampaignSettlementLeaveRejected(CampaignSettlementLeaveRequest request) => Request = request;
}
