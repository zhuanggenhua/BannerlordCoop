using Common;
using Common.Util;
using GameInterface.Services.Heroes.Interfaces;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameState;
using Xunit;

namespace GameInterface.Tests.Services.Players;

[Collection("CampaignCurrentCollection")]
public class TransferredPlayerContextTests
{
    [Theory]
    [InlineData(SessionMode.ListenAuthority, "town")]
    [InlineData(SessionMode.RemoteReplica, null)]
    public void TransferredMenuIsClearedOnlyInReceivingProcess(SessionMode mode, string expected)
    {
        var previous = ModInformation.SessionMode;
        var previousCampaign = Campaign.Current;
        try
        {
            ModInformation.SessionMode = mode;
            var campaign = ObjectHelper.SkipConstructor<Campaign>();
            campaign.MapStateData = new MapStateData();
            Campaign.Current = campaign;
            var map = ObjectHelper.SkipConstructor<MapState>();
            map.GameMenuId = "town";
            HeroInterface.ClearTransferredPlayerContext(map, campaign);
            Assert.Equal(expected, map.GameMenuId);
        }
        finally { ModInformation.SessionMode = previous; Campaign.Current = previousCampaign; }
    }
}
