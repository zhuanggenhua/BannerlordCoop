using Common;
using GameInterface.Services.Entity;
using System;

namespace GameInterface.Services.CampaignAuthority;

public interface ICampaignAuthorityContext
{
    bool IsAuthorityKnown { get; }
    bool IsLocalCampaignAuthority { get; }
    bool TryGetCurrentLease(out AuthorityLease lease);
    bool TryGetLocalAuthorityLease(out AuthorityLease lease);
    bool TryExecuteWithCurrentLease(AuthorityLease lease, Action action);
}

public sealed class CampaignAuthorityContext : ICampaignAuthorityContext
{
    private readonly ISessionIdentity sessionIdentity;
    private readonly IControllerIdProvider controllerIdProvider;

    public CampaignAuthorityContext(
        ISessionIdentity sessionIdentity,
        IControllerIdProvider controllerIdProvider)
    {
        if (sessionIdentity == null) throw new ArgumentNullException(nameof(sessionIdentity));
        if (controllerIdProvider == null) throw new ArgumentNullException(nameof(controllerIdProvider));

        this.sessionIdentity = sessionIdentity;
        this.controllerIdProvider = controllerIdProvider;
    }

    public bool IsAuthorityKnown
    {
        get => sessionIdentity.TryGetConfirmedLease(out _);
    }

    public bool IsLocalCampaignAuthority
    {
        get
        {
            return TryGetLocalAuthorityLease(out _);
        }
    }

    public bool TryGetCurrentLease(out AuthorityLease lease) =>
        sessionIdentity.TryGetConfirmedLease(out lease);

    public bool TryExecuteWithCurrentLease(AuthorityLease lease, Action action)
    {
        if (!lease.IsValid || lease.IsPaused) return false;
        return sessionIdentity.TryExecuteWithLease(lease, action);
    }

    public bool TryGetLocalAuthorityLease(out AuthorityLease lease)
    {
        if (!sessionIdentity.TryGetConfirmedLease(out lease)) return false;
        if (lease.IsPaused) return false;
        if (ModInformation.IsDedicatedAuthority) return true;

        string controllerId = controllerIdProvider.ControllerId;
        return !string.IsNullOrWhiteSpace(controllerId) &&
            string.Equals(controllerId, lease.AuthorityControllerId, StringComparison.Ordinal);
    }
}
