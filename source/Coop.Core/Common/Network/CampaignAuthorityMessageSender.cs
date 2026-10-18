using Common.Messaging;
using Common.Network.Session;
using Common.PacketHandlers;
using Common.Serialization;
using GameInterface.Services.CampaignAuthority;
using System;

namespace Coop.Core.Common.Network;

public interface ICampaignAuthorityMessageSender
{
    void Send(IMessage message, Action<MessagePacket> sendPacket);
}

/// <summary>Keeps campaign envelope sequence assignment atomic with insertion into the transport.</summary>
public sealed class CampaignAuthorityMessageSender : ICampaignAuthorityMessageSender
{
    private readonly ICommonSerializer serializer;
    private readonly ICampaignAuthorityContext authorityContext;
    private readonly object gate = new object();
    private AuthorityLease sequenceLease;
    private long nextSequence;

    public CampaignAuthorityMessageSender(ICommonSerializer serializer, ICampaignAuthorityContext authorityContext)
    {
        this.serializer = serializer;
        this.authorityContext = authorityContext;
    }

    public void Send(IMessage message, Action<MessagePacket> sendPacket)
    {
        if (message is not ICampaignAuthorityScopedMessage || authorityContext == null ||
            !authorityContext.TryGetLocalAuthorityLease(out var lease))
        {
            sendPacket(MessagePacket.Create(message, serializer));
            return;
        }

        lock (gate)
        {
            if (sequenceLease != lease)
            {
                sequenceLease = lease;
                nextSequence = 0;
            }

            if (nextSequence == long.MaxValue)
                throw new InvalidOperationException("Campaign authority sync sequence space is exhausted.");

            var envelope = new NetworkCampaignAuthoritySyncEnvelope(
                lease.RoomId, lease.AuthorityId, lease.AuthorityControllerId, lease.Epoch,
                ++nextSequence, serializer.Serialize(message));
            sendPacket(MessagePacket.Create(envelope, serializer));
        }
    }
}
