using Common.Logging;
using Serilog;
using System;
using TaleWorlds.CampaignSystem;
using GameInterface.Services.CampaignAuthority.Lifecycle;

namespace GameInterface.Services.CampaignAuthority;

public interface ICampaignAsyncTaskDrain
{
    bool TryDrain();
}

internal sealed class CampaignAsyncTaskDrain : ICampaignAsyncTaskDrain
{
    private static readonly ILogger Logger = LogManager.GetLogger<CampaignAsyncTaskDrain>();
    private readonly ICampaignLifetimeCompletionDrain lifetimeCompletion;

    public CampaignAsyncTaskDrain(ICampaignLifetimeCompletionDrain lifetimeCompletion = null) =>
        this.lifetimeCompletion = lifetimeCompletion;
    public bool TryDrain() => TryDrain(Campaign.Current);

    internal bool TryDrain(Campaign campaign)
    {
        if (campaign == null) return false;
        try
        {
            if (lifetimeCompletion?.IsComplete == false) return false;
            // RealTick waits for this task at the beginning of the next frame. A handoff can
            // arrive before that frame, so drain it while its original lease still permits writes.
            campaign.WaitAsyncTasks();
            campaign.CampaignLateAITickTask = null;
            return lifetimeCompletion?.IsComplete != false;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not drain native campaign AI task; authority transfer is refused");
            return false;
        }
    }
}
