using Autofac;
using Common;
using Common.Commands;
using Common.Util;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.MobileParties.Patches.Disable;
using HarmonyLib;
using Moq;
using System;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.ComponentInterfaces;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using Xunit;

namespace GameInterface.Tests.Services.MobileParties;

[Collection(ModInformationRoleCollection.Name)]
public class CampaignAuthorityTrainingBehaviorTests : IDisposable
{
    private readonly Campaign previousCampaign = Campaign.Current;
    private readonly SessionMode previousMode = ModInformation.SessionMode;
    private readonly ILifetimeScope previousContainer;
    private readonly IContainer container;
    private readonly Harmony harmony = new("Coop.Tests.CampaignTraining." + Guid.NewGuid().ToString("N"));
    private readonly SessionIdentity identity = new();
    private readonly ControllerIdProvider controller = new();
    private readonly MobilePartyTrainingBehavior behavior = new();
    private readonly Campaign campaign = ObjectHelper.SkipConstructor<Campaign>();
    private readonly Mock<PartyTrainingModel> trainingModel = new();
    private readonly MobileParty party = ObjectHelper.SkipConstructor<MobileParty>();
    private readonly CharacterObject troop = new();
    private static int hourlyOriginalCalls;
    private static int upgradeOriginalCalls;

    static CampaignAuthorityTrainingBehaviorTests() =>
        RuntimeHelpers.RunModuleConstructor(typeof(Coop.Tests.Mocks.TestNetwork).Module.ModuleHandle);

    public CampaignAuthorityTrainingBehaviorTests()
    {
        ContainerProvider.TryGetContainer(out previousContainer);
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new CampaignAuthorityContext(identity, controller)).As<ICampaignAuthorityContext>();
        container = builder.Build();
        ContainerProvider.SetContainer(container);
        campaign.CampaignEvents = new CampaignEvents();
        campaign._gameModels = ObjectHelper.SkipConstructor<GameModels>();
        campaign._gameModels.PartyTrainingModel = trainingModel.Object;
        trainingModel.Setup(model => model.GetEffectiveDailyExperience(party, It.IsAny<TroopRosterElement>()))
            .Returns(new ExplainedNumber(7f));
        var roster = new TroopRoster();
        roster.AddToCounts(troop, 4, xpChange: 100);
        party.Party = ObjectHelper.SkipConstructor<PartyBase>();
        party.Party.MemberRoster = roster;
        party.IsDisbanding = true; // Skip the separate optional Bow perk after the real roster-training loop.
        Campaign.Current = campaign;
        harmony.CreateClassProcessor(typeof(DisableMobilePartyTrainingBehavior)).Patch();
        hourlyOriginalCalls = 0;
        upgradeOriginalCalls = 0;
        harmony.Patch(AccessTools.Method(typeof(MobilePartyTrainingBehavior), nameof(MobilePartyTrainingBehavior.HourlyTickParty)),
            postfix: new HarmonyMethod(typeof(CampaignAuthorityTrainingBehaviorTests), nameof(RecordHourlyOriginal)));
        harmony.Patch(AccessTools.Method(typeof(MobilePartyTrainingBehavior), nameof(MobilePartyTrainingBehavior.OnPlayerUpgradedTroops)),
            postfix: new HarmonyMethod(typeof(CampaignAuthorityTrainingBehaviorTests), nameof(RecordUpgradeOriginal)));
        behavior.RegisterEvents();
    }

    [Fact]
    public void NativeDailyTrainingMovesFromAToBAndBackWithoutRegisteringListenersAgain()
    {
        int expectedXp = 100;
        foreach (var lease in new[]
        {
            new AuthorityLease("room", "writer-a", "a", 1),
            new AuthorityLease("room", "writer-b", "b", 2),
            new AuthorityLease("room", "writer-a-returned", "a", 3),
        })
        {
            Assert.True(identity.TryApply(lease));
            foreach (string local in new[] { "a", "b" })
            {
                SetLocal(local);
                ((MbEvent<MobileParty>)CampaignEvents.DailyTickPartyEvent).Invoke(party);
                if (local == lease.AuthorityControllerId) expectedXp += 28;
                Assert.Equal(expectedXp, party.MemberRoster.GetElementXp(troop));
            }
            AssertSingleNativeListeners();
        }
        trainingModel.Verify(model => model.GetEffectiveDailyExperience(party, It.IsAny<TroopRosterElement>()), Times.Exactly(3));
        Assert.Equal(0, upgradeOriginalCalls); // Daily training does not re-run the separate player-upgrade path.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeDailyTrainingDoesNotRunForPausedOrFrozenLease(bool frozen)
    {
        var lease = new AuthorityLease("room", "writer", frozen ? "b" : "", 1, isPaused: !frozen);
        Assert.True(identity.TryApply(lease));
        SetLocal("b");
        if (frozen) Assert.True(identity.TryFreezeLocalAuthority(lease, 1));

        using (new AllowedThread())
            ((MbEvent<MobileParty>)CampaignEvents.DailyTickPartyEvent).Invoke(party);

        Assert.Equal(100, party.MemberRoster.GetElementXp(troop));
        trainingModel.Verify(model => model.GetEffectiveDailyExperience(party, It.IsAny<TroopRosterElement>()), Times.Never);
        AssertSingleNativeListeners();
    }

    [Theory]
    [InlineData(SessionMode.DedicatedAuthority, true)]
    [InlineData(SessionMode.ListenAuthority, true)]
    [InlineData(SessionMode.RemoteReplica, false)]
    public void UnconfirmedSessionPreservesLegacyTrainingExecution(SessionMode mode, bool runs)
    {
        ModInformation.SessionMode = mode;
        ((MbEvent<MobileParty>)CampaignEvents.DailyTickPartyEvent).Invoke(party);
        Assert.Equal(runs ? 128 : 100, party.MemberRoster.GetElementXp(troop));
    }

    [Theory]
    [InlineData("a", "b", false, false)]
    [InlineData("b", "a", false, false)]
    [InlineData("a", "a", false, true)]
    [InlineData("b", "b", false, true)]
    [InlineData("a", "", true, false)]
    public void ObserverPausedAndFrozenActionEventsDoNotReplayTrainingSideEffects(
        string local, string writer, bool paused, bool frozen)
    {
        var lease = new AuthorityLease("room", "writer", writer, 1, paused);
        Assert.True(identity.TryApply(lease));
        SetLocal(local);
        if (frozen) Assert.True(identity.TryFreezeLocalAuthority(lease, 1));
        using (new AllowedThread())
        {
            ((MbEvent<MobileParty>)CampaignEvents.HourlyTickPartyEvent).Invoke(party);
            ((MbEvent<CharacterObject, CharacterObject, int>)CampaignEvents.PlayerUpgradedTroopsEvent)
                .Invoke(troop, troop, 1);
        }
        Assert.Equal(0, hourlyOriginalCalls);
        Assert.Equal(0, upgradeOriginalCalls);
        Assert.Equal(100, party.MemberRoster.GetElementXp(troop));
        AssertSingleNativeListeners();
    }

    [Theory]
    [InlineData("a", "b")]
    [InlineData("b", "a")]
    public void NativeHourlyListenerRemainsReadyAfterPromotion(string initialWriter, string nextWriter)
    {
        SetLocal(nextWriter);
        Assert.True(identity.TryApply(new AuthorityLease("room", "first", initialWriter, 1)));
        ((MbEvent<MobileParty>)CampaignEvents.HourlyTickPartyEvent).Invoke(party);
        Assert.Equal(0, hourlyOriginalCalls);

        Assert.True(identity.TryApply(new AuthorityLease("room", "second", nextWriter, 2)));
        ((MbEvent<MobileParty>)CampaignEvents.HourlyTickPartyEvent).Invoke(party);
        Assert.Equal(1, hourlyOriginalCalls);
        AssertSingleNativeListeners();
    }

#if DEBUG
    [Fact]
    public void DailyProbeCallsNativeTrainingOnceAndRejectsTheObserver()
    {
        Assert.True(identity.TryApply(new AuthorityLease("room", "writer-b", "b", 1)));
        SetLocal("b");
        var manager = new Mock<ICampaignBehaviorManager>();
        manager.Setup(value => value.GetBehavior<MobilePartyTrainingBehavior>()).Returns(behavior);
        campaign._campaignBehaviorManager = manager.Object;
        var objects = new Mock<IObjectManager>();
        var foundParty = party;
        objects.Setup(value => value.TryGetObject("npc", out foundParty)).Returns(true);
        var command = new DisableMobilePartyTrainingBehavior.TrainingDailyCoopCommand(objects.Object);
        var args = new CoopCommandArgsFactory().FromValues(new[] { "npc" });

        var result = command.ProcessCommand(args);
        Assert.True(result.Succeeded);
        Assert.Contains("OnDailyTickParty", result.Output);
        Assert.Equal(128, party.MemberRoster.GetElementXp(troop));
        trainingModel.Verify(model => model.GetEffectiveDailyExperience(party, It.IsAny<TroopRosterElement>()), Times.Once);

        SetLocal("a");
        Assert.Equal("not_world_writer", command.ProcessCommand(args).ErrorCode);
        Assert.Equal(128, party.MemberRoster.GetElementXp(troop));
    }
#endif

    private static void RecordHourlyOriginal(bool __runOriginal)
    {
        if (__runOriginal) hourlyOriginalCalls++;
    }

    private static void RecordUpgradeOriginal(bool __runOriginal)
    {
        if (__runOriginal) upgradeOriginalCalls++;
    }

    private void SetLocal(string local)
    {
        controller.SetControllerId(local);
        ModInformation.SessionMode = local == "a" ? SessionMode.ListenAuthority : SessionMode.RemoteReplica;
    }

    private void AssertSingleNativeListeners()
    {
        Assert.Same(behavior, ((MbEvent<MobileParty>)CampaignEvents.HourlyTickPartyEvent)._nonSerializedListenerList.Owner);
        Assert.Null(((MbEvent<MobileParty>)CampaignEvents.HourlyTickPartyEvent)._nonSerializedListenerList.Next);
        Assert.Same(behavior, ((MbEvent<MobileParty>)CampaignEvents.DailyTickPartyEvent)._nonSerializedListenerList.Owner);
        Assert.Null(((MbEvent<MobileParty>)CampaignEvents.DailyTickPartyEvent)._nonSerializedListenerList.Next);
        var upgrades = (MbEvent<CharacterObject, CharacterObject, int>)CampaignEvents.PlayerUpgradedTroopsEvent;
        Assert.Same(behavior, upgrades._nonSerializedListenerList.Owner);
        Assert.Null(upgrades._nonSerializedListenerList.Next);
    }

    public void Dispose()
    {
        harmony.UnpatchAll(harmony.Id);
        Campaign.Current = previousCampaign;
        ModInformation.SessionMode = previousMode;
        if (previousContainer != null) ContainerProvider.SetContainer(previousContainer);
        else ContainerProvider.Clear();
        container.Dispose();
    }
}
