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
    public void ComponentBeforeWorldPublicationIsAddedToNativeCaravanPartitionOnlyOnce()
    {
        var manager = new CampaignObjectManager();
        var party = ObjectHelper.SkipConstructor<MobileParty>();
        var caravan = ObjectHelper.SkipConstructor<CaravanPartyComponent>();
        PartyComponentHandler.ApplyComponentBinding(manager, party, caravan);

        // Exact global-list and partition tail of original AddMobileParty; no GUID/cache prefix.
        manager._mobileParties.Add(party);
        manager.AddPartyToAppropriateList(party);

        Assert.Same(party, Assert.Single(manager._mobileParties));
        Assert.Same(party, Assert.Single(manager._caravanParties));
        Assert.Same(party, caravan.MobileParty);
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnpublishedRebindRepeatAndClearOnlyBindStateUntilNativePublication(bool clear)
    {
        var manager = new CampaignObjectManager();
        var party = ObjectHelper.SkipConstructor<MobileParty>();
        var bandit = ObjectHelper.SkipConstructor<BanditPartyComponent>();
        var caravan = ObjectHelper.SkipConstructor<CaravanPartyComponent>();
        PartyComponentHandler.ApplyComponentBinding(manager, party, bandit);
        PartyComponentHandler.ApplyComponentBinding(manager, party, bandit);
        PartyComponentHandler.ApplyComponentBinding(manager, party, caravan);
        PartyComponentHandler.ApplyComponentBinding(manager, party, caravan);
        if (clear)
        {
            PartyComponentHandler.ApplyComponentBinding(manager, party, null);
            PartyComponentHandler.ApplyComponentBinding(manager, party, null);
        }
        Assert.Empty(manager._mobileParties);Assert.Empty(manager._banditParties);
        Assert.Empty(manager._caravanParties);Assert.Empty(manager._partiesWithoutPartyComponent);
        Assert.Same(party, caravan.MobileParty);Assert.Equal(!clear, party.IsCaravan);Assert.False(party.IsBandit);
        Assert.Same(clear ? null : caravan, party._partyComponent);
        PublishTail(manager, party);
        Assert.Same(party, Assert.Single(manager._mobileParties));
        Assert.Same(party, Assert.Single(clear ? manager._partiesWithoutPartyComponent : manager._caravanParties));
        Assert.Empty(clear ? manager._caravanParties : manager._partiesWithoutPartyComponent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BindingBeforeOrAfterPublicationKeepsOneMembershipAndNativeRemovalLeavesNone(bool before)
    {
        var manager = new CampaignObjectManager();var party = ObjectHelper.SkipConstructor<MobileParty>();
        if (!before) PublishTail(manager, party);
        var caravan = ObjectHelper.SkipConstructor<CaravanPartyComponent>();
        PartyComponentHandler.ApplyComponentBinding(manager, party, caravan);
        if (before) PublishTail(manager, party);
        PartyComponentHandler.ApplyComponentBinding(manager, party, caravan);
        Assert.Same(party, Assert.Single(manager._caravanParties));
        var bandit = ObjectHelper.SkipConstructor<BanditPartyComponent>();
        PartyComponentHandler.ApplyComponentBinding(manager, party, bandit);
        Assert.Empty(manager._caravanParties);Assert.Empty(manager._partiesWithoutPartyComponent);
        Assert.Same(party, Assert.Single(manager._banditParties));
        manager.RemoveMobileParty(party);
        Assert.Empty(manager._mobileParties);Assert.Empty(manager._caravanParties);Assert.Empty(manager._banditParties);
        Assert.Empty(manager._partiesWithoutPartyComponent);
    }

    [Fact]
    public void SameStringIdDifferentReferenceDoesNotQualifyUnpublishedPartyForBookkeeping()
    {
        var manager = new CampaignObjectManager();
        var published = ObjectHelper.SkipConstructor<MobileParty>();published.StringId = "same-id";
        PublishTail(manager, published);
        var pending = ObjectHelper.SkipConstructor<MobileParty>();pending.StringId = "same-id";
        var caravan = ObjectHelper.SkipConstructor<CaravanPartyComponent>();
        PartyComponentHandler.ApplyComponentBinding(manager, pending, caravan);
        Assert.NotSame(published, pending);Assert.Same(published, Assert.Single(manager._mobileParties));
        Assert.Same(published, Assert.Single(manager._partiesWithoutPartyComponent));Assert.Empty(manager._caravanParties);
        Assert.Same(pending, caravan.MobileParty);Assert.Same(caravan, pending._partyComponent);Assert.True(pending.IsCaravan);
    }

    [Fact]
    public void BindingAfterNativeRemovalCannotResurrectPartitionMembership()
    {
        var manager = new CampaignObjectManager();var party = ObjectHelper.SkipConstructor<MobileParty>();
        PublishTail(manager, party);
        PartyComponentHandler.ApplyComponentBinding(manager, party, ObjectHelper.SkipConstructor<CaravanPartyComponent>());
        manager.RemoveMobileParty(party);
        var bandit = ObjectHelper.SkipConstructor<BanditPartyComponent>();
        PartyComponentHandler.ApplyComponentBinding(manager, party, bandit);
        Assert.Same(party, bandit.MobileParty);Assert.True(party.IsBandit);
        Assert.Empty(manager._mobileParties);Assert.Empty(manager._banditParties);Assert.Empty(manager._caravanParties);
        PartyComponentHandler.ApplyComponentBinding(manager, party, null);
        Assert.Empty(manager._partiesWithoutPartyComponent);
    }

    private static void PublishTail(CampaignObjectManager manager, MobileParty party)
    {
        manager._mobileParties.Add(party);
        manager.AddPartyToAppropriateList(party);
    }
}
