using Common;
using Common.Util;
using System;

namespace GameInterface.Services.CampaignAuthority;

/// <summary>World writes and received-state application have separate capabilities.</summary>
public interface ICampaignWorldExecution
{
    bool TryAcquireWritePermit(out CampaignWritePermit permit);
    bool TryExecuteWorldWrite(CampaignWritePermit permit, Action action);
    bool TryApplyReplay(AuthorityLease expectedLease, Action action);
}

/// <summary>A lease-bound permit, valid only at the context that issued it.</summary>
public readonly struct CampaignWritePermit
{
    internal CampaignWritePermit(CampaignAuthorityContext issuer, AuthorityLease lease)
    {
        Issuer = issuer;
        Lease = lease;
    }

    internal CampaignAuthorityContext Issuer { get; }
    public AuthorityLease Lease { get; }
    public bool IsValid => Issuer != null && Lease.IsValid && !Lease.IsPaused;
}

public sealed partial class CampaignAuthorityContext : ICampaignWorldExecution
{
    public bool TryAcquireWritePermit(out CampaignWritePermit permit)
    {
        permit = default;
        if (AllowedThread.IsThisThreadAllowed() || CampaignExecutionScope.IsWorldWriteRestricted ||
            !TryGetLocalAuthorityLease(out var lease)) return false;
        permit = new CampaignWritePermit(this, lease);
        return true;
    }

    public bool TryExecuteWorldWrite(CampaignWritePermit permit, Action action)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));
        if (!ReferenceEquals(permit.Issuer, this) || !permit.IsValid ||
            AllowedThread.IsThisThreadAllowed() || CampaignExecutionScope.IsWorldWriteRestricted)
            return false;
        // The existing session gate fences this small synchronous operation against freeze/lease change.
        // It must never wrap a wait for engine workers, networking, or a game-thread queue.
        return sessionIdentity.TryExecuteWithLease(permit.Lease, () =>
        {
            if (!TryGetLocalAuthorityLease(out var currentLease) || currentLease != permit.Lease)
                throw new InvalidOperationException("Campaign writer identity changed during a fenced operation.");
            using (CampaignExecutionScope.Enter(CampaignExecutionPhase.WorldWrite)) action();
        });
    }

    public bool TryApplyReplay(AuthorityLease expectedLease, Action action)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));
        if (!expectedLease.IsValid || expectedLease.IsPaused) return false;
        return sessionIdentity.TryExecuteWithLease(expectedLease, () =>
        {
            using (CampaignExecutionScope.Enter(CampaignExecutionPhase.Replay))
            using (new AllowedThread()) action();
        });
    }
}
