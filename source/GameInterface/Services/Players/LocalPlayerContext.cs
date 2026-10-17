using Common;
using GameInterface.Services.Entity;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players.Data;
using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.Players;

public interface ILocalPlayerContext
{
    Player RegisterNativePlayer();
}

/// <summary>Registers the existing native player graph; never imports or creates a second actor.</summary>
public sealed class LocalPlayerContext : ILocalPlayerContext
{
    private readonly IPlayerManager players;
    private readonly IObjectManager objects;
    private readonly IControllerIdProvider controller;

    public LocalPlayerContext(IPlayerManager players, IObjectManager objects, IControllerIdProvider controller)
    {
        this.players = players;
        this.objects = objects;
        this.controller = controller;
    }

    public Player RegisterNativePlayer()
    {
        if (!ModInformation.IsListenAuthority)
            throw new InvalidOperationException("Native player registration requires ListenAuthority");
        if (string.IsNullOrWhiteSpace(controller.ControllerId) || controller.ControllerId == "Server")
            throw new InvalidOperationException("ListenAuthority requires a real local controller identity");

        var hero = Hero.MainHero ?? throw new InvalidOperationException("Native main hero is not ready");
        var party = MobileParty.MainParty ?? throw new InvalidOperationException("Native main party is not ready");
        if (!objects.TryGetId(hero, out string heroId) ||
            !objects.TryGetId(party, out string partyId) ||
            !objects.TryGetId(hero.Clan, out string clanId) ||
            !objects.TryGetId(hero.CharacterObject, out string characterId))
            throw new InvalidOperationException("Native player graph must be registered before binding its controller");

        if (players.TryGetPlayer(controller.ControllerId, out var existing))
        {
            if (existing.HeroId != heroId || existing.MobilePartyId != partyId || existing.ClanId != clanId)
                throw new InvalidOperationException("Saved local player does not match the native campaign actor");
            return existing;
        }

        var player = new Player(controller.ControllerId, heroId, partyId, clanId, characterId);
        if (!players.AddPlayer(player))
            throw new InvalidOperationException("Could not register the native local player");
        return player;
    }
}
