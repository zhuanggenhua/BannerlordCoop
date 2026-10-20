#if DEBUG
using Common;
using GameInterface.Services.Entity;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Engine;

namespace GameInterface.Services.LiveTesting.MovementLatency;

internal sealed class MovementLatencyActor
{
    public MobileParty Party;
    public string Controller;
    public string Id;
    public uint Handle;
}

internal interface IMovementLatencyEnvironment
{
    object CampaignToken { get; }
    bool TryBind(out string campaign, out MovementLatencyActor[] actors, out string error);
    MovementLatencyClock ReadClock();
    MovementLatencyRow Sample(MovementLatencyActor actor, string kind, long observationId);
}

internal sealed class MovementLatencyEnvironment : IMovementLatencyEnvironment
{
    private readonly IPlayerManager players;
    private readonly IObjectManager objects;
    private readonly ISessionIdentity identity;
    private readonly IControllerIdProvider controller;
    private readonly int pid = Process.GetCurrentProcess().Id;
    public object CampaignToken => Campaign.Current;

    public MovementLatencyEnvironment(IPlayerManager players, IObjectManager objects,
        ISessionIdentity identity, IControllerIdProvider controller)
    { this.players = players; this.objects = objects; this.identity = identity; this.controller = controller; }

    public bool TryBind(out string campaign, out MovementLatencyActor[] actors, out string error)
    {
        campaign = Campaign.Current?.UniqueGameId;
        actors = null;
        error = "Exactly two registered player parties and a loaded campaign are required.";
        if (string.IsNullOrEmpty(campaign)) return false;
        var registered = players.Players.ToArray();
        if (registered.Length != 2 || registered.Any(p => string.IsNullOrEmpty(p.ControllerId)) ||
            registered.Select(p => p.ControllerId).Distinct(StringComparer.Ordinal).Count() != 2) return false;
        var bound = new List<MovementLatencyActor>();
        foreach (var player in registered)
        {
            if (!objects.TryGetObject<MobileParty>(player.MobilePartyId, out var party) || party == null ||
                !party.IsActive || !objects.TryGetHandle(party, out uint handle) || handle == 0 ||
                bound.Any(a => ReferenceEquals(a.Party, party) || a.Handle == handle)) return false;
            bound.Add(new MovementLatencyActor { Party = party, Controller = player.ControllerId, Id = player.MobilePartyId, Handle = handle });
        }
        actors = bound.ToArray();
        error = null;
        return true;
    }

    public MovementLatencyClock ReadClock() => new MovementLatencyClock
    {
        Pid = pid, StopwatchTicks = Stopwatch.GetTimestamp(), Frequency = Stopwatch.Frequency,
        Utc = DateTime.UtcNow, EngineFrame = Utilities.EngineFrameNo,
    };

    public MovementLatencyRow Sample(MovementLatencyActor actor, string kind, long observationId)
    {
        var lease = identity.CurrentLease;
        var party = actor.Party;
        return new MovementLatencyRow
        {
            Kind = kind, ObservationId = observationId, Clock = ReadClock(), Campaign = Campaign.Current?.UniqueGameId,
            Epoch = lease.Epoch, AuthorityController = lease.AuthorityControllerId,
            Frozen = identity.IsLocallyFrozen(lease), LocalController = controller.ControllerId,
            ActorController = actor.Controller, PartyId = actor.Id, Handle = actor.Handle,
            X = party.Position.X, Y = party.Position.Y, TargetX = party.MoveTargetPoint.X, TargetY = party.MoveTargetPoint.Y,
            Mode = party.PartyMoveMode.ToString(), Behavior = party.DefaultBehavior.ToString(),
        };
    }
}
#endif
