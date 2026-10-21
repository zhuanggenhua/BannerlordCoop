using Common;
using Common.Util;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.MobileParties.Extensions;
using GameInterface.Services.Players;
using System;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.CampaignAuthority;

/// <summary>
/// Resolves who may advance shared campaign simulation while preserving the legacy
/// dedicated/listen authority behavior until a confirmed campaign lease is available.
/// </summary>
public static class CampaignSimulationAuthorityPolicy
{
    internal static bool CanInitializeUnregisteredNativeObject(bool registered) =>
        !registered && (CampaignExecutionScope.Current == CampaignExecutionPhase.Bootstrap ||
            CampaignExecutionScope.Current == CampaignExecutionPhase.CheckpointInstall);

    /// <summary>
    /// Shared simulation uses the confirmed campaign lease when one is known. Before that,
    /// retain the existing process-role behavior so a session can initialize normally.
    /// </summary>
    internal static bool CanRunWorldSimulation()
    {
        if (!CanEnterWorldCallback()) return false;
        return CanPublishAppliedWorldResult();
    }

    /// <summary>
    /// Publishing an already executed result does not authorize simulation. AllowedThread alone
    /// neither grants authority nor identifies replay; receive paths must carry Replay provenance.
    /// </summary>
    public static bool CanPublishAppliedWorldResult()
    {
        if (CampaignExecutionScope.IsWorldWriteRestricted) return false;
        bool hasAuthorityContext = ContainerProvider.TryResolve<ICampaignAuthorityContext>(out var authorityContext);
        if (hasAuthorityContext)
        {
            bool localAuthority = authorityContext.TryGetLocalAuthorityLease(out var lease);
            if (lease.IsValid)
                return ShouldRunWorldSimulation(
                    authorityKnown: true,
                    localAuthority: localAuthority,
                    legacyIsServer: false);
        }

        return ShouldRunWorldSimulation(
            authorityKnown: false,
            localAuthority: false,
            legacyIsServer: ModInformation.IsServer);
    }

    public static bool CanSendWorldResult() =>
        CanPublishAppliedWorldResult() &&
        ContainerProvider.TryResolve<ICampaignAuthorityContext>(out var context) && context.IsAuthorityKnown;

    /// <summary>
    /// The authority runs world AI. An observer also updates its own player's navigation,
    /// which CheckMainPartyNeedsUpdate needs to turn a map order into a movement target.
    /// </summary>
    internal static bool CanTickParty(MobileParty party)
    {
        if (!CanEnterWorldCallback()) return false;
        bool hasAuthorityContext = ContainerProvider.TryResolve<ICampaignAuthorityContext>(out var authorityContext);
        if (hasAuthorityContext)
        {
            bool localAuthority = authorityContext.TryGetLocalAuthorityLease(out var lease);
            if (lease.IsValid)
            {
                if (lease.IsPaused) return false;
                if (localAuthority) return true;
                return PlayerManager.TryGetControlledObjectInfo(party, out var control) && control.IsControlled &&
                    !string.Equals(control.ObjectControllerId, lease.AuthorityControllerId, StringComparison.Ordinal);
            }
        }

        return ShouldTickParty(
            authorityKnown: false,
            localAuthority: false,
            legacyPartyControl: party.IsControlledByThisInstance());
    }

    internal static bool CanEnterWorldCallback() =>
        !AllowedThread.IsThisThreadAllowed() &&
        !CampaignExecutionScope.IsWorldWriteRestricted &&
        CampaignExecutionScope.Current != CampaignExecutionPhase.Bootstrap;

    internal static bool ShouldSuppressPlayerInitiative(MobileParty party)
    {
        if (party == null || ModInformation.IsDedicatedAuthority ||
            !PlayerManager.TryGetControlledObjectInfo(party, out var control) || control == null) return false;
        if (!ContainerProvider.TryResolve<ICampaignAuthorityContext>(out var context) ||
            !context.TryGetCurrentLease(out var lease) || !lease.IsValid ||
            !ContainerProvider.TryResolve<ISessionIdentity>(out var identity) ||
            !identity.TryGetConfirmedLease(out var confirmed) || confirmed != lease ||
            !ContainerProvider.TryResolve<IPlayerManager>(out var players) ||
            !ContainerProvider.TryResolve<IObjectManager>(out var objects) ||
            !MatchesRegisteredPlayerParty(players, objects, control.ObjectControllerId, party)) return false;

        return players.TryGetPlayer(identity.RoomHostControllerId, out var host) && host != null &&
            objects.TryGetObject<MobileParty>(host.MobilePartyId, out var hostParty) &&
            MatchesRegisteredPlayerParty(players, objects, identity.RoomHostControllerId, hostParty);
    }

    private static bool MatchesRegisteredPlayerParty(IPlayerManager players, IObjectManager objects,
        string controllerId, MobileParty party) =>
        party != null && players.TryGetPlayer(controllerId, out var player) && player != null &&
        player.ControllerId == controllerId &&
        objects.TryGetObject<MobileParty>(player.MobilePartyId, out var registered) &&
        ReferenceEquals(registered, party) && PlayerManager.TryGetControlledObjectInfo(party, out var control) && control != null &&
        control.ObjectControllerId == controllerId;

    internal static bool ShouldRunWorldSimulation(
        bool authorityKnown,
        bool localAuthority,
        bool legacyIsServer) => authorityKnown ? localAuthority : legacyIsServer;

    internal static bool ShouldTickParty(
        bool authorityKnown,
        bool localAuthority,
        bool legacyPartyControl) => authorityKnown ? localAuthority : legacyPartyControl;
}
