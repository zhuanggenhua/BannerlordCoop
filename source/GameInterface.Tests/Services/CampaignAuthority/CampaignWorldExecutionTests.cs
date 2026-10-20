using Common;
using Common.Util;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using System;
using System.Threading;
using Xunit;

namespace GameInterface.Tests.Services.CampaignAuthority;

[Collection(ModInformationRoleCollection.Name)]
public class CampaignWorldExecutionTests
{
    [Fact]
    public void RoomHostCannotAcquireWorldPermitWhenOtherPlayerWrites()
    {
        WithReplica(() =>
        {
            var context = Create("a", "b", out _, out _);
            Assert.False(context.TryAcquireWritePermit(out _));
            var writer = Create("b", "b", out _, out _);
            Assert.True(writer.TryAcquireWritePermit(out var permit));
            Assert.True(writer.TryExecuteWorldWrite(permit, () =>
                Assert.Equal(CampaignExecutionPhase.WorldWrite, CampaignExecutionScope.Current)));
        });
    }

    [Fact]
    public void QueuedPermitDoesNotSurviveFreezeOrNewEpoch()
    {
        WithReplica(() =>
        {
            var context = Create("b", "b", out var identity, out var lease);
            Assert.True(context.TryAcquireWritePermit(out var permit));
            Assert.True(identity.TryFreezeLocalAuthority(lease, 1));
            Assert.False(context.TryExecuteWorldWrite(permit, () => throw new Exception("Frozen write")));
            Assert.True(identity.TryApply(new AuthorityLease("room", "next", "b", 4), "a"));
            Assert.False(context.TryExecuteWorldWrite(permit, () => throw new Exception("Stale write")));
            Assert.True(context.TryAcquireWritePermit(out var next));
            Assert.True(context.TryExecuteWorldWrite(next, () => { }));
        });
    }

    [Fact]
    public void ReplayAllowsLeafApplyButCannotAcquireOrUseWriterPermit()
    {
        WithReplica(() =>
        {
            var context = Create("b", "b", out _, out var lease);
            Assert.True(context.TryAcquireWritePermit(out var permit));
            bool applied = false;
            Assert.True(context.TryApplyReplay(lease, () =>
            {
                applied = true;
                Assert.True(AllowedThread.IsThisThreadAllowed());
                Assert.False(context.TryAcquireWritePermit(out _));
                using (CampaignExecutionScope.Enter(CampaignExecutionPhase.WorldWrite))
                    Assert.False(context.TryExecuteWorldWrite(permit, () => throw new Exception("Replay write")));
            }));
            Assert.True(applied);
            Assert.False(AllowedThread.IsThisThreadAllowed());
            Assert.Equal(CampaignExecutionPhase.Unspecified, CampaignExecutionScope.Current);
        });
    }

    [Fact]
    public void ForeignAndDefaultPermitsCannotExecute()
    {
        WithReplica(() =>
        {
            var first = Create("b", "b", out _, out _);
            var second = Create("b", "b", out _, out _);
            Assert.True(first.TryAcquireWritePermit(out var permit));
            Assert.False(second.TryExecuteWorldWrite(permit, () => throw new Exception("Foreign write")));
            Assert.False(first.TryExecuteWorldWrite(default, () => throw new Exception("Default write")));
        });
    }

    [Theory]
    [InlineData(CampaignExecutionPhase.Bootstrap)]
    [InlineData(CampaignExecutionPhase.Replay)]
    [InlineData(CampaignExecutionPhase.Prediction)]
    [InlineData(CampaignExecutionPhase.CheckpointInstall)]
    [InlineData(CampaignExecutionPhase.LocalIntent)]
    public void NonWorldPhaseCannotElevateAnExistingPermit(CampaignExecutionPhase phase)
    {
        WithReplica(() =>
        {
            var context = Create("b", "b", out _, out _);
            Assert.True(context.TryAcquireWritePermit(out var permit));
            using (CampaignExecutionScope.Enter(phase))
            using (CampaignExecutionScope.Enter(CampaignExecutionPhase.WorldWrite))
            {
                Assert.False(context.TryAcquireWritePermit(out _));
                Assert.False(context.TryExecuteWorldWrite(permit, () => throw new Exception("Elevated write")));
            }
        });
    }

    [Fact]
    public void FailedReplayRestoresBothScopesAndIsNotReportedAsSuccess()
    {
        WithReplica(() =>
        {
            var context = Create("a", "b", out _, out var lease);
            Assert.Throws<InvalidOperationException>(() => context.TryApplyReplay(lease,
                () => throw new InvalidOperationException("Native apply failed")));
            Assert.False(AllowedThread.IsThisThreadAllowed());
            Assert.Equal(CampaignExecutionPhase.Unspecified, CampaignExecutionScope.Current);
        });
    }

    [Fact]
    public void ProvenanceDoesNotPropagateToWorkerAndNestedScopesCannotElevate()
    {
        using (CampaignExecutionScope.Enter(CampaignExecutionPhase.Prediction))
        using (CampaignExecutionScope.Enter(CampaignExecutionPhase.WorldWrite))
        {
            Assert.True(CampaignExecutionScope.IsWorldWriteRestricted);
            var observed = CampaignExecutionPhase.WorldWrite;
            var worker = new Thread(() => observed = CampaignExecutionScope.Current);
            worker.Start();
            worker.Join();
            Assert.Equal(CampaignExecutionPhase.Unspecified, observed);
        }
        Assert.False(CampaignExecutionScope.IsWorldWriteRestricted);
    }

    private static CampaignAuthorityContext Create(string local, string writer,
        out SessionIdentity identity, out AuthorityLease lease)
    {
        identity = new SessionIdentity();
        lease = new AuthorityLease("room", "authority", writer, 3);
        Assert.True(identity.TryApply(lease, "a"));
        var controller = new ControllerIdProvider();
        controller.SetControllerId(local);
        return new CampaignAuthorityContext(identity, controller);
    }

    private static void WithReplica(Action action)
    {
        var previous = ModInformation.SessionMode;
        try { ModInformation.SessionMode = SessionMode.RemoteReplica; action(); }
        finally { ModInformation.SessionMode = previous; }
    }
}
