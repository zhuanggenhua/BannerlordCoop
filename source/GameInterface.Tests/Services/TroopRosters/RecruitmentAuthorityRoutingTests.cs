using Common;
using GameInterface.Services.CampaignAuthority;
using Common.Util;
using Common.Messaging;
using GameInterface.Services.Heroes.Messages.Collections;
using GameInterface.Services.Entity;
using GameInterface.Services.TroopRosters.Patches;
using Xunit;
using TaleWorlds.CampaignSystem;

namespace GameInterface.Tests.Services.TroopRosters;

[Collection(ModInformationRoleCollection.Name)]
public class RecruitmentAuthorityRoutingTests
{
    [Theory]
    [InlineData("a", "a", true, false, false, true)]
    [InlineData("a", "b", true, false, false, false)]
    [InlineData("b", "b", false, false, false, true)]
    [InlineData("b", "a", false, false, false, false)]
    [InlineData("a", "a", true, false, true, false)]
    [InlineData("b", "b", false, false, true, false)]
    [InlineData("a", "", true, true, false, false)]
    public void NativeOnDoneOnlyRunsForConfirmedUnfrozenWriter(
        string local, string writer, bool roomHost, bool paused, bool frozen, bool expected)
    {
        var identity = new SessionIdentity();
        var lease = new AuthorityLease("room", "world", writer, 5, paused);
        Assert.True(identity.TryApply(lease, "a"));
        if (frozen) Assert.True(identity.TryFreezeLocalAuthority(lease, 7));
        var controller = new ControllerIdProvider();
        controller.SetControllerId(local);
        var context = new CampaignAuthorityContext(identity, controller);

        Assert.Equal(expected, RecruitmentVMPatches.ShouldUseNativeRecruitment(context, roomHost));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NoConfirmedLeasePreservesLegacyMode(bool roomHost) =>
        Assert.Equal(roomHost, RecruitmentVMPatches.ShouldUseNativeRecruitment(null!, roomHost));

    [Fact]
    public void NativeCartRejectsAlreadyConsumedAndDuplicateVolunteerSlots()
    {
        var hero = ObjectHelper.SkipConstructor<Hero>();
        var troop = new CharacterObject();
        hero.VolunteerTypes = new[] { troop };
        Assert.True(RecruitmentVMPatches.AreVolunteerSlotsCurrent(new[] { (hero, troop, 0) }));
        Assert.False(RecruitmentVMPatches.AreVolunteerSlotsCurrent(new[] { (hero, troop, 0), (hero, troop, 0) }));
        hero.VolunteerTypes[0] = null!;
        Assert.False(RecruitmentVMPatches.AreVolunteerSlotsCurrent(new[] { (hero, troop, 0) }));
    }

    [Fact]
    public void NativeResultPublishesOnlyConsumedSlotsAndDoesNotModifyRosterOrVolunteerData()
    {
        var hero = ObjectHelper.SkipConstructor<Hero>();
        var troop = new CharacterObject();
        hero.VolunteerTypes = new[] { null!, troop };
        int published = 0;
        System.Action<MessagePayload<VolunteerTypesArrayUpdated>> observe = payload =>
        {
            Assert.Same(hero, payload.What.Instance);
            Assert.Equal(0, payload.What.Index);
            Assert.Null(payload.What.Value);
            published++;
        };
        MessageBroker.Instance.Subscribe(observe);
        try
        {
            RecruitmentVMPatches.PublishConsumedVolunteerSlots(new[] { (hero, troop, 0), (hero, troop, 1) });
            Assert.Equal(1, published);
            Assert.Null(hero.VolunteerTypes[0]);
            Assert.Same(troop, hero.VolunteerTypes[1]);
        }
        finally { MessageBroker.Instance.Unsubscribe(observe); }
    }
}
