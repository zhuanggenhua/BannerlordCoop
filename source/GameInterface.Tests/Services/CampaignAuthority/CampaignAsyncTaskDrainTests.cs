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
    public void PreservesReusableNativeTaskAcrossDrainsAndSubsequentInvoke()
    {
        var campaign = (Campaign)RuntimeHelpers.GetUninitializedObject(typeof(Campaign));
        var task = new Mock<ITask>(MockBehavior.Strict);
        int waits = 0;
        task.Setup(value => value.Wait()).Callback(() =>
        {
            Assert.Same(task.Object, campaign.CampaignLateAITickTask);
            waits++;
        });
        task.Setup(value => value.Invoke());
        campaign.CampaignLateAITickTask = task.Object;
        var service = new CampaignAsyncTaskDrain();
        Assert.True(service.TryDrain(campaign));
        Assert.Equal(1, waits);
        Assert.Same(task.Object, campaign.CampaignLateAITickTask);
        task.Verify(value => value.Invoke(), Times.Never);
        campaign.CampaignLateAITickTask.Invoke();
        Assert.True(service.TryDrain(campaign));
        Assert.Same(task.Object, campaign.CampaignLateAITickTask);
        Assert.Equal(2, waits);
        task.Verify(value => value.Wait(), Times.Exactly(2));
        task.Verify(value => value.Invoke(), Times.Once);
        task.VerifyNoOtherCalls();
    }

    [Fact]
    public void FailedWaitDoesNotDiscardTheTaskOrReportSuccess()
    {
        var campaign = (Campaign)RuntimeHelpers.GetUninitializedObject(typeof(Campaign));
        var task = new Mock<ITask>(MockBehavior.Strict);
        task.Setup(value => value.Wait()).Throws(new InvalidOperationException("AI failed"));
        campaign.CampaignLateAITickTask = task.Object;
        Assert.False(new CampaignAsyncTaskDrain().TryDrain(campaign));
        Assert.Same(task.Object, campaign.CampaignLateAITickTask);
        task.Verify(value => value.Wait(), Times.Once);
        task.VerifyNoOtherCalls();
    }

    [Fact]
    public void MissingCampaignCannotReportDrained()
    {
        Assert.False(new CampaignAsyncTaskDrain().TryDrain(null!));
    }

    [Fact]
    public void CampaignWithoutNativeTaskRemainsWithoutTask()
    {
        var campaign = (Campaign)RuntimeHelpers.GetUninitializedObject(typeof(Campaign));
        Assert.True(new CampaignAsyncTaskDrain().TryDrain(campaign));
        Assert.Null(campaign.CampaignLateAITickTask);
    }
}
