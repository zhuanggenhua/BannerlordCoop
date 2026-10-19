#if DEBUG
using Common.Commands;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using Newtonsoft.Json;
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;

namespace GameInterface.Services.CampaignAuthority;

public sealed class CampaignCycleDebugCommands : ICoopCommand
{
    public string Prefix => "coop.debug.authority";
    public string Name => "cycles";
    public string Description => "Reads native completed hourly/daily dispatcher counts; does not invoke events or change scheduler callbacks.";
    public CoopCommandSide Side => CoopCommandSide.Both;
    public IExpectedArgs[] ExpectedArgs { get; } = Array.Empty<IExpectedArgs>();
    public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
    {
        if (Campaign.Current == null) return new(false, "A campaign is required.", "campaign_missing");
        var collector = CampaignCycleDispatchPatches.For(Campaign.Current);
        var output = JsonConvert.SerializeObject(new
        {
            instance = collector.InstanceId, campaign = Campaign.Current.UniqueGameId,
            processId = System.Diagnostics.Process.GetCurrentProcess().Id,
            overflowed = collector.Overflowed, rows = collector.Snapshot(),
            coverage = "Completed native dispatcher calls only; no listener subscriptions, artificial ticks, or world mutations.",
        });
        return collector.Overflowed ? new(false, output, "cycle_evidence_overflow") : new(true, output);
    }
}

public sealed class CampaignSemanticStateCommand : ICoopCommand
{
    private readonly IObjectManager objects;
    private readonly IPlayerManager players;
    public CampaignSemanticStateCommand(IObjectManager objects, IPlayerManager players)
    { this.objects = objects; this.players = players; }
    public string Prefix => "coop.debug.authority";
    public string Name => "semantic_state";
    public string Description => "Reads handle-free world roster hashes, all players, at most 24 NPC details and all alive hero gold/volunteers.";
    public CoopCommandSide Side => CoopCommandSide.Both;
    public IExpectedArgs[] ExpectedArgs { get; } =
    { new ExpectedArgs("npc_ids", "Optional comma-separated observed NPC party StringIds, at most24.", false) };
    public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
    {
        var campaign = Campaign.Current;
        if (campaign == null) return new(false, "A campaign is required.", "campaign_missing");
        var requested = args.Count == 0
            ? campaign.MobileParties.Where(party => party.IsActive && party.IsLordParty)
                .OrderBy(party => party.StringId, StringComparer.Ordinal).Take(24).Select(party => party.StringId).ToArray()
            : args[0].Split(',');
        if (requested.Length > 24 || requested.Any(string.IsNullOrWhiteSpace) || requested.Distinct().Count() != requested.Length)
            return new(false, "Expected at most24 distinct observed NPC StringIds.", "invalid_party_sample");
        var npcs = requested.Select(id => campaign.MobileParties.FirstOrDefault(party => party.StringId == id)).ToArray();
        if (npcs.Any(party => party == null)) return new(false, "A requested NPC party is missing.", "sample_party_missing");
        var actorRows = players.Players.OrderBy(player => player.ControllerId).Select(player =>
        {
            if (!objects.TryGetObject(player.MobilePartyId, out MobileParty party) ||
                !objects.TryGetObject(player.HeroId, out Hero hero)) return null;
            return new
            {
                controller = player.ControllerId, hero = hero.StringId, clan = hero.Clan?.StringId,
                character = hero.CharacterObject.StringId, party = party.StringId,
                gold = hero.Gold, x = party.Position.X, y = party.Position.Y,
                settlement = party.CurrentSettlement?.StringId,
                members = Stacks(party.MemberRoster), prisoners = Stacks(party.PrisonRoster),
            };
        }).ToArray();
        if (actorRows.Any(row => row == null)) return new(false, "A registered player cannot be resolved.", "player_missing");
        var heroes = Hero.AllAliveHeroes.OrderBy(hero => hero.StringId, StringComparer.Ordinal).ToArray();
        var parties = campaign.MobileParties.OrderBy(party => party.StringId, StringComparer.Ordinal).Select(party => new
        {
            id = party.StringId, hero = party.LeaderHero?.StringId, clan = party.ActualClan?.StringId,
            active = party.IsActive, members = Stacks(party.MemberRoster), prisoners = Stacks(party.PrisonRoster),
        }).ToArray();
        return new(true, JsonConvert.SerializeObject(new
        {
            campaign = campaign.UniqueGameId, ticks = CampaignTime.Now.NumTicks,
            partyCount = parties.Length, activePartyCount = parties.Count(party => party.active),
            duplicatePartyIds = parties.GroupBy(party => party.id).Where(group => group.Count() > 1).Select(group => group.Key).ToArray(),
            missingPartyIds = parties.Count(party => string.IsNullOrWhiteSpace(party.id)),
            worldIdentityHash = Hash(parties.Select(party => new { party.id, party.hero, party.clan, party.active })),
            worldRostersHash = Hash(parties), activeWorldRostersHash = Hash(parties.Where(party => party.active)),
            players = actorRows,
            sampledNpcIds = requested,
            npcs = npcs.Select(party => new
            {
                party = party.StringId, hero = party.LeaderHero?.StringId, clan = party.ActualClan?.StringId,
                x = party.Position.X, y = party.Position.Y, gold = party.PartyTradeGold,
                members = Stacks(party.MemberRoster), prisoners = Stacks(party.PrisonRoster),
            }).ToArray(),
            aliveHeroes = heroes.Length,
            heroGoldHash = Hash(heroes.Select(hero => new { id = hero.StringId, hero.Gold })),
            volunteerHash = Hash(heroes.Select(hero => new
            {
                id = hero.StringId,
                troops = (hero.VolunteerTypes ?? Array.Empty<CharacterObject>()).Select(troop => troop?.StringId).ToArray(),
            })),
            coverage = "All MobileParty StringId/hero/clan/activity and member+prisoner Character.StringId/count/wounded/XP hashes; all registered players and alive hero gold/volunteers; at most24 NPC details; no handles.",
        }));
    }

    private static object[] Stacks(TroopRoster roster) => Enumerable.Range(0, roster.Count)
        .Select(index => roster.GetElementCopyAtIndex(index)).OrderBy(element => element.Character.StringId, StringComparer.Ordinal)
        .Select(element => (object)new { id = element.Character.StringId, element.Number, element.WoundedNumber, element.Xp }).ToArray();

    private static string Hash(object value)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value)))).Replace("-", "");
    }
}
#endif
