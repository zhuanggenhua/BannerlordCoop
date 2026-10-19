using Autofac;
using Common;
using Common.Messaging;
using Common.Util;
using GameInterface.Policies;
using GameInterface.Services.Armies.Messages;
using GameInterface.Services.Armies.Patches;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using GameInterface.Services.MapEvents.Messages.Leave;
using GameInterface.Services.MapEvents.Participation;
using GameInterface.Services.MapEvents.Patches;
using HarmonyLib;
using Moq;
using System;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using Xunit;

namespace GameInterface.Tests.Services.CampaignAuthority;

[Collection(ModInformationRoleCollection.Name)]
public class ArmyAndMapEventAuthorityRoleTests
{
    private static int resultCommits;
    private static bool CountCommit(ref int __result) { resultCommits++; __result = 0; return false; }
    private static bool ReadyGraph(ref bool __result) { __result = true; return false; }

    private static void WithRole(SessionMode mode, string controllerId, string state, Action<bool> action)
    {
        var previousMode = ModInformation.SessionMode;
        ContainerProvider.TryGetContainer(out var previousContainer);
        var session = new SessionIdentity();
        var lease = state == "paused" ? new AuthorityLease("room", "paused", "", 3, true) : new AuthorityLease("room", "writer-B", "B", 2);
        if (state != "unknown") Assert.True(session.TryApply(lease, "A"));
        if (state == "frozen") Assert.True(session.TryFreezeLocalAuthority(lease, 1));
        var controller = new ControllerIdProvider(); controller.SetControllerId(controllerId);
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new CampaignAuthorityContext(session, controller)).As<ICampaignAuthorityContext>();
        builder.RegisterInstance(Mock.Of<ISyncPolicy>(value => value.AllowOriginal() == false)).As<ISyncPolicy>();
        using var container = builder.Build();
        ModInformation.SessionMode = mode; ContainerProvider.SetContainer(container);
        try { action(state == "active" && controllerId == "B" || state == "unknown" && mode == SessionMode.ListenAuthority); }
        finally
        {
            ModInformation.SessionMode = previousMode;
            if (previousContainer != null) ContainerProvider.SetContainer(previousContainer); else ContainerProvider.Clear();
        }
    }

    [Theory]
    [InlineData(SessionMode.ListenAuthority, "A", "active")]
    [InlineData(SessionMode.RemoteReplica, "B", "active")]
    [InlineData(SessionMode.RemoteReplica, "B", "frozen")]
    [InlineData(SessionMode.ListenAuthority, "A", "paused")]
    [InlineData(SessionMode.ListenAuthority, "A", "unknown")]
    [InlineData(SessionMode.RemoteReplica, "B", "unknown")]
    public void ActualArmyPublishPrefixesUseCurrentWriterAndPreserveAllowedReceive(SessionMode mode, string controller, string state)
    {
        WithRole(mode, controller, state, eligible =>
        {
            var army = ObjectHelper.SkipConstructor<Army>();
            var party = ObjectHelper.SkipConstructor<MobileParty>();
            int published = 0;
            Action<MessagePayload<MobilePartyInArmyAdded>> add = _ => published++;
            Action<MessagePayload<MobilePartyInArmyRemoved>> remove = _ => published++;
            Action<MessagePayload<SetArmyKingdom>> kingdom = _ => published++;
            Action<MessagePayload<ArmyAiBehaviorObjectChanged>> objective = _ => published++;
            MessageBroker.Instance.Subscribe(add); MessageBroker.Instance.Subscribe(remove);
            MessageBroker.Instance.Subscribe(kingdom); MessageBroker.Instance.Subscribe(objective);
            try
            {
                bool Run(string method) => (bool)AccessTools.Method(typeof(ArmyPatches), method).Invoke(null, new object[] { army, party })!;
                bool Objective() => (bool)AccessTools.Method(typeof(ArmyAiBehaviorObjectPatch), "Prefix").Invoke(null, new object?[] { army, null })!;
                Assert.Equal(eligible, Run("OnAddPartyInternalPrefix"));
                Assert.Equal(eligible, Run("OnRemovePartyInternalPrefix"));
                Assert.Equal(eligible, ArmyPatches.ArmySetKingdomPrefix(army, null));
                Assert.Equal(eligible, Objective());
                Assert.Equal(eligible ? 4 : 0, published);
                Assert.Equal(eligible, ArmyPatches.CanRunAuthoritativeSideEffects());
                using (new AllowedThread())
                {
                    Assert.True(Run("OnAddPartyInternalPrefix"));
                    Assert.True(Run("OnRemovePartyInternalPrefix"));
                    Assert.True(ArmyPatches.ArmySetKingdomPrefix(army, null));
                    Assert.True(Objective());
                    Assert.False(ArmyPatches.CanRunAuthoritativeSideEffects());
                }
                Assert.Equal(eligible ? 4 : 0, published);
            }
            finally
            {
                MessageBroker.Instance.Unsubscribe(add); MessageBroker.Instance.Unsubscribe(remove);
                MessageBroker.Instance.Unsubscribe(kingdom); MessageBroker.Instance.Unsubscribe(objective);
            }
        });
    }

    [Theory]
    [InlineData(SessionMode.ListenAuthority, "A", "active")]
    [InlineData(SessionMode.RemoteReplica, "B", "active")]
    [InlineData(SessionMode.RemoteReplica, "B", "frozen")]
    [InlineData(SessionMode.RemoteReplica, "B", "paused")]
    [InlineData(SessionMode.ListenAuthority, "A", "unknown")]
    [InlineData(SessionMode.RemoteReplica, "B", "unknown")]
    public void ActualMapEventFinalizeCommitAndUpdatePrefixesUseWorldWriter(SessionMode mode, string controller, string state)
    {
        WithRole(mode, controller, state, eligible =>
        {
            var mapEvent = ObjectHelper.SkipConstructor<MapEvent>();
            AccessTools.Field(typeof(MapEvent), nameof(MapEvent._sides)).SetValue(mapEvent, Array.Empty<MapEventSide>());
            object[] finalizeArgs = { mapEvent, false };
            bool mayFinalize = (bool)AccessTools.Method(typeof(MapEventPatches), "Prefix_FinalizeEventAux").Invoke(null, finalizeArgs)!;
            Assert.Equal(eligible, mayFinalize);
            Assert.Equal(eligible, finalizeArgs[1]);
            using var harmony = new HarmonyCleanup("tests.map-event-authority-prefixes");
            var commit = AccessTools.GetDeclaredMethods(typeof(MapEventPatches)).Single(value => value.Name == "CommitCalculatedMapEventResults" && value.GetParameters().Length == 3);
            harmony.Harmony.Patch(commit, prefix: new HarmonyMethod(typeof(ArmyAndMapEventAuthorityRoleTests), nameof(CountCommit)));
            harmony.Harmony.Patch(AccessTools.Method(typeof(MapEventPatches), "RepairLeaderParties"), prefix: new HarmonyMethod(typeof(ArmyAndMapEventAuthorityRoleTests), nameof(ReadyGraph)));
            resultCommits = 0;
            Assert.False((bool)AccessTools.Method(typeof(MapEventPatches), "Prefix_CommitCalculatedMapEventResults").Invoke(null, new object[] { mapEvent })!);
            Assert.Equal(eligible ? 1 : 0, resultCommits);
            Assert.Equal(eligible, (bool)AccessTools.Method(typeof(MapEventPatches), "PrefixUpdate").Invoke(null, new object[] { mapEvent })!);
            using (new AllowedThread())
            {
                finalizeArgs[1] = false;
                Assert.True((bool)AccessTools.Method(typeof(MapEventPatches), "Prefix_FinalizeEventAux").Invoke(null, finalizeArgs)!);
                Assert.False((bool)finalizeArgs[1]);
            }
        });
    }

    private sealed class HarmonyCleanup : IDisposable
    {
        internal Harmony Harmony { get; }
        internal HarmonyCleanup(string id) => Harmony = new Harmony(id);
        public void Dispose() => Harmony.UnpatchAll(Harmony.Id);
    }
}
