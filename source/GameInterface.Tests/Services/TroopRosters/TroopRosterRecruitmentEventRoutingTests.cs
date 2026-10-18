using Common.Util;
using GameInterface.Services.TroopRosters.Interfaces;
using Moq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using Xunit;

namespace GameInterface.Tests.Services.TroopRosters;

public sealed class TroopRosterRecruitmentEventRoutingTests
{
    [Fact]
    public void RemotePartyRecruitmentUsesItsHeroAndDoesNotUseLocalMainPartyEvent()
    {
        var dispatcher = new Mock<IRecruitmentCampaignEventDispatcher>();
        var recruitedParty = ObjectHelper.SkipConstructor<MobileParty>();
        var localMainParty = ObjectHelper.SkipConstructor<MobileParty>();
        var recruiterHero = ObjectHelper.SkipConstructor<Hero>();
        var settlement = ObjectHelper.SkipConstructor<Settlement>();
        var troopSource = ObjectHelper.SkipConstructor<Hero>();
        var troop = ObjectHelper.SkipConstructor<CharacterObject>();

        TroopRosterInterface.DispatchRecruitmentEventForParty(
            dispatcher.Object,
            recruitedParty,
            localMainParty,
            recruiterHero,
            settlement,
            troopSource,
            troop);

        dispatcher.Verify(
            events => events.OnTroopRecruited(recruiterHero, settlement, troopSource, troop, 1),
            Times.Once);
        dispatcher.Verify(events => events.OnUnitRecruited(It.IsAny<CharacterObject>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void LocalMainPartyRecruitmentKeepsNativeMainPartyEvent()
    {
        var dispatcher = new Mock<IRecruitmentCampaignEventDispatcher>();
        var mainParty = ObjectHelper.SkipConstructor<MobileParty>();
        var recruiterHero = ObjectHelper.SkipConstructor<Hero>();
        var settlement = ObjectHelper.SkipConstructor<Settlement>();
        var troopSource = ObjectHelper.SkipConstructor<Hero>();
        var troop = ObjectHelper.SkipConstructor<CharacterObject>();

        TroopRosterInterface.DispatchRecruitmentEventForParty(
            dispatcher.Object,
            mainParty,
            mainParty,
            recruiterHero,
            settlement,
            troopSource,
            troop);

        dispatcher.Verify(events => events.OnUnitRecruited(troop, 1), Times.Once);
        dispatcher.Verify(
            events => events.OnTroopRecruited(
                It.IsAny<Hero>(),
                It.IsAny<Settlement>(),
                It.IsAny<Hero>(),
                It.IsAny<CharacterObject>(),
                It.IsAny<int>()),
            Times.Never);
    }
}