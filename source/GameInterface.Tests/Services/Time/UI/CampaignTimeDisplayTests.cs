using GameInterface.Services.Heroes.Enum;
using GameInterface.Services.Time.UI;
using GameInterface.Services.Time.Interfaces;
using Xunit;

namespace GameInterface.Tests.Services.Time.UI;

public class CampaignTimeDisplayTests
{
    [Theory]
    [InlineData(TimeControlEnum.Play_1x)]
    [InlineData(TimeControlEnum.Play_2x)]
    public void WaitingForFinalRemoteCheckpointDisplaysPauseUntilObserverHoldIsCancelled(TimeControlEnum selectedMode)
    {
        var identity = new SessionIdentity();
        var lease = new AuthorityLease("room", "writer-b", "b", 5);
        Assert.True(identity.TryApply(lease));
        var clock = new MapTimeTrackerInterface();
        clock.SetObserverClockSuspended(true);
        Assert.True(CampaignTimeDisplay.IsSimulationSuspended(identity, clock));
        Assert.Equal(TimeControlEnum.Pause, CampaignTimeDisplay.GetEffectiveMode(selectedMode, identity, clock));
        Assert.Equal(lease, identity.CurrentLease);
        Assert.False(identity.IsLocallyFrozen(lease));

        clock.SetObserverClockSuspended(false);
        Assert.False(CampaignTimeDisplay.IsSimulationSuspended(identity, clock));
        Assert.Equal(selectedMode, CampaignTimeDisplay.GetEffectiveMode(selectedMode, identity, clock));
    }

    [Theory]
    [InlineData(TimeControlEnum.Play_1x)]
    [InlineData(TimeControlEnum.Play_2x)]
    public void ConfirmedAllAwayPauseOverridesDisplayAndResumeRestoresSelectedMode(TimeControlEnum mode)
    {
        var identity = new SessionIdentity();
        Assert.True(identity.TryApply(new AuthorityLease("room", "paused", "", 2, isPaused: true)));
        Assert.Equal(TimeControlEnum.Pause, CampaignTimeDisplay.GetEffectiveMode(mode, identity));

        Assert.True(identity.TryApply(new AuthorityLease("room", "next-writer", "b", 3)));
        Assert.Equal(mode, CampaignTimeDisplay.GetEffectiveMode(mode, identity));
    }

    [Fact]
    public void FrozenWriterShowsPauseUntilFreezeIsCancelled()
    {
        var identity = new SessionIdentity();
        var lease = new AuthorityLease("room", "writer-a", "a", 1);
        Assert.True(identity.TryApply(lease));
        Assert.True(identity.TryFreezeLocalAuthority(lease, 1));
        Assert.Equal(TimeControlEnum.Pause, CampaignTimeDisplay.GetEffectiveMode(TimeControlEnum.Play_1x, identity));

        Assert.True(identity.TryCancelLocalAuthorityFreeze(lease, 1));
        Assert.Equal(TimeControlEnum.Play_1x, CampaignTimeDisplay.GetEffectiveMode(TimeControlEnum.Play_1x, identity));
    }

    [Fact]
    public void NewLeaseClearsOldFreezeFromDisplay()
    {
        var identity = new SessionIdentity();
        var lease = new AuthorityLease("room", "writer-a", "a", 1);
        Assert.True(identity.TryApply(lease));
        Assert.True(identity.TryFreezeLocalAuthority(lease, 1));
        Assert.True(identity.TryApply(new AuthorityLease("room", "writer-b", "b", 2)));

        Assert.Equal(TimeControlEnum.Play_2x, CampaignTimeDisplay.GetEffectiveMode(TimeControlEnum.Play_2x, identity));
    }

    [Theory]
    [InlineData(TimeControlEnum.Pause)]
    [InlineData(TimeControlEnum.Play_1x)]
    [InlineData(TimeControlEnum.Play_2x)]
    public void OfflineUnconfirmedAndRunningSessionsKeepSelectedMode(TimeControlEnum mode)
    {
        Assert.Equal(mode, CampaignTimeDisplay.GetEffectiveMode(mode, null));
        var identity = new SessionIdentity();
        Assert.Equal(mode, CampaignTimeDisplay.GetEffectiveMode(mode, identity));
        Assert.True(identity.TryApply(new AuthorityLease("room", "writer-a", "a", 1)));
        Assert.Equal(mode, CampaignTimeDisplay.GetEffectiveMode(mode, identity));
    }
}
