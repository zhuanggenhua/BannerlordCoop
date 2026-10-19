using Autofac;
using Common;
using Common.Messaging;
using Common.Util;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using GameInterface.Services.MobileParties.Messages.Behavior;
using GameInterface.Services.MobileParties.Patches;
using GameInterface.Services.SiegeEvents.Commands;
using HarmonyLib;
using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using Xunit;

namespace GameInterface.Tests.Services.MobileParties;

[Collection(ModInformationRoleCollection.Name)]
public sealed class PlayerLeaveAuthorityRoutingTests
{
    [Theory]
    [InlineData("a", "a", true, false, false, true, 0)]
    [InlineData("b", "b", false, false, false, true, 0)]
    [InlineData("a", "b", true, false, false, false, 1)]
    [InlineData("b", "a", false, false, false, false, 1)]
    [InlineData("a", "a", true, false, true, false, 0)]
    [InlineData("b", "b", false, false, true, false, 0)]
    [InlineData("a", "", true, true, false, false, 0)]
    [InlineData("b", "", false, true, false, false, 0)]
    public void ConfirmedWriterLeavesNativelyWhileFrozenOrPausedRolesNeverPublish(
        string local, string writer, bool host, bool paused, bool frozen, bool native, int requests)
    {
        using var fixture = new Fixture(local, writer, host, paused, frozen);
        Assert.Equal(native, PlayerLeaveSettlementPatch.RequestLeave());
        Assert.Equal(requests, fixture.Requests);
    }

    [Fact]
    public void MissingControllerCannotConvertFrozenWriterIntoObserverRequest()
    {
        using var fixture = new Fixture("b", "b", false, frozen: true, registerController: false);
        Assert.False(PlayerLeaveSettlementPatch.RequestLeave());
        Assert.Equal(0, fixture.Requests);
    }

    [Fact]
    public void UnconfirmedListenSessionKeepsNativeLegacyConsequence()
    {
        using var fixture = new Fixture("a", "a", true, confirmed: false);
        Assert.True(PlayerLeaveSettlementPatch.RequestLeave());
        Assert.Equal(0, fixture.Requests);
    }

    [Fact]
    public void DebugRemoteWriterCallsPatchedNativeConsequenceOnceWithoutRequestOrAllowance()
    {
        using var fixture = new Fixture("b", "b", false);
        fixture.Party._currentSettlement = ObjectHelper.SkipConstructor<Settlement>();
        var harmony = new Harmony(nameof(DebugRemoteWriterCallsPatchedNativeConsequenceOnceWithoutRequestOrAllowance));
        nativeCalls = 0;
        try
        {
            harmony.CreateClassProcessor(typeof(PlayerLeaveSettlementPatch)).Patch();
            harmony.Patch(AccessTools.Method(typeof(PlayerTownVisitCampaignBehavior), "game_menu_settlement_leave_on_consequence"),
                prefix: new HarmonyMethod(typeof(PlayerLeaveAuthorityRoutingTests), nameof(CaptureNativeConsequence)) { priority = Priority.Last });

            var result = new SiegeDebugCommand.LeaveSettlementCoopCommand().ProcessCommand(null!);

            Assert.Equal(1, nativeCalls);
            Assert.Equal(0, fixture.Requests);
            Assert.False(result.Succeeded); // The probe intercepted the engine body, so no leave completed.
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DebugPausedOrFrozenRemoteWriterDoesNotInvokeNativeOrRequest(bool frozen)
    {
        using var fixture = new Fixture("b", frozen ? "b" : "", false, paused: !frozen, frozen: frozen);
        fixture.Party._currentSettlement = ObjectHelper.SkipConstructor<Settlement>();
        var result = new SiegeDebugCommand.LeaveSettlementCoopCommand().ProcessCommand(null!);
        Assert.False(result.Succeeded);
        Assert.Equal(0, fixture.Requests);
    }

    private static int nativeCalls;
    private static bool CaptureNativeConsequence(MenuCallbackArgs args)
    {
        Assert.Null(args);
        Assert.False(AllowedThread.IsThisThreadAllowed());
        nativeCalls++;
        return false;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SessionMode previousMode = ModInformation.SessionMode;
        private readonly Campaign previousCampaign = Campaign.Current;
        private readonly bool hadContainer;
        private readonly ILifetimeScope previousContainer;
        private readonly IContainer container;
        private readonly Action<MessagePayload<EndSettlementEncounterAttempted>> capture;
        public MobileParty Party { get; } = ObjectHelper.SkipConstructor<MobileParty>();
        public int Requests { get; private set; }

        public Fixture(string local, string writer, bool host, bool paused = false, bool frozen = false,
            bool registerController = true, bool confirmed = true)
        {
            hadContainer = ContainerProvider.TryGetContainer(out previousContainer);
            ModInformation.SessionMode = host ? SessionMode.ListenAuthority : SessionMode.RemoteReplica;
            var identity = new SessionIdentity();
            var lease = new AuthorityLease("room", "world", writer, 5, paused);
            if (confirmed) Assert.True(identity.TryApply(lease, "a"));
            if (frozen) Assert.True(identity.TryFreezeLocalAuthority(lease, 9));
            var controller = new ControllerIdProvider();
            controller.SetControllerId(local);
            var builder = new ContainerBuilder();
            builder.RegisterInstance(new CampaignAuthorityContext(identity, controller)).As<ICampaignAuthorityContext>();
            if (registerController) builder.RegisterInstance(controller).As<IControllerIdProvider>();
            container = builder.Build();
            ContainerProvider.SetContainer(container);
            var campaign = ObjectHelper.SkipConstructor<Campaign>();
            campaign.MainParty = Party;
            Campaign.Current = campaign;
            capture = payload => { Assert.Same(Party, payload.What.Party); Requests++; };
            MessageBroker.Instance.Subscribe(capture);
        }

        public void Dispose()
        {
            MessageBroker.Instance.Unsubscribe(capture);
            Campaign.Current = previousCampaign;
            ModInformation.SessionMode = previousMode;
            if (hadContainer) ContainerProvider.SetContainer(previousContainer);
            else ContainerProvider.Clear();
            container.Dispose();
        }
    }
}
