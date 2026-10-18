using Common.Util;
using GameInterface.Services.PartyComponents.Handlers;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using Xunit;

namespace GameInterface.Tests.Services.PartyComponents;

public class PartyComponentMembershipTests
{
    [Fact]
    public void ReplicatedComponentBindingUpdatesNativeAiPartitionOnce()
    {
        var manager = new CampaignObjectManager();
        var party = ObjectHelper.SkipConstructor<MobileParty>();
        manager._mobileParties.Add(party);
        manager.AddPartyToAppropriateList(party);
        var bandit = ObjectHelper.SkipConstructor<BanditPartyComponent>();

        PartyComponentHandler.ApplyComponentBinding(manager, party, bandit);
        Assert.Same(party, bandit.MobileParty);
        Assert.True(party.IsBandit);
        Assert.Same(party, Assert.Single(manager._banditParties));
        Assert.Empty(manager._partiesWithoutPartyComponent);

        PartyComponentHandler.ApplyComponentBinding(manager, party, bandit);
        Assert.Single(manager._banditParties);
        Assert.Single(manager._mobileParties);

        var caravan = ObjectHelper.SkipConstructor<CaravanPartyComponent>();
        PartyComponentHandler.ApplyComponentBinding(manager, party, caravan);
        Assert.Empty(manager._banditParties);
        Assert.Same(party, Assert.Single(manager._caravanParties));
        Assert.False(party.IsBandit);
        Assert.True(party.IsCaravan);

        PartyComponentHandler.ApplyComponentBinding(manager, party, null);
        Assert.Empty(manager._caravanParties);
        Assert.Same(party, Assert.Single(manager._partiesWithoutPartyComponent));
        Assert.Single(manager._mobileParties);
    }
}
