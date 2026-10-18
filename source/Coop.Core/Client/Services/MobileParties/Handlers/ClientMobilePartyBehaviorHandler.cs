using Common.Messaging;
using Common.Network;
using Common;
using Coop.Core.Server.Services.MobileParties.Packets;
using GameInterface.Services.MobileParties.Messages.Behavior;
using GameInterface.Services.MobileParties.Data;
using GameInterface.Services.CampaignAuthority;
using System.Threading;

namespace Coop.Core.Client.Services.MobileParties.Handlers
{
    /// <summary>
    /// Handles client communication related to party behavior synchronisation.
    /// </summary>
    /// <seealso cref="GameInterface.Services.MobileParties.Handlers.MobilePartyBehaviorHandler">Game Interface's Handler</seealso>
    public class ClientMobilePartyBehaviorHandler : IHandler
    {
        private readonly IMessageBroker messageBroker;
        private readonly INetwork network;
        private readonly IPartyBehaviorWireMapper wireMapper;
        private readonly ICampaignAuthorityContext authorityContext;
        private long nextRequestSequence;
        private readonly object authorityCommandGate = new object();
        private AuthorityLease lastAuthorityCommandLease;
        private long lastAuthorityCommandSequence;

        public ClientMobilePartyBehaviorHandler(
            IMessageBroker messageBroker,
            INetwork network,
            IPartyBehaviorWireMapper wireMapper,
            ICampaignAuthorityContext authorityContext)
        {
            this.messageBroker = messageBroker;
            this.network = network;
            this.wireMapper = wireMapper;
            this.authorityContext = authorityContext;
            messageBroker.Subscribe<ControlledPartyBehaviorUpdated>(Handle);
            messageBroker.Subscribe<PartyBehaviorUpdated>(HandleAuthorityUpdate);
            messageBroker.Subscribe<NetworkCampaignAuthorityPartyBehaviorCommand>(HandleAuthorityCommand);
        }

        internal void Handle(MessagePayload<ControlledPartyBehaviorUpdated> obj)
        {
            if (!wireMapper.TryToNetwork(obj.What.BehaviorUpdateData, out var data)) return;
            AuthorityLease lease = default;
            if (authorityContext.IsAuthorityKnown &&
                (!authorityContext.TryGetCurrentLease(out lease) || !lease.IsValid || lease.IsPaused))
                return;

            long sequence = Interlocked.Increment(ref nextRequestSequence);
            network.SendAll(new RequestMobilePartyBehaviorPacket(data, sequence, lease));
        }

        internal void HandleAuthorityUpdate(MessagePayload<PartyBehaviorUpdated> obj)
        {
            if (!authorityContext.TryGetLocalAuthorityLease(out var lease) || !lease.IsValid ||
                !wireMapper.TryToNetwork(obj.What.BehaviorUpdateData, out var data))
                return;

            network.SendAll(new NetworkCampaignAuthorityPartyBehaviorDelta(data));
        }

        internal void HandleAuthorityCommand(MessagePayload<NetworkCampaignAuthorityPartyBehaviorCommand> obj)
        {
            GameThread.RunSafe(() =>
            {
                // Recheck the lease after earlier world deltas and freeze actions, on the
                // same thread that resolves handles and applies this command once.
                if (!authorityContext.TryGetLocalAuthorityLease(out var lease) ||
                    !obj.What.Matches(lease) ||
                    !wireMapper.TryFromNetwork(obj.What.BehaviorUpdateData, out var data) ||
                    !TryAdvanceAuthorityCommandSequence(lease, obj.What.Sequence))
                    return;

                data.OriginControllerId = obj.What.OriginControllerId;
                messageBroker.Publish(obj.Who, new UpdatePartyBehavior(ref data, alreadyOnGameThread: true));
            }, context: nameof(ClientMobilePartyBehaviorHandler));
        }

        private bool TryAdvanceAuthorityCommandSequence(AuthorityLease lease, long sequence)
        {
            lock (authorityCommandGate)
            {
                if (lastAuthorityCommandLease != lease)
                {
                    lastAuthorityCommandLease = lease;
                    lastAuthorityCommandSequence = 0;
                }

                if (sequence <= lastAuthorityCommandSequence) return false;
                lastAuthorityCommandSequence = sequence;
                return true;
            }
        }

        public void Dispose()
        {
            messageBroker.Unsubscribe<ControlledPartyBehaviorUpdated>(Handle);
            messageBroker.Unsubscribe<PartyBehaviorUpdated>(HandleAuthorityUpdate);
            messageBroker.Unsubscribe<NetworkCampaignAuthorityPartyBehaviorCommand>(HandleAuthorityCommand);
        }
    }
}
