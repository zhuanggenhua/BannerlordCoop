using Common;
using Common.Util;
using Coop.Tests.Mocks;
using GameInterface.Services.Entity;
using GameInterface.Services.MobileParties.Extensions;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using HarmonyLib;
using Moq;
using Serilog;
using System;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;
using Xunit;

namespace GameInterface.Tests.Services.MobileParties;

[Collection(ModInformationRoleCollection.Name)]
public class PlayerPartySettlementExitPatchTests : IDisposable
{
    private readonly Harmony harmony = new("tests.player-party-settlement-exit");
    private readonly Campaign previousCampaign = Campaign.Current;
    private readonly Campaign campaign = ObjectHelper.SkipConstructor<Campaign>();
    private readonly MobileParty party = ObjectHelper.SkipConstructor<MobileParty>();
    private readonly Settlement settlement = ObjectHelper.SkipConstructor<Settlement>();
    private readonly PlayerManager players;
    private readonly Player player = new("remote-player", "", "party", "", "");
    private readonly SessionMode previousMode = ModInformation.SessionMode;

    static PlayerPartySettlementExitPatchTests() =>
        RuntimeHelpers.RunModuleConstructor(typeof(TestNetwork).Module.ModuleHandle);

    public PlayerPartySettlementExitPatchTests()
    {
        ModInformation.SessionMode = SessionMode.ListenAuthority;
        Campaign.Current = campaign;
        campaign.MainParty = ObjectHelper.SkipConstructor<MobileParty>();
        campaign.CampaignEventDispatcher = new CampaignEventDispatcher(Array.Empty<CampaignEventReceiver>());
        party.Ai = ObjectHelper.SkipConstructor<MobilePartyAi>();
        party.Party = ObjectHelper.SkipConstructor<PartyBase>();
        party.ShortTermBehavior = AiBehavior.GoToPoint;
        party._currentSettlement = settlement;
        party._attachedParties = new MBList<MobileParty>();
        settlement._partiesCache = new MBList<MobileParty> { party };
        settlement.SettlementComponent = Mock.Of<SettlementComponent>();
        var objects = new Mock<IObjectManager>();
        var found = party;
        objects.Setup(value => value.TryGetObjectWithLogging<MobileParty>("party", out found)).Returns(true);
        objects.Setup(value => value.TryGetObject<MobileParty>("party", out found)).Returns(true);
        var controller = new ControllerIdProvider();
        controller.SetControllerId("local-player");
        players = new PlayerManager(Mock.Of<ILogger>(), objects.Object, controller);
        // The red run has no production patch yet; removal of the patch also makes the player cases fail.
        var patch = typeof(MobilePartyExtensions).Assembly.GetType(
            "GameInterface.Services.MobileParties.Patches.PlayerPartySettlementExitPatch");
        if (patch != null) harmony.CreateClassProcessor(patch).Patch();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RegisteredPlayerNeverEntersNativeExitOrPortTransitionLists(bool disconnected, bool portTransition)
    {
        Assert.True(players.AddPlayer(player));
        var peer = new TestNetwork().CreatePeer();
        players.SetPeer(player.ControllerId, peer);
        if (disconnected) players.ClearPeer(peer);
        Assert.Equal(!disconnected, players.IsConnected(player));
        Assert.True(party.IsPlayerParty());
        Assert.False(party.IsMainParty);
        party.StartTransitionNextFrameToExitFromPort = portTransition;
        // At sea the native port branch consumes the flag without invoking scene navigation.
        party.IsCurrentlyAtSea = portTransition;
        var marker = ObjectHelper.SkipConstructor<MobileParty>();
        var exits = new[] { marker, marker };
        var grids = new[] { marker, marker };
        var originalExits = exits;
        var originalGrids = grids;
        int exitCount = -1, gridCount = -1;
        var start = party.NavigationTransitionStartTime;
        var end = party.EndPositionForNavigationTransition;

        party.CheckExitingSettlementParallel(ref exitCount, ref exits, ref gridCount, ref grids);

        Assert.Equal(-1, exitCount);
        Assert.Equal(-1, gridCount);
        Assert.Same(originalExits, exits);
        Assert.Same(originalGrids, grids);
        Assert.All(exits, value => Assert.Same(marker, value));
        Assert.All(grids, value => Assert.Same(marker, value));
        Assert.Equal(portTransition, party.StartTransitionNextFrameToExitFromPort);
        Assert.Equal(start, party.NavigationTransitionStartTime);
        Assert.Equal(end, party.EndPositionForNavigationTransition);
        Assert.Same(settlement, party.CurrentSettlement);
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(false, true, -1)]
    [InlineData(true, false, -1)]
    public void NativeNpcAndLocalMainPartyRulesRemainIntact(bool mainParty, bool hold, int expectedExitCount)
    {
        if (mainParty) campaign.MainParty = party;
        if (hold) party.ShortTermBehavior = AiBehavior.Hold;
        var exits = new MobileParty[1];
        var grids = new MobileParty[1];
        int exitCount = -1, gridCount = -1;
        party.CheckExitingSettlementParallel(ref exitCount, ref exits, ref gridCount, ref grids);
        Assert.Equal(expectedExitCount, exitCount);
        Assert.Equal(-1, gridCount);
        if (expectedExitCount == 0) Assert.Same(party, exits[0]);
        else Assert.Null(exits[0]);
    }

    [Fact]
    public void DirectNativeLeaveStillRemovesRegisteredPlayerFromSettlement()
    {
        Assert.True(players.AddPlayer(player));
        var receiver = new Mock<CampaignEventReceiver>();
        campaign.CampaignEventDispatcher = new CampaignEventDispatcher(new[] { receiver.Object });
        LeaveSettlementAction.ApplyForParty(party);
        Assert.Null(party.CurrentSettlement);
        Assert.DoesNotContain(party, settlement._partiesCache);
        receiver.Verify(value => value.OnSettlementLeft(party, settlement), Times.Once);
    }

    public void Dispose()
    {
        players.RemovePlayer(player);
        harmony.UnpatchAll(harmony.Id);
        Campaign.Current = previousCampaign;
        ModInformation.SessionMode = previousMode;
    }
}