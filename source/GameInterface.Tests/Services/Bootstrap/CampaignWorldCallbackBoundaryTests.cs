using Autofac;
using Common;
using Common.Util;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.CampaignService.Patches;
using GameInterface.Services.Entity;
using GameInterface.Services.MobilePartyAIs.Patches;
using HarmonyLib;
using System;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem.Party;
using Xunit;

namespace GameInterface.Tests.Services.Bootstrap;

[Collection(ModInformationRoleCollection.Name)]
public class CampaignWorldCallbackBoundaryTests
{
    [Theory]
    [InlineData(CampaignExecutionPhase.Replay)]
    [InlineData(CampaignExecutionPhase.Prediction)]
    [InlineData(CampaignExecutionPhase.CheckpointInstall)]
    [InlineData(CampaignExecutionPhase.LocalIntent)]
    [InlineData(CampaignExecutionPhase.Bootstrap)]
    public void WriterCannotRunWorldCallbacksFromRestrictedProvenance(CampaignExecutionPhase phase)
    {
        WithWriter(() =>
        {
            using (CampaignExecutionScope.Enter(phase))
            {
                AssertBlocked();
                using (CampaignExecutionScope.Enter(CampaignExecutionPhase.WorldWrite))
                    AssertBlocked();
            }
            Assert.True(CampaignSimulationAuthorityPolicy.CanRunWorldSimulation());
        });
    }

    [Fact]
    public void AllowedThreadOnCurrentWriterDoesNotPermitNativeAiOrWorldTick()
    {
        WithWriter(() =>
        {
            using (new AllowedThread()) AssertBlocked();
            Assert.True(CampaignSimulationAuthorityPolicy.CanRunWorldSimulation());
        });
    }

    [Fact]
    public void PromotedReplicaRunsWorldCallbacksWithoutBecomingRoomHost()
    {
        WithWriter(() =>
        {
            Assert.False(ModInformation.IsServer);
            Assert.True(CampaignSimulationAuthorityPolicy.CanRunWorldSimulation());
            Assert.True(MobilePartyAIDisablePatches.IsTickAuthority(null));
            Assert.True(CampaignRobustnessPatches.Prefix_RealTick(null, .016f));
            Assert.True(CampaignRobustnessPatches.Prefix_Tick());
        });
    }

    private static void AssertBlocked()
    {
        Assert.False(CampaignSimulationAuthorityPolicy.CanRunWorldSimulation());
        Assert.False(MobilePartyAIDisablePatches.IsTickAuthority(null));
        Assert.False(CampaignRobustnessPatches.Prefix_Tick());
        Assert.False(CampaignRobustnessPatches.Prefix_RealTick(null, .016f));
        var prefix = AccessTools.Method(typeof(MobilePartyAIDisablePatches), "TickPrefix");
        Assert.False((bool)prefix.Invoke(null, new object[] { RuntimeHelpers.GetUninitializedObject(typeof(MobilePartyAi)) })!);
    }

    private static void WithWriter(Action action)
    {
        var previousMode = ModInformation.SessionMode;
        bool hadPrevious = ContainerProvider.TryGetContainer(out var previous);
        var identity = new SessionIdentity();
        Assert.True(identity.TryApply(new AuthorityLease("room", "world", "b", 4), "a"));
        var controller = new ControllerIdProvider();
        controller.SetControllerId("b");
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new CampaignAuthorityContext(identity, controller)).As<ICampaignAuthorityContext>();
        using var container = builder.Build();
        try
        {
            ModInformation.SessionMode = SessionMode.RemoteReplica;
            ContainerProvider.SetContainer(container);
            action();
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
            if (hadPrevious) ContainerProvider.SetContainer(previous);
            else ContainerProvider.Clear();
        }
    }
}
