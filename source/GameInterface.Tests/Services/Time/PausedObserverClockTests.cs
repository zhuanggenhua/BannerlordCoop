using Common;
using GameInterface.Services.Time.Interfaces;
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using TaleWorlds.CampaignSystem;
using Xunit;

namespace GameInterface.Tests.Services.Time;

[Collection(ModInformationRoleCollection.Name)]
public class PausedObserverClockTests
{
    static PausedObserverClockTests()
    {
        RuntimeHelpers.RunModuleConstructor(typeof(Coop.Tests.Mocks.TestNetwork).Module.ModuleHandle);
    }

    [Theory]
    [InlineData(CampaignTimeControlMode.Stop, 1200L)]
    [InlineData(CampaignTimeControlMode.Stop, 800L)]
    [InlineData(CampaignTimeControlMode.FastForwardStop, 1200L)]
    public void PausedHeartbeatConvergesExactlyWithoutWaitingForAnotherMapTick(CampaignTimeControlMode mode, long localTicks)
    {
        using var fixture = new ClockFixture(mode, localTicks);
        bool applied = false;
        fixture.Clock.SyncCampaignTime(1000L, 0.25f,
            action => fixture.Identity.TryExecuteWithLease(fixture.Lease, action), () => applied = true);
        GameThread.Run(() => { }, blocking: true);

        Assert.True(applied);
        Assert.Equal(1000L, fixture.Campaign.MapTimeTracker._numTicks);
        Assert.Equal(0L, fixture.Campaign.MapTimeTracker._deltaTimeInTicks);
        Assert.Equal(mode, fixture.Campaign._timeControlMode);
        Assert.False(fixture.Clock.TryConsumeHardSync(out _));
    }

    [Fact]
    public void PausedHeartbeatOlderThanJoinBaselineCannotRewindTheClock()
    {
        using var fixture = new ClockFixture(CampaignTimeControlMode.Stop, 1200L);
        fixture.Clock.CompleteCampaignJoinBaseline(1200L);
        fixture.Clock.SyncCampaignTime(1000L, 0f);
        GameThread.Run(() => { }, blocking: true);
        Assert.Equal(1200L, fixture.Campaign.MapTimeTracker._numTicks);
        Assert.False(fixture.Clock.TryCompleteCampaignJoinCatchUp(1200L, 0L, out _));
    }

    [Fact]
    public void CurrentPausedHeartbeatCompletesJoinCatchUpWithoutAClockTick()
    {
        using var fixture = new ClockFixture(CampaignTimeControlMode.Stop, 1000L);
        fixture.Clock.CompleteCampaignJoinBaseline(1000L);
        fixture.Clock.SyncCampaignTime(1000L, 0f);
        GameThread.Run(() => { }, blocking: true);
        Assert.True(fixture.Clock.TryCompleteCampaignJoinCatchUp(1000L, 0L, out bool refresh));
        Assert.False(refresh);
    }

    [Fact]
    public void RunningObserverKeepsPacingInsteadOfApplyingHeartbeatExactly()
    {
        using var fixture = new ClockFixture(CampaignTimeControlMode.UnstoppablePlay, 1200L);
        fixture.Clock.SyncCampaignTime(1000L, 0f);
        GameThread.Run(() => { }, blocking: true);
        Assert.Equal(1200L, fixture.Campaign.MapTimeTracker._numTicks);
        Assert.True(fixture.Clock.TryConsumeHardSync(out long ticks));
        Assert.Equal(1000L, ticks);
    }

    [Fact]
    public void DelayedPausedHeartbeatFromOldLeaseCannotChangeClockOrNotifyJoin()
    {
        using var fixture = new ClockFixture(CampaignTimeControlMode.Stop, 1200L);
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        GameThread.EnqueueSafe(() =>
        {
            started.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
        });
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        bool applied = false;
        try
        {
            fixture.Clock.SyncCampaignTime(1000L, 0f,
                action => fixture.Identity.TryExecuteWithLease(fixture.Lease, action), () => applied = true);
            Assert.True(fixture.Identity.TryApply(new AuthorityLease("room", "next", "b", 2)));
        }
        finally
        {
            release.Set();
        }
        GameThread.Run(() => { }, blocking: true);
        Assert.False(applied);
        Assert.Equal(1200L, fixture.Campaign.MapTimeTracker._numTicks);
    }

    private sealed class ClockFixture : IDisposable
    {
        private readonly Campaign previousCampaign = Campaign.Current;
        private readonly long previousSecond = CampaignTime.TimeTicksPerSecond;
        private readonly long previousHour = CampaignTime.TimeTicksPerHour;
        public Campaign Campaign { get; } = (Campaign)RuntimeHelpers.GetUninitializedObject(typeof(Campaign));
        public MapTimeTrackerInterface Clock { get; } = new MapTimeTrackerInterface();
        public SessionIdentity Identity { get; } = new SessionIdentity();
        public AuthorityLease Lease { get; } = new AuthorityLease("room", "writer", "a", 1);

        public ClockFixture(CampaignTimeControlMode mode, long ticks)
        {
            Campaign.MapTimeTracker = new MapTimeTracker(new CampaignTime(ticks));
            Campaign._timeControlMode = mode;
            Campaign.Current = Campaign;
            CampaignTime.TimeTicksPerSecond = 10000L;
            CampaignTime.TimeTicksPerHour = 36000000L;
            Assert.True(Identity.TryApply(Lease));
        }

        public void Dispose()
        {
            GameThread.Run(() => { }, blocking: true);
            Campaign.Current = previousCampaign;
            CampaignTime.TimeTicksPerSecond = previousSecond;
            CampaignTime.TimeTicksPerHour = previousHour;
        }
    }
}
