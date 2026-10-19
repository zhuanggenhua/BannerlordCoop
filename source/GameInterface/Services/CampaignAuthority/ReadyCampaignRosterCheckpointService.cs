using Common;
using GameInterface.Services.CampaignAuthority.Lifecycle;
using GameInterface.Services.ObjectReadiness;

namespace GameInterface.Services.CampaignAuthority;

internal sealed class ReadyCampaignRosterCheckpointService : ICampaignRosterCheckpointService
{
    private readonly ICampaignRosterCheckpointService inner;
    private readonly ICampaignObjectGraphReadiness graph;
    private readonly ICampaignLifetimeCompletionDrain lifetime;

    public ReadyCampaignRosterCheckpointService(ICampaignRosterCheckpointService inner,
        ICampaignObjectGraphReadiness graph, ICampaignLifetimeCompletionDrain lifetime)
    {
        this.inner = inner;
        this.graph = graph;
        this.lifetime = lifetime;
    }

    private bool Ready() => lifetime.IsComplete && graph.TryEnsureReady(out _);

    public bool TryCapture(out CampaignRosterCheckpoint checkpoint)
    {
        checkpoint = null;
        if (!Ready() || !inner.TryCapture(out var captured) || !Ready()) return false;
        checkpoint = captured;
        return true;
    }

    public bool TryApply(CampaignRosterCheckpoint checkpoint) =>
        Ready() && inner.TryApply(checkpoint) && Ready();
}
