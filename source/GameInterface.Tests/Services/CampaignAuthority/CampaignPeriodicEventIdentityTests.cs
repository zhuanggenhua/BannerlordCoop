using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.ObjectManager;
using Moq;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using Xunit;

namespace GameInterface.Tests.Services.CampaignAuthority;

[Collection(ModInformationRoleCollection.Name)]
public class CampaignPeriodicEventIdentityTests
{
    static CampaignPeriodicEventIdentityTests() =>
        RuntimeHelpers.RunModuleConstructor(typeof(Coop.Tests.Mocks.TestNetwork).Module.ModuleHandle);

    [Theory]
    [InlineData("campaign", false)]
    [InlineData("campaign", true)]
    [InlineData("weather", false)]
    [InlineData("weather", true)]
    [InlineData("tracks", false)]
    [InlineData("tracks", true)]
    public void OnlyTheActualKnownCampaignSingletonCanSupplyAnUnregisteredCallback(string kind, bool foreignOwner)
    {
        var previous = Campaign.Current;
        var campaign = New<Campaign>();
        campaign.UniqueGameId = "campaign";
        var weather = New<MapWeatherCampaignBehavior>();
        var tracks = New<MapTracksCampaignBehavior>();
        var behaviors = new Mock<ICampaignBehaviorManager>();
        behaviors.Setup(value => value.GetBehavior<MapWeatherCampaignBehavior>()).Returns(weather);
        behaviors.Setup(value => value.GetBehavior<MapTracksCampaignBehavior>()).Returns(tracks);
        campaign.AddCampaignBehaviorManager(behaviors.Object);
        Campaign.Current = campaign;
        try
        {
            var timer = new MBCampaignEvent(null);
            if (kind == "campaign") timer.AddHandler((foreignOwner ? New<Campaign>() : campaign).HourlyTick);
            else if (kind == "weather") timer.AddHandler((foreignOwner ? New<MapWeatherCampaignBehavior>() : weather).WeatherUpdateTick);
            else timer.AddHandler((foreignOwner ? New<MapTracksCampaignBehavior>() : tracks).QuarterHourlyTick);

            var service = new CampaignSchedulerCheckpointService(Mock.Of<IObjectManager>());
            Assert.Equal(!foreignOwner, service.TryCaptureCallbacks(timer, out var identities));
            if (!foreignOwner)
            {
                Assert.Single(identities);
                Assert.Contains(kind == "campaign" ? "Campaign:campaign|" : "CampaignBehavior:", identities[0]);
            }
        }
        finally { Campaign.Current = previous; }
    }

    [Fact]
    public void UnregisteredUnknownCallbackOwnerIsRejectedInsteadOfUsingItsProcessLocalIdentity()
    {
        var timer = new MBCampaignEvent(null);
        timer.AddHandler(new UnknownOwner().Record);

        var service = new CampaignSchedulerCheckpointService(Mock.Of<IObjectManager>());
        Assert.False(service.TryCaptureCallbacks(timer, out _));
    }

    private sealed class UnknownOwner
    {
        public void Record(MBCampaignEvent _, object[] args) { }
    }

    private static T New<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
}
