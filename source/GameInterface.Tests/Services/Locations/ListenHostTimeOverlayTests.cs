using Autofac;
using Common;
using Common.Messaging;
using GameInterface.Services.Heroes.Interaces;
using GameInterface.Services.Locations;
using GameInterface.Services.Locations.Messages;
using GameInterface.Services.Locations.Patches;
using GameInterface.Services.Time.UI;
using GameInterface.Services.UI.CoopOptions;
using HarmonyLib;
using Moq;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using TaleWorlds.MountAndBlade;
using Xunit;

namespace GameInterface.Tests.Services.Locations;

[Collection(ModInformationRoleCollection.Name)]
public sealed class ListenHostTimeOverlayTests : IDisposable
{
    private readonly SessionMode previousMode = ModInformation.SessionMode;
    private readonly bool hadContainer;
    private readonly ILifetimeScope previousContainer;
    private readonly IContainer container;
    private int createdViews;
    private int createdLocationControllers;
    private int locationRequests;
    private readonly Action<MessagePayload<PlayerEnteredLocation>> onLocationRequest;

    public ListenHostTimeOverlayTests()
    {
        RuntimeHelpers.RunModuleConstructor(typeof(Coop.Tests.Mocks.TestNetwork).Module.ModuleHandle);
        hadContainer = ContainerProvider.TryGetContainer(out previousContainer);
        var builder = new ContainerBuilder();
        builder.Register(_ =>
        {
            createdViews++;
            return new MissionMapTimeView(Mock.Of<IMessageBroker>(), Mock.Of<ITimeControlInterface>(),
                Mock.Of<ICoopOptionsStore>());
        }).AsSelf().As<ILocationMissionBehavior>().InstancePerDependency();
        builder.Register(_ =>
        {
            createdLocationControllers++;
            return new LocationControllerSentinel();
        }).As<ILocationMissionBehavior>().InstancePerDependency();
        container = builder.Build();
        ContainerProvider.SetContainer(container);
        onLocationRequest = _ => locationRequests++;
        MessageBroker.Instance.Subscribe(onLocationRequest);
    }

    [Fact]
    public void ListenHostAttachesRealViewOnceWithoutResolvingP2pControllersOrRequestingInstance()
    {
        ModInformation.SessionMode = SessionMode.ListenAuthority;
        var mission = NewMission();

        // Both native indoor overload postfixes can report the same newly opened Mission.
        PlayerLocationEntryPatches.Handle("tavern", null!, mission);
        PlayerLocationEntryPatches.Handle("tavern", null!, mission);

        var view = Assert.IsType<MissionMapTimeView>(Assert.Single(mission.MissionBehaviors));
        Assert.Same(view, mission.GetMissionBehavior<MissionMapTimeView>());
        Assert.Same(mission, view.Mission);
        Assert.Equal(1, createdViews);
        Assert.Equal(0, createdLocationControllers);
        Assert.Equal(0, locationRequests);
        Assert.False(LocationMissionTracker.IsLocationMission(mission));
    }

    [Fact]
    public void NextListenMissionGetsItsOwnFreshView()
    {
        ModInformation.SessionMode = SessionMode.ListenAuthority;
        var first = NewMission();
        var second = NewMission();
        PlayerLocationEntryPatches.Handle("tavern", null!, first);
        PlayerLocationEntryPatches.Handle("tavern", null!, second);

        Assert.NotSame(first.GetMissionBehavior<MissionMapTimeView>(), second.GetMissionBehavior<MissionMapTimeView>());
        Assert.Equal(2, createdViews);
        Assert.Equal(0, createdLocationControllers);
    }

    [Fact]
    public void DedicatedAuthorityDoesNotAttachViewOrRequestLocationInstance()
    {
        ModInformation.SessionMode = SessionMode.DedicatedAuthority;
        var mission = NewMission();
        PlayerLocationEntryPatches.Handle("tavern", null!, mission);

        Assert.Empty(mission.MissionBehaviors);
        Assert.Equal(0, createdViews);
        Assert.Equal(0, createdLocationControllers);
        Assert.Equal(0, locationRequests);
    }

    [Fact]
    public void RemoteClientKeepsItsFullLocationCompositionAndDeduplication()
    {
        ModInformation.SessionMode = SessionMode.RemoteReplica;
        var mission = NewMission();
        PlayerLocationEntryPatches.AttachLocationBehaviors(mission);
        PlayerLocationEntryPatches.AttachLocationBehaviors(mission);

        Assert.Equal(2, mission.MissionBehaviors.Count);
        Assert.NotNull(mission.GetMissionBehavior<MissionMapTimeView>());
        Assert.NotNull(mission.GetMissionBehavior<LocationControllerSentinel>());
        Assert.True(LocationMissionTracker.IsLocationMission(mission));
        Assert.Equal(1, createdViews);
        Assert.Equal(1, createdLocationControllers);
    }

    [Fact]
    public void NullMissionDoesNotResolvePresentationOrP2pServices()
    {
        ModInformation.SessionMode = SessionMode.ListenAuthority;
        PlayerLocationEntryPatches.Handle("tavern", null!, null!);
        Assert.Equal(0, createdViews);
        Assert.Equal(0, createdLocationControllers);
        Assert.Equal(0, locationRequests);
    }

    private static Mission NewMission()
    {
        // Avoid the native mission allocation, but use the real Add/GetMissionBehavior methods.
        var mission = (Mission)RuntimeHelpers.GetUninitializedObject(typeof(Mission));
        AccessTools.Field(typeof(Mission), "<MissionBehaviors>k__BackingField")
            .SetValue(mission, new List<MissionBehavior>());
        AccessTools.Field(typeof(Mission), "<MissionLogics>k__BackingField")
            .SetValue(mission, new List<MissionLogic>());
        mission._otherMissionBehaviors = new List<MissionBehavior>();
        return mission;
    }

    private sealed class LocationControllerSentinel : MissionBehavior, ILocationMissionBehavior
    {
        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;
    }

    public void Dispose()
    {
        MessageBroker.Instance.Unsubscribe(onLocationRequest);
        ModInformation.SessionMode = previousMode;
        if (hadContainer) ContainerProvider.SetContainer(previousContainer);
        else ContainerProvider.Clear();
        container.Dispose();
    }
}
