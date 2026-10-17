#if DEBUG
using Common;
using Common.Commands;
using GameInterface.Services.Entity;
using GameInterface.Services.Players;
using HarmonyLib;
using SandBox.View.Map;
using System;
using System.Linq;
using System.Threading;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.Time;

// Diagnostic counters do not drive simulation. Scoped to one live-test process.
[HarmonyPatch(typeof(Campaign), "RealTick")]
internal static class ListenAuthorityTickEvidence
{
    internal static long Frames, RealTicks, DuplicateRealTicks, FailedRealTicks, AiTicks;
    private static int realTicksThisFrame;

    internal static void BeginFrame()
    {
        if (!ModInformation.IsListenAuthority) return;
        Interlocked.Increment(ref Frames);
        Interlocked.Exchange(ref realTicksThisFrame, 0);
    }

    [HarmonyPrefix]
    private static void Prefix()
    {
        if (!ModInformation.IsListenAuthority) return;
        Interlocked.Increment(ref RealTicks);
        if (Interlocked.Increment(ref realTicksThisFrame) > 1)
            Interlocked.Increment(ref DuplicateRealTicks);
    }

    internal static void RecordFailure() => Interlocked.Increment(ref FailedRealTicks);
}

public sealed class ListenAuthorityStateCommand : ICoopCommand
{
    private readonly IPlayerManager players;
    private readonly IControllerIdProvider controller;
    private readonly ISessionIdentity identity;

    public ListenAuthorityStateCommand(IPlayerManager players, IControllerIdProvider controller, ISessionIdentity identity)
    {
        this.players = players;
        this.controller = controller;
        this.identity = identity;
    }

    public string Prefix => "coop.debug.listen";
    public string Name => "state";
    public string Description => "Reports P1-A native player, simulation and rendering evidence without changing state.";
    public CoopCommandSide Side => CoopCommandSide.Server;
    public IExpectedArgs[] ExpectedArgs { get; } = Array.Empty<IExpectedArgs>();

    public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
    {
        if (!ModInformation.IsListenAuthority || Campaign.Current == null)
            return new CoopCommandResult(false, "ListenAuthority campaign required", "wrong_session");
        var hero = Hero.MainHero;
        var party = MobileParty.MainParty;
        players.TryGetPlayer(controller.ControllerId, out var local);
        return new CoopCommandResult(true,
            $"mode={ModInformation.SessionMode}|room={identity.RoomId}|authority={identity.AuthorityId}|local={controller.ControllerId}" +
            $"|campaign={Campaign.Current.GetHashCode()}|hero={hero?.StringId}|party={party?.StringId}|clan={hero?.Clan?.StringId}" +
            $"|players={players.Players.Count}|localConnected={players.IsConnected(local)}|localPeer={players.TryGetPeer(controller.ControllerId, out _)}" +
            $"|localHeroRegistrations={players.Players.Count(p => p.HeroId == local?.HeroId)}|localPartyRegistrations={players.Players.Count(p => p.MobilePartyId == local?.MobilePartyId)}" +
            $"|localClanRegistrations={players.Players.Count(p => p.ClanId == local?.ClanId)}|position={party?.Position}|hours={CampaignTime.Now.ToHours}" +
            $"|ticks={Campaign.Current.CurrentTickCount}|frames={Interlocked.Read(ref ListenAuthorityTickEvidence.Frames)}|realTicks={Interlocked.Read(ref ListenAuthorityTickEvidence.RealTicks)}" +
            $"|duplicateRealTicks={Interlocked.Read(ref ListenAuthorityTickEvidence.DuplicateRealTicks)}|failedRealTicks={Interlocked.Read(ref ListenAuthorityTickEvidence.FailedRealTicks)}" +
            $"|aiTicks={Interlocked.Read(ref ListenAuthorityTickEvidence.AiTicks)}|visibleParties={Campaign.Current.MobileParties.Count(p => p.IsVisible)}|parties={Campaign.Current.MobileParties.Count}" +
            $"|sceneEnabled={MapScreen.Instance?._isSceneViewEnabled}");
    }
}
#endif
