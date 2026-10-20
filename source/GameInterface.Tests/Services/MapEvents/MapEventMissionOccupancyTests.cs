using Autofac;
using Autofac.Core;
using Common.Logging;
using GameInterface.Services.MapEvents;
using GameInterface.Services.MapEvents.Patches;
using GameInterface.Services.Missions;
using GameInterface.Services.ObjectManager;
using HarmonyLib;
using Moq;
using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem.MapEvents;
using Xunit;

namespace GameInterface.Tests.Services.MapEvents;

[Collection(ModInformationRoleCollection.Name)]
public sealed class MapEventMissionOccupancyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegisteredUnclaimedOrSimulationEventNeverResolvesMissionMembership(bool simulation)
    {
        using var fixture = new Fixture();
        if (simulation) Assert.True(ServerBattleModeArbiter.TryClaimSimulation(fixture.Id));
        int resolutions = 0;
        fixture.WithContainer(builder => builder.Register<IMissionMembershipRegistry>(_ =>
        {
            resolutions++;throw new InvalidOperationException("unclaimed membership factory must not run");
        }), () => Assert.False(fixture.Occupied()));
        Assert.Equal(0, resolutions);
        fixture.Objects.Verify(value => value.TryGetId(fixture.Event, out fixture.Id), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClaimedMissionUsesTheOriginalRealOccupiedResult(bool occupied)
    {
        using var fixture = new Fixture();Assert.True(ServerBattleModeArbiter.TryClaimMission(fixture.Id));
        var membership = new Mock<IMissionMembershipRegistry>(MockBehavior.Strict);
        membership.Setup(value => value.IsInstanceOccupied(fixture.Id)).Returns(occupied);
        int resolutions = 0;
        fixture.WithContainer(builder => builder.Register<IMissionMembershipRegistry>(_ => { resolutions++;return membership.Object; }),
            () => Assert.Equal(occupied, fixture.Occupied()));
        Assert.Equal(1, resolutions);membership.Verify(value => value.IsInstanceOccupied(fixture.Id), Times.Once);
        membership.VerifyNoOtherCalls();
    }

    [Fact]
    public void MissingRegisteredIdRejectsBeforeEvenAClaimedMissionFactory()
    {
        using var fixture = new Fixture(false);Assert.True(ServerBattleModeArbiter.TryClaimMission(fixture.Id));
        int resolutions = 0;
        fixture.WithContainer(builder => builder.Register<IMissionMembershipRegistry>(_ =>
        { resolutions++;throw new InvalidOperationException("missing id must reject first"); }), () => Assert.False(fixture.Occupied()));
        Assert.Equal(0, resolutions);
    }

    [Fact]
    public void ClaimedMissionMissingMembershipRetainsTheExistingVisibleResolveFailure()
    {
        using var fixture = new Fixture();Assert.True(ServerBattleModeArbiter.TryClaimMission(fixture.Id));
        var logs = new ConcurrentQueue<string>();Action<string> capture = logs.Enqueue;OutputSinkManager.AddLogCallback(capture);
        try
        {
            fixture.WithContainer(_ => { }, () => Assert.False(fixture.Occupied()));
            Assert.Contains(logs, entry => entry.Contains(nameof(IMissionMembershipRegistry)));
        }
        finally { OutputSinkManager.RemoveLogCallback(capture); }
    }

    [Fact]
    public void ClaimedMissionFactoryFailureIsNotTurnedIntoAnEmptyMembershipResult()
    {
        using var fixture = new Fixture();Assert.True(ServerBattleModeArbiter.TryClaimMission(fixture.Id));
        var original = new InvalidOperationException("real membership resolution failure");
        fixture.WithContainer(builder => builder.Register<IMissionMembershipRegistry>(_ => throw original), () =>
        {
            var invocation = Assert.Throws<TargetInvocationException>(() => fixture.Occupied());
            var resolution = Assert.IsType<DependencyResolutionException>(invocation.InnerException);
            Exception? cause = resolution;while (cause?.InnerException != null) cause = cause.InnerException;
            Assert.Same(original, cause);
        });
    }

    private sealed class Fixture : IDisposable
    {
        internal string Id = "mission-occupancy-" + Guid.NewGuid().ToString("N");
        internal readonly MapEvent Event = (MapEvent)RuntimeHelpers.GetUninitializedObject(typeof(MapEvent));
        internal readonly Mock<IObjectManager> Objects = new();
        internal Fixture(bool registered = true) { Objects.Setup(value => value.TryGetId(Event, out Id)).Returns(registered); }
        internal bool Occupied() => (bool)AccessTools.Method(typeof(MapEventPatches), "HasOccupiedBattleMission").Invoke(null, new object[] { Event })!;
        internal void WithContainer(Action<ContainerBuilder> configure, Action action)
        {
            ContainerProvider.TryGetContainer(out var previous);var builder = new ContainerBuilder();
            builder.RegisterInstance(Objects.Object).As<IObjectManager>();configure(builder);
            using var scope = builder.Build();ContainerProvider.SetContainer(scope);
            try { action(); }
            finally { if (previous != null) ContainerProvider.SetContainer(previous);else ContainerProvider.Clear(); }
        }
        public void Dispose() => ServerBattleModeArbiter.Release(Id);
    }
}
