using System;
using System.Threading;

namespace GameInterface.Services.CampaignAuthority;

/// <summary>Execution provenance; this does not grant a campaign-authority lease.</summary>
public enum CampaignExecutionPhase
{
    Unspecified,
    Bootstrap,
    LocalIntent,
    WorldWrite,
    Replay,
    Prediction,
    CheckpointInstall,
    Retirement
}

/// <summary>
/// Synchronous, thread-bound provenance for native callbacks. Queued work must enter a fresh scope
/// after validating its captured lease; an ambient scope is deliberately not an async capability.
/// </summary>
public sealed class CampaignExecutionScope : IDisposable
{
    [ThreadStatic] private static CampaignExecutionScope current;
    private readonly CampaignExecutionScope parent;
    private readonly int threadId;
    private bool disposed;

    private CampaignExecutionScope(CampaignExecutionPhase phase)
    {
        Phase = phase;
        parent = current;
        threadId = Thread.CurrentThread.ManagedThreadId;
        current = this;
    }

    public CampaignExecutionPhase Phase { get; }
    public static CampaignExecutionPhase Current => current?.Phase ?? CampaignExecutionPhase.Unspecified;

    // An inner scope must not turn a receive, prediction or installation scope into world execution.
    public static bool IsWorldWriteRestricted
    {
        get
        {
            for (var scope = current; scope != null; scope = scope.parent)
                if (scope.Phase == CampaignExecutionPhase.Bootstrap ||
                    scope.Phase == CampaignExecutionPhase.Replay ||
                    scope.Phase == CampaignExecutionPhase.Prediction ||
                    scope.Phase == CampaignExecutionPhase.CheckpointInstall ||
                    scope.Phase == CampaignExecutionPhase.LocalIntent)
                    return true;
            return false;
        }
    }

    public static CampaignExecutionScope Enter(CampaignExecutionPhase phase) => new CampaignExecutionScope(phase);

    public void Dispose()
    {
        if (disposed) return;
        if (Thread.CurrentThread.ManagedThreadId != threadId || !ReferenceEquals(current, this))
            throw new InvalidOperationException("Campaign execution scopes must be disposed in order on their entering thread.");
        current = parent;
        disposed = true;
    }
}
