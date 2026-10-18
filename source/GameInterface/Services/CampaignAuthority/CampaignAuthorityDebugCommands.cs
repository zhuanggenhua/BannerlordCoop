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
