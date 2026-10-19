using Common;
using Common.Messaging;
using Common.Network;
using Common.Util;
using GameInterface.Registry.Auto;
using GameInterface.Services.Armies;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.CampaignAuthority.Lifecycle;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.PartyBases;
using Moq;
using Serilog;
using System;
using System.Collections.Generic;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;
using Xunit;

namespace GameInterface.Tests.Services.CampaignAuthority;

public class CampaignLifetimeCompletionTests : IDisposable
{
    private readonly ObjectManager objects = new(Mock.Of<ILogger>());
    private readonly MessageBroker broker = new();
    private readonly Mock<INetwork> network = new();
    private readonly Mock<ICampaignAuthorityContext> authority = new();
    private readonly List<IMessage> sent = new();
    private readonly CampaignLifetimeCompletion completion;
    private readonly AutoRegistryHandler<Army> armyHandler;
    private readonly AutoRegistryHandler<PartyBase> partyHandler;
    private AuthorityLease lease = new("room", "writer", "A", 1);

    public CampaignLifetimeCompletionTests()
    {
        authority.Setup(value => value.TryGetLocalAuthorityLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease result) => { result = lease; return result.IsValid && !result.IsPaused; });
        network.Setup(value => value.SendAll(It.IsAny<IMessage>())).Callback<IMessage>(sent.Add);
        completion = new CampaignLifetimeCompletion(objects, broker, authority.Object);
        armyHandler = new AutoRegistryHandler<Army>(new ArmyRegistry(Mock.Of<ILogger>(), Mock.Of<IAutoRegistryFactory>(), objects), broker, network.Object, objects);
        partyHandler = new AutoRegistryHandler<PartyBase>(new PartyBaseRegistry(Mock.Of<ILogger>(), Mock.Of<IAutoRegistryFactory>(), objects), broker, network.Object, objects);
    }

    private Army CreateArmy(bool registered = true)
    {
        var army = ObjectHelper.SkipConstructor<Army>();
        AccessTools.Field(typeof(Army), nameof(Army._parties)).SetValue(army, new MBList<MobileParty>());
        if (registered) Assert.True(objects.AddExisting("Army_test", army, 160));
        return army;
    }

    private (MapEvent Event, MobileParty Garrison) Event()
    {
        var party = ObjectHelper.SkipConstructor<MobileParty>();
        party.IsGarrison = true;
        party.Party = ObjectHelper.SkipConstructor<PartyBase>();
        Assert.True(objects.AddExisting("PartyBase_garrison", party.Party, 32519));
        var participant = ObjectHelper.SkipConstructor<MapEventParty>();
        participant.Party = party.Party;
        var mapEvent = ObjectHelper.SkipConstructor<MapEvent>();
        var first = ObjectHelper.SkipConstructor<MapEventSide>();
        AccessTools.Field(typeof(MapEventSide), nameof(MapEventSide._battleParties)).SetValue(first, new MBList<MapEventParty> { participant });
        var second = ObjectHelper.SkipConstructor<MapEventSide>();
        AccessTools.Field(typeof(MapEventSide), nameof(MapEventSide._battleParties)).SetValue(second, new MBList<MapEventParty>());
        AccessTools.Field(typeof(MapEvent), nameof(MapEvent._sides)).SetValue(mapEvent, new[] { first, second });
        return (mapEvent, party);
    }

    [Fact]
    public void OriginalImmediateDestroyReproducesMissingIdentityBeforeOuterFieldWrite()
    {
        var army = CreateArmy();
        broker.Publish(this, new InstanceDestroyed<Army>(army));
        Assert.False(objects.TryGetHandle(army, out _));
        Assert.Single(sent);
    }

    [Fact]
    public void NestedDisperseRetainsIdentityThroughOuterWritesAndPublishesOriginalDestroyOnce()
    {
        var army = CreateArmy();
        Assert.True(objects.TryGetHandle(army, out var handle));
        var setter = completion.EnterArmy(army);
        var removal = completion.EnterArmy(army);
        var disperse = completion.EnterArmy(army);
        Assert.True(completion.MayDisperse(disperse));
        completion.LeaveArmy(disperse, true, true, null);
        Assert.True(objects.TryGetHandle(army, out var remaining));
        Assert.Equal(handle, remaining);
        var duplicate = completion.EnterArmy(army);
        Assert.False(completion.MayDisperse(duplicate));
        completion.LeaveArmy(duplicate, true, false, null);
        completion.LeaveArmy(removal, false, true, null);
        Assert.True(objects.Contains(army));
        Assert.False(completion.IsComplete);
        Assert.Empty(sent);
        completion.LeaveArmy(setter, false, true, null);
        Assert.False(objects.Contains(army));
        Assert.True(completion.IsComplete);
        Assert.Equal(handle, Assert.IsType<NetworkDestroyInstance<Army>>(Assert.Single(sent)).InstanceHandle);
    }

    [Fact]
    public void StandaloneSuccessfulDisperseUsesOriginalDestroyPipeline()
    {
        var army = CreateArmy();
        completion.LeaveArmy(completion.EnterArmy(army), true, true, null);
        Assert.True(completion.IsComplete);
        Assert.False(objects.Contains(army));
        Assert.IsType<NetworkDestroyInstance<Army>>(Assert.Single(sent));
    }

    [Fact]
    public void ReentrantNativeEarlyReturnDoesNotCertifyRetirement()
    {
        var army = CreateArmy();
        army._armyIsDispersing = true;
        completion.LeaveArmy(completion.EnterArmy(army), true, true, null);
        Assert.True(objects.Contains(army));
        Assert.Empty(sent);
    }

    [Fact]
    public void LiveMemberOrKingdomPreventsRetirementCertificate()
    {
        var army = CreateArmy();
        army._parties.Add(ObjectHelper.SkipConstructor<MobileParty>());
        completion.LeaveArmy(completion.EnterArmy(army), true, true, null);
        Assert.True(objects.Contains(army));
        army._parties.Clear();
        army._kingdom = ObjectHelper.SkipConstructor<Kingdom>();
        completion.LeaveArmy(completion.EnterArmy(army), true, true, null);
        Assert.True(objects.Contains(army));
        Assert.Empty(sent);
    }

    [Fact]
    public void UnknownArmyIsNotGivenSuppressionOrInventedIdentity()
    {
        var army = CreateArmy(false);
        Assert.Null(completion.EnterArmy(army));
        Assert.True(completion.MayDisperse(null));
        Assert.False(objects.Contains(army));
        Assert.True(completion.IsComplete);
    }

    [Fact]
    public void OriginalExceptionKeepsPendingIdentityAndRefusesHandoff()
    {
        var army = CreateArmy();
        var error = new InvalidOperationException("native disperse failed");
        completion.LeaveArmy(completion.EnterArmy(army), true, true, error);
        Assert.False(completion.IsComplete);
        Assert.True(objects.Contains(army));
        Assert.Empty(sent);
        Assert.False(new CampaignAsyncTaskDrain(completion).TryDrain(ObjectHelper.SkipConstructor<Campaign>()));
    }

    [Fact]
    public void ChangedEpochCannotCompleteOrRetireOldScope()
    {
        var army = CreateArmy();
        var scope = completion.EnterArmy(army);
        lease = new AuthorityLease("room", "writer2", "B", 2);
        completion.LeaveArmy(scope, true, true, null);
        Assert.False(completion.IsComplete);
        Assert.True(objects.Contains(army));
        Assert.Empty(sent);
    }

    [Fact]
    public void NewMembersAfterSuccessfulDisperseDoNotReceiveDuplicateSuppressionOrRetirement()
    {
        var army = CreateArmy();
        var outer = completion.EnterArmy(army);
        completion.LeaveArmy(completion.EnterArmy(army), true, true, null);
        army._parties.Add(ObjectHelper.SkipConstructor<MobileParty>());
        Assert.Throws<InvalidOperationException>(() => completion.MayDisperse(outer));
        completion.LeaveArmy(outer, false, true, null);
        Assert.True(objects.Contains(army));
        Assert.False(completion.IsComplete);
        Assert.Empty(sent);
    }

    [Fact]
    public void ChangedSessionWithSameEpochCannotCompleteScope()
    {
        var army = CreateArmy();
        var scope = completion.EnterArmy(army);
        lease = new AuthorityLease("other-room", "writer", "A", 1);
        completion.LeaveArmy(scope, true, true, null);
        Assert.False(completion.IsComplete);
        Assert.True(objects.Contains(army));
        Assert.Empty(sent);
    }

    [Fact]
    public void GarrisonIdentitySurvivesOriginalFinalizationAndAllSubscribersThenDestroysOnce()
    {
        var (mapEvent, party) = Event();
        Assert.True(objects.TryGetHandle(party.Party, out var handle));
        var scope = completion.EnterFinalization(mapEvent);
        Assert.True(completion.TryHoldGarrison(party));
        Assert.True(completion.TryHoldGarrison(party));
        mapEvent._state = MapEventState.WaitingRemoval;
        // Original finalization and all normal postfix/subscribers still resolve the exact value handle.
        Assert.True(objects.TryGetHandle(party.Party, out var finalHandle));
        Assert.Equal(handle, finalHandle);
        Assert.False(completion.IsComplete);
        Assert.Empty(sent);
        completion.LeaveFinalization(scope, true, null);
        Assert.True(completion.IsComplete);
        Assert.False(objects.Contains(party.Party));
        Assert.Equal(handle, Assert.IsType<NetworkDestroyInstance<PartyBase>>(Assert.Single(sent)).InstanceHandle);
    }

    [Fact]
    public void GarrisonOutsideExactEventGraphUsesExistingImmediateDestroy()
    {
        var (mapEvent, _) = Event();
        var other = ObjectHelper.SkipConstructor<MobileParty>();
        other.IsGarrison = true;
        other.Party = ObjectHelper.SkipConstructor<PartyBase>();
        Assert.True(objects.AddExisting("PartyBase_other", other.Party, 32520));
        completion.EnterFinalization(mapEvent);
        Assert.False(completion.TryHoldGarrison(other));
        broker.Publish(this, new InstanceDestroyed<PartyBase>(other.Party));
        Assert.False(objects.Contains(other.Party));
    }

    [Fact]
    public void OrdinaryPartyAndAlreadyFinalizedEventDoNotAcquireHold()
    {
        var (mapEvent, party) = Event();
        party.IsGarrison = false;
        Assert.False(completion.TryHoldGarrison(party));
        mapEvent._state = MapEventState.WaitingRemoval;
        Assert.Null(completion.EnterFinalization(mapEvent));
        Assert.True(completion.IsComplete);
    }

    [Fact]
    public void FinalizationExceptionCannotReleaseHeldIdentityOrPassDrain()
    {
        var (mapEvent, party) = Event();
        var scope = completion.EnterFinalization(mapEvent);
        Assert.True(completion.TryHoldGarrison(party));
        mapEvent._state = MapEventState.WaitingRemoval;
        completion.LeaveFinalization(scope, true, new InvalidOperationException("postfix failed"));
        Assert.False(completion.IsComplete);
        Assert.True(objects.Contains(party.Party));
        Assert.Empty(sent);
        Assert.False(new CampaignAsyncTaskDrain(completion).TryDrain(ObjectHelper.SkipConstructor<Campaign>()));
    }

    [Fact]
    public void SkippedOrIncompleteOriginalFinalizationCannotAuthorizeRelease()
    {
        var (mapEvent, party) = Event();
        var scope = completion.EnterFinalization(mapEvent);
        Assert.True(completion.TryHoldGarrison(party));
        completion.LeaveFinalization(scope, false, null);
        Assert.False(completion.IsComplete);
        Assert.True(objects.Contains(party.Party));
    }

    public void Dispose() { armyHandler.Dispose(); partyHandler.Dispose(); broker.Dispose(); }
}


