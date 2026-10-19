using Autofac;
using Common;
using Common.Messaging;
using Common.Network;
using Common.Util;
using GameInterface.Policies;
using GameInterface.Registry.Auto;
using GameInterface.Services.Armies;
using GameInterface.Services.Armies.Handlers;
using GameInterface.Services.Armies.Messages;
using GameInterface.Services.Armies.Patches;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.CampaignAuthority.Lifecycle;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.PartyBases;
using GameInterface.Services.MapEvents.Messages;
using GameInterface.Services.MapEvents.Messages.Leave;
using GameInterface.Services.MapEvents.Patches;
using GameInterface.Services.MapEventParties.Messages;
using HarmonyLib;
using Moq;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Map;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;
using Xunit;

namespace GameInterface.Tests.Services.CampaignAuthority;

[Collection(ModInformationRoleCollection.Name)]
public class CampaignLifetimeNativePathTests : IDisposable
{
    private readonly Harmony harmony = new("tests.campaign-lifetime-native-path");
    private readonly Campaign previousCampaign = Campaign.Current;
    private readonly SessionMode previousMode = ModInformation.SessionMode;
    private readonly ILifetimeScope? previousContainer;
    private readonly ObjectManager objects = new(Mock.Of<ILogger>());
    private readonly List<IMessage> sent = new();
    private readonly IContainer container;
    private readonly CampaignLifetimeCompletion completion;
    private readonly AutoRegistryHandler<Army> handler;
    private static int dispersed, moved;
    private static bool finalObjectiveResolvable;
    private static Exception? injected;
    private static MobileParty? activeGarrison;
    private static bool heldInNative, heldInPostfix;

    static CampaignLifetimeNativePathTests() =>
        RuntimeHelpers.RunModuleConstructor(typeof(Coop.Tests.Mocks.TestNetwork).Module.ModuleHandle);

    public CampaignLifetimeNativePathTests()
    {
        ContainerProvider.TryGetContainer(out previousContainer);
        ModInformation.SessionMode = SessionMode.ListenAuthority;
        var authority = new Mock<ICampaignAuthorityContext>();
        var lease = new AuthorityLease("room", "writer", "A", 1);
        authority.Setup(value => value.TryGetLocalAuthorityLease(out lease)).Returns(true);
        authority.Setup(value => value.TryGetCurrentLease(out lease)).Returns(true);
        var network = new Mock<INetwork>();
        network.Setup(value => value.SendAll(It.IsAny<IMessage>())).Callback<IMessage>(sent.Add);
        completion = new CampaignLifetimeCompletion(objects, MessageBroker.Instance, authority.Object);
        var builder = new ContainerBuilder();
        builder.RegisterInstance(completion);
        builder.RegisterInstance(authority.Object).As<ICampaignAuthorityContext>();
        builder.RegisterInstance(objects).As<IObjectManager>();
        builder.RegisterInstance(Mock.Of<ISyncPolicy>(value => value.AllowOriginal() == false)).As<ISyncPolicy>();
        container = builder.Build();
        ContainerProvider.SetContainer(container);
        handler = new AutoRegistryHandler<Army>(new ArmyRegistry(Mock.Of<ILogger>(), Mock.Of<IAutoRegistryFactory>(), objects), MessageBroker.Instance, network.Object, objects);
        dispersed = moved = 0;
        finalObjectiveResolvable = false;
        injected = null;
        activeGarrison = null;
        heldInNative = heldInPostfix = false;

        // Only environment dependencies are shimmed. The native Army setter, OnRemovePartyInternal,
        // DisbandArmyAction, DisperseInternal, FinishArmyObjective, Kingdom/AiBehaviorObject and
        // DeletePeriodicEvent bodies below are the installed game's actual implementations.
        Patch(typeof(MobilePartyAi), nameof(MobilePartyAi.SetInitiative), nameof(Skip));
        Patch(typeof(MobileParty), nameof(MobileParty.SetMoveModeHold), nameof(Move));
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(PartyBase)).Where(value => value.Name == nameof(PartyBase.UpdateVisibilityAndInspected)))
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(CampaignLifetimeNativePathTests), nameof(Skip)));
        harmony.Patch(AccessTools.Method(typeof(CampaignEventDispatcher), nameof(CampaignEventDispatcher.OnArmyDispersed)),
            postfix: new HarmonyMethod(typeof(CampaignLifetimeNativePathTests), nameof(Dispersed)));
        harmony.Patch(AccessTools.PropertySetter(typeof(Army), nameof(Army.AiBehaviorObject)),
            prefix: new HarmonyMethod(typeof(CampaignLifetimeNativePathTests), nameof(Objective)));
    }

    private void Patch(Type type, string method, string prefix) => harmony.Patch(AccessTools.Method(type, method),
        prefix: new HarmonyMethod(typeof(CampaignLifetimeNativePathTests), prefix));
    private static bool Skip() => false;
    private static bool Move() { moved++; return false; }
    private static void Dispersed() => dispersed++;
    private static void Objective(Army __instance, IMapPoint value)
    {
        if (injected != null) throw injected;
        if (value == null && ContainerProvider.TryResolve<IObjectManager>(out var objects))
            finalObjectiveResolvable = objects.TryGetHandle(__instance, out _);
    }

    private static void OnGameThread(Action action)
    {
        int thread = GameThread.Instance.GameThreadId;
        ExceptionDispatchInfo? error = null;
        try
        {
            GameThread.Run(() =>
            {
                try { action(); }
                catch (Exception exception) { error = ExceptionDispatchInfo.Capture(exception); }
            }, blocking: true);
            error?.Throw();
        }
        finally { Assert.Equal(thread, GameThread.Instance.GameThreadId); }
    }

    private Army SetupArmy()
    {
        var campaign = ObjectHelper.SkipConstructor<Campaign>();
        campaign.MainParty = ObjectHelper.SkipConstructor<MobileParty>();
        campaign.MainParty.Party = ObjectHelper.SkipConstructor<PartyBase>();
        campaign.CampaignEventDispatcher = new CampaignEventDispatcher(Array.Empty<CampaignEventReceiver>());
        Campaign.Current = campaign;
        var party = ObjectHelper.SkipConstructor<MobileParty>();
        party.Ai = ObjectHelper.SkipConstructor<MobilePartyAi>();
        party.Party = ObjectHelper.SkipConstructor<PartyBase>();
        var army = ObjectHelper.SkipConstructor<Army>();
        AccessTools.Field(typeof(Army), nameof(Army._parties)).SetValue(army, new MBList<MobileParty>());
        army.LeaderParty = party;
        army._aiBehaviorObject = party;
        party._army = army;
        army._hourlyTickEvent = new MBCampaignEvent("hourly");
        army._tickEvent = new MBCampaignEvent("tick");
        Assert.True(objects.AddExisting("Army_native", army, 160));
        return army;
    }

    private void Adapter()
    {
        harmony.CreateClassProcessor(typeof(ArmySetterCompletionPatch)).Patch();
        harmony.CreateClassProcessor(typeof(ArmyMemberRemovalCompletionPatch)).Patch();
        harmony.CreateClassProcessor(typeof(ArmyDisperseCompletionPatch)).Patch();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualNativeOuterSetterReproducesOldEarlyRetirementAndAdapterKeepsLastWrite(bool adapter)
    {
        OnGameThread(() =>
        {
            var army = SetupArmy();
            if (adapter) Adapter();
            else harmony.Patch(AccessTools.Method(typeof(Army), nameof(Army.DisperseInternal)),
                postfix: new HarmonyMethod(typeof(LifetimePatches<Army>), nameof(LifetimePatches<Army>.DestroyPostfix)));
            army.LeaderParty.Army = null;
            Assert.Null(army.LeaderParty.Army);
            Assert.Null(army.AiBehaviorObject);
            Assert.Null(army.Kingdom);
            Assert.True(army._hourlyTickEvent.isEventDeleted);
            Assert.True(army._tickEvent.isEventDeleted);
            Assert.Equal(1, moved); // The original FinishArmyObjective movement must never be skipped.
            Assert.Equal(adapter ? 1 : 2, dispersed);
            Assert.Equal(adapter, finalObjectiveResolvable);
            Assert.False(objects.Contains(army));
            Assert.Single(sent.OfType<NetworkDestroyInstance<Army>>());
            Assert.True(completion.IsComplete);
        });
    }

    [Fact]
    public void ActualStandaloneDispersePreservesOriginalObjectiveAndDeletesPeriodicEventsOnce()
    {
        OnGameThread(() =>
        {
            var army = SetupArmy();
            var objective = army.AiBehaviorObject;
            Adapter();
            army.DisperseInternal();
            Assert.Same(objective, army.AiBehaviorObject);
            Assert.Equal(1, dispersed);
            Assert.Equal(0, moved);
            Assert.True(army._hourlyTickEvent.isEventDeleted);
            Assert.True(army._tickEvent.isEventDeleted);
            Assert.False(objects.Contains(army));
            Assert.Single(sent.OfType<NetworkDestroyInstance<Army>>());
        });
    }

    [Fact]
    public void OriginalOuterSetterExceptionIsPreservedAndPendingIdentityBlocksDrain()
    {
        OnGameThread(() =>
        {
            var army = SetupArmy();
            Adapter();
            var error = new InvalidOperationException("injected original objective setter failure");
            injected = error;
            Assert.Same(error, Assert.Throws<InvalidOperationException>(() => army.LeaderParty.Army = null));
            injected = null;
            Assert.Equal(1, dispersed);
            Assert.True(objects.Contains(army));
            Assert.Empty(sent);
            Assert.False(completion.IsComplete);
            Assert.False(new CampaignAsyncTaskDrain(completion).TryDrain(Campaign.Current));
        });
    }

    [Fact]
    public void OriginalObjectiveAndKingdomMessagesPrecedeSingleOriginalDestroy()
    {
        OnGameThread(() =>
        {
            var army = SetupArmy();
            Adapter();
            harmony.CreateClassProcessor(typeof(ArmyAiBehaviorObjectPatch)).Patch();
            harmony.Patch(AccessTools.PropertySetter(typeof(Army), nameof(Army.Kingdom)),
                prefix: new HarmonyMethod(typeof(ArmyPatches), nameof(ArmyPatches.ArmySetKingdomPrefix)));
            var network = new Mock<INetwork>();
            network.Setup(value => value.SendAll(It.IsAny<IMessage>())).Callback<IMessage>(sent.Add);
            using var armyMessages = new ArmyHandler(MessageBroker.Instance, network.Object, objects);
            army.LeaderParty.Army = null;
            Assert.Collection(sent,
                value => Assert.IsType<NetworkSetArmyKingdom>(value),
                value => Assert.Null(Assert.IsType<NetworkSetArmyAiBehaviorObject>(value).AiBehaviorObjectId),
                value => Assert.IsType<NetworkDestroyInstance<Army>>(value));
            Assert.True(finalObjectiveResolvable);
            Assert.Equal(1, dispersed);
        });
    }

    [Theory]
    [InlineData(SessionMode.ListenAuthority)]
    [InlineData(SessionMode.RemoteReplica)]
    public void ConfirmedArmyRemovalReceiveUsesOriginalReplicaCleanupWithoutDisperseOrRebroadcast(SessionMode mode)
    {
        OnGameThread(() =>
        {
            var army = SetupArmy();
            var party = army.LeaderParty;
            army._parties.Add(party);
            Assert.True(objects.AddExisting("party", party, 161));
            ModInformation.SessionMode = mode;
            var lease = new AuthorityLease("room", "other-writer", "other", 2);
            var observer = new Mock<ICampaignAuthorityContext>();
            observer.Setup(value => value.TryGetCurrentLease(out lease)).Returns(true);
            observer.Setup(value => value.TryGetLocalAuthorityLease(out lease)).Returns(false);
            var builder = new ContainerBuilder();
            builder.RegisterInstance(observer.Object).As<ICampaignAuthorityContext>();
            builder.RegisterInstance(Mock.Of<ISyncPolicy>(value => value.AllowOriginal() == false)).As<ISyncPolicy>();
            using var receiving = builder.Build();
            ContainerProvider.SetContainer(receiving);
            try
            {
                using var broker = new MessageBroker();
                var network = new Mock<INetwork>();
                network.Setup(value => value.SendAll(It.IsAny<IMessage>())).Callback<IMessage>(sent.Add);
                using var armyMessages = new ArmyHandler(broker, network.Object, objects);
                Assert.True(ArmyHandler.HasConfirmedCampaignSession());
                broker.Publish(null, new NetworkRemovePartyInArmy("Army_native", "party", null));
                Assert.Null(party.Army);
                Assert.DoesNotContain(party, army.Parties);
                Assert.True(objects.Contains(army));
                Assert.Same(party, army.AiBehaviorObject);
                Assert.Equal(0, dispersed);
                Assert.Equal(0, moved);
                Assert.Empty(sent);
            }
            finally { ContainerProvider.SetContainer(container); }
        });
    }

    [Theory]
    [InlineData(SessionMode.ListenAuthority, "unknown", true)]
    [InlineData(SessionMode.DedicatedAuthority, "unknown", true)]
    [InlineData(SessionMode.RemoteReplica, "unknown", false)]
    [InlineData(SessionMode.ListenAuthority, "observer", false)]
    [InlineData(SessionMode.RemoteReplica, "frozen", false)]
    [InlineData(SessionMode.ListenAuthority, "paused", false)]
    public void UnknownBootstrapUsesOriginalDestroyAndKnownNonWritersCannotDisperse(SessionMode mode, string state, bool runs)
    {
        OnGameThread(() =>
        {
            var army = SetupArmy();
            ModInformation.SessionMode = mode;
            var lease = state == "unknown" ? default : state == "paused" ?
                new AuthorityLease("room", "paused", "", 3, true) : new AuthorityLease("room", "writer", "other", 2);
            var context = new Mock<ICampaignAuthorityContext>();
            context.Setup(value => value.TryGetCurrentLease(out lease)).Returns(state != "unknown");
            context.Setup(value => value.TryGetLocalAuthorityLease(out lease)).Returns(false);
            var service = new CampaignLifetimeCompletion(objects, MessageBroker.Instance, context.Object);
            var builder = new ContainerBuilder();
            builder.RegisterInstance(context.Object).As<ICampaignAuthorityContext>();
            builder.RegisterInstance(service);
            builder.RegisterInstance(Mock.Of<ISyncPolicy>(value => value.AllowOriginal() == false)).As<ISyncPolicy>();
            using var session = builder.Build();
            ContainerProvider.SetContainer(session);
            try
            {
                Adapter();
                army.DisperseInternal();
                Assert.Equal(runs ? 1 : 0, dispersed);
                Assert.Equal(!runs, objects.Contains(army));
                Assert.Equal(runs ? 1 : 0, sent.OfType<NetworkDestroyInstance<Army>>().Count());
                Assert.Equal(runs, army._tickEvent.isEventDeleted);
                Assert.True(service.IsComplete);
            }
            finally { ContainerProvider.SetContainer(container); }
        });
    }

    private static void HoldDuringNativeFinalization()
    {
        if (activeGarrison != null && ContainerProvider.TryResolve<CampaignLifetimeCompletion>(out var completion))
            heldInNative = completion.TryHoldGarrison(activeGarrison);
    }

    private static void LastOrdinaryPostfix()
    {
        if (activeGarrison != null && ContainerProvider.TryResolve<IObjectManager>(out var objects))
            heldInPostfix = objects.TryGetHandle(activeGarrison.Party, out _);
        if (injected != null) throw injected;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ActualNativeFinalizeReleasesAfterOriginalOrdinaryPostfixAndSubscribers(bool postfixThrows, bool reentrant)
    {
        OnGameThread(() =>
        {
            SetupArmy(); // Campaign dispatcher fixture, not a simulated finalization body.
            var garrison = ObjectHelper.SkipConstructor<MobileParty>();
            garrison.IsGarrison = true;
            garrison.Party = ObjectHelper.SkipConstructor<PartyBase>();
            activeGarrison = garrison;
            objects.AddExisting("PartyBase_garrison", garrison.Party, 32519);
            var participant = ObjectHelper.SkipConstructor<MapEventParty>();
            participant.Party = garrison.Party;
            var mapEvent = ObjectHelper.SkipConstructor<MapEvent>();
            var first = ObjectHelper.SkipConstructor<MapEventSide>();
            var second = ObjectHelper.SkipConstructor<MapEventSide>();
            AccessTools.Field(typeof(MapEventSide), nameof(MapEventSide._battleParties)).SetValue(first, new MBList<MapEventParty> { participant });
            AccessTools.Field(typeof(MapEventSide), nameof(MapEventSide._battleParties)).SetValue(second, new MBList<MapEventParty>());
            AccessTools.Field(typeof(MapEvent), nameof(MapEvent._sides)).SetValue(mapEvent, new[] { first, second });
            var network = new Mock<INetwork>();
            network.Setup(value => value.SendAll(It.IsAny<IMessage>())).Callback<IMessage>(sent.Add);
            using var partyHandler = new AutoRegistryHandler<PartyBase>(
                new PartyBaseRegistry(Mock.Of<ILogger>(), Mock.Of<IAutoRegistryFactory>(), objects), MessageBroker.Instance, network.Object, objects);
            // This fixture leaves participant end accounting to the existing regression suite.
            // The installed FinalizeEventAux runs to its real Clear() end; only this nested dependency is shimmed.
            Patch(typeof(MapEventSide), nameof(MapEventSide.HandleMapEventEnd), nameof(Skip));
            harmony.Patch(AccessTools.Method(typeof(CampaignEventDispatcher), nameof(CampaignEventDispatcher.OnMapEventEnded)),
                postfix: new HarmonyMethod(typeof(CampaignLifetimeNativePathTests), nameof(HoldDuringNativeFinalization)));
            var finalize = AccessTools.Method(typeof(MapEvent), nameof(MapEvent.FinalizeEventAux));
            harmony.Patch(finalize,
                prefix: new HarmonyMethod(typeof(MapEventPatches), "Prefix_FinalizeEventAux"),
                postfix: new HarmonyMethod(typeof(MapEventPatches), "Postfix_FinalizeEventAux"));
            harmony.Patch(finalize, postfix: new HarmonyMethod(typeof(CampaignLifetimeNativePathTests), nameof(LastOrdinaryPostfix)) { priority = Priority.Last });
            harmony.CreateClassProcessor(typeof(MapEventFinalizationCompletionPatch)).Patch();
            bool subscriberResolved = false;
            int finalizedCount = 0;
            bool enteredAgain = false;
            bool heldAfterInner = false;
            Action<MessagePayload<MapEventFinalized>> subscriber = payload => subscriberResolved = objects.TryGetHandle(garrison.Party, out _) && !completion.IsComplete;
            Action<MessagePayload<MapEventFinalized>> countFinalized = payload => finalizedCount++;
            Action<MessagePayload<MapEventContributionFlushRequested>> reenter = payload =>
            {
                if (!reentrant || enteredAgain) return;
                enteredAgain = true;
                mapEvent.FinalizeEventAux();
                heldAfterInner = objects.Contains(garrison.Party) && !completion.IsComplete;
            };
            MessageBroker.Instance.Subscribe(subscriber);
            MessageBroker.Instance.Subscribe(countFinalized);
            MessageBroker.Instance.Subscribe(reenter);
            try
            {
                if (postfixThrows)
                {
                    var error = new InvalidOperationException("ordinary postfix failed after native finalization");
                    injected = error;
                    Assert.Same(error, Assert.Throws<InvalidOperationException>(() => mapEvent.FinalizeEventAux()));
                    injected = null;
                }
                else mapEvent.FinalizeEventAux();
                Assert.True(mapEvent.IsFinalized);
                Assert.Empty(first.Parties);
                Assert.True(heldInNative);
                Assert.True(heldInPostfix);
                Assert.True(subscriberResolved);
                Assert.Equal(1, finalizedCount);
                Assert.Equal(reentrant, enteredAgain);
                if (reentrant) Assert.True(heldAfterInner);
                Assert.Equal(postfixThrows, objects.Contains(garrison.Party));
                Assert.Equal(!postfixThrows, completion.IsComplete);
                Assert.Equal(postfixThrows ? 0 : 1, sent.OfType<NetworkDestroyInstance<PartyBase>>().Count());
            }
            finally
            {
                MessageBroker.Instance.Unsubscribe(subscriber);
                MessageBroker.Instance.Unsubscribe(countFinalized);
                MessageBroker.Instance.Unsubscribe(reenter);
            }
        });
    }

    public void Dispose()
    {
        harmony.UnpatchAll(harmony.Id);
        handler.Dispose();
        Campaign.Current = previousCampaign;
        ModInformation.SessionMode = previousMode;
        if (previousContainer != null) ContainerProvider.SetContainer(previousContainer);
        else ContainerProvider.Clear();
        container.Dispose();
    }
}
