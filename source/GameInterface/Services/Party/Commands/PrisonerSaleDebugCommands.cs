#if DEBUG
using Common;
using Common.Commands;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Party.Patches;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using Helpers;
using Newtonsoft.Json;
using System;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.MountAndBlade;

namespace GameInterface.Services.Party.Commands;

/// <summary>Bounded native fixture for validating the live campaign-authority prisoner-sale route.</summary>
public static class PrisonerSaleDebugCommands
{
    private const string SettlementId = "town_ES1";
    private const string CharacterId = "imperial_infantryman";
    private const int FixtureCount = 2;
    private const int FixtureWounded = 1;
    private const int FixtureXp = 137;
    private const string BackstreetMenu = "town_backstreet";

    private static CoopCommandResult Failed(string error, string code = "command_failed") =>
        new(false, error, code);

    private static CoopCommandResult Succeeded(object value) =>
        new(true, "LIVE_TEST_JSON=" + JsonConvert.SerializeObject(value));

    private static bool IsP2P() => !ModInformation.IsDedicatedAuthority;

    private static bool TryGetLocalActor(
        IObjectManager objects,
        IPlayerManager players,
        IControllerIdProvider controller,
        out Player player,
        out Hero hero,
        out MobileParty party)
    {
        player = null;
        hero = null;
        party = null;
        string controllerId = controller?.ControllerId;
        return !string.IsNullOrWhiteSpace(controllerId) &&
            players.TryGetPlayer(controllerId, out player) && player != null &&
            objects.TryGetObject(player.HeroId, out hero) && hero != null &&
            objects.TryGetObject(player.MobilePartyId, out party) && party != null &&
            ReferenceEquals(hero, Hero.MainHero) && ReferenceEquals(party, MobileParty.MainParty);
    }

    private static bool TryGetRegisteredActor(
        IObjectManager objects,
        IPlayerManager players,
        string controllerId,
        out Player player,
        out Hero hero,
        out MobileParty party)
    {
        player = null;
        hero = null;
        party = null;
        return !string.IsNullOrWhiteSpace(controllerId) &&
            players.TryGetPlayer(controllerId, out player) && player != null &&
            objects.TryGetObject(player.HeroId, out hero) && hero != null &&
            objects.TryGetObject(player.MobilePartyId, out party) && party != null;
    }

    private static bool TryGetLease(ICampaignAuthorityContext authority, out AuthorityLease lease)
    {
        lease = default;
        return authority != null && authority.TryGetCurrentLease(out lease) && lease.IsValid && !lease.IsPaused;
    }

    private static bool TryGetWriterLease(
        ICampaignAuthorityContext authority,
        out AuthorityLease lease)
    {
        lease = default;
        return TryGetLease(authority, out lease) &&
            authority.TryGetLocalAuthorityLease(out var localLease) && localLease == lease;
    }

    internal static bool CanOpenBackstreet(string settlementId, string currentMenu, bool missionActive) =>
        !missionActive && string.Equals(settlementId, SettlementId, StringComparison.Ordinal) &&
        (string.Equals(currentMenu, "town", StringComparison.Ordinal) ||
            string.Equals(currentMenu, BackstreetMenu, StringComparison.Ordinal));

    private static object[] Snapshot(TroopRoster roster) =>
        roster.GetTroopRoster().Select(element => new
        {
            character = element.Character?.StringId,
            element.Number,
            element.WoundedNumber,
            element.Xp,
            isHero = element.Character?.IsHero ?? false,
        }).Cast<object>().ToArray();

    private static Hero NativeRansomRecipient(MobileParty party)
    {
        var sellerParty = party?.Party;
        if (sellerParty == null) return null;
        if (sellerParty.LeaderHero != null && sellerParty.LeaderHero.HeroState == Hero.CharacterStates.Active)
            return sellerParty.LeaderHero;
        if (sellerParty.Owner != null && sellerParty.Owner.HeroState == Hero.CharacterStates.Active)
            return sellerParty.Owner;
        return party.ActualClan?.Leader;
    }

    private static long RansomTotal(TroopRoster roster, MobileParty sellingParty)
    {
        long total = 0;
        foreach (TroopRosterElement element in roster.GetTroopRoster())
            total += (long)element.Number * Campaign.Current.Models.RansomValueCalculationModel
                .PrisonerRansomValue(element.Character, sellingParty.Party.LeaderHero);
        return total;
    }

    private static bool IsFixtureRoster(TroopRoster roster, CharacterObject character)
    {
        var elements = roster.GetTroopRoster();
        return elements.Count == 1 && elements[0].Character == character &&
            elements[0].Number == FixtureCount && elements[0].WoundedNumber == FixtureWounded &&
            elements[0].Xp == FixtureXp && !elements[0].Character.IsHero;
    }

    public sealed class PrepareCommand : ICoopCommand
    {
        private readonly IObjectManager objects;
        private readonly IPlayerManager players;
        private readonly ICampaignAuthorityContext authority;
        private readonly IControllerIdProvider controller;

        public PrepareCommand(IObjectManager objects, IPlayerManager players,
            ICampaignAuthorityContext authority, IControllerIdProvider controller)
        { this.objects = objects; this.players = players; this.authority = authority; this.controller = controller; }

        public string Prefix => "coop.debug.prisoner_sale";
        public string Name => "prepare";
        public string Description => "Stages a regular NPC prisoner fixture on the current unfrozen campaign writer.";
        public CoopCommandSide Side => CoopCommandSide.Both;
        public IExpectedArgs[] ExpectedArgs { get; } =
            { new ExpectedArgs("controller_id", "Registered player whose party receives the fixture.") };

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            if (!IsP2P()) return Failed("The prisoner-sale fixture is disabled on dedicated authority.", "p2p_only");
            if (args.Count != 1 || string.IsNullOrWhiteSpace(args[0]))
                return Failed("Expected one registered controller id.", "invalid_args");
            if (!TryGetWriterLease(authority, out var lease))
                return Failed("A confirmed, unfrozen campaign writer is required to prepare prisoners.", "not_world_writer");
            if (!TryGetLocalActor(objects, players, controller, out var localPlayer, out _, out _))
                return Failed("The local registered campaign actor is unavailable.", "local_actor_unavailable");
            if (!TryGetRegisteredActor(objects, players, args[0], out var target, out var targetHero, out var party))
                return Failed("The target controller has no registered hero and party.", "target_unavailable");
            if (!objects.TryGetHandle(party.Party, out var partyHandle))
                return Failed("The target party has no registered native handle.", "party_handle_unavailable");
            if (party.CurrentSettlement?.StringId != SettlementId)
                return Failed("The target player party must be at Danustica (town_ES1).", "settlement_required");
            if (NativeRansomRecipient(party) != targetHero)
                return Failed("Vanilla prisoner ransom would not pay the target player hero.", "ransom_recipient_mismatch");
            if (party.PrisonRoster.TotalManCount != 0)
                return Failed("The target prison roster must be empty before fixture preparation.", "prison_roster_not_empty");
            if (!objects.TryGetObject<CharacterObject>(CharacterId, out var character) || character == null || character.IsHero)
                return Failed($"The regular fixture troop '{CharacterId}' is unavailable.", "fixture_character_unavailable");

            bool staged = false;
            int goldBefore = targetHero.Gold;
            if (!authority.TryExecuteWithCurrentLease(lease, () =>
            {
                if (!TryGetWriterLease(authority, out var current) || current != lease ||
                    !TryGetLocalActor(objects, players, controller, out var currentLocal, out _, out _) ||
                    currentLocal.ControllerId != localPlayer.ControllerId ||
                    !TryGetRegisteredActor(objects, players, args[0], out var currentTarget, out _, out var currentParty) ||
                    !ReferenceEquals(currentTarget, target) || !ReferenceEquals(currentParty, party) ||
                    currentParty.CurrentSettlement?.StringId != SettlementId || currentParty.PrisonRoster.TotalManCount != 0)
                    return;

                currentParty.PrisonRoster.AddToCounts(character, FixtureCount, false, FixtureWounded, FixtureXp, true);
                staged = IsFixtureRoster(currentParty.PrisonRoster, character);
            }))
                return Failed("Campaign authority changed before fixture preparation.", "stale_lease");

            if (!staged) return Failed("Fixture preparation was skipped because its writer or empty-roster preconditions changed.", "fixture_not_staged");
            return Succeeded(new
            {
                status = "prepared",
                lease,
                writerController = lease.AuthorityControllerId,
                targetController = target.ControllerId,
                partyId = party.StringId,
                partyHandle,
                settlementId = party.CurrentSettlement?.StringId,
                ransomRecipientHeroId = targetHero.StringId,
                characterId = CharacterId,
                count = FixtureCount,
                wounded = FixtureWounded,
                xp = FixtureXp,
                goldBefore,
                prisonRoster = Snapshot(party.PrisonRoster),
            });
        }

    }

    public sealed class InspectCommand : ICoopCommand
    {
        private readonly IObjectManager objects;
        private readonly IPlayerManager players;
        private readonly ICampaignAuthorityContext authority;
        private readonly IControllerIdProvider controller;

        public InspectCommand(IObjectManager objects, IPlayerManager players,
            ICampaignAuthorityContext authority, IControllerIdProvider controller)
        { this.objects = objects; this.players = players; this.authority = authority; this.controller = controller; }

        public string Prefix => "coop.debug.prisoner_sale";
        public string Name => "inspect";
        public string Description => "Reads actor, prisoner stacks, native ransom value, and current campaign lease.";
        public CoopCommandSide Side => CoopCommandSide.Both;
        public IExpectedArgs[] ExpectedArgs { get; } =
            { new ExpectedArgs("controller_id", "Registered player whose party to inspect.") };

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            if (args.Count != 1 || string.IsNullOrWhiteSpace(args[0]))
                return Failed("Expected one registered controller id.", "invalid_args");
            if (Campaign.Current == null)
                return Failed("A native campaign is required.", "campaign_unavailable");
            if (!TryGetRegisteredActor(objects, players, args[0], out var target, out var targetHero, out var targetParty))
                return Failed("The target controller has no registered hero and party.", "target_unavailable");

            AuthorityLease lease = default;
            authority?.TryGetCurrentLease(out lease);
            TryGetLocalActor(objects, players, controller, out var localPlayer, out _, out _);
            return Succeeded(new
            {
                lease,
                localAuthority = authority != null && authority.TryGetLocalAuthorityLease(out var localLease) && localLease == lease,
                localController = localPlayer?.ControllerId,
                mainHero = Hero.MainHero?.StringId,
                mainParty = MobileParty.MainParty?.StringId,
                mainSettlement = MobileParty.MainParty?.CurrentSettlement?.StringId,
                currentMenu = Campaign.Current.CurrentMenuContext?.GameMenu?.StringId,
                targetController = target.ControllerId,
                targetHeroId = targetHero.StringId,
                targetPartyId = targetParty.StringId,
                targetPartyHandle = objects.TryGetHandle(targetParty.Party, out var partyHandle) ? partyHandle : (uint?)null,
                settlement = targetParty.CurrentSettlement?.StringId,
                gold = targetHero.Gold,
                ransomRecipientHeroId = NativeRansomRecipient(targetParty)?.StringId,
                ransomRecipientGold = NativeRansomRecipient(targetParty)?.Gold,
                memberRoster = Snapshot(targetParty.MemberRoster),
                prisonRoster = Snapshot(targetParty.PrisonRoster),
                expectedRansomTotal = RansomTotal(targetParty.PrisonRoster, targetParty),
                players = players.Players.Select(player => player.ControllerId).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            });
        }
    }

    public sealed class OpenBackstreetCommand : ICoopCommand
    {
        private readonly IObjectManager objects;
        private readonly IPlayerManager players;
        private readonly ICampaignAuthorityContext authority;
        private readonly IControllerIdProvider controller;

        public OpenBackstreetCommand(IObjectManager objects, IPlayerManager players,
            ICampaignAuthorityContext authority, IControllerIdProvider controller)
        { this.objects = objects; this.players = players; this.authority = authority; this.controller = controller; }

        public string Prefix => "coop.debug.prisoner_sale";
        public string Name => "open_backstreet";
        public string Description => "Opens the local player's native Danustica backstreet menu.";
        public CoopCommandSide Side => CoopCommandSide.Both;
        public IExpectedArgs[] ExpectedArgs { get; } =
            { new ExpectedArgs("controller_id", "Must match this process's local registered player actor.") };

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            if (!IsP2P()) return Failed("The prisoner-sale fixture is disabled on dedicated authority.", "p2p_only");
            if (args.Count != 1 || string.IsNullOrWhiteSpace(args[0]))
                return Failed("Expected one local controller id.", "invalid_args");
            if (!TryGetLocalActor(objects, players, controller, out var localPlayer, out var localHero, out var localParty))
                return Failed("The local registered campaign actor is unavailable.", "local_actor_unavailable");
            if (!string.Equals(args[0], localPlayer.ControllerId, StringComparison.Ordinal))
                return Failed("Backstreet presentation is limited to the local registered player actor.", "not_local_actor");
            if (!TryGetLease(authority, out var lease))
                return Failed("A confirmed, unfrozen campaign lease is required.", "lease_unavailable");
            if (Campaign.Current == null)
                return Failed("A native campaign is required.", "campaign_unavailable");

            string currentMenu = Campaign.Current.CurrentMenuContext?.GameMenu?.StringId;
            if (!CanOpenBackstreet(localParty.CurrentSettlement?.StringId, currentMenu, Mission.Current != null))
                return Failed("The local player must be at Danustica's town menu with no active Mission.", "native_menu_required");

            bool alreadyOpen = false;
            bool opened = false;
            if (!authority.TryExecuteWithCurrentLease(lease, () =>
            {
                if (!TryGetLease(authority, out var currentLease) || currentLease != lease ||
                    !TryGetLocalActor(objects, players, controller, out var currentPlayer, out var currentHero, out var currentParty) ||
                    !string.Equals(currentPlayer.ControllerId, localPlayer.ControllerId, StringComparison.Ordinal) ||
                    !ReferenceEquals(currentHero, localHero) || !ReferenceEquals(currentParty, localParty) ||
                    Campaign.Current == null ||
                    !CanOpenBackstreet(currentParty.CurrentSettlement?.StringId,
                        Campaign.Current.CurrentMenuContext?.GameMenu?.StringId, Mission.Current != null))
                    return;

                string confirmedMenu = Campaign.Current.CurrentMenuContext?.GameMenu?.StringId;
                if (string.Equals(confirmedMenu, BackstreetMenu, StringComparison.Ordinal))
                {
                    alreadyOpen = true;
                    return;
                }

                GameMenu.SwitchToMenu(BackstreetMenu);
                opened = true;
            }))
                return Failed("Campaign authority changed before opening the native menu.", "stale_lease");

            if (!opened && !alreadyOpen)
                return Failed("The local menu context changed before opening the native menu.", "context_changed");

            return Succeeded(new
            {
                status = alreadyOpen ? "already_open" : "opened",
                lease,
                controller = localPlayer.ControllerId,
                heroId = localHero.StringId,
                partyId = localParty.StringId,
                settlementId = localParty.CurrentSettlement?.StringId,
                menu = BackstreetMenu,
            });
        }
    }

    public sealed class SubmitCommand : ICoopCommand
    {
        private readonly IObjectManager objects;
        private readonly IPlayerManager players;
        private readonly ICampaignAuthorityContext authority;
        private readonly IControllerIdProvider controller;

        public SubmitCommand(IObjectManager objects, IPlayerManager players,
            ICampaignAuthorityContext authority, IControllerIdProvider controller)
        { this.objects = objects; this.players = players; this.authority = authority; this.controller = controller; }

        public string Prefix => "coop.debug.prisoner_sale";
        public string Name => "submit";
        public string Description => "Invokes the real native sell-prisoners Harmony prefix for one staged regular NPC stack.";
        public CoopCommandSide Side => CoopCommandSide.Both;
        public IExpectedArgs[] ExpectedArgs { get; } =
            { new ExpectedArgs("controller_id", "Must match this process's local registered player actor.") };

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            if (!IsP2P()) return Failed("The prisoner-sale fixture is disabled on dedicated authority.", "p2p_only");
            if (args.Count != 1 || string.IsNullOrWhiteSpace(args[0]))
                return Failed("Expected one local controller id.", "invalid_args");
            if (!TryGetLocalActor(objects, players, controller, out var localPlayer, out var localHero, out var localParty))
                return Failed("The local registered campaign actor is unavailable.", "local_actor_unavailable");
            if (!objects.TryGetHandle(localParty.Party, out var partyHandle))
                return Failed("The local party has no registered native handle.", "party_handle_unavailable");
            if (args[0] != localPlayer.ControllerId)
                return Failed("Submission is limited to the local registered player actor.", "not_local_actor");
            if (Campaign.Current == null || localParty.CurrentSettlement?.StringId != SettlementId ||
                Campaign.Current.CurrentMenuContext?.GameMenu?.StringId != "town_backstreet")
                return Failed("The local player must be in Danustica's native town backstreet menu.", "native_backstreet_required");
            if (NativeRansomRecipient(localParty) != localHero)
                return Failed("Vanilla prisoner ransom would not pay the local player hero.", "ransom_recipient_mismatch");
            if (!TryGetLease(authority, out var lease))
                return Failed("A confirmed, unfrozen campaign lease is required.", "lease_unavailable");
            if (!objects.TryGetObject<CharacterObject>(CharacterId, out var character) || character == null || character.IsHero ||
                !IsFixtureRoster(localParty.PrisonRoster, character))
                return Failed("The local prison roster must contain only the staged regular NPC fixture.", "fixture_roster_required");

            var selected = MobilePartyHelper.GetPlayerPrisonersPlayerCanSell();
            if (selected == null || !IsFixtureRoster(selected, character))
                return Failed("The native sellable-prisoner selection does not match the staged fixture.", "native_selection_mismatch");

            bool localAuthority = authority.TryGetLocalAuthorityLease(out var writerLease) && writerLease == lease;
            var goldBefore = localHero.Gold;
            var expectedRansom = RansomTotal(selected, localParty);
            bool submitted = false;
            if (!authority.TryExecuteWithCurrentLease(lease, () =>
            {
                if (!TryGetLease(authority, out var current) || current != lease ||
                    !TryGetLocalActor(objects, players, controller, out var currentPlayer, out var currentHero, out var currentParty) ||
                    currentPlayer.ControllerId != localPlayer.ControllerId || currentHero != localHero || currentParty != localParty ||
                    currentParty.CurrentSettlement?.StringId != SettlementId ||
                    Campaign.Current?.CurrentMenuContext?.GameMenu?.StringId != "town_backstreet" ||
                    !IsFixtureRoster(currentParty.PrisonRoster, character) ||
                    !IsFixtureRoster(MobilePartyHelper.GetPlayerPrisonersPlayerCanSell(), character))
                    return;

                // The prefix publishes the production PrisonersSold event and suppresses vanilla's original.
                bool runOriginal = SellAllPrisonersPatch.SellAllTransferablePrisonersPrefix();
                submitted = !runOriginal;
            }))
                return Failed("Campaign authority changed before native sale submission.", "stale_lease");
            if (!submitted)
                return Failed("Native sale submission was skipped because the local menu or fixture changed.", "submission_skipped");

            authority.TryGetLocalAuthorityLease(out writerLease);
            return Succeeded(new
            {
                status = "submitted",
                lease,
                localAuthority,
                controller = localPlayer.ControllerId,
                heroId = localHero.StringId,
                partyId = localParty.StringId,
                partyHandle,
                settlementId = localParty.CurrentSettlement?.StringId,
                menu = Campaign.Current.CurrentMenuContext?.GameMenu?.StringId,
                characterId = CharacterId,
                count = FixtureCount,
                wounded = FixtureWounded,
                xp = FixtureXp,
                expectedRansomTotal = expectedRansom,
                goldBefore,
                goldAfterImmediateLocal = localHero.Gold,
                prisonRosterAfterImmediateLocal = Snapshot(localParty.PrisonRoster),
                note = "Observer submissions apply asynchronously on the confirmed writer; compare both replica snapshots.",
            });
        }
    }
}
#endif
