using GameInterface.Services.CampaignAuthority;
using Moq;
using System;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;
using Xunit;

namespace GameInterface.Tests.Services.CampaignAuthority;

public class CampaignAsyncTaskDrainTests
{
    [Fact]
    public void WaitsForTheNativeTaskBeforeClearingIt()
    {
        var campaign = (Campaign)RuntimeHelpers.GetUninitializedObject(typeof(Campaign));
        var task = new Mock<ITask>();
        bool waited = false;
        task.Setup(value => value.Wait()).Callback(() =>
        {
            Assert.Same(task.Object, campaign.CampaignLateAITickTask);
            waited = true;
        });
        campaign.CampaignLateAITickTask = task.Object;
        var service = new CampaignAsyncTaskDrain();
        Assert.True(service.TryDrain(campaign));
        Assert.True(waited);
        Assert.Null(campaign.CampaignLateAITickTask);
        Assert.True(service.TryDrain(campaign));
        task.Verify(value => value.Wait(), Times.Once);
    }

    [Fact]
    public void FailedWaitDoesNotDiscardTheTaskOrReportSuccess()
    {
        var campaign = (Campaign)RuntimeHelpers.GetUninitializedObject(typeof(Campaign));
        var task = new Mock<ITask>();
        task.Setup(value => value.Wait()).Throws(new InvalidOperationException("AI failed"));
        campaign.CampaignLateAITickTask = task.Object;
        Assert.False(new CampaignAsyncTaskDrain().TryDrain(campaign));
        Assert.Same(task.Object, campaign.CampaignLateAITickTask);
    }
}
