using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Coop.Core.Server.Services.Save.Messages;
using Coop.Core.Common;
using Coop.Core.Common.Services.Connection.Messages;
using Coop.Core.Server.Services.Shutdown;
using GameInterface.CoopSessionData;
using GameInterface.CoopSessionData.Save.Data;
using GameInterface.Registry.Messages;
using GameInterface.Services.Entity;
using GameInterface.Services.Heroes.Messages;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using GameInterface.Services.Save.Messages;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace Coop.Core.Server.Services.Save.Handlers;

/// <summary>
/// Handles Coop specific saving and loading
/// </summary>
internal class SaveGameHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<SaveGameHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly ICoopSaveManager saveManager;
    private readonly ICoopSessionProvider coopSessionProvider;
    private readonly IPlayerManager playerRegistry;
    private readonly IPlayerPartyRestorer playerPartyRestorer;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;
    private readonly ISessionIdentity sessionIdentity;
    private readonly IControllerIdProvider localController;
    private readonly IServerAdmissionGate admissionGate;
    private readonly ICoopFinalizer finalizer;
    private readonly HashSet<object> activeSaveSources = new HashSet<object>();

    public SaveGameHandler(
        IMessageBroker messageBroker,
        ICoopSaveManager saveManager,
        ICoopSessionProvider coopSessionProvider,
        IPlayerManager playerRegistry,
        IPlayerPartyRestorer playerPartyRestorer,
        INetwork network,
        IObjectManager objectManager,
        ISessionIdentity sessionIdentity = null,
        IControllerIdProvider localController = null,
        IServerAdmissionGate admissionGate = null,
        ICoopFinalizer finalizer = null)
    {
        this.messageBroker = messageBroker;
        this.saveManager = saveManager;
        this.coopSessionProvider = coopSessionProvider;
        this.playerRegistry = playerRegistry;
        this.playerPartyRestorer = playerPartyRestorer;
        this.network = network;
        this.objectManager = objectManager;
        this.sessionIdentity = sessionIdentity;
        this.localController = localController;
        this.admissionGate = admissionGate;
        this.finalizer = finalizer;

        messageBroker.Subscribe<GameSaved>(Handle_GameSaved);
        messageBroker.Subscribe<GameLoaded>(Handle_GameLoaded);
        messageBroker.Subscribe<GameSaveStateChanged>(Handle_GameSaveStateChanged);

        messageBroker.Subscribe<AllGameObjectsRegistered>(Handle_AllGameObjectsRegistered);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<GameSaved>(Handle_GameSaved);
        messageBroker.Unsubscribe<GameLoaded>(Handle_GameLoaded);
        messageBroker.Unsubscribe<GameSaveStateChanged>(Handle_GameSaveStateChanged);

        messageBroker.Unsubscribe<AllGameObjectsRegistered>(Handle_AllGameObjectsRegistered);
    }

    private void Handle_GameSaveStateChanged(MessagePayload<GameSaveStateChanged> payload)
    {
        lock (activeSaveSources)
        {
            if (payload.What.IsSaving)
            {
                if (!activeSaveSources.Add(payload.Who) || activeSaveSources.Count != 1)
                    return;
            }
            else if (!activeSaveSources.Remove(payload.Who) || activeSaveSources.Count != 0)
            {
                return;
            }

            network.SendAll(new NetworkGameSaveStateChanged(payload.What.IsSaving));
        }
    }

    private void Handle_GameSaved(MessagePayload<GameSaved> obj)
    {
        var saveName = obj.What.SaveName;
        try
        {
            WriteCoopSession(saveName);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to write the co-op session for save {SaveName}", saveName);
            messageBroker.Publish(this, new CoopSessionWritten(saveName, false));
            return;
        }

        messageBroker.Publish(this, new CoopSessionWritten(saveName, true));
    }

    private void WriteCoopSession(string saveName)
    {
        var current = coopSessionProvider.CoopSession;
        var empty = CoopSession.Empty;
        var lease = sessionIdentity?.CurrentLease;

        CoopSession session = new CoopSession(
            saveName,
            playerRegistry.Players.ToArray(),
            current?.CraftingPlayerData ?? empty.CraftingPlayerData,
            current?.WorkshopPlayerData ?? empty.WorkshopPlayerData,
            current?.CaravansPlayerData ?? empty.CaravansPlayerData,
            current?.AlleyPlayerData ?? empty.AlleyPlayerData,
            current?.InteractionsPlayerData ?? empty.InteractionsPlayerData,
            current?.TradePlayerData ?? empty.TradePlayerData,
            current?.InventoryPlayerData ?? empty.InventoryPlayerData,
            current?.HeroMeetingData ?? empty.HeroMeetingData,
            current?.AgingPlayerData ?? empty.AgingPlayerData,
            current?.ClanFinance ?? empty.ClanFinance,
            lease?.RoomId ?? current?.RoomId,
            lease?.AuthorityId ?? current?.AuthorityId,
            lease?.AuthorityControllerId ?? current?.AuthorityControllerId,
            lease?.Epoch ?? current?.AuthorityEpoch ?? 0,
            string.IsNullOrWhiteSpace(sessionIdentity?.RoomHostControllerId)
                ? current?.RoomHostControllerId
                : sessionIdentity.RoomHostControllerId,
            lease?.IsPaused ?? current?.AuthorityPaused ?? false,
            current?.BattlePreparations ?? empty.BattlePreparations);

        coopSessionProvider.CoopSession = session;

        saveManager.SaveCoopSession(saveName, session);
    }

    private ICoopSession savedSession;
    private void Handle_GameLoaded(MessagePayload<GameLoaded> obj)
    {
        var loaded = saveManager.LoadCoopSession(obj.What.SaveName);
        var empty = CoopSession.Empty;

        savedSession = new CoopSession(
            loaded?.UniqueGameId ?? obj.What.SaveName,
            loaded?.Players ?? empty.Players,
            loaded?.CraftingPlayerData ?? empty.CraftingPlayerData,
            loaded?.WorkshopPlayerData ?? empty.WorkshopPlayerData,
            loaded?.CaravansPlayerData ?? empty.CaravansPlayerData,
            loaded?.AlleyPlayerData ?? empty.AlleyPlayerData,
            loaded?.InteractionsPlayerData ?? empty.InteractionsPlayerData,
            loaded?.TradePlayerData ?? empty.TradePlayerData,
            loaded?.InventoryPlayerData ?? empty.InventoryPlayerData,
            loaded?.HeroMeetingData ?? empty.HeroMeetingData,
            loaded?.AgingPlayerData ?? empty.AgingPlayerData,
            loaded?.ClanFinance ?? empty.ClanFinance,
            loaded?.RoomId,
            loaded?.AuthorityId,
            loaded?.AuthorityControllerId,
            loaded?.AuthorityEpoch ?? 0,
            loaded?.RoomHostControllerId,
            loaded?.AuthorityPaused ?? false,
            loaded?.BattlePreparations ?? empty.BattlePreparations);

        var savedLease = new AuthorityLease(
            savedSession.RoomId,
            savedSession.AuthorityId,
            savedSession.AuthorityControllerId,
            savedSession.AuthorityEpoch,
            savedSession.AuthorityPaused);
        if (!savedSession.BattlePreparations.CanTransferHistory(out var battleReason))
        {
            RejectColdBoot(battleReason);
            return;
        }
        if (ModInformation.IsListenAuthority && sessionIdentity != null && !sessionIdentity.IsConfirmed &&
            HasSavedAuthorityMetadata(savedSession))
        {
            if (!TryCreateColdBootLease(savedLease, out var bootLease, out var reason) ||
                !sessionIdentity.TryRestore(bootLease, savedSession.RoomHostControllerId))
            {
                RejectColdBoot(reason ?? "The new campaign boot lease could not be installed.");
                return;
            }
            savedSession = CopyWithLease(savedSession, bootLease, sessionIdentity.RoomHostControllerId);
            coopSessionProvider.CoopSession = savedSession;
            Logger.Information("Restored local host campaign under fresh boot room {RoomId}, authority {AuthorityId}, epoch {Epoch}; saved room was {SavedRoomId}",
                bootLease.RoomId, bootLease.AuthorityId, bootLease.Epoch, savedLease.RoomId);
            return;
        }
        bool restoredLease = sessionIdentity != null && savedSession.AuthorityEpoch > 0 &&
            sessionIdentity.TryRestore(savedLease, savedSession.RoomHostControllerId);
        if (sessionIdentity != null && savedSession.AuthorityEpoch > 0 && !restoredLease)
        {
            Logger.Warning("Ignored saved campaign authority lease for room {RoomId} at epoch {Epoch}; " +
                "current lease is room {CurrentRoomId} at epoch {CurrentEpoch}",
                savedSession.RoomId, savedSession.AuthorityEpoch, sessionIdentity.RoomId, sessionIdentity.Epoch);
        }
        else if (sessionIdentity != null && savedSession.AuthorityEpoch <= 0 &&
            !sessionIdentity.TrySetRoomHostControllerId(savedSession.RoomHostControllerId))
        {
            Logger.Warning("Ignored saved room host controller {SavedControllerId}; " +
                "current room host controller is {CurrentControllerId}",
                savedSession.RoomHostControllerId, sessionIdentity.RoomHostControllerId);
        }

        if (ModInformation.IsListenAuthority && sessionIdentity?.IsConfirmed == true)
            savedSession = CopyWithLease(savedSession, sessionIdentity.CurrentLease, sessionIdentity.RoomHostControllerId);
        coopSessionProvider.CoopSession = savedSession;
    }

    private static bool HasSavedAuthorityMetadata(ICoopSession session) =>
        session.AuthorityEpoch != 0 || session.AuthorityPaused || !string.IsNullOrEmpty(session.RoomId) ||
        !string.IsNullOrEmpty(session.AuthorityId) || !string.IsNullOrEmpty(session.AuthorityControllerId);

    private bool TryCreateColdBootLease(AuthorityLease savedLease, out AuthorityLease bootLease, out string reason)
    {
        bootLease = default;
        reason = "Cold hosting requires a complete save owned by this local room host, with an active local campaign authority.";
        string local = localController?.ControllerId;
        if (!savedLease.IsValid || savedLease.IsPaused || string.IsNullOrWhiteSpace(local) || local == "Server" ||
            !string.Equals(local, savedSession.RoomHostControllerId, StringComparison.Ordinal) ||
            !string.Equals(local, savedLease.AuthorityControllerId, StringComparison.Ordinal)) return false;
        var registrations = savedSession.Players;
        if (registrations == null || registrations.Length == 0 || registrations.Any(player => player == null ||
            string.IsNullOrWhiteSpace(player.ControllerId) || string.IsNullOrWhiteSpace(player.HeroId) ||
            string.IsNullOrWhiteSpace(player.MobilePartyId) || string.IsNullOrWhiteSpace(player.ClanId) ||
            string.IsNullOrWhiteSpace(player.CharacterObjectId)) ||
            registrations.Select(player => player.ControllerId).Distinct(StringComparer.Ordinal).Count() != registrations.Length ||
            registrations.Select(player => player.HeroId).Distinct(StringComparer.Ordinal).Count() != registrations.Length ||
            registrations.Select(player => player.MobilePartyId).Distinct(StringComparer.Ordinal).Count() != registrations.Length ||
            registrations.Count(player => player.ControllerId == local) != 1) return false;
        if (savedLease.Epoch == long.MaxValue)
        {
            reason = "Cold campaign authority epoch is exhausted; hosting was stopped rather than reusing the saved lease.";
            return false;
        }
        bootLease = new AuthorityLease(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), local,
            checked(savedLease.Epoch + 1));
        reason = null;
        return true;
    }

    private void RejectColdBoot(string reason)
    {
        admissionGate?.Close();
        Logger.Error("Cannot restore ListenAuthority save: {Reason}", reason);
        Dispose();
        if (finalizer != null) finalizer.Finalize(reason);
        else messageBroker.Publish(this, new EndCoopMode());
    }

    private static ICoopSession CopyWithLease(ICoopSession session, AuthorityLease lease, string roomHostController) => new CoopSession(
        session.UniqueGameId, session.Players, session.CraftingPlayerData, session.WorkshopPlayerData,
        session.CaravansPlayerData, session.AlleyPlayerData, session.InteractionsPlayerData, session.TradePlayerData,
        session.InventoryPlayerData, session.HeroMeetingData, session.AgingPlayerData, session.ClanFinance,
        lease.RoomId, lease.AuthorityId, lease.AuthorityControllerId, lease.Epoch, roomHostController, lease.IsPaused, session.BattlePreparations);

    private void Handle_AllGameObjectsRegistered(MessagePayload<AllGameObjectsRegistered> obj)
    {
        // savedSession is only set by GameLoaded; hosting a NEW campaign never loads a save, so
        // there is no previous session (and no players) to restore.
        if (savedSession?.Players == null) return;

        foreach (var player in SelectOneRegistrationPerController(savedSession.Players))
        {
            if (!playerPartyRestorer.TryRestore(player, out var registration))
            {
                Logger.Error(
                    "Skipped saved registration for controller {ControllerId} (hero {HeroId}): " +
                    "the player graph could not be restored",
                    player.ControllerId,
                    player.HeroId);
                continue;
            }

            if (!playerRegistry.AddPlayer(registration))
                Logger.Warning(
                    "Skipped saved registration for controller {ControllerId} (hero {HeroId}): " +
                    "that controller is already registered",
                    registration?.ControllerId, registration?.HeroId);
        }

        messageBroker.Publish(this, new SavedPlayerRegistrationsRestored());
    }

    /// <summary>
    /// Picks the single registration to restore per controller. A save written before controller
    /// ids were unique can carry more than one, in either order, so keep the one whose hero and
    /// party still exist rather than the one that happens to come first: the campaign decides
    /// which registration is real. Keeping the first can retain an incomplete player graph, which
    /// leaves the live party unregistered and sends its owner off to build another character.
    /// </summary>
    private IEnumerable<Player> SelectOneRegistrationPerController(IEnumerable<Player> players)
    {
        // AllGameObjectsRegistered means the campaign's objects are resolvable, so these lookups
        // are authoritative about which registration still has a complete player graph.
        foreach (var group in players.Where(player => player != null).GroupBy(player => player.ControllerId))
        {
            var registrations = group.ToArray();
            if (registrations.Length == 1)
            {
                yield return registrations[0];
                continue;
            }

            var live = registrations.FirstOrDefault(PlayerGraphExists) ??
                registrations.FirstOrDefault(PlayerHeroExists) ??
                registrations[0];

            Logger.Warning(
                "Save carries {Count} registrations for controller {ControllerId} (heroes {HeroIds}); " +
                "restoring {KeptHeroId} and dropping the rest",
                registrations.Length,
                group.Key,
                string.Join(", ", registrations.Select(registration => registration.HeroId)),
                live.HeroId);

            yield return live;
        }
    }

    private bool PlayerGraphExists(Player player) =>
        !string.IsNullOrEmpty(player.HeroId) &&
        !string.IsNullOrEmpty(player.MobilePartyId) &&
        objectManager.TryGetObject<Hero>(player.HeroId, out _) &&
        objectManager.TryGetObject<MobileParty>(player.MobilePartyId, out _);

    private bool PlayerHeroExists(Player player) =>
        !string.IsNullOrEmpty(player.HeroId) &&
        objectManager.TryGetObject<Hero>(player.HeroId, out _);
}
