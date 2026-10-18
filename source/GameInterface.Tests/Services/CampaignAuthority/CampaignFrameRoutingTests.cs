using Autofac;
using Common;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.CampaignService.Patches;
using GameInterface.Services.Entity;
using Moq;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using Xunit;

namespace GameInterface.Tests.Services.CampaignAuthority;

[Collection(ModInformationRoleCollection.Name)]
public class CampaignFrameRoutingTests
{
    [Theory]
    [InlineData("a", "a", false, false, true, false)]
    [InlineData("b", "a", false, false, false, true)]
    [InlineData("a", "b", false, false, false, true)]
    [InlineData("b", "b", false, true, false, false)]
    [InlineData("a", "", true, false, false, false)]
    public void RoutesWorldAndPresentationFramesWithoutLettingFrozenOrPausedWritersRun(
        string local, string writer, bool paused, bool frozen, bool nativeWorldFrame, bool observerFrame)
    {
        var identity = new SessionIdentity();
        var lease = new AuthorityLease("room", "authority", writer, 3, paused);
        Assert.True(identity.TryApply(lease, "a"));
        if (frozen) Assert.True(identity.TryFreezeLocalAuthority(lease, 1));
        var controller = new ControllerIdProvider();
        controller.SetControllerId(local);
        var frames = new Mock<ICampaignObserverFrame>();
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new CampaignAuthorityContext(identity, controller)).As<ICampaignAuthorityContext>();
        builder.RegisterInstance(controller).As<IControllerIdProvider>();
        builder.RegisterInstance(frames.Object).As<ICampaignObserverFrame>();
        using var container = builder.Build();
        bool hadContainer = ContainerProvider.TryGetContainer(out var previous);
        var oldMode = ModInformation.SessionMode;
        var campaign = (Campaign)RuntimeHelpers.GetUninitializedObject(typeof(Campaign));
        try
        {
            ModInformation.SessionMode = local == "a" ? SessionMode.ListenAuthority : SessionMode.RemoteReplica;
            ContainerProvider.SetContainer(container);
            Assert.Equal(nativeWorldFrame, CampaignRobustnessPatches.Prefix_RealTick(campaign, .016f));
            Assert.Equal(nativeWorldFrame, CampaignRobustnessPatches.Prefix_Tick());
            frames.Verify(value => value.Tick(campaign, .016f), observerFrame ? Times.Once() : Times.Never());
        }
        finally
        {
            ModInformation.SessionMode = oldMode;
            if (hadContainer) ContainerProvider.SetContainer(previous);
            else ContainerProvider.Clear();
        }
    }
}
