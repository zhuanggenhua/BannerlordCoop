using Common.Logging;
using Common.Messaging;
using Common;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Settlements.Messages;
using GameInterface.Services.Settlements.Patches;
using Serilog;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace GameInterface.Services.Settlements.Handlers
{
    /// <summary>
    /// Handles syncs for <see cref="SettlementComponent"/> patches
    /// </summary>
    public class SettlementComponentHandler : IHandler
    {
        private static readonly ILogger Logger = LogManager.GetLogger<SettlementHandler>();
        private readonly IMessageBroker messageBroker;
        private readonly IObjectManager objectManager;

        public SettlementComponentHandler(IMessageBroker messageBroker, IObjectManager objectManager)
        {
            this.messageBroker = messageBroker;
            this.objectManager = objectManager;
            messageBroker.Subscribe<ChangeSettlementComponentGold>(HandleGoldChanged);
            messageBroker.Subscribe<ChangeSettlementComponentIsOwnerUnassigned>(HandleIsOwnerUnassignedChanged);
            messageBroker.Subscribe<ChangeSettlementComponentOwner>(HandleOwnerChanged);
        }
        public void Dispose()
        {
            messageBroker.Unsubscribe<ChangeSettlementComponentGold>(HandleGoldChanged);
            messageBroker.Unsubscribe<ChangeSettlementComponentIsOwnerUnassigned>(HandleIsOwnerUnassignedChanged);
            messageBroker.Unsubscribe<ChangeSettlementComponentOwner>(HandleOwnerChanged);
        }
        private void HandleOwnerChanged(MessagePayload<ChangeSettlementComponentOwner> payload)
        {
            GameThread.RunSafe(() => ApplyOwner(payload.What), context: nameof(HandleOwnerChanged));
        }

        private void ApplyOwner(ChangeSettlementComponentOwner message)
        {
            using var replay = CampaignExecutionScope.Enter(CampaignExecutionPhase.Replay);
            if (!objectManager.TryGetObject<SettlementComponent>(message.SettlementComponentId, out var obj))
            {
                MessageApplyScope.Fail("Missing SettlementComponent " + message.SettlementComponentId);
                Logger.Error("Unable to find SettlementComponent ({SettlementComponentId})", message.SettlementComponentId);
                return;
            }
            PartyBase owner;
            if (message.OwnerId == null) owner = null;
            else if (objectManager.TryGetObject<Settlement>(message.OwnerId, out var settlement))
            {
                owner = settlement.Party;
            }
            else if (objectManager.TryGetObject<MobileParty>(message.OwnerId, out var party))
            {
                owner = party.Party;
            }
            else
            {
                MessageApplyScope.Fail("Missing SettlementComponent owner PartyBase " + message.OwnerId);
                Logger.Error("Unable to find PartyBase ({OwnerId})", message.OwnerId);
                return;
            }
            OwnerSettlementComponentPatch.RunSettlementComponentOwnerChanged(obj, owner);
        }

        private void HandleIsOwnerUnassignedChanged(MessagePayload<ChangeSettlementComponentIsOwnerUnassigned> payload)
        {
            GameThread.RunSafe(() => ApplyIsOwnerUnassigned(payload.What), context: nameof(HandleIsOwnerUnassignedChanged));
        }

        private void ApplyIsOwnerUnassigned(ChangeSettlementComponentIsOwnerUnassigned message)
        {
            using var replay = CampaignExecutionScope.Enter(CampaignExecutionPhase.Replay);
            if (!objectManager.TryGetObject<SettlementComponent>(message.SettlementComponentId, out var obj))
            {
                MessageApplyScope.Fail("Missing SettlementComponent " + message.SettlementComponentId);
                Logger.Error("Unable to find SettlementComponent ({SettlementComponentId})", message.SettlementComponentId);
                return;
            }
            IsOwnerUnassignedSettlementComponentPatch.RunSettlementComponentIsOwnerUnassignedChanged(obj, message.IsOwnerUnassigned);
        }

        private void HandleGoldChanged(MessagePayload<ChangeSettlementComponentGold> payload)
        {
            GameThread.RunSafe(() => ApplyGold(payload.What), context: nameof(HandleGoldChanged));
        }

        private void ApplyGold(ChangeSettlementComponentGold message)
        {
            using var replay = CampaignExecutionScope.Enter(CampaignExecutionPhase.Replay);
            if (!objectManager.TryGetObject<SettlementComponent>(message.SettlementComponentId, out var obj))
            {
                MessageApplyScope.Fail("Missing SettlementComponent " + message.SettlementComponentId);
                Logger.Error("Unable to find SettlementComponent ({SettlementComponentId})", message.SettlementComponentId);
                return;
            }
            GoldSettlementComponentPatch.RunSettlementComponentGoldChanged(obj, message.Gold);
        }
    }
}
