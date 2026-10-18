#if DEBUG
using Common;
using Common.Commands;
using Common.Messaging;
using GameInterface.Services.Entity;
using GameInterface.Services.Time.Messages;
using Newtonsoft.Json;
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;
using GameInterface.Registry.Auto;
using TaleWorlds.CampaignSystem.Roster;
using GameInterface.Services.ObjectManager;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace GameInterface.Services.CampaignAuthority;

public static class CampaignAuthorityDebugCommands
{
    internal static bool YieldMapAuthority;

    public sealed class StateCommand : ICoopCommand
    {
        private readonly ISessionIdentity identity;
        private readonly ICampaignAuthorityContext context;
        private readonly IControllerIdProvider controller;
        public StateCommand(ISessionIdentity identity, ICampaignAuthorityContext context, IControllerIdProvider controller)
        { this.identity = identity; this.context = context; this.controller = controller; }
        public string Prefix => "coop.debug.authority";
        public string Name => "state";
        public string Description => "Reports the local campaign lease, world tick and actor identity for handoff validation.";
        public CoopCommandSide Side => CoopCommandSide.Both;
        public IExpectedArgs[] ExpectedArgs { get; } = Array.Empty<IExpectedArgs>();
        public CoopCommandResult ProcessCommand(ICoopCommandArgs args) => new(Campaign.Current != null, JsonConvert.SerializeObject(new
        {
            controller = controller.ControllerId,
            roomHost = identity.RoomHostControllerId,
            lease = identity.CurrentLease,
            localAuthority = context.IsLocalCampaignAuthority,
            frozen = identity.IsLocallyFrozen(identity.CurrentLease),
            yielded = YieldMapAuthority,
            campaign = Campaign.Current?.UniqueGameId,
            worldTicks = Campaign.Current?.CurrentTickCount,
            hours = Campaign.Current == null ? (double?)null : CampaignTime.Now.ToHours,
            mainHero = Hero.MainHero?.StringId,
            mainParty = TaleWorlds.CampaignSystem.Party.MobileParty.MainParty?.StringId,
            gameState = GameStateManager.Current?.ActiveState?.GetType().Name,
            mapInputEnabled = SandBox.View.Map.MapScreen.Instance?.MapCameraView?.ProcessCameraInput,
            mainNavigation = TaleWorlds.CampaignSystem.Party.MobileParty.MainParty == null ? null : new
            {
                x = TaleWorlds.CampaignSystem.Party.MobileParty.MainParty.Position.X,
                y = TaleWorlds.CampaignSystem.Party.MobileParty.MainParty.Position.Y,
                targetX = TaleWorlds.CampaignSystem.Party.MobileParty.MainParty.TargetPosition.X,
                targetY = TaleWorlds.CampaignSystem.Party.MobileParty.MainParty.TargetPosition.Y,
                moveX = TaleWorlds.CampaignSystem.Party.MobileParty.MainParty.MoveTargetPoint.X,
                moveY = TaleWorlds.CampaignSystem.Party.MobileParty.MainParty.MoveTargetPoint.Y,
                behavior = TaleWorlds.CampaignSystem.Party.MobileParty.MainParty.DefaultBehavior.ToString(),
                shortTermBehavior = TaleWorlds.CampaignSystem.Party.MobileParty.MainParty.ShortTermBehavior.ToString(),
                mode = TaleWorlds.CampaignSystem.Party.MobileParty.MainParty.PartyMoveMode.ToString(),
                disabled = TaleWorlds.CampaignSystem.Party.MobileParty.MainParty.Ai.IsDisabled,
                needsUpdate = TaleWorlds.CampaignSystem.Party.MobileParty.MainParty.Ai.DefaultBehaviorNeedsUpdate,
                nextCheck = TaleWorlds.CampaignSystem.Party.MobileParty.MainParty.Ai._nextAiCheckTime.NumTicks,
                dt = Campaign.Current._dt,
            },
            scheduler = Campaign.Current?._campaignPeriodicEventManager == null ? null : new
            {
                minimumIntervalTicks = Campaign.Current._campaignPeriodicEventManager.MinimumPeriodicEventInterval.NumTicks,
                lastGameTimeTicks = Campaign.Current._campaignPeriodicEventManager._lastGameTime.NumTicks,
                events = Campaign.Current.CustomPeriodicCampaignEvents.Select(value => new
                {
                    description = value.description,
                    periodTicks = value.TriggerPeriod.NumTicks,
                    initialWaitTicks = value.InitialWait.NumTicks,
                    nextTriggerTicks = value.NextTriggerTime.NumTicks,
                    deleted = value.isEventDeleted,
                }).ToArray(),
            },
        }));
    }

    public sealed class WorldStateCommand : ICoopCommand
    {
        private readonly IObjectManager objects;
        private readonly ICampaignRosterCheckpointService rosters;
        public WorldStateCommand(IObjectManager objects, ICampaignRosterCheckpointService rosters)
        { this.objects = objects; this.rosters = rosters; }
        public string Prefix => "coop.debug.authority";
        public string Name => "world_state";
        public string Description => "Reports critical world hashes and an NPC position sample without changing game state.";
        public CoopCommandSide Side => CoopCommandSide.Both;
        public IExpectedArgs[] ExpectedArgs { get; } = Array.Empty<IExpectedArgs>();
        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            if (Campaign.Current == null || !rosters.TryCapture(out var checkpoint))
                return new(false, "World rosters are unavailable or contain unregistered identities");
            var parties = Campaign.Current.MobileParties.Select(party => new
            {
                handle = objects.TryGetHandle(party, out var handle) ? handle : 0,
                id = party.StringId,
                x = party.Position.X, y = party.Position.Y,
                troops = party.MemberRoster.TotalManCount,
                wounded = party.MemberRoster.TotalWounded,
                hero = party.LeaderHero?.StringId,
                clan = party.ActualClan?.StringId,
                active = party.IsActive,
                movingNpc = party.IsLordParty || party.IsCaravan || party.IsVillager,
            }).OrderBy(party => party.handle).ToArray();
            var allRosters = checkpoint.Rosters.OrderBy(roster => roster.RosterHandle).ToArray();
            var heroes = Hero.AllAliveHeroes.OrderBy(hero => hero.StringId, StringComparer.Ordinal).ToArray();
            var allocation = objects.CaptureAllocationCheckpoint();
            return new(true, JsonConvert.SerializeObject(new
            {
                campaign = Campaign.Current.UniqueGameId,
                ticks = CampaignTime.Now.NumTicks,
                parties = parties.Length,
                unregisteredParties = parties.Count(party => party.handle == 0),
                partyIdentityHash = Hash(parties.Select(party => new { party.handle, party.id, party.hero, party.clan })),
                rosterCount = allRosters.Length,
                rosterCountsHash = Hash(allRosters.Select(roster => new { roster.RosterHandle,
                    elements = roster.Elements.OrderBy(element => element.CharacterHandle).Select(element =>
                        new { element.CharacterHandle, element.Number, element.Wounded }) })),
                rosterXpHash = Hash(allRosters.Select(roster => new { roster.RosterHandle,
                    elements = roster.Elements.OrderBy(element => element.CharacterHandle).ToArray() })),
                volunteerHash = Hash(heroes.Select(hero => new { id = hero.StringId,
                    troops = (hero.VolunteerTypes ?? Array.Empty<CharacterObject>()).Select(troop => troop?.StringId).ToArray() })),
                heroGoldHash = Hash(heroes.Select(hero => new { id = hero.StringId, hero.Gold })),
                nextHandle = allocation.NextHandle,
                allocationCounters = allocation.TypeCounters.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(pair => pair.Key, pair => pair.Value),
                allocationCounterHash = Hash(allocation.TypeCounters.OrderBy(pair => pair.Key, StringComparer.Ordinal)),
                npcSample = parties.Where(party => party.active && party.movingNpc).Take(24).ToArray(),
            }));
        }

        private static string Hash(object value)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value)))).Replace("-", "");
        }
    }

    public sealed class AllocationProbeCommand : ICoopCommand
    {
        private static readonly HashSet<string> probes = new(StringComparer.Ordinal);
        private readonly IObjectManager objects;
        private readonly ICampaignAuthorityContext context;
        public AllocationProbeCommand(IObjectManager objects, ICampaignAuthorityContext context)
        { this.objects = objects; this.context = context; }
        public string Prefix => "coop.debug.authority";
        public string Name => "allocation_probe";
        public string Description => "Creates, inspects or retires a scoped empty native roster to verify allocation across handoffs.";
        public CoopCommandSide Side => CoopCommandSide.Both;
        public IExpectedArgs[] ExpectedArgs { get; } =
        { new ExpectedArgs("operation", "create, inspect or retire"), new ExpectedArgs("id", "Probe registry id", false) };
        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            if (args.Count < 1) return new(false, "Expected create, inspect or retire");
            TroopRoster roster;
            string id;
            if (args[0] == "create" && args.Count == 1)
            {
                if (!context.TryGetLocalAuthorityLease(out _)) return new(false, "An unfrozen writer is required");
                roster = TroopRoster.CreateDummyTroopRoster();
                if (!objects.TryGetId(roster, out id)) return new(false, "Native roster creation did not register an identity");
                probes.Add(id);
            }
            else if (args.Count == 2 && (args[0] == "inspect" || args[0] == "retire"))
            {
                id = args[1];
                if (!objects.TryGetObject(id, out roster)) return new(false, "Probe identity is absent");
            }
            else return new(false, "Expected create, inspect <id> or retire <id>");
            if (!objects.TryGetHandle(roster, out var handle)) return new(false, "Probe handle is absent");
            if (args[0] == "retire")
            {
                if (!context.TryGetLocalAuthorityLease(out _) || !probes.Contains(id) || roster.OwnerParty != null || roster.Count != 0)
                    return new(false, "Only a local empty probe can be retired by its writer");
                LifetimePatches<TroopRoster>.DestroyPostfix(ref roster, true);
                if (objects.TryGetObject<TroopRoster>(id, out _)) return new(false, "Probe retirement did not remove its identity");
                probes.Remove(id);
            }
            var allocation = objects.CaptureAllocationCheckpoint();
            return new(true, JsonConvert.SerializeObject(new
            { id, handle, retired = args[0] == "retire", nextHandle = allocation.NextHandle }));
        }
    }

    public sealed class AvailabilityCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.authority";
        public string Name => "availability";
        public string Description => "Yield map authority or restore native availability for a map-only live handoff test.";
        public CoopCommandSide Side => CoopCommandSide.Both;
        public IExpectedArgs[] ExpectedArgs { get; } = { new ExpectedArgs("mode", "yield or auto") };
        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            if (args.Count != 1 || (args[0] != "yield" && args[0] != "auto"))
                return new(false, "Expected yield or auto", "invalid_argument");
            if (Campaign.Current == null) return new(false, "A loaded campaign is required", "campaign_missing");
            YieldMapAuthority = args[0] == "yield";
            var states = GameStateManager.Current?.GameStates;
            bool ready = states != null && states.Any(value => value is MapState) &&
                !states.Any(value => value is MissionState) && !YieldMapAuthority;
            MessageBroker.Instance.Publish(this, new CampaignMapSimulationReadinessChanged(ready));
            return new(true, "yielded=" + YieldMapAuthority + "|mapReady=" + ready);
        }
    }
}
#endif
