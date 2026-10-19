using Common.Logging;
using Serilog;
using System;
using TaleWorlds.CampaignSystem;
using GameInterface.Services.CampaignAuthority.Lifecycle;
using GameInterface.Services.ObjectReadiness;

namespace GameInterface.Services.CampaignAuthority;

public interface ICampaignAsyncTaskDrain
{
    bool TryDrain();
}

internal sealed class CampaignAsyncTaskDrain : ICampaignAsyncTaskDrain
{
    private static readonly ILogger Logger = LogManager.GetLogger<CampaignAsyncTaskDrain>();
    private readonly ICampaignLifetimeCompletionDrain lifetimeCompletion;
    private readonly ICampaignObjectGraphReadiness graphReadiness;

    public CampaignAsyncTaskDrain(ICampaignLifetimeCompletionDrain lifetimeCompletion = null,
        ICampaignObjectGraphReadiness graphReadiness = null)
    {
        this.lifetimeCompletion = lifetimeCompletion;
        this.graphReadiness = graphReadiness;
    }
    public bool TryDrain() => TryDrain(Campaign.Current);

    internal bool TryDrain(Campaign campaign)
    {
        if (campaign == null) return false;
        try
        {
            if (!Ready()) return false;
            // RealTick waits for this task at the beginning of the next frame. A handoff can
            // arrive before that frame, so drain it while its original lease still permits writes.
            campaign.WaitAsyncTasks();
            campaign.CampaignLateAITickTask = null;
            return Ready();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not drain native campaign AI task; authority transfer is refused");
            return false;
        }
    }

    private bool Ready() => lifetimeCompletion?.IsComplete != false &&
        (graphReadiness == null || graphReadiness.TryEnsureReady(out _));
}
