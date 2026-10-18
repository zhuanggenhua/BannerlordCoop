using Common;
using GameInterface.Services.MobileParties.Extensions;
using GameInterface.Services.Players;
using System;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.CampaignAuthority;

/// <summary>
/// Resolves who may advance shared campaign simulation while preserving the legacy
/// dedicated/listen authority behavior until a confirmed campaign lease is available.
/// </summary>
internal static class CampaignSimulationAuthorityPolicy
{
    /// <summary>
    /// Shared simulation uses the confirmed campaign lease when one is known. Before that,
    /// retain the existing process-role behavior so a session can initialize normally.
    /// </summary>
    internal static bool CanRunWorldSimulation()
    {
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

    /// <summary>
    /// The authority runs world AI. An observer also updates its own player's navigation,
    /// which CheckMainPartyNeedsUpdate needs to turn a map order into a movement target.
    /// </summary>
    internal static bool CanTickParty(MobileParty party)
    {
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

    internal static bool ShouldRunWorldSimulation(
        bool authorityKnown,
        bool localAuthority,
        bool legacyIsServer) => authorityKnown ? localAuthority : legacyIsServer;

    internal static bool ShouldTickParty(
        bool authorityKnown,
        bool localAuthority,
        bool legacyPartyControl) => authorityKnown ? localAuthority : legacyPartyControl;
}
