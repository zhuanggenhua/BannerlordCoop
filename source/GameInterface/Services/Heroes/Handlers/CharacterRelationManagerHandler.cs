using Common;
using Common.Messaging;
using Common.Network;
using Common.Util;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Heroes.Messages;
using GameInterface.Services.ObjectManager;
using TaleWorlds.CampaignSystem;

namespace GameInterface.Services.Heroes.Handlers
{
    /// <summary>
    /// Replicates server-authoritative hero relation changes to clients.
    /// </summary>
    public class CharacterRelationManagerHandler : IHandler
    {
        private readonly IMessageBroker messageBroker;
        private readonly IObjectManager objectManager;
        private readonly INetwork network;

        public CharacterRelationManagerHandler(IMessageBroker messageBroker, IObjectManager objectManager, INetwork network)
        {
            this.messageBroker = messageBroker;
            this.objectManager = objectManager;
            this.network = network;
            messageBroker.Subscribe<HeroRelationChanged>(Handle);
            messageBroker.Subscribe<NetworkHeroRelationChanged>(Handle);
            messageBroker.Subscribe<NetworkCampaignAuthorityHeroRelationChanged>(Handle);
        }

        public void Dispose()
        {
            messageBroker.Unsubscribe<HeroRelationChanged>(Handle);
            messageBroker.Unsubscribe<NetworkHeroRelationChanged>(Handle);
            messageBroker.Unsubscribe<NetworkCampaignAuthorityHeroRelationChanged>(Handle);
        }

        private void Handle(MessagePayload<HeroRelationChanged> obj)
        {
            if (!CampaignSimulationAuthorityPolicy.CanRunWorldSimulation()) return;

            var payload = obj.What;
            var change = new NetworkHeroRelationChanged(payload.Hero1Id, payload.Hero2Id, payload.Value);

            if (ContainerProvider.TryResolve<ICampaignAuthorityContext>(out var authorityContext) &&
                authorityContext.IsAuthorityKnown)
            {
                if (!authorityContext.TryGetLocalAuthorityLease(out _)) return;
                network.SendAll(new NetworkCampaignAuthorityHeroRelationChanged(
                    payload.Hero1Id,
                    payload.Hero2Id,
                    payload.Value));
                return;
            }

            // Preserve the pre-lease room-host broadcast during session initialization.
            if (ModInformation.IsServer) network.SendAll(change);
        }

        private void Handle(MessagePayload<NetworkHeroRelationChanged> obj)
        {
            ApplyRelation(obj.What.Hero1Id, obj.What.Hero2Id, obj.What.Value);
        }

        private void Handle(MessagePayload<NetworkCampaignAuthorityHeroRelationChanged> obj)
        {
            ApplyRelation(obj.What.Hero1Id, obj.What.Hero2Id, obj.What.Value);
        }

        private void ApplyRelation(string hero1Id, string hero2Id, int value)
        {
            GameThread.RunSafe(() =>
            {
                using var replay = CampaignExecutionScope.Enter(CampaignExecutionPhase.Replay);
                if (!objectManager.TryGetObjectWithLogging<Hero>(hero1Id, out var hero1))
                { MessageApplyScope.Fail("Missing relation source Hero " + hero1Id); return; }
                if (!objectManager.TryGetObjectWithLogging<Hero>(hero2Id, out var hero2))
                { MessageApplyScope.Fail("Missing relation target Hero " + hero2Id); return; }

                // The change is applied as an absolute value (SetHeroRelation) to match the server exactly
                // and avoid the RNG/model factor ChangeRelationAction applies to positive gains; capture the
                // delta first so we can still show the bottom-left popup vanilla would.
                int delta = value - CharacterRelationManager.GetHeroRelation(hero1, hero2);

                using (new AllowedThread())
                {
                    CharacterRelationManager.SetHeroRelation(hero1, hero2, value);
                }
            }, context: $"apply hero relation {hero1Id}<->{hero2Id}");
        }
    }
}
