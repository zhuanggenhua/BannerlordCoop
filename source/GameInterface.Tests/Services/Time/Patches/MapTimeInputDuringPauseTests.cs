using Autofac;
using GameInterface.Services.Heroes.Interaces;
using GameInterface.Services.Heroes.Enum;
using GameInterface.Services.Heroes.Patches;
using Moq;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.ViewModelCollection.Map.MapBar;
using Xunit;

namespace GameInterface.Tests.Services.Time.Patches;

[Collection(ModInformationRoleCollection.Name)]
public class MapTimeInputDuringPauseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClickingPauseDuringLeasePauseOrFreezeStillRunsNativeInputOnce(bool frozen)
    {
        var identity = new SessionIdentity();
        var lease = new AuthorityLease("room", "writer", frozen ? "a" : "", 1, isPaused: !frozen);
        Assert.True(identity.TryApply(lease));
        if (frozen) Assert.True(identity.TryFreezeLocalAuthority(lease, 1));
        var builder = new ContainerBuilder();
        builder.RegisterInstance(identity).As<ISessionIdentity>();
        builder.RegisterInstance(Mock.Of<ITimeControlInterface>(value => value.CanSetTimeControl(
            It.IsAny<TimeControlEnum>()) == true));
        using var container = builder.Build();
        bool hadContainer = ContainerProvider.TryGetContainer(out var previousContainer);
        var previousCampaign = Campaign.Current;
        var campaign = (Campaign)RuntimeHelpers.GetUninitializedObject(typeof(Campaign));
        campaign._timeControlMode = CampaignTimeControlMode.UnstoppablePlay;
        var viewModel = (MapTimeControlVM)RuntimeHelpers.GetUninitializedObject(typeof(MapTimeControlVM));
        viewModel.TimeFlowState = 0; // The display is paused while selected campaign speed is Play.
        int nativeInputCalls = 0;
        viewModel._onTimeFlowStateChange = () => nativeInputCalls++;
        try
        {
            Campaign.Current = campaign;
            ContainerProvider.SetContainer(container);
            Assert.False(AllowTimeControlFromControlsPatches.ExecuteTimeControlChangePrefix(ref viewModel, 0));

            Assert.Equal(1, nativeInputCalls);
            Assert.Equal(CampaignTimeControlMode.Stop, campaign._timeControlMode);
            Assert.True(identity.IsConfirmed);
            Assert.Equal(lease, identity.CurrentLease);
        }
        finally
        {
            Campaign.Current = previousCampaign;
            if (hadContainer) ContainerProvider.SetContainer(previousContainer);
            else ContainerProvider.Clear();
        }
    }
}
