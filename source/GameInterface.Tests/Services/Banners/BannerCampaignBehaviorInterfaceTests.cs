using Common.Logging;
using Common.Util;
using GameInterface.Services.Banners.Interfaces;
using GameInterface.Services.Entity;
using GameInterface.Services.MobileParties.Extensions;
using GameInterface.Services.Players;
using HarmonyLib;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.ComponentInterfaces;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.Core;
using TaleWorlds.Library;
using Xunit;

namespace GameInterface.Tests.Services.Banners;

[Collection(ModInformationRoleCollection.Name)]
public sealed class BannerCampaignBehaviorInterfaceTests : IDisposable
{
    private readonly Campaign previousCampaign = Campaign.Current;
    private readonly Harmony harmony = new("coop.tests.static-winner-banner");
    private readonly List<string> logs = new();
    private readonly Action<string> logCallback;
    private readonly Mock<BattleRewardModel> rewards = new(MockBehavior.Strict);
    private readonly List<MobileParty> playerMarkers = new();
    private static int playerPredicates;
    private static int nullPlayerPredicates;
    private static int randomCalls;

    static BannerCampaignBehaviorInterfaceTests() =>
        RuntimeHelpers.RunModuleConstructor(typeof(Coop.Tests.Mocks.TestNetwork).Module.ModuleHandle);

    public BannerCampaignBehaviorInterfaceTests()
    {
        var campaign = ObjectHelper.SkipConstructor<Campaign>();
        campaign._gameModels = ObjectHelper.SkipConstructor<GameModels>();
        campaign._gameModels.BattleRewardModel = rewards.Object;
        Campaign.Current = campaign;
        logCallback = message => { lock (logs) logs.Add(message); };
        OutputSinkManager.AddLogCallback(logCallback);
        harmony.Patch(AccessTools.Method(typeof(MobilePartyExtensions), nameof(MobilePartyExtensions.IsPlayerParty)),
            prefix: new HarmonyMethod(typeof(BannerCampaignBehaviorInterfaceTests), nameof(CountPlayerPredicate)));
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(MBRandom)).Where(method =>
            method.Name == "get_RandomFloat" || method.Name == nameof(MBRandom.RandomInt)))
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(BannerCampaignBehaviorInterfaceTests), nameof(CountRandom)));
        playerPredicates = nullPlayerPredicates = randomCalls = 0;
    }

    private static void CountPlayerPredicate(MobileParty party)
    {
        playerPredicates++;
        if (party == null) nullPlayerPredicates++;
    }
    private static void CountRandom() => randomCalls++;

    [Fact]
    public void StaticWinnerDoesNotCallNullPlayerPredicateOrConsumeRewards()
    {
        var winner = ObjectHelper.SkipConstructor<PartyBase>();
        var gained = new ItemRoster();
        Assert.False(winner.IsMobile);
        new BannerCampaignBehaviorInterface().OnCollectLootItems(ObjectHelper.SkipConstructor<BannerCampaignBehavior>(), winner, gained);
        Assert.Equal(0, playerPredicates); Assert.Equal(0, nullPlayerPredicates);
        Assert.Equal(0, randomCalls); Assert.Empty(gained);
        rewards.VerifyNoOtherCalls(); AssertNoNullPartyError();
    }

    [Fact]
    public void NullWinnerRemainsAnErrorRatherThanBecomingAStaticParty()
    {
        Assert.Throws<NullReferenceException>(() => new BannerCampaignBehaviorInterface().OnCollectLootItems(
            ObjectHelper.SkipConstructor<BannerCampaignBehavior>(), null!, new ItemRoster()));
        Assert.Equal(0, playerPredicates); Assert.Equal(0, randomCalls); rewards.VerifyNoOtherCalls();
    }

    [Fact]
    public void MobileNpcWinnerStillUsesExistingNonPlayerReturn()
    {
        var winner = MobileWinner(); var gained = new ItemRoster();
        Assert.True(winner.IsMobile);
        new BannerCampaignBehaviorInterface().OnCollectLootItems(ObjectHelper.SkipConstructor<BannerCampaignBehavior>(), winner, gained);
        Assert.Equal(1, playerPredicates); Assert.Equal(0, nullPlayerPredicates);
        Assert.Equal(0, randomCalls); Assert.Empty(gained);
        rewards.VerifyNoOtherCalls(); AssertNoNullPartyError();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegisteredPlayerWinnerPreservesOptionalBannerRewardExactlyOnce(bool hasBanner)
    {
        var winner = MobileWinner(); MarkPlayer(winner.MobileParty);
        var mapEvent = ObjectHelper.SkipConstructor<MapEvent>();
        var winningSide = Side(mapEvent); var defeatedSide = Side(mapEvent);
        mapEvent._sides = new[] { defeatedSide, winningSide };
        mapEvent._battleState = BattleState.AttackerVictory;
        // Side indexing follows the actual enum, rather than assuming Attacker is zero.
        mapEvent._sides[(int)BattleSideEnum.Attacker] = winningSide;
        mapEvent._sides[(int)BattleSideEnum.Defender] = defeatedSide;
        winner._mapEventSide = winningSide;
        var banner = hasBanner ? new ItemObject() : null;
        rewards.Setup(model => model.GetBannerRewardForWinningMapEvent(mapEvent)).Returns(banner!);
        var gained = new ItemRoster();
        new BannerCampaignBehaviorInterface().OnCollectLootItems(ObjectHelper.SkipConstructor<BannerCampaignBehavior>(), winner, gained);
        rewards.Verify(model => model.GetBannerRewardForWinningMapEvent(mapEvent), Times.Once);
        rewards.VerifyNoOtherCalls();
        Assert.Equal(1, playerPredicates); Assert.Equal(0, nullPlayerPredicates);
        if (hasBanner) { Assert.Single(gained); Assert.Same(banner, gained.GetElementCopyAtIndex(0).EquipmentElement.Item); Assert.Equal(1, gained.GetElementNumber(0)); }
        else Assert.Empty(gained);
        AssertNoNullPartyError();
    }

    private static PartyBase MobileWinner()
    {
        var mobile = ObjectHelper.SkipConstructor<MobileParty>();
        var party = ObjectHelper.SkipConstructor<PartyBase>();
        party.MobileParty = mobile; mobile.Party = party;
        return party;
    }
    private static MapEventSide Side(MapEvent mapEvent)
    {
        var side = ObjectHelper.SkipConstructor<MapEventSide>();
        AccessTools.Field(typeof(MapEventSide), "_mapEvent").SetValue(side, mapEvent);
        AccessTools.Field(typeof(MapEventSide), "_battleParties").SetValue(side, new MBList<MapEventParty>());
        return side;
    }
    private void MarkPlayer(MobileParty party)
    {
        PlayerObjects().Add(party, new ControlledObjectInfo("banner-player", new ControllerIdProvider()));
        playerMarkers.Add(party);
    }
    private static ConditionalWeakTable<object, ControlledObjectInfo> PlayerObjects() =>
        (ConditionalWeakTable<object, ControlledObjectInfo>)AccessTools.Field(typeof(PlayerManager), "PlayerObjects").GetValue(null)!;
    private void AssertNoNullPartyError()
    {
        lock (logs) Assert.DoesNotContain(logs, text => text.Contains("party was null"));
    }
    public void Dispose()
    {
        foreach (var party in playerMarkers) PlayerObjects().Remove(party);
        OutputSinkManager.RemoveLogCallback(logCallback);
        harmony.UnpatchAll(harmony.Id);
        Campaign.Current = previousCampaign;
    }
}
