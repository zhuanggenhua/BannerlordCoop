using Common;
using Common.Logging;
using Common.Util;
using Common.Messaging;
using GameInterface.Services.Inventory.TradeSkills.Interfaces;
using GameInterface.Services.Inventory.TradeSkills.Messages;
using GameInterface.Services.Inventory.Data;
using GameInterface.Services.ObjectManager;
using Serilog;
using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;

namespace GameInterface.Services.Inventory.TradeSkills.Handlers;

internal class TradeSkillHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<TradeSkillHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly IObjectManager objectManager;
    private readonly ISessionTradePlayerDataInterface sessionTradePlayerDataInterface;

    public TradeSkillHandler(
        IMessageBroker messageBroker,
        IObjectManager objectManager,
        ISessionTradePlayerDataInterface sessionTradePlayerDataInterface)
    {
        this.messageBroker = messageBroker;
        this.objectManager = objectManager;
        this.sessionTradePlayerDataInterface = sessionTradePlayerDataInterface;

        messageBroker.Subscribe<NetworkUpdateTradeData>(Handle_NetworkUpdateTradeData);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<NetworkUpdateTradeData>(Handle_NetworkUpdateTradeData);
    }

    private void Handle_NetworkUpdateTradeData(MessagePayload<NetworkUpdateTradeData> obj)
    {
        var data = obj.What;

        // Locally update trade data on clients
        GameThread.RunSafe(() =>
        {
            if (!sessionTradePlayerDataInterface.TryGetTradeSkillBehavior(out var tradeSkillBehavior)) return;
            if (!objectManager.TryGetObjectWithLogging<Hero>(data.PlayerHeroId, out var playerHero)) return;

            if (playerHero != Hero.MainHero) return;

            if (!TryResolveTradeItems(data.PurchasedItems, out var purchasedItems) ||
                !TryResolveTradeItems(data.SoldItems, out var soldItems)) return;

            if (data.IsTrading)
            {
                foreach (var valueTuple in purchasedItems)
                {
                    tradeSkillBehavior.ProcessPurchases(valueTuple.Item1, valueTuple.Item2);
                }
            }
            if (soldItems != null)
            {
                foreach (var valueTuple2 in soldItems)
                {
                    tradeSkillBehavior.ProcessSales(valueTuple2.Item1, valueTuple2.Item2, data.IsTrading);
                }
            }
        });
    }

    private bool TryResolveTradeItems(
        IEnumerable<TradeItemData> items,
        out List<(ItemRosterElement, int)> resolvedItems)
    {
        var result = new List<(ItemRosterElement, int)>();
        foreach (var tradeItem in items ?? Array.Empty<TradeItemData>())
        {
            if (!objectManager.TryGetObject<ItemObject>(tradeItem.Element.ItemObjectData.ItemObjectId, out var itemObject))
            {
                resolvedItems = null;
                return false;
            }

            ItemModifier itemModifier = null;
            if (!tradeItem.Element.ItemObjectData.ItemModifierNull &&
                !objectManager.TryGetObject(tradeItem.Element.ItemObjectData.ItemModifierId, out itemModifier))
            {
                resolvedItems = null;
                return false;
            }

            ItemRosterElement element;
            using (new AllowedThread())
            {
                element = new ItemRosterElement(itemObject, tradeItem.Element.Amount, itemModifier);
            }

            result.Add((element, tradeItem.TotalPrice));
        }

        resolvedItems = result;
        return true;
    }
}
