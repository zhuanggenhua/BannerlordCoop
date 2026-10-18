#if DEBUG
using Common;
using Common.Commands;
using Common.Messaging;
using GameInterface.Services.Entity;
using GameInterface.Services.Time.Messages;
using Newtonsoft.Json;
using System;
using System.Linq;
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
