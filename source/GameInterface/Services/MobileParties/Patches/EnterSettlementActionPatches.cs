using Common;
using Common.Messaging;
using GameInterface.Policies;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.MobileParties.Messages.Behavior;
using HarmonyLib;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace GameInterface.Services.MobileParties.Patches
{
    [HarmonyPatch(typeof(EnterSettlementAction))]
    internal class EnterSettlementActionPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(EnterSettlementAction.ApplyForParty))]
        private static bool ApplyForPartyPrefix(ref MobileParty mobileParty, ref Settlement settlement)
        {
            if (mobileParty?.Party?.MapEventSide != null) return false;
            if (mobileParty.CurrentSettlement == settlement) return false;

            if (CallOriginalPolicy.IsOriginalAllowed()) return true;

            if (ContainerProvider.TryResolve<ICampaignAuthorityContext>(out var authority) && authority.IsAuthorityKnown)
            {
                if (!CampaignSimulationAuthorityPolicy.CanRunWorldSimulation()) return false;
                // The remote writer publishes native AutoSync results; the host keeps its population notification.
                if (ModInformation.IsClient) return true;
            }

            var message = new PartyEnterSettlementAttempted(settlement, mobileParty);
            MessageBroker.Instance.Publish(mobileParty, message);

            return ModInformation.IsServer;
        }
    }
}
