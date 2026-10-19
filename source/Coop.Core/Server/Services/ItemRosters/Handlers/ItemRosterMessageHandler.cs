using Common.Messaging;
using Common.Network;
using Common.Network.Coalescing;
using Coop.Core.Server.Services.ItemRosters.Messages;
using GameInterface.Services.ItemObjects;
using GameInterface.Services.ItemRosters.Messages;
using GameInterface.Services.ItemRosters;
using Common;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.ObjectManager;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.Core;

namespace Coop.Core.Server.Services.ItemRosters.Handlers;

/// <summary>
/// Handles ItemRosterUpdated and sends network event to all clients.
/// </summary>
public class ItemRosterMessageHandler : IHandler
{
    // Coalescer channel for per-element ItemRoster updates; member is the item+modifier pair.
    private const string ItemRosterUpdateChannel = "ItemRosterUpdate";

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;
    private readonly ISendCoalescer coalescer;
    private readonly ItemObjectRegistry itemObjectRegistry;
    private readonly ICampaignAuthorityContext authorityContext;

    public ItemRosterMessageHandler(
        IMessageBroker broker,
        INetwork network,
        IObjectManager objectManager,
        ISendCoalescer coalescer,
        ItemObjectRegistry itemObjectRegistry,
        ICampaignAuthorityContext authorityContext = null)
    {
        messageBroker = broker;
        this.network = network;
        this.objectManager = objectManager;
        this.coalescer = coalescer;
        this.itemObjectRegistry = itemObjectRegistry;
        this.authorityContext = authorityContext;
        messageBroker.Subscribe<ItemRosterUpdated>(Handle);
        messageBroker.Subscribe<ItemRosterCleared>(Handle);
        messageBroker.Subscribe<NetworkCampaignItemRosterCount>(Handle);
    }

    public void Handle(MessagePayload<ItemRosterUpdated> payload)
    {
        var message = payload.What;

        if (SettlementMarketRosterPolicy.IsLocalMarketRoster(message.Instance))
            return;

        if (authorityContext?.IsAuthorityKnown == true)
        {
            if (!authorityContext.TryGetLocalAuthorityLease(out _)) return;
            if (!objectManager.TryGetHandleWithLogging(message.Instance, out var rosterId) ||
                !itemObjectRegistry.TryRegisterExistingItem(message.Item, out _, out var itemHandle, out _)) return;
            uint modifierHandle = 0;
            if (message.ItemModifier != null &&
                !objectManager.TryGetHandleWithLogging(message.ItemModifier, out modifierHandle)) return;
            int index = message.Instance.FindIndexOfElement(new EquipmentElement(message.Item, message.ItemModifier));
            int count = index < 0 ? 0 : message.Instance.GetElementNumber(index);
            network.SendAll(new NetworkCampaignItemRosterCount(
                rosterId, itemHandle, modifierHandle, message.Item.StringId, count));
            return;
        }
        if (ModInformation.IsClient) return;

        if (!objectManager.TryGetHandle(message.Instance, out var itemRosterId)) return;
        if (!TryGetItemHandle(message.Item, out var itemId)) return;

        uint itemModifierId = 0;
        if (message.ItemModifier != null &&
            !objectManager.TryGetHandleWithLogging(message.ItemModifier, out itemModifierId))
        {
            return;
        }

        // Sum this tick's deltas for the element and send one update at flush instead of one per AddToCounts.
        var key = new CoalesceKey(ItemRosterUpdateChannel, itemRosterId, $"{itemId}:{itemModifierId}");
        coalescer.Enqueue(key, new SummedPayload<int>(
            message.Amount,
            (running, next) => running + next,
            total => new NetworkItemRosterUpdate(itemRosterId, itemId, itemModifierId, total)));
    }

    private bool TryGetItemHandle(ItemObject item, out uint itemHandle)
    {
        if (!itemObjectRegistry.TryRegisterExistingItem(
                item,
                out _,
                out itemHandle,
                out var announceHandle))
        {
            itemHandle = 0;
            return false;
        }

        if (announceHandle)
        {
            network.SendAll(new NetworkRegisterItemHandle(item.StringId, itemHandle));
            itemObjectRegistry.MarkHandleKnownToClients(itemHandle);
        }

        return true;
    }

    public void Handle(MessagePayload<ItemRosterCleared> payload)
    {
        var message = payload.What;

        if (authorityContext?.IsAuthorityKnown == true)
        {
            if (!authorityContext.TryGetLocalAuthorityLease(out _) ||
                SettlementMarketRosterPolicy.IsLocalMarketRoster(message.ItemRoster)) return;
            if (objectManager.TryGetHandleWithLogging(message.ItemRoster, out var rosterId))
                network.SendAll(new NetworkCampaignItemRosterCount(rosterId, 0, 0, null, 0, clear: true));
            return;
        }
        if (ModInformation.IsClient) return;

        if (!objectManager.TryGetHandle(message.ItemRoster, out var itemRosterId)) return;

        if (SettlementMarketRosterPolicy.IsLocalMarketRoster(message.ItemRoster))
        {
            coalescer.DropInstance(itemRosterId);
            return;
        }

        // A clear supersedes this roster's pending updates; drop them so the clear isn't trailed by a stale update.
        coalescer.DropInstance(itemRosterId);

        network.SendAll(new NetworkItemRosterClear(itemRosterId));
    }

    public void Handle(MessagePayload<NetworkCampaignItemRosterCount> payload)
    {
        var message = payload.What;
        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging<ItemRoster>(message.RosterId, out var roster) ||
                SettlementMarketRosterPolicy.IsLocalMarketRoster(roster)) return;
            if (message.Clear)
            {
                messageBroker.Publish(this, new ClearItemRoster(message.RosterId));
                return;
            }
            if (message.Count < 0 || message.ItemId == 0 || string.IsNullOrEmpty(message.ItemStringId))
                throw new System.InvalidOperationException("Invalid authoritative item roster count.");
            if (objectManager.TryGetObject<ItemObject>(message.ItemId, out var item))
            {
                if (item.StringId != message.ItemStringId)
                    throw new System.InvalidOperationException("Authoritative item handle does not match its string identity.");
            }
            else if (!itemObjectRegistry.TryRegisterExistingItem(message.ItemStringId, message.ItemId)) return;
            messageBroker.Publish(this, new UpdateItemRoster(
                message.RosterId, message.ItemId, message.ModifierId, message.Count, isAbsolute: true));
        }, context: nameof(NetworkCampaignItemRosterCount));
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<ItemRosterUpdated>(Handle);
        messageBroker.Unsubscribe<ItemRosterCleared>(Handle);
        messageBroker.Unsubscribe<NetworkCampaignItemRosterCount>(Handle);
    }
}
