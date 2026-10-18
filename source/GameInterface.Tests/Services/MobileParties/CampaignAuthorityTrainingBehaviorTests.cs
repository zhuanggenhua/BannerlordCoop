using Autofac;
using Common;
using Common.Util;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
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
