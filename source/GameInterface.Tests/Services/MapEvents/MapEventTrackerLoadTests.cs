using Autofac;
using Common;
using Common.Util;
using GameInterface.Policies;
using GameInterface.Services.MapEvents.Patches;
using HarmonyLib;
using Moq;
using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using Xunit;

namespace GameInterface.Tests.Services.MapEvents;

[Collection(ModInformationRoleCollection.Name)]
public sealed class MapEventTrackerLoadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeAfterLoadOwnsNullTrackerRepairAndRuntimeFallbackResumes(bool client)
    {
        using var fixture = new Fixture(client);
        fixture.Policy.Setup(value => value.AllowOriginal()).Returns(true);
        Assert.Null(fixture.MapEvent.TroopUpgradeTracker);
        var nativeReplacement = new TroopUpgradeTracker();
        fixture.MapEvent.TroopUpgradeTracker = nativeReplacement;
        Assert.Same(nativeReplacement, fixture.MapEvent.TroopUpgradeTracker);
        fixture.Policy.Setup(value => value.AllowOriginal()).Returns(false);
        fixture.MapEvent.TroopUpgradeTracker = null;
        if (client) Assert.Null(fixture.MapEvent.TroopUpgradeTracker);
        else Assert.NotNull(fixture.MapEvent.TroopUpgradeTracker);
    }

    [Fact]
    public void ReceivedNullReferenceDoesNotAllocateUnregisteredReplacement()
    {
        using var fixture = new Fixture(false);
        fixture.Policy.Setup(value => value.AllowOriginal()).Returns(false);
        using (new AllowedThread()) Assert.Null(fixture.MapEvent.TroopUpgradeTracker);
        Assert.NotNull(fixture.MapEvent.TroopUpgradeTracker);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SessionMode mode = ModInformation.SessionMode;
        private readonly bool hadContainer;
        private readonly ILifetimeScope previous;
        private readonly IContainer container;
        private readonly Harmony harmony = new("tests.map-event-tracker-load");
        public readonly Mock<ISyncPolicy> Policy = new();
        public readonly MapEvent MapEvent = ObjectHelper.SkipConstructor<MapEvent>();

        public Fixture(bool client)
        {
            hadContainer = ContainerProvider.TryGetContainer(out previous);
            ModInformation.SessionMode = client ? SessionMode.RemoteReplica : SessionMode.ListenAuthority;
            var builder = new ContainerBuilder();
            builder.RegisterInstance(Policy.Object).As<ISyncPolicy>();
            container = builder.Build();
            ContainerProvider.SetContainer(container);
            harmony.Patch(AccessTools.PropertyGetter(typeof(MapEvent), nameof(MapEvent.TroopUpgradeTracker)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(MapEventRobustnessPatches), "PostfixTroopUpgradeTracker")));
        }

        public void Dispose()
        {
            harmony.UnpatchAll(harmony.Id);
            ModInformation.SessionMode = mode;
            if (hadContainer) ContainerProvider.SetContainer(previous); else ContainerProvider.Clear();
            container.Dispose();
        }
    }
}
