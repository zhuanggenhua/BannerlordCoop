#if DEBUG
using Common;
using Common.Commands;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using Newtonsoft.Json;
using System;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.ViewModelCollection.GameMenu.Recruitment;
using TaleWorlds.CampaignSystem.ViewModelCollection.Input;

namespace GameInterface.Services.TroopRosters.Commands;

/// <summary>Live evidence commands: read current data, or submit one real volunteer through the native VM.</summary>
public static class RecruitmentDebugCommands
{
    public sealed class InspectCommand : ICoopCommand
    {
        private readonly IObjectManager objects;
        private readonly IPlayerManager players;
        private readonly ICampaignAuthorityContext authority;
        public InspectCommand(IObjectManager objects, IPlayerManager players, ICampaignAuthorityContext authority)
        { this.objects = objects; this.players = players; this.authority = authority; }
        public string Prefix => "coop.debug.recruitment";
        public string Name => "inspect";
        public string Description => "Reads native actor, player rosters, gold and current settlement volunteer slots.";
        public CoopCommandSide Side => CoopCommandSide.Both;
        public IExpectedArgs[] ExpectedArgs { get; } =
        {
            new ExpectedArgs("query", "Optional town_catalog scans existing town stock for registered heroes.", false),
        };
        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            if (Campaign.Current == null || MobileParty.MainParty == null)
                return new(false, "A native campaign player is required.", "campaign_unavailable");
            if (args.Count > 0)
                return args[0] == "town_catalog"
                    ? CaptureTownCatalog()
                    : new(false, "Expected no argument or town_catalog.", "invalid_args");
            var settlement = MobileParty.MainParty.CurrentSettlement;
            authority.TryGetCurrentLease(out var lease);
            return new(true, JsonConvert.SerializeObject(new
            {
                lease,
                localAuthority = authority.IsLocalCampaignAuthority,
                mainHero = Hero.MainHero?.StringId,
                mainParty = MobileParty.MainParty.StringId,
                settlement = settlement?.StringId,
                players = players.Players.OrderBy(value => value.ControllerId).Select(player =>
                {
                    objects.TryGetObject<MobileParty>(player.MobilePartyId, out var party);
                    return new
                    {
                        controller = player.ControllerId,
                        partyId = player.MobilePartyId,
                        heroId = player.HeroId,
                        currentSettlement = party?.CurrentSettlement?.StringId,
                        gold = party?.LeaderHero?.Gold,
                        troops = party?.MemberRoster.GetTroopRoster().Select(element => new
                        {
                            character = element.Character.StringId,
                            element.Number,
                            element.WoundedNumber,
                            element.Xp,
                        }).ToArray(),
                    };
                }).ToArray(),
                volunteers = settlement?.Notables.Select(notable => new
                {
                    id = objects.TryGetId(notable, out var id) ? id : null,
                    nativeId = notable.StringId,
                    maximumRecruitableIndex = Campaign.Current.Models.VolunteerModel.MaximumIndexHeroCanRecruitFromHero(Hero.MainHero, notable),
                    slots = notable.VolunteerTypes?.Select((troop, index) => new
                    {
                        index,
                        character = troop?.StringId,
                        cost = troop == null ? (int?)null : Campaign.Current.Models.PartyWageModel
                            .GetTroopRecruitmentCost(troop, Hero.MainHero).RoundedResultNumber,
                    }).ToArray(),
                }).ToArray(),
            }));
        }

        private CoopCommandResult CaptureTownCatalog()
        {
            var registered = players.Players.OrderBy(player => player.ControllerId).ToArray();
            if (registered.Length < 2)
                return new(false, "Two registered player heroes are required.", "players_unavailable");
            var heroes = new Hero[registered.Length];
            for (int index = 0; index < registered.Length; index++)
                if (!objects.TryGetObject(registered[index].HeroId, out heroes[index]))
                    return new(false, "A registered player hero is unavailable.", "player_unavailable");
            int maximumCost = heroes.Min(hero => hero.Gold) / 4;
            var scanned = Campaign.Current.Settlements.Where(settlement => settlement.IsTown).ToArray();
            var candidates = scanned.Select(settlement =>
            {
                var notables = settlement.Notables.Select(notable =>
                {
                    var limits = registered.Select((player, index) => new
                    {
                        controller = player.ControllerId,
                        maximumRecruitableIndex = Campaign.Current.Models.VolunteerModel
                            .MaximumIndexHeroCanRecruitFromHero(heroes[index], notable),
                    }).ToArray();
                    var slots = (notable.VolunteerTypes ?? Array.Empty<CharacterObject>()).Select((troop, slot) =>
                    {
                        if (troop == null) return null;
                        var prices = registered.Select((player, index) => new
                        {
                            controller = player.ControllerId,
                            cost = Campaign.Current.Models.PartyWageModel
                                .GetTroopRecruitmentCost(troop, heroes[index]).RoundedResultNumber,
                        }).ToArray();
                        if (limits.Any(limit => slot >= limit.maximumRecruitableIndex) ||
                            prices.Any(price => price.cost < 0 || price.cost > maximumCost)) return null;
                        return new { index = slot, character = troop.StringId, players = prices };
                    }).Where(slot => slot != null).ToArray();
                    return new
                    {
                        id = objects.TryGetId(notable, out var id) ? id : null,
                        nativeId = notable.StringId,
                        players = limits,
                        slots,
                    };
                }).Where(notable => notable.id != null && notable.slots.Length > 0).ToArray();
                return new { id = settlement.StringId, sharedSlotCount = notables.Sum(notable => notable.slots.Length), notables };
            }).Where(town => town.sharedSlotCount >= 4)
                .OrderByDescending(town => town.sharedSlotCount).ThenBy(town => town.id, StringComparer.Ordinal).ToArray();

            return new(true, JsonConvert.SerializeObject(new
            {
                query = "town_catalog",
                campaign = Campaign.Current.UniqueGameId,
                scannedTownCount = scanned.Length,
                eligibleTownCount = candidates.Length,
                returnedTownCount = Math.Min(16, candidates.Length),
                limited = candidates.Length > 16,
                maximumCost,
                requiredSharedSlots = 4,
                players = registered.Select((player, index) => new
                {
                    controller = player.ControllerId,
                    heroId = player.HeroId,
                    nativeHeroId = heroes[index].StringId,
                    gold = heroes[index].Gold,
                }).ToArray(),
                towns = candidates.Take(16).ToArray(),
            }));
        }
    }

    public sealed class SubmitCommand : ICoopCommand
    {
        private readonly IObjectManager objects;
        private readonly ICampaignAuthorityContext authority;
        public SubmitCommand(IObjectManager objects, ICampaignAuthorityContext authority)
        { this.objects = objects; this.authority = authority; }
        public string Prefix => "coop.debug.recruitment";
        public string Name => "submit";
        public string Description => "Selects one existing recruit with native ExecuteRecruit, then runs native ExecuteDone.";
        public CoopCommandSide Side => CoopCommandSide.Both;
        public IExpectedArgs[] ExpectedArgs { get; } = new IExpectedArgs[]
        {
            new ExpectedArgs("notableId", "Registered id from recruitment.inspect in the current settlement."),
            new ExpectedArgs("slot", "Existing volunteer slot index."),
        };

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            if (args.Count != 2 || !int.TryParse(args[1], out var slot) || slot < 0)
                return new(false, "Expected notableId and a nonnegative slot index.", "invalid_args");
            var party = MobileParty.MainParty;
            if (Campaign.Current == null || party?.CurrentSettlement == null || Hero.MainHero == null)
                return new(false, "The native player must be in a settlement encounter.", "settlement_required");
            if (!objects.TryGetObject<Hero>(args[0], out var notable) ||
                notable.CurrentSettlement != party.CurrentSettlement)
                return new(false, "Notable does not belong to the native player's settlement.", "notable_unavailable");
            if (!authority.TryGetCurrentLease(out var lease) || !lease.IsValid || lease.IsPaused)
                return new(false, "A running campaign lease is required.", "lease_unavailable");

            var vm = new RecruitmentVM
            {
                // The UI normally supplies these. They let the native finalizer detach its own listeners.
                CancelInputKey = new InputKeyItemVM(),
                DoneInputKey = new InputKeyItemVM(),
                ResetInputKey = new InputKeyItemVM(),
                RecruitAllInputKey = new InputKeyItemVM(),
            };
            try
            {
                var owner = vm.VolunteerList.FirstOrDefault(value => value.OwnerHero == notable);
                var recruit = owner?.Troops.FirstOrDefault(value => value.Index == slot);
                if (recruit == null || !recruit.CanBeRecruited)
                    return new(false, "Native recruitment UI rejects this slot.", "slot_unavailable");
                if (party.MemberRoster.TotalManCount >= party.Party.PartySizeLimit || recruit.Cost > Hero.MainHero.Gold)
                    return new(false, "Native party capacity or gold would reject this recruit.", "recruitment_unavailable");

                int goldBefore = Hero.MainHero.Gold;
                int troopsBefore = party.MemberRoster.TotalManCount;
                recruit.ExecuteRecruit();
                if (vm.TroopsInCart.Count != 1)
                    return new(false, "Native ExecuteRecruit did not put one volunteer in the cart.", "cart_unavailable");
                var cart = vm.TroopsInCart.Select(value => new
                {
                    notable = value.Owner.OwnerHero.StringId,
                    value.Index,
                    character = value.Character.StringId,
                    value.Cost,
                }).ToArray();

                // This invokes RecruitmentVMPatches.OnDone and the production request / native writer path.
                vm.ExecuteDone();
                return new(true, JsonConvert.SerializeObject(new
                {
                    status = "submitted",
                    lease,
                    localAuthority = authority.IsLocalCampaignAuthority,
                    cart,
                    goldBefore,
                    goldAfterLocal = Hero.MainHero.Gold,
                    troopsBefore,
                    troopsAfterLocal = party.MemberRoster.TotalManCount,
                    note = "Observer submission completes asynchronously; compare both recruitment.inspect results.",
                }));
            }
            finally
            {
                vm.Deactivate();
                vm.OnFinalize();
            }
        }
    }
}
#endif
