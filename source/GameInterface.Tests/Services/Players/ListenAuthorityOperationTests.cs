using Common;
using Common.Messaging;
using GameInterface.Services.Entity;
using GameInterface.Services.MobileParties.Data;
using GameInterface.Services.MobileParties.Handlers;
using GameInterface.Services.MobileParties.Interfaces;
using GameInterface.Services.MobileParties.Messages.Behavior;
using GameInterface.Services.MobileParties.Patches;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.UI.Patches;
using GameInterface.Services.CampaignService.Patches;
using TaleWorlds.CampaignSystem.GameState;
using Moq;
using System;
using TaleWorlds.CampaignSystem.Party;
using Xunit;

namespace GameInterface.Tests.Services.Players;

[Collection(ModInformationRoleCollection.Name)]
public class ListenAuthorityOperationTests : IDisposable
{
    [Fact]
    public void NativeListenLeaveConsequenceIsNotConvertedIntoClientRequest()
    {
        ModInformation.SessionMode = SessionMode.ListenAuthority;
        Assert.True(PlayerLeaveSettlementPatch.RequestLeave());
    }
    private readonly SessionMode previous = ModInformation.SessionMode;
    public void Dispose() => ModInformation.SessionMode = previous;

    [Fact]
    public void AlreadyAppliedLocalMovementPublishesResultWithoutRequestOrSecondApply()
    {
        ModInformation.SessionMode = SessionMode.ListenAuthority;
        var broker = new Mock<IMessageBroker>();
        var snapshot = new Mock<IMobilePartyBehaviorSnapshot>();
        var party = Common.Util.ObjectHelper.SkipConstructor<MobileParty>();
        var data = new PartyBehaviorUpdateData();
        snapshot.Setup(s => s.TryCreate(party, out data)).Returns(true);
        using var handler = new MobilePartyBehaviorHandler(broker.Object, Mock.Of<IControllerIdProvider>(),
            Mock.Of<IMobilePartyInterface>(), Mock.Of<IObjectManager>(), snapshot.Object);
        handler.Handle_PartyBehaviorChanged(new MessagePayload<PartyBehaviorChangeAttempted>(
            party, new PartyBehaviorChangeAttempted(party)));
        broker.Verify(b => b.Publish(It.IsAny<object>(), It.IsAny<PartyBehaviorUpdated>()), Times.Once);
        broker.Verify(b => b.Publish(It.IsAny<object>(), It.IsAny<ControlledPartyBehaviorUpdated>()), Times.Never);
        snapshot.Verify(s => s.TryCreate(party, out data), Times.Once);
        snapshot.VerifyNoOtherCalls();
    }

    [Fact]
    public void ListenAuthorityPreservesRealTickException()
    {
        ModInformation.SessionMode = SessionMode.ListenAuthority;
        var failure = new InvalidOperationException("simulation failure");
        Assert.Same(failure, CampaignRobustnessPatches.Finalizer_RealTick(failure));
        Assert.Null(CampaignRobustnessPatches.Finalizer_RealTick(null));
    }

    [Fact]
    public void DedicatedAuthorityKeepsExistingRealTickRecovery()
    {
        ModInformation.SessionMode = SessionMode.DedicatedAuthority;
        Assert.Null(CampaignRobustnessPatches.Finalizer_RealTick(new InvalidOperationException("legacy failure")));
    }

    [Fact]
    public void ListenAuthorityDoesNotForcePartyVisibilityEvenInDebug()
    {
        ModInformation.SessionMode = SessionMode.ListenAuthority;
        var party = Common.Util.ObjectHelper.SkipConstructor<MobileParty>();
        bool spotted = false, visible = false, inspected = false;
        PartyIsSpottedServerPatch.Postfix(party, ref spotted);
        PartyVisibilityOnServerPatch.PrefixIsVisible(party, ref visible);
        PartyVisibilityOnServerPatch.PrefixIsInspected(ref inspected);
        Assert.False(spotted);
        Assert.False(visible);
        Assert.False(inspected);
    }

    [Theory]
    [InlineData(typeof(InventoryState))]
    [InlineData(typeof(PartyState))]
    [InlineData(typeof(ClanState))]
    [InlineData(typeof(QuestsState))]
    [InlineData(typeof(CharacterDeveloperState))]
    [InlineData(typeof(KingdomState))]
    public void LocalAuthorityCanEnterNativePlayerScreensWhileDedicatedCannot(Type type)
    {
        var state = (TaleWorlds.Core.GameState)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
        ModInformation.SessionMode = SessionMode.DedicatedAuthority;
        Assert.False(GameUIDisable.PushStatePatch(state));
        ModInformation.SessionMode = SessionMode.ListenAuthority;
        Assert.True(GameUIDisable.PushStatePatch(state));
    }
}
