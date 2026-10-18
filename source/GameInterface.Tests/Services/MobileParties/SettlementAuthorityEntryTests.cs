using Autofac;
using Common;
using Common.Messaging;
using Common.Util;
using GameInterface.Policies;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using GameInterface.Services.MobileParties.Messages.Behavior;
using GameInterface.Services.MobileParties.Patches;
using GameInterface.Services.Settlements.Messages;
using GameInterface.Services.Settlements.Patches;
using HarmonyLib;
using System;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;
using Xunit;

namespace GameInterface.Tests.Services.MobileParties;

[Collection(ModInformationRoleCollection.Name)]
public class SettlementAuthorityEntryTests : IDisposable
{
    private readonly SessionMode previousMode = ModInformation.SessionMode;
    private readonly ILifetimeScope? previousContainer;
    private readonly IContainer container;
    private readonly SessionIdentity identity = new();
    private readonly ControllerIdProvider controller = new();
    private readonly MobileParty party = ObjectHelper.SkipConstructor<MobileParty>();
    private readonly Settlement settlement = ObjectHelper.SkipConstructor<Settlement>();
    private int entered, left, cacheChanges;

    static SettlementAuthorityEntryTests() =>
        RuntimeHelpers.RunModuleConstructor(typeof(Coop.Tests.Mocks.TestNetwork).Module.ModuleHandle);

    public SettlementAuthorityEntryTests()
    {
        ContainerProvider.TryGetContainer(out previousContainer);
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new CampaignAuthorityContext(identity, controller)).As<ICampaignAuthorityContext>();
        builder.RegisterInstance(new GameplayPolicy()).As<ISyncPolicy>();
        container = builder.Build();
        ContainerProvider.SetContainer(container);
        settlement._partiesCache = new MBList<MobileParty>();
        MessageBroker.Instance.Subscribe<PartyEnterSettlementAttempted>(OnEnter);
        MessageBroker.Instance.Subscribe<PartyLeaveSettlementAttempted>(OnLeave);
        MessageBroker.Instance.Subscribe<SettlementChangedMobileParty>(OnCache);
    }

    [Theory]
    [InlineData("a", "a", false, false, true, 1)]
    [InlineData("b", "b", false, false, true, 0)]
    [InlineData("a", "b", false, false, false, 0)]
    [InlineData("b", "a", false, false, false, 0)]
    [InlineData("a", "", true, false, false, 0)]
    [InlineData("b", "", true, false, false, 0)]
    [InlineData("a", "a", false, true, false, 0)]
    [InlineData("b", "b", false, true, false, 0)]
    public void ConfirmedLeaseGatesNativeEntryAndExitWithoutObserverNotifications(
        string local, string writer, bool paused, bool frozen, bool runs, int notifications)
    {
        SetRole(local, writer, paused, frozen);
        Assert.Equal(runs, Enter());
        party._currentSettlement = settlement;
        Assert.Equal(runs, Leave());
        Assert.Equal(notifications, entered);
        Assert.Equal(notifications, left);
    }

    [Theory]
    [InlineData(SessionMode.DedicatedAuthority, true)]
    [InlineData(SessionMode.ListenAuthority, true)]
    [InlineData(SessionMode.RemoteReplica, false)]
    public void UnknownLeasePreservesLegacyActionNotifications(SessionMode mode, bool runs)
    {
        ModInformation.SessionMode = mode;
        Assert.Equal(runs, Enter());
        party._currentSettlement = settlement;
        Assert.Equal(runs, Leave());
        Assert.Equal(1, entered);
        Assert.Equal(1, left);
    }

    [Theory]
    [InlineData("a", "b", false, false)]
    [InlineData("b", "a", false, false)]
    [InlineData("a", "", true, false)]
    [InlineData("b", "b", false, true)]
    public void ReceivedApplicationsKeepNativePathWithoutRepublishing(string local, string writer, bool paused, bool frozen)
    {
        SetRole(local, writer, paused, frozen);
        using (new AllowedThread())
        {
            Assert.True(Enter());
            party._currentSettlement = settlement;
            Assert.True(Leave());
            Assert.True(Cache("AddMobilePartyPrefix"));
            Assert.True(Cache("RemoveMobilePartyPrefix"));
        }
        Assert.Equal(0, entered + left + cacheChanges);
    }

    [Theory]
    [InlineData("a", "a", false, false, true)]
    [InlineData("b", "b", false, false, true)]
    [InlineData("a", "b", false, false, false)]
    [InlineData("b", "a", false, false, false)]
    [InlineData("a", "", true, false, false)]
    [InlineData("b", "", true, false, false)]
    [InlineData("a", "a", false, true, false)]
    [InlineData("b", "b", false, true, false)]
    public void OnlyWriterChangesCacheAndEachMembershipChangePublishesOnce(
        string local, string writer, bool paused, bool frozen, bool writes)
    {
        SetRole(local, writer, paused, frozen);
        Assert.False(Cache("AddMobilePartyPrefix"));
        Assert.False(Cache("AddMobilePartyPrefix"));
        Assert.Equal(writes ? 1 : 0, settlement._partiesCache.Count);
        Assert.Equal(writes ? 1 : 0, cacheChanges);
        if (!writes) settlement._partiesCache.Add(party);
        Assert.False(Cache("RemoveMobilePartyPrefix"));
        Assert.False(Cache("RemoveMobilePartyPrefix"));
        Assert.Equal(writes ? 0 : 1, settlement._partiesCache.Count);
        Assert.Equal(writes ? 2 : 0, cacheChanges);
    }

    [Fact]
    public void ExistingSettlementAndPlayerSwitchGuardsStillSuppressActions()
    {
        SetRole("b", "b", false, false);
        Assert.False(Leave());
        party._currentSettlement = settlement;
        Assert.False(Enter());
        bool previous = LeaveSettlementActionPatches.SuppressForPlayerSwitch;
        try
        {
            LeaveSettlementActionPatches.SuppressForPlayerSwitch = true;
            using (new AllowedThread()) Assert.False(Leave());
        }
        finally { LeaveSettlementActionPatches.SuppressForPlayerSwitch = previous; }
        Assert.Equal(0, entered + left);
    }

    [Theory]
    [InlineData(SessionMode.DedicatedAuthority, false, 1)]
    [InlineData(SessionMode.ListenAuthority, false, 1)]
    [InlineData(SessionMode.RemoteReplica, true, 0)]
    public void UnknownLeasePreservesLegacyCachePath(SessionMode mode, bool nativeAdd, int changes)
    {
        ModInformation.SessionMode = mode;
        Assert.Equal(nativeAdd, Cache("AddMobilePartyPrefix"));
        Assert.Equal(changes, cacheChanges);
        if (nativeAdd) settlement._partiesCache.Add(party);
        Assert.False(Cache("RemoveMobilePartyPrefix"));
        Assert.Equal(changes * 2, cacheChanges);
        Assert.Equal(nativeAdd ? 1 : 0, settlement._partiesCache.Count);
    }

    [Fact]
    public void RepeatedReceivedCacheResultsNeverDuplicateMembership()
    {
        SetRole("a", "b", false, false);
        MobilePartyCachePatch.RunMobileParty(settlement, party, true);
        MobilePartyCachePatch.RunMobileParty(settlement, party, true);
        GameThread.Run(() => { }, blocking: true);
        Assert.Same(party, Assert.Single(settlement._partiesCache));
        MobilePartyCachePatch.RunMobileParty(settlement, party, false);
        MobilePartyCachePatch.RunMobileParty(settlement, party, false);
        GameThread.Run(() => { }, blocking: true);
        Assert.Empty(settlement._partiesCache);
        Assert.Equal(0, cacheChanges);
    }

    private void SetRole(string local, string writer, bool paused, bool frozen)
    {
        ModInformation.SessionMode = local == "a" ? SessionMode.ListenAuthority : SessionMode.RemoteReplica;
        controller.SetControllerId(local);
        var lease = new AuthorityLease("room", "lease", writer, 1, paused);
        Assert.True(identity.TryApply(lease, "a"));
        if (frozen) Assert.True(identity.TryFreezeLocalAuthority(lease, 1));
    }

    private bool Enter() => (bool)AccessTools.Method(typeof(EnterSettlementActionPatches), "ApplyForPartyPrefix")
        .Invoke(null, new object[] { party, settlement });
    private bool Leave() => (bool)AccessTools.Method(typeof(LeaveSettlementActionPatches), "Prefix")
        .Invoke(null, new object[] { party });
    private bool Cache(string method) => (bool)AccessTools.Method(typeof(MobilePartyCachePatch), method)
        .Invoke(null, new object[] { settlement, party });
    private void OnEnter(MessagePayload<PartyEnterSettlementAttempted> _) => entered++;
    private void OnLeave(MessagePayload<PartyLeaveSettlementAttempted> _) => left++;
    private void OnCache(MessagePayload<SettlementChangedMobileParty> _) => cacheChanges++;
    private sealed class GameplayPolicy : ISyncPolicy { public bool AllowOriginal() => false; }

    public void Dispose()
    {
        MessageBroker.Instance.Unsubscribe<PartyEnterSettlementAttempted>(OnEnter);
        MessageBroker.Instance.Unsubscribe<PartyLeaveSettlementAttempted>(OnLeave);
        MessageBroker.Instance.Unsubscribe<SettlementChangedMobileParty>(OnCache);
        ModInformation.SessionMode = previousMode;
        if (previousContainer != null) ContainerProvider.SetContainer(previousContainer);
        else ContainerProvider.Clear();
        container.Dispose();
    }
}
