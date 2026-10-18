using Common;
using Common.Logging;
using Common.Messaging;
using GameInterface.CoopSessionData;
using GameInterface.CoopSessionData.Save.Data;
using GameInterface.Services.Inventory;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.CampaignAuthority;

public interface ICampaignSessionCheckpointService
{
    bool TryCapture(out CampaignSessionCheckpoint checkpoint);
    bool TryApply(CampaignSessionCheckpoint checkpoint);
}

internal sealed class CampaignSessionCheckpointService : ICampaignSessionCheckpointService
{
    private static readonly ILogger Logger = LogManager.GetLogger<CampaignSessionCheckpointService>();
    // Match JsonFileIO's co-op save shape, including Player's public identity fields. JSON preserves
    // empty nested dictionaries needed by the session interfaces after promotion.
    private static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true };
    private readonly IObjectManager objects;
    private readonly IPlayerManager players;
    private readonly ICoopSessionProvider provider;
    private readonly ISessionIdentity identity;
    private readonly IMessageBroker broker;

    public CampaignSessionCheckpointService(IObjectManager objects, IPlayerManager players,
        ICoopSessionProvider provider, ISessionIdentity identity, IMessageBroker broker)
    {
        this.objects = objects;
        this.players = players;
        this.provider = provider;
        this.identity = identity;
        this.broker = broker;
    }

    public bool TryCapture(out CampaignSessionCheckpoint checkpoint) =>
        TryCapture(Campaign.Current?.UniqueGameId, out checkpoint);
    public bool TryApply(CampaignSessionCheckpoint checkpoint) =>
        TryApply(checkpoint, Campaign.Current?.UniqueGameId);

    internal bool TryCapture(string campaignId, out CampaignSessionCheckpoint checkpoint)
    {
        checkpoint = null;
        if (string.IsNullOrWhiteSpace(campaignId) || !identity.TryGetConfirmedLease(out var lease)) return false;
        var currentPlayers = players.Players?.ToArray();
        if (!ValidatePlayers(currentPlayers, out _)) return false;
        try
        {
            var detachedPlayers = currentPlayers.Select(player => new Player(player.ControllerId, player.HeroId,
                player.MobilePartyId, player.ClanId, player.CharacterObjectId, player.OriginalClanId)
            { CrimeRatings = new Dictionary<string, float>(player.CrimeRatings) }).ToArray();
            var snapshot = BuildSession(provider.CoopSession, detachedPlayers, lease,
                identity.RoomHostControllerId, CoopSession.Empty.InventoryPlayerData);
            checkpoint = new CampaignSessionCheckpoint(campaignId, JsonSerializer.Serialize(snapshot, JsonOptions));
            return true;
        }
        catch (Exception exception)
        {
            Logger.Warning(exception, "Could not capture co-op session for authority handoff");
            return false;
        }
    }

    internal bool TryApply(CampaignSessionCheckpoint checkpoint, string campaignId)
    {
        if (checkpoint == null || string.IsNullOrWhiteSpace(campaignId) ||
            !string.Equals(checkpoint.CampaignId, campaignId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(checkpoint.SessionJson) || !identity.TryGetConfirmedLease(out var lease)) return false;
        CoopSession snapshot;
        try { snapshot = JsonSerializer.Deserialize<CoopSession>(checkpoint.SessionJson, JsonOptions); }
        catch (Exception exception)
        {
            Logger.Warning(exception, "Rejected malformed co-op session checkpoint");
            return false;
        }
        if (!HasRequiredSharedData(snapshot) || snapshot.RoomId != lease.RoomId || snapshot.AuthorityId != lease.AuthorityId ||
            snapshot.AuthorityControllerId != lease.AuthorityControllerId || snapshot.AuthorityEpoch != lease.Epoch ||
            snapshot.AuthorityPaused != lease.IsPaused || snapshot.RoomHostControllerId != identity.RoomHostControllerId ||
            !ValidatePlayers(snapshot.Players, out var registered)) return false;

        var localInventory = provider.CoopSession?.InventoryPlayerData ?? CoopSession.Empty.InventoryPlayerData;
        var merged = BuildSession(snapshot, registered.Values.ToArray(), lease, identity.RoomHostControllerId, localInventory);
        return identity.TryExecuteWithLease(lease, () =>
        {
            foreach (var player in snapshot.Players)
                registered[player.ControllerId].CrimeRatings = new Dictionary<string, float>(player.CrimeRatings);
            provider.CoopSession = merged;
            CoopSessionUpdateHandler.InitializeSessionReaders(broker, this, merged);
        });
    }

    private bool ValidatePlayers(Player[] snapshot, out Dictionary<string, Player> registered)
    {
        registered = new Dictionary<string, Player>(StringComparer.Ordinal);
        var local = players.Players;
        if (snapshot == null || local == null || snapshot.Length != local.Count) return false;
        foreach (var player in local)
        {
            if (player == null || string.IsNullOrWhiteSpace(player.ControllerId) || registered.ContainsKey(player.ControllerId)) return false;
            registered.Add(player.ControllerId, player);
        }
        var controllers = new HashSet<string>(StringComparer.Ordinal);
        var heroes = new HashSet<string>(StringComparer.Ordinal);
        var parties = new HashSet<string>(StringComparer.Ordinal);
        foreach (var player in snapshot)
        {
            if (player == null || !controllers.Add(player.ControllerId ?? "") ||
                !registered.TryGetValue(player.ControllerId ?? "", out var existing) ||
                !SameIdentity(existing, player) || !heroes.Add(player.HeroId ?? "") || !parties.Add(player.MobilePartyId ?? "") ||
                !objects.TryGetObject<Hero>(player.HeroId, out var hero) ||
                !objects.TryGetObject<MobileParty>(player.MobilePartyId, out _) ||
                !objects.TryGetObject<CharacterObject>(player.CharacterObjectId, out var character) ||
                !ReferenceEquals(hero.CharacterObject, character) ||
                (!string.IsNullOrEmpty(player.ClanId) && !objects.TryGetObject<Clan>(player.ClanId, out _)) ||
                (!string.IsNullOrEmpty(player.OriginalClanId) && !objects.TryGetObject<Clan>(player.OriginalClanId, out _))) return false;
        }
        return true;
    }

    private static bool SameIdentity(Player left, Player right) =>
        left.HeroId == right.HeroId && left.MobilePartyId == right.MobilePartyId && left.ClanId == right.ClanId &&
        left.CharacterObjectId == right.CharacterObjectId &&
        (left.OriginalClanId ?? left.ClanId) == (right.OriginalClanId ?? right.ClanId);

    private static bool HasRequiredSharedData(CoopSession session) =>
        session != null && session.CraftingPlayerData != null && session.WorkshopPlayerData != null &&
        session.CaravansPlayerData != null && session.AlleyPlayerData != null && session.InteractionsPlayerData != null &&
        session.TradePlayerData != null && session.HeroMeetingData != null && session.AgingPlayerData != null;

    private static CoopSession BuildSession(ICoopSession source, Player[] currentPlayers, AuthorityLease lease,
        string roomHost, InventoryPlayerData inventory)
    {
        source ??= CoopSession.Empty;
        var empty = CoopSession.Empty;
        return new CoopSession(source.UniqueGameId, currentPlayers,
            source.CraftingPlayerData ?? empty.CraftingPlayerData, source.WorkshopPlayerData ?? empty.WorkshopPlayerData,
            source.CaravansPlayerData ?? empty.CaravansPlayerData, source.AlleyPlayerData ?? empty.AlleyPlayerData,
            source.InteractionsPlayerData ?? empty.InteractionsPlayerData, source.TradePlayerData ?? empty.TradePlayerData,
            inventory, source.HeroMeetingData ?? empty.HeroMeetingData, source.AgingPlayerData ?? empty.AgingPlayerData,
            source.ClanFinance ?? empty.ClanFinance, lease.RoomId, lease.AuthorityId, lease.AuthorityControllerId,
            lease.Epoch, roomHost, lease.IsPaused);
    }
}
