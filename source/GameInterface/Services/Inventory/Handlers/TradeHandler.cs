using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Util;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using GameInterface.Services.GameDebug.Messages;
using GameInterface.Services.Inventory.Data;
using GameInterface.Services.Inventory.Interfaces;
using GameInterface.Services.Inventory.Messages;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.ItemRosters;
using GameInterface.Services.Villages.Interfaces;
using GameInterface.Services.Workshops.Messages;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using HarmonyLib;
using Helpers;
using LiteNetLib;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace GameInterface.Services.Inventory.Handlers;

internal class TradeHandler : IHandler
{
    private static readonly ILogger logger = LogManager.GetLogger<TradeHandler>();

    private readonly IInventoryLogicInterface inventoryLogicInterface;
    private readonly IMessageBroker messageBroker;
    private readonly IObjectManager objectManager;
    private readonly INetwork network;
    private readonly IVillageHostileActionInterface villageHostileActionInterface;
    private readonly IPlayerManager players;
    private readonly ICampaignAuthorityContext authorityContext;
    private readonly IControllerIdProvider controllerIdProvider;
    private readonly ISessionIdentity sessionIdentity;
    private readonly object authorityCommandGate = new();
    private AuthorityLease authorityCommandLease;
    private long nextAuthorityCommandSequence;
    private AuthorityLease lastAuthorityCommandLease;
    private long lastAuthorityCommandSequence;
    private sealed class CommitCursor { public long LastRequest; }
    private readonly ConditionalWeakTable<object, CommitCursor> commits = new();

    internal bool TryBeginCommit(object sender, long requestId)
    {
        if (requestId <= 0) return false;
        var cursor = commits.GetValue(sender ?? this, _ => new CommitCursor());
        // Invoked on the game thread after ownership checks. Reconnecting peers get a fresh window.
        if (requestId <= cursor.LastRequest) return false;
        cursor.LastRequest = requestId;
        return true;
    }

    public TradeHandler(
        IInventoryLogicInterface inventoryLogicInterface,
        IMessageBroker messageBroker,
        IObjectManager objectManager,
        INetwork network,
        IVillageHostileActionInterface villageHostileActionInterface,
        IPlayerManager players,
        ICampaignAuthorityContext authorityContext = null,
        IControllerIdProvider controllerIdProvider = null,
        ISessionIdentity sessionIdentity = null)
    {
        this.inventoryLogicInterface = inventoryLogicInterface;
        this.messageBroker = messageBroker;
        this.objectManager = objectManager;
        this.network = network;
        this.villageHostileActionInterface = villageHostileActionInterface;
        this.players = players;
        this.authorityContext = authorityContext;
        this.controllerIdProvider = controllerIdProvider;
        this.sessionIdentity = sessionIdentity;

        messageBroker.Subscribe<TradeAttempted>(Handle_TradeAttempted);
        messageBroker.Subscribe<CompleteTrade>(Handle_CompleteTrade);
        messageBroker.Subscribe<NetworkCampaignAuthorityCompleteTradeCommand>(Handle_AuthorityCompleteTrade);
        messageBroker.Subscribe<UpdateEquipmentClients>(Handle_UpdateEquipmentClients);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<TradeAttempted>(Handle_TradeAttempted);
        messageBroker.Unsubscribe<CompleteTrade>(Handle_CompleteTrade);
        messageBroker.Unsubscribe<NetworkCampaignAuthorityCompleteTradeCommand>(Handle_AuthorityCompleteTrade);
        messageBroker.Unsubscribe<UpdateEquipmentClients>(Handle_UpdateEquipmentClients);
        lock (authorityCommandGate)
        {
            authorityCommandLease = default;
            nextAuthorityCommandSequence = 0;
            lastAuthorityCommandLease = default;
            lastAuthorityCommandSequence = 0;
        }
    }

    private void Handle_TradeAttempted(MessagePayload<TradeAttempted> payload)
    {
        var what = payload.What;

        var isManagingWarehouse = what.InventoryMode == InventoryScreenHelper.InventoryMode.Warehouse;

        // Don't update warehouse rosters directly if managing a warehouse, server uses CoopSession.WorkshopPlayerData
        // Not all left rosters need to be managed by server so no need to check result of resolving it
        // e.g. Discarding items in default inventory screen only needs to save the right roster
        // Don't need to log the check here, from roster can not resolve legimately
        string fromRosterId = null;
        if (!isManagingWarehouse) objectManager.TryGetId(what.FromRoster, out fromRosterId);

        if (!objectManager.TryGetIdWithLogging(what.ToRoster, out var toRosterId)) return;
        if (!objectManager.TryGetIdWithLogging(what.Hero, out var heroId)) return;
        if (!objectManager.TryGetIdWithLogging(what.OwnerParty, out var ownerPartyId)) return;
        if (!objectManager.TryGetIdWithLogging(what.InitialCharacterEquipment.HeroObject, out var initialHeroId)) return;

        // CurrentMobileParty can be already destroyed when this logic runs. Attempt to get an id without logging
        objectManager.TryGetId(what.CurrentMobileParty, out var currentMobilePartyId);

        string currentSettlementComponentId = null;
        if (what.CurrentSettlementComponent is not null && 
            !objectManager.TryGetIdWithLogging(what.CurrentSettlementComponent, out currentSettlementComponentId)) return;

        if (!TryResolveItemRosterData(what.FromRoster, out var fromRosterData) ||
            !TryResolveItemRosterData(what.ToRoster, out var toRosterData))
            return;

        if (!TryResolveTradeItemIds(what.BoughtItems, out var boughtItems) ||
            !TryResolveTradeItemIds(what.SoldItems, out var soldItems))
            return;

        if (what.CanGainXpFromDiscarding)
        {
            if (!TryResolveLeftLootIds(what.FromRoster, out soldItems)) return;
        }

        AuthorityLease authorityLease = default;
        if (authorityContext != null && authorityContext.TryGetCurrentLease(out var currentLease))
            authorityLease = currentLease;

        if (!TryResolveCharacterIdEquipmentsData(what.OwnerParty, what.InitialCharacterEquipment, out var characterIdEquipmentsData))
            return;

        var message = new CompleteTrade(
            fromRosterId,
            fromRosterId is null,
            toRosterId,
            fromRosterData,
            toRosterData,
            characterIdEquipmentsData,
            what.IsTrading,
            what.CanGainXpFromDiscarding,
            isManagingWarehouse,
            heroId,
            initialHeroId,
            what.TotalAmount,
            what.MerchantGold,
            ownerPartyId,
            currentMobilePartyId,
            currentSettlementComponentId is null,
            currentSettlementComponentId,
            boughtItems,
            soldItems,
            what.ForceTransferId,
            authorityLease
        );

        ScheduleLocalMarketRosterCommit(message);
        DispatchCompleteTrade(message);
    }

    private void ScheduleLocalMarketRosterCommit(CompleteTrade message)
    {
        if (message.IsFromItemRosterNull || string.IsNullOrWhiteSpace(message.FromItemRosterId) ||
            message.FromItemRosterData == null)
            return;

        // DoneLogic resets the preview after publishing TradeAttempted. Reapply only this replica's
        // settlement shop stock after that reset; the authority command carries the shared party-side result.
        GameThread.EnqueueSafe(() =>
        {
            if (!objectManager.TryGetObject<ItemRoster>(message.FromItemRosterId, out var marketRoster) ||
                !SettlementMarketRosterPolicy.IsLocalMarketRoster(marketRoster) ||
                !TryResolveItemRosterData(message.FromItemRosterData, out var marketElements))
                return;

            ApplyLocalMarketRosterUpdate(
                inventoryLogicInterface,
                marketRoster,
                marketElements,
                isClient: ModInformation.IsClient);
        }, context: "LocalMarketRosterCommit");
    }

    internal static void ApplyLocalMarketRosterUpdate(
        IInventoryLogicInterface inventoryLogic,
        ItemRoster marketRoster,
        ItemRosterElement[] marketElements,
        bool isClient)
    {
        if (isClient)
        {
            using (new AllowedThread())
                inventoryLogic.UpdateRosterWithData(marketRoster, marketElements);
        }
        else
        {
            inventoryLogic.UpdateRosterWithData(marketRoster, marketElements);
        }
    }

    internal void DispatchCompleteTrade(CompleteTrade message)
    {
        if (CampaignSimulationAuthorityPolicy.CanRunWorldSimulation() || ModInformation.IsListenAuthority)
            // DoneLogic still has to reset its preview. Apply the frozen commit after it returns.
            GameThread.EnqueueSafe(() => messageBroker.Publish(this, message), context: "ListenAuthority.TradeCommit");
        else
            network.SendAll(message);
    }

    private void Handle_CompleteTrade(MessagePayload<CompleteTrade> payload)
    {
        var message = payload.What;
        var peer = payload.Who as NetPeer;

        GameThread.RunSafe(() =>
        {
            if (TryRouteToRemoteAuthority(message, peer)) return;
            ApplyCompleteTrade(message, peer, requiredLease: null, authorityCommand: false);
        }, context: nameof(TradeHandler));
    }

    private bool TryRouteToRemoteAuthority(CompleteTrade message, NetPeer peer)
    {
        if (authorityContext?.IsAuthorityKnown != true) return false;
        if (!authorityContext.TryGetCurrentLease(out var lease) || !lease.IsValid || lease.IsPaused)
            return true;
        if (!message.MatchesAuthorityLease(lease)) return true;
        if (authorityContext.TryGetLocalAuthorityLease(out var localLease) && localLease == lease)
            return false;

        // Non-trading warehouse transfers remain owned by the room host's saved session state.
        if (message.IsManagingWarehouse && !message.IsTrading) return false;
        if (!ModInformation.IsServer) return true;

        string originControllerId;
        Player player;
        if (peer != null)
        {
            if (!players.ValidateCurrentPartySender(peer, message.OwnerPartyId) ||
                !players.ValidateHeroSender(peer, message.HeroId) ||
                !players.TryGetPlayer(peer, out player))
                return true;

            originControllerId = player.ControllerId;
        }
        else if (ModInformation.IsListenAuthority && controllerIdProvider != null &&
                 players.TryGetPlayer(controllerIdProvider.ControllerId, out player))
        {
            originControllerId = player.ControllerId;
            if (!PlayerOwnsTrade(player, message)) return true;
        }
        else
        {
            return true;
        }

        // Validate ownership and consume any one-use village transfer token at the room host
        // before forwarding. The authority command sequence fences the remote apply.
        if (peer != null && !PlayerOwnsTrade(player, message)) return true;
        if (!TryBeginCommit((object)peer ?? this, message.RequestId)) return true;

        if (!message.IsTrading && message.ForceTransferId != null &&
            !TryValidateForceTransfer(message, peer))
            return true;

        if (TryRouteToAuthority(message, originControllerId, lease)) return true;

        logger.Warning(
            "Could not route trade commit {RequestId} from controller {ControllerId} to campaign authority {AuthorityControllerId}",
            message.RequestId,
            originControllerId,
            lease.AuthorityControllerId);
        return true;
    }

    internal bool TryRouteToAuthority(CompleteTrade message, string originControllerId, AuthorityLease lease)
    {
        if (!ModInformation.IsServer || authorityContext == null || !lease.IsValid || lease.IsPaused ||
            !message.MatchesAuthorityLease(lease) ||
            string.IsNullOrWhiteSpace(originControllerId) ||
            !authorityContext.TryGetCurrentLease(out var currentLease) || currentLease != lease ||
            authorityContext.TryGetLocalAuthorityLease(out var localLease) && localLease == lease ||
            !players.TryGetPeer(lease.AuthorityControllerId, out var authorityPeer) || authorityPeer == null)
            return false;

        long sequence;
        lock (authorityCommandGate)
        {
            if (authorityCommandLease != lease)
            {
                authorityCommandLease = lease;
                nextAuthorityCommandSequence = 0;
            }

            if (nextAuthorityCommandSequence == long.MaxValue)
                throw new System.InvalidOperationException("Campaign authority trade-command sequence space is exhausted.");

            sequence = ++nextAuthorityCommandSequence;
        }

        network.Send(authorityPeer, new NetworkCampaignAuthorityCompleteTradeCommand(
            lease,
            sequence,
            originControllerId,
            message));
        return true;
    }

    private void Handle_AuthorityCompleteTrade(MessagePayload<NetworkCampaignAuthorityCompleteTradeCommand> payload)
    {
        var command = payload.What;
        if (authorityContext == null ||
            !CampaignAuthorityCommandSenderValidator.IsRoomHost(payload.Who as NetPeer, players, sessionIdentity) ||
            !authorityContext.TryGetLocalAuthorityLease(out var lease) || !command.Matches(lease) ||
            !players.TryGetPlayer(command.OriginControllerId, out var player) ||
            !PlayerOwnsTrade(player, command.Trade) || players.IsReservedController(player.ControllerId) ||
            !TryAdvanceAuthorityCommandSequence(lease, command.Sequence))
            return;

        GameThread.RunSafe(() =>
        {
            // A lease or player registration may change while this command waits for the game thread.
            if (!authorityContext.TryGetLocalAuthorityLease(out var currentLease) || currentLease != lease ||
                !players.TryGetPlayer(command.OriginControllerId, out var currentPlayer) ||
                !PlayerOwnsTrade(currentPlayer, command.Trade) ||
                players.IsReservedController(currentPlayer.ControllerId))
                return;

            ApplyCompleteTrade(command.Trade, peer: null, requiredLease: lease, authorityCommand: true);
        }, context: nameof(TradeHandler));
    }

    internal bool TryAdvanceAuthorityCommandSequence(AuthorityLease lease, long sequence)
    {
        lock (authorityCommandGate)
        {
            if (lastAuthorityCommandLease != lease)
            {
                lastAuthorityCommandLease = lease;
                lastAuthorityCommandSequence = 0;
            }

            if (sequence <= 0 || sequence <= lastAuthorityCommandSequence) return false;
            lastAuthorityCommandSequence = sequence;
            return true;
        }
    }

    internal static bool PlayerOwnsTrade(Player player, CompleteTrade message) =>
        player != null && player.HeroId == message.HeroId && player.MobilePartyId == message.OwnerPartyId;

    private void ApplyCompleteTrade(
        CompleteTrade message,
        NetPeer peer,
        AuthorityLease? requiredLease,
        bool authorityCommand)
    {
        if (authorityContext?.IsAuthorityKnown == true &&
            (!authorityContext.TryGetCurrentLease(out var currentLease) ||
             !message.MatchesAuthorityLease(currentLease)))
            return;

        if (requiredLease.HasValue &&
            (authorityContext == null ||
             !authorityContext.TryGetLocalAuthorityLease(out var localLease) ||
             localLease != requiredLease.Value))
            return;

        if (ModInformation.IsServer && peer != null &&
            (!players.ValidateCurrentPartySender(peer, message.OwnerPartyId) ||
             !players.ValidateHeroSender(peer, message.HeroId))) return;

        ItemRoster fromRoster = null;
        if (!message.IsFromItemRosterNull && !objectManager.TryGetObjectWithLogging<ItemRoster>(message.FromItemRosterId, out fromRoster)) return;

        if (!objectManager.TryGetObjectWithLogging<ItemRoster>(message.ToItemRosterId, out var toRoster)) return;
        if (!objectManager.TryGetObjectWithLogging<Hero>(message.HeroId, out var hero)) return;
        if (!objectManager.TryGetObjectWithLogging<MobileParty>(message.OwnerPartyId, out var ownerParty)) return;
        if (!objectManager.TryGetObjectWithLogging<Hero>(message.InitialHeroId, out var initialHero)) return;
        if (peer != null && !ReferenceEquals(toRoster, ownerParty.ItemRoster)) return;

        SettlementComponent currentSettlementComponent = null;
        if (!message.IsSettlementComponentNull &&
            !objectManager.TryGetObjectWithLogging<SettlementComponent>(message.CurrentSettlementComponentId, out currentSettlementComponent)) return;

        MobileParty currentMobileParty = null;
        if (message.CurrentMobilePartyId != null && !objectManager.TryGetObjectWithLogging<MobileParty>(message.CurrentMobilePartyId, out currentMobileParty)) return;

        if (!TryResolveTradeItems(message.BoughtItems, out var boughtItems) ||
            !TryResolveTradeItems(message.SoldItems, out var soldItems) ||
            !TryResolveCharacterEquipmentsData(message.CharacterIdEquipmentsData, out var characterEquipmentsData))
            return;

        if (ModInformation.IsServer && !message.IsTrading)
        {
            if (message.ForceTransferId != null && !authorityCommand)
            {
                if (!TryValidateForceTransfer(message, peer)) return;
            }
            else if (message.ForceTransferId == null && villageHostileActionInterface.HasPendingForceTransferForParty(message.OwnerPartyId))
            {
                // Attribution missed (see ForceTransferScreenTracker): the cap cannot
                // be enforced on this commit. Logged so live runs prove attribution
                // works; still applied under the trusted-client model.
                logger.Warning(
                    "ForceTransfer loot commit without attribution while a pool is pending (Party={PartyId})",
                    message.OwnerPartyId);
            }
        }

        if (!TryResolveItemRosterData(message.FromItemRosterData, out var fromItemRosterData) ||
            !TryResolveItemRosterData(message.ToItemRosterData, out var toItemRosterData))
            return;
        bool localMarketRoster = SettlementMarketRosterPolicy.IsLocalMarketRoster(fromRoster);
        var totalAmount = message.TotalAmount;
        if (!authorityCommand && !TryBeginCommit(peer, message.RequestId)) return;

        // Undo any purchases that are no longer present in the roster
        // When looting, taken items are treated as bought items. Don't need to manage changed rosters in these cases
        if (fromRoster != null && !localMarketRoster)
        {
            totalAmount = ReconcilePurchases(fromRoster, fromItemRosterData, toItemRosterData, boughtItems, totalAmount);
        }

        // Update rosters with new data
        if (toRoster != null) inventoryLogicInterface.UpdateRosterWithData(toRoster, toItemRosterData);
        if (fromRoster != null && !localMarketRoster)
            inventoryLogicInterface.UpdateRosterWithData(fromRoster, fromItemRosterData);
        else if (message.IsManagingWarehouse)
        {
            // Manage warehouse rosters separately as they involve more complicated logic
            MessageBroker.Instance.Publish(this, new WarehouseRosterManaged(peer, hero, currentSettlementComponent.Settlement, fromItemRosterData));
        }

        // Update hero equipment with new data
        inventoryLogicInterface.UpdateEquipmentWithData(ownerParty, characterEquipmentsData, initialHero);
        network.SendAll(new UpdateEquipmentClients(message.CharacterIdEquipmentsData, message.OwnerPartyId, message.InitialHeroId));

        inventoryLogicInterface.ApplyDoneLogic(
            localMarketRoster ? null : fromRoster,
            toRoster,
            message.IsTrading,
            message.CanGainXpFromDiscarding,
            hero,
            totalAmount,
            message.MerchantGold,
            currentMobileParty,
            currentSettlementComponent,
            boughtItems,
            soldItems
        );
    }

    private bool TryValidateForceTransfer(CompleteTrade message, NetPeer peer)
    {
        if (!TryResolveLeftRemainder(message.FromItemRosterData, out var leftRemainder, out var remainderError))
        {
            logger.Warning(
                "Rejected force supplies transfer with unverifiable left roster (Party={PartyId}, Request={RequestId}, Reason={Reason})",
                message.OwnerPartyId,
                message.ForceTransferId,
                remainderError);
        }
        else if (!villageHostileActionInterface.TryPeekForceTransfer(message.ForceTransferId, message.OwnerPartyId, out var pool))
        {
            logger.Warning(
                "Rejected force supplies transfer with stale or consumed pool (Party={PartyId}, Request={RequestId})",
                message.OwnerPartyId,
                message.ForceTransferId);
        }
        else if (!VillageHostileActionInterface.TryValidateSuppliesTakeAndRemainder(
                     pool.SuppliesItems,
                     message.BoughtItems.Select(item => (item.Element, item.TotalPrice)),
                     message.SoldItems.Select(item => (item.Element, item.TotalPrice)),
                     leftRemainder,
                     out var error))
        {
            // The pool is deliberately left intact: a rejection must not mutate
            // pool state, so a false positive never destroys the reward.
            logger.Warning(
                "Rejected force supplies transfer exceeding the authorized pool (Party={PartyId}, Request={RequestId}, Reason={Reason})",
                message.OwnerPartyId,
                message.ForceTransferId,
                error);
        }
        else if (!villageHostileActionInterface.TryConsumeForceTransfer(message.ForceTransferId, message.OwnerPartyId, out _))
        {
            logger.Warning(
                "Rejected force supplies transfer with stale or consumed pool (Party={PartyId}, Request={RequestId})",
                message.OwnerPartyId,
                message.ForceTransferId);
        }
        else
        {
            logger.Information(
                "ForceTransfer supplies commit accepted (Party={PartyId}, Request={RequestId})",
                message.OwnerPartyId,
                message.ForceTransferId);
            return true;
        }

        if (peer != null)
            network.Send(peer, new SendInformationMessage("The village has nothing left to give."));
        return false;
    }

    internal bool TryResolveLeftRemainder(ItemRosterElement[] leftRosterData, out List<(string itemId, string modifierId, int amount)> remainder, out string error)
    {
        remainder = new List<(string itemId, string modifierId, int amount)>();
        error = null;
        if (leftRosterData == null)
            return true;

        for (var i = 0; i < leftRosterData.Length; i++)
        {
            var element = leftRosterData[i];
            var item = element.EquipmentElement.Item;
            if (item == null && element.Amount == 0)
            {
                // Vanilla leaves empty tombstone slots behind when a kind is fully
                // taken. They carry no outflow, so they are skipped, not rejected.
                // A null item with any other amount is corrupt and rejected below.
                continue;
            }
            if (!objectManager.TryGetId(item, out var itemId))
            {
                // Either a corrupt slot (null item, positive amount) or a real item
                // the server cannot resolve.
                error = $"left remainder element [{i}/{leftRosterData.Length}] without registry id " +
                    $"(Amount={element.Amount}, ItemNull={item == null}, StringId={item?.StringId ?? "<null>"})";
                return false;
            }

            string modifierId = null;
            var modifier = element.EquipmentElement.ItemModifier;
            if (modifier != null &&
                !objectManager.TryGetId(modifier, out modifierId))
            {
                error = $"left remainder element [{i}/{leftRosterData.Length}] modifier without registry id " +
                    $"(Amount={element.Amount}, StringId={modifier?.StringId ?? "<null>"})";
                return false;
            }

            remainder.Add((itemId, modifierId, element.Amount));
        }

        return true;
    }

    internal bool TryResolveLeftRemainder(ItemRosterElementData[] leftRosterData, out List<(string itemId, string modifierId, int amount)> remainder, out string error)
    {
        remainder = new List<(string itemId, string modifierId, int amount)>();
        error = null;
        if (leftRosterData == null) return true;

        for (int i = 0; i < leftRosterData.Length; i++)
        {
            var element = leftRosterData[i];
            string itemId = element.ItemObjectData.ItemObjectId;
            if (string.IsNullOrWhiteSpace(itemId) ||
                !objectManager.TryGetObject<ItemObject>(itemId, out _))
            {
                error = $"left remainder element [{i}/{leftRosterData.Length}] has an unknown item id '{itemId ?? "<null>"}'";
                return false;
            }

            string modifierId = null;
            if (!element.ItemObjectData.ItemModifierNull)
            {
                modifierId = element.ItemObjectData.ItemModifierId;
                if (string.IsNullOrWhiteSpace(modifierId) ||
                    !objectManager.TryGetObject<ItemModifier>(modifierId, out _))
                {
                    error = $"left remainder element [{i}/{leftRosterData.Length}] has an unknown item modifier id '{modifierId ?? "<null>"}'";
                    return false;
                }
            }

            remainder.Add((itemId, modifierId, element.Amount));
        }

        return true;
    }

    private void Handle_UpdateEquipmentClients(MessagePayload<UpdateEquipmentClients> obj)    {
        GameThread.RunSafe(() =>
        {
            using (new AllowedThread())
            {
                if (!objectManager.TryGetObjectWithLogging<MobileParty>(obj.What.MobilePartyId, out var mobileParty)) return;
                if (!objectManager.TryGetObjectWithLogging<Hero>(obj.What.InitialHeroId, out var initialHero)) return;

                if (!TryResolveCharacterEquipmentsData(obj.What.CharacterIdEquipmentsData, out var characterEquipmentsData)) return;
                inventoryLogicInterface.UpdateEquipmentWithData(mobileParty, characterEquipmentsData, initialHero);
            }
        });
    }

    /// <summary>
    /// Removes any purchased quantity the merchant can no longer supply from the party roster data and
    /// refunds the prorated cost.
    /// </summary>
    internal static int ReconcilePurchases(
        ItemRoster merchantRoster,
        ItemRosterElement[] merchantRosterData,
        ItemRosterElement[] partyRosterData,
        List<(ItemRosterElement, int)> boughtItems,
        int totalAmount)
    {
        foreach (var boughtItem in boughtItems)
        {
            int amount = boughtItem.Item1.Amount;
            int elementIndex = merchantRoster.FindIndexOfElement(boughtItem.Item1.EquipmentElement);
            int available = elementIndex >= 0 ? merchantRoster.GetElementNumber(elementIndex) : 0;
            int difference = available - amount;

            if (difference < 0)
            {
                int fromRosterDataIndex = merchantRosterData.FindIndex(rosterElement => rosterElement.EquipmentElement.Equals(boughtItem.Item1));
                if (fromRosterDataIndex >= 0) merchantRosterData[fromRosterDataIndex].Amount -= difference;
                else merchantRosterData.AddItem(new ItemRosterElement(boughtItem.Item1.EquipmentElement, -difference));

                int toRosterDataIndex = partyRosterData.FindIndex(rosterElement => rosterElement.EquipmentElement.Equals(boughtItem.Item1));
                if (toRosterDataIndex >= 0) partyRosterData[toRosterDataIndex].Amount += difference;
                else partyRosterData.AddItem(new ItemRosterElement(boughtItem.Item1.EquipmentElement, difference));

                totalAmount -= amount > 0 ? (boughtItem.Item2 * -difference) / amount : boughtItem.Item2;
            }
        }

        return totalAmount;
    }

    private bool TryResolveTradeItemIds(
        IEnumerable<(ItemRosterElement, int)> items,
        out TradeItemData[] resolvedItems)
    {
        var result = new List<TradeItemData>();
        foreach (var (item, totalPrice) in items ?? Enumerable.Empty<(ItemRosterElement, int)>())
        {
            if (!TryResolveItemRosterId(item, out var resolvedItem))
            {
                resolvedItems = Array.Empty<TradeItemData>();
                return false;
            }

            result.Add(new TradeItemData(resolvedItem, totalPrice));
        }

        resolvedItems = result.ToArray();
        return true;
    }

    internal bool TryResolveLeftLootIds(ItemRoster items, out TradeItemData[] resolvedItems)
    {
        var result = new List<TradeItemData>();
        for (int i = 0; i < items.Count; i++)
        {
            if (!TryResolveItemRosterId(items[i], out var resolvedItem))
            {
                resolvedItems = Array.Empty<TradeItemData>();
                return false;
            }

            result.Add(new TradeItemData(resolvedItem, items[i].Amount));
        }

        resolvedItems = result.ToArray();
        return true;
    }

    private bool TryResolveTradeItems(
        IEnumerable<TradeItemData> items,
        out List<(ItemRosterElement, int)> resolvedItems)
    {
        var result = new List<(ItemRosterElement, int)>();
        foreach (var tradeItem in items ?? Enumerable.Empty<TradeItemData>())
        {
            if (!TryResolveItemRosterElement(tradeItem.Element, out var item))
            {
                resolvedItems = new List<(ItemRosterElement, int)>();
                return false;
            }

            result.Add((item, tradeItem.TotalPrice));
        }

        resolvedItems = result;
        return true;
    }

    private bool TryResolveItemRosterData(ItemRoster roster, out ItemRosterElementData[] data)
    {
        if (roster == null)
        {
            data = null;
            return true;
        }

        var result = new List<ItemRosterElementData>();
        foreach (var element in roster._data)
        {
            if (element.EquipmentElement.Item == null && element.Amount == 0) continue;
            if (element.EquipmentElement.Item == null || !TryResolveItemRosterId(element, out var resolved))
            {
                logger.Error("Could not serialize an occupied trade roster slot for network commit");
                data = Array.Empty<ItemRosterElementData>();
                return false;
            }

            result.Add(resolved);
        }

        data = result.ToArray();
        return true;
    }

    private bool TryResolveItemRosterData(ItemRosterElementData[] data, out ItemRosterElement[] elements)
    {
        if (data == null)
        {
            elements = null;
            return true;
        }

        var result = new ItemRosterElement[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            if (!TryResolveItemRosterElement(data[i], out result[i]))
            {
                elements = Array.Empty<ItemRosterElement>();
                return false;
            }
        }

        elements = result;
        return true;
    }

    private bool TryResolveItemRosterElement(ItemRosterElementData data, out ItemRosterElement result)
    {
        result = default;

        var itemObjectData = data.ItemObjectData;

        if (!objectManager.TryGetObject<ItemObject>(itemObjectData.ItemObjectId, out var itemObject))
        {
            logger.Error("Failed to get {type} with id: {id}", typeof(ItemObject), itemObjectData.ItemObjectId);
            return false;
        }

        ItemModifier itemModifier = null;
        if (!itemObjectData.ItemModifierNull && !objectManager.TryGetObject(itemObjectData.ItemModifierId, out itemModifier))
        {
            logger.Error("Failed to get {type} with id: {id}", typeof(ItemModifier), itemObjectData.ItemModifierId);
            return false;
        }

        using (new AllowedThread())
        {
            result = new ItemRosterElement(itemObject, data.Amount, itemModifier);
        }

        return true;
    }

    private bool TryResolveItemRosterId(ItemRosterElement itemRosterElement, out ItemRosterElementData result)
    {
        result = default;

        if (!objectManager.TryGetId(itemRosterElement.EquipmentElement.Item, out var itemObjectId))
        {
            logger.Error("Failed to get id for {type}", nameof(itemRosterElement.EquipmentElement.Item));
            return false;
        }

        string itemModifierId = null;
        if (itemRosterElement.EquipmentElement.ItemModifier is not null && !objectManager.TryGetId(itemRosterElement.EquipmentElement.ItemModifier, out itemModifierId))
        {
            logger.Error("Failed to get id for {type}", nameof(itemRosterElement.EquipmentElement.ItemModifier));
            return false;
        }

        var itemModifierNull = itemRosterElement.EquipmentElement.ItemModifier is null;

        result = new ItemRosterElementData(
            new ItemObjectData(itemObjectId, itemModifierId, itemModifierNull),
            itemRosterElement.Amount
        );

        return true;
    }

    private bool TryResolveCharacterIdEquipmentsData(
        MobileParty party,
        CharacterObject initialCharacter,
        out Dictionary<string, EquipmentData[]> characterIdEquipmentsData)
    {
        characterIdEquipmentsData = new Dictionary<string, EquipmentData[]>();
        bool initialCharacterInParty = false;

        for (int i = 0; i < party.MemberRoster.Count; i++)
        {
            var character = party.MemberRoster.GetElementCopyAtIndex(i).Character;

            if (!TryAddHeroEquipmentData(characterIdEquipmentsData, character)) return false;

            if (character == initialCharacter)
            {
                initialCharacterInParty = true;
            }
        }

        if (!initialCharacterInParty)
        {
            if (!TryAddHeroEquipmentData(characterIdEquipmentsData, initialCharacter)) return false;
        }

        return true;
    }

    private bool TryAddHeroEquipmentData(Dictionary<string, EquipmentData[]> characterIdEquipmentsData, CharacterObject character)
    {
        if (!character.IsHero) return true;

        if (!objectManager.TryGetIdWithLogging(character.HeroObject, out var heroId)) return false;

        if (!TryResolveEquipmentData(character.FirstBattleEquipment, out var battle) ||
            !TryResolveEquipmentData(character.FirstCivilianEquipment, out var civilian) ||
            !TryResolveEquipmentData(character.FirstStealthEquipment, out var stealth))
            return false;

        characterIdEquipmentsData[heroId] = new[] { battle, civilian, stealth };
        return true;
    }

    private bool TryResolveEquipmentData(Equipment equipment, out EquipmentData data)
    {
        var slots = new EquipmentSlotData[Equipment.EquipmentSlotLength];
        for (int i = 0; i < slots.Length; i++)
        {
            var element = equipment._itemSlots[i];
            if (element.Item == null) continue;

            if (!objectManager.TryGetId(element.Item, out var itemId))
            {
                logger.Error("Could not serialize equipment item {ItemId} for campaign trade", element.Item.StringId);
                data = default;
                return false;
            }

            string modifierId = null;
            if (element.ItemModifier != null && !objectManager.TryGetId(element.ItemModifier, out modifierId))
            {
                logger.Error("Could not serialize equipment modifier {ModifierId} for campaign trade", element.ItemModifier.StringId);
                data = default;
                return false;
            }

            slots[i] = new EquipmentSlotData(new ItemObjectData(
                itemId,
                modifierId,
                itemModifierNull: element.ItemModifier == null));
        }

        data = new EquipmentData(equipment._equipmentType, slots);
        return true;
    }

    private bool TryResolveCharacterEquipmentsData(
        Dictionary<string, EquipmentData[]> characterIdEquipmentsData,
        out Dictionary<CharacterObject, Equipment[]> characterEquipmentsData)
    {
        characterEquipmentsData = new();
        foreach (KeyValuePair<string, EquipmentData[]> characterIdEquipment in characterIdEquipmentsData ?? new Dictionary<string, EquipmentData[]>())
        {
            if (characterIdEquipment.Value == null || characterIdEquipment.Value.Length != 3 ||
                !objectManager.TryGetObjectWithLogging<Hero>(characterIdEquipment.Key, out var hero)) return false;

            var character = hero.CharacterObject;
            characterEquipmentsData[character] = new Equipment[3];
            for (int i = 0; i < 3; i++)
            {
                if (!TryResolveEquipmentData(characterIdEquipment.Value[i], out characterEquipmentsData[character][i])) return false;
            }
        }

        return true;
    }
    
    private bool TryResolveEquipmentData(EquipmentData equipmentData, out Equipment equipment)
    {
        equipment = null;
        if (equipmentData.ItemSlots == null || equipmentData.ItemSlots.Length != Equipment.EquipmentSlotLength) return false;

        equipment = new Equipment(equipmentData.EquipmentType);
        for (int i = 0; i < equipmentData.ItemSlots.Length; i++)
        {
            var slot = equipmentData.ItemSlots[i];
            if (!slot.HasItem)
            {
                equipment._itemSlots[i] = default;
                continue;
            }

            if (!objectManager.TryGetObject<ItemObject>(slot.ItemObjectData.ItemObjectId, out var itemObject)) return false;

            ItemModifier itemModifier = null;
            if (!slot.ItemObjectData.ItemModifierNull &&
                !objectManager.TryGetObject(slot.ItemObjectData.ItemModifierId, out itemModifier)) return false;

            equipment._itemSlots[i] = new EquipmentElement(itemObject, itemModifier);
        }

        return true;
    }
}
