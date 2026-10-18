using Autofac;
using Common;
using Common.Util;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.MapTracks.Interfaces;
using GameInterface.Services.MapTracks.Patches;
using HarmonyLib;
using Moq;
using System;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using Xunit;

namespace GameInterface.Tests.Services.MapTracks;

[Collection(ModInformationRoleCollection.Name)]
public class MapTracksAuthorityTests
{
    private static int timerCreations;
    static MapTracksAuthorityTests()
    {
        RuntimeHelpers.RunModuleConstructor(typeof(Coop.Tests.Mocks.TestNetwork).Module.ModuleHandle);
    }

    private static bool CreateTimer(CampaignTime triggerPeriod, CampaignTime initialWait, ref MBCampaignEvent __result)
    {
        timerCreations++;
        __result = new MBCampaignEvent("test-track-timer") { TriggerPeriod = triggerPeriod, InitialWait = initialWait };
        return false;
    }

    [Fact]
    public void ObserverLoadRegistersExactlyOneNativeTimerWithoutRunningSavedTrackCleanup()
    {
        var harmony = new Harmony("Coop.Tests.MapTrackTimer." + Guid.NewGuid().ToString("N"));
        var factory = AccessTools.Method(typeof(CampaignPeriodicEventManager), nameof(CampaignPeriodicEventManager.CreatePeriodicEvent));
        var oldMode = ModInformation.SessionMode;
        try
        {
            ModInformation.SessionMode = SessionMode.RemoteReplica;
            timerCreations = 0;
            harmony.Patch(factory, prefix: new HarmonyMethod(typeof(MapTracksAuthorityTests), nameof(CreateTimer)));
            var behavior = ObjectHelper.SkipConstructor<MapTracksCampaignBehavior>();
            Assert.False(MapTracksCampaignBehaviorPatches.GameLoadFinishedPrefix(behavior));
            var timer = behavior._quarterHourlyTick;
            Assert.NotNull(timer);
            Assert.Equal(CampaignTime.Hours(.25f).NumTicks, timer.TriggerPeriod.NumTicks);
            Assert.Equal(CampaignTime.Hours(.1f).NumTicks, timer.InitialWait.NumTicks);
            Assert.Single(timer.handlers);
            Assert.False(MapTracksCampaignBehaviorPatches.GameLoadFinishedPrefix(behavior));
            Assert.Equal(1, timerCreations);
            Assert.Same(timer, behavior._quarterHourlyTick);
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            ModInformation.SessionMode = oldMode;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TrackCallbacksFollowTheLeaseInsteadOfTheRoomTransportRole(bool localAuthority)
    {
        var context = new Mock<ICampaignAuthorityContext>();
        context.Setup(value => value.TryGetLocalAuthorityLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease lease) => { lease = new AuthorityLease("room", "authority", "b", 5); return localAuthority; });
        var tracks = new Mock<IMapTracksCampaignBehaviorInterface>();
        var builder = new ContainerBuilder();
        builder.RegisterInstance(context.Object).As<ICampaignAuthorityContext>();
        builder.RegisterInstance(tracks.Object).As<IMapTracksCampaignBehaviorInterface>();
        using var container = builder.Build();
        bool hadPrevious = ContainerProvider.TryGetContainer(out var previous);
        var oldMode = ModInformation.SessionMode;
        var behavior = ObjectHelper.SkipConstructor<MapTracksCampaignBehavior>();
        try
        {
            ModInformation.SessionMode = localAuthority ? SessionMode.RemoteReplica : SessionMode.ListenAuthority;
            ContainerProvider.SetContainer(container);
            Assert.False(MapTracksCampaignBehaviorPatches.OnHourlyTickPrefix(behavior));
            Assert.False(MapTracksCampaignBehaviorPatches.QuarterHourlyTickPrefix(behavior));
            Assert.Equal(localAuthority, MapTracksCampaignBehaviorPatches.OnHourlyTickPartyPrefix());
            Assert.Equal(localAuthority, MapTracksCampaignBehaviorPatches.OnMobilePartyDestroyedPrefix());
            tracks.Verify(value => value.OnHourlyTick(behavior), localAuthority ? Times.Once() : Times.Never());
            tracks.Verify(value => value.QuarterHourlyTick(behavior), localAuthority ? Times.Once() : Times.Never());
        }
        finally
        {
            ModInformation.SessionMode = oldMode;
            if (hadPrevious) ContainerProvider.SetContainer(previous);
            else ContainerProvider.Clear();
        }
    }
}
