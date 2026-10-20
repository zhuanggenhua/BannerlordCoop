using Common;
using Common.Messaging;
using Common.Tests.Utils;
using GameInterface.CoopSessionData;
using GameInterface.CoopSessionData.Save.Data;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.CampaignAuthority.NativeState;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using Moq;
using Serilog;
using System;
using Xunit;
using NativeObjects = GameInterface.Services.ObjectManager.ObjectManager;

namespace GameInterface.Tests.Services.CampaignAuthority.NativeState;

public class SessionNativeStateIntegrationTests
{
    [Fact]
    public void SessionMetadataIncludesNativePayloadAndInstallsOnlyAfterSuccessfulPreparation()
    {
        var fixture = new Fixture();
        var native = new CampaignNativeBehaviorCheckpoint(new CampaignAgingBehaviorState(42,
            Array.Empty<CampaignHeroIntState>(), Array.Empty<CampaignHeroIntState>()));
        fixture.Native.Setup(value => value.TryCapture(out native)).Returns(true);
        int installed = 0;
        Action install = () => installed++;
        fixture.Native.Setup(value => value.TryPrepare(native, out install)).Returns(true);
        Assert.True(fixture.Service.TryCapture("campaign", out var checkpoint));
        Assert.Same(native, checkpoint.NativeBehaviors);
        Assert.True(fixture.Service.TryApply(checkpoint, "campaign"));
        Assert.Equal(1, installed);
    }

    [Fact]
    public void NativePreparationFailureLeavesBothSessionAndNativeStateUnchanged()
    {
        var fixture = new Fixture();
        var native = new CampaignNativeBehaviorCheckpoint(null!);
        fixture.Native.Setup(value => value.TryCapture(out native)).Returns(true);
        Assert.True(fixture.Service.TryCapture("campaign", out var checkpoint));
        Action install = null!;
        fixture.Native.Setup(value => value.TryPrepare(native, out install)).Returns(false);
        var original = fixture.Provider.CoopSession;
        Assert.False(fixture.Service.TryApply(checkpoint, "campaign"));
        Assert.Same(original, fixture.Provider.CoopSession);
    }

    [Fact]
    public void LeaseReplacementAfterPreparationPreventsNativeAndSessionInstallation()
    {
        var fixture = new Fixture();
        var native = new CampaignNativeBehaviorCheckpoint(null!);
        fixture.Native.Setup(value => value.TryCapture(out native)).Returns(true);
        Assert.True(fixture.Service.TryCapture("campaign", out var checkpoint));
        bool installed = false;
        Action install = () => installed = true;
        fixture.Native.Setup(value => value.TryPrepare(native, out install)).Callback(() =>
            Assert.True(fixture.Identity.TryApply(new AuthorityLease("room", "next", "b", 4), "a"))).Returns(true);
        var original = fixture.Provider.CoopSession;
        Assert.False(fixture.Service.TryApply(checkpoint, "campaign"));
        Assert.False(installed);
        Assert.Same(original, fixture.Provider.CoopSession);
    }

    [Fact]
    public void ProductionEntryRefusesMissingNativeAdapterRatherThanIssuingAnIncompleteCheckpoint()
    {
        var fixture = new Fixture();
        var legacyMetadataOnly = new CampaignSessionCheckpointService(fixture.Objects, fixture.Players.Object,
            fixture.Provider, fixture.Identity, fixture.Broker);
        Assert.False(legacyMetadataOnly.TryCapture(out var checkpoint));
        Assert.Null(checkpoint);
        Assert.False(legacyMetadataOnly.TryApply(new CampaignSessionCheckpoint("campaign", "{}")));
    }

    private sealed class Fixture
    {
        internal readonly NativeObjects Objects = new(Mock.Of<ILogger>());
        internal readonly Mock<IPlayerManager> Players = new();
        internal readonly CoopSessionProvider Provider = new();
        internal readonly TestMessageBroker Broker = new();
        internal readonly SessionIdentity Identity = new();
        internal readonly Mock<ICampaignNativeBehaviorCheckpointService> Native = new();
        internal readonly CampaignSessionCheckpointService Service;
        internal Fixture()
        {
            Assert.True(Identity.TryApply(new AuthorityLease("room", "active", "a", 3), "a"));
            Players.SetupGet(value => value.Players).Returns(Array.Empty<Player>());
            Service = new CampaignSessionCheckpointService(Objects, Players.Object, Provider, Identity, Broker, Native.Object);
        }
    }
}
