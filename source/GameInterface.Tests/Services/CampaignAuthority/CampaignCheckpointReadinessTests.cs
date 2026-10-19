using Common;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.CampaignAuthority.Lifecycle;
using GameInterface.Services.ObjectReadiness;
using Moq;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;
using Xunit;

namespace GameInterface.Tests.Services.CampaignAuthority;

public class CampaignCheckpointReadinessTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void PendingCompletionNeverDelegatesCaptureOrApply(bool graphReady, bool lifetimeReady)
    {
        var inner = new Mock<ICampaignRosterCheckpointService>(MockBehavior.Strict);
        var service = new ReadyCampaignRosterCheckpointService(inner.Object, Graph(graphReady).Object,
            Lifetime(lifetimeReady).Object);
        Assert.False(service.TryCapture(out var checkpoint));
        Assert.Null(checkpoint);
        Assert.False(service.TryApply(new CampaignRosterCheckpoint(System.Array.Empty<CampaignRosterState>())));
        inner.VerifyNoOtherCalls();
    }

    [Fact]
    public void ReadyCaptureAndApplyKeepOriginalCheckpointAndRunOnce()
    {
        var checkpoint = new CampaignRosterCheckpoint(System.Array.Empty<CampaignRosterState>());
        var inner = new Mock<ICampaignRosterCheckpointService>(MockBehavior.Strict);
        inner.Setup(value => value.TryCapture(out checkpoint)).Returns(true);
        inner.Setup(value => value.TryApply(checkpoint)).Returns(true);
        var service = new ReadyCampaignRosterCheckpointService(inner.Object, Graph(true).Object, Lifetime(true).Object);
        Assert.True(service.TryCapture(out var captured));
        Assert.Same(checkpoint, captured);
        Assert.True(service.TryApply(captured));
        inner.Verify(value => value.TryCapture(out checkpoint), Times.Once);
        inner.Verify(value => value.TryApply(checkpoint), Times.Once);
        inner.VerifyNoOtherCalls();
    }

    [Fact]
    public void StateBecomingIncompleteDuringCaptureDoesNotReturnAnAckCheckpoint()
    {
        var checkpoint = new CampaignRosterCheckpoint(System.Array.Empty<CampaignRosterState>());
        var graph = Graph(true);
        var lifetime = Lifetime(true);
        var inner = new Mock<ICampaignRosterCheckpointService>();
        inner.Setup(value => value.TryCapture(out checkpoint)).Callback(() => lifetime.SetupGet(value => value.IsComplete).Returns(false)).Returns(true);
        var service = new ReadyCampaignRosterCheckpointService(inner.Object, graph.Object, lifetime.Object);
        Assert.False(service.TryCapture(out var captured));
        Assert.Null(captured);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void NativeDrainRetainsTaskWhenGraphOrLifetimeIsPending(bool graphReady, bool lifetimeReady)
    {
        var campaign = (Campaign)RuntimeHelpers.GetUninitializedObject(typeof(Campaign));
        var task = new Mock<ITask>();
        campaign.CampaignLateAITickTask = task.Object;
        var service = new CampaignAsyncTaskDrain(Lifetime(lifetimeReady).Object, Graph(graphReady).Object);
        Assert.False(service.TryDrain(campaign));
        task.Verify(value => value.Wait(), Times.Never);
        Assert.Same(task.Object, campaign.CampaignLateAITickTask);
    }

    [Fact]
    public void NativeTaskCompletionCannotCertifyANewlyIncompleteGraph()
    {
        var campaign = (Campaign)RuntimeHelpers.GetUninitializedObject(typeof(Campaign));
        var graph = Graph(true);
        var task = new Mock<ITask>();
        task.Setup(value => value.Wait()).Callback(() => SetGraph(graph, false));
        campaign.CampaignLateAITickTask = task.Object;
        Assert.False(new CampaignAsyncTaskDrain(Lifetime(true).Object, graph.Object).TryDrain(campaign));
        task.Verify(value => value.Wait(), Times.Once);
        Assert.Null(campaign.CampaignLateAITickTask);
    }

    private static Mock<ICampaignObjectGraphReadiness> Graph(bool ready)
    {
        var graph = new Mock<ICampaignObjectGraphReadiness>();
        SetGraph(graph, ready);
        return graph;
    }

    private static void SetGraph(Mock<ICampaignObjectGraphReadiness> graph, bool ready)
    {
        string reason = ready ? null : "tracked:creation-pending";
        graph.Setup(value => value.TryEnsureReady(out reason)).Returns(ready);
    }

    private static Mock<ICampaignLifetimeCompletionDrain> Lifetime(bool ready)
    {
        var lifetime = new Mock<ICampaignLifetimeCompletionDrain>();
        lifetime.SetupGet(value => value.IsComplete).Returns(ready);
        return lifetime;
    }
}
