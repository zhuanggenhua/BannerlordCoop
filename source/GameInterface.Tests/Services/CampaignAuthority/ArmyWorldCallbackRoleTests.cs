using Autofac;
using Common;
using Common.Messaging;
using Common.Network;
using Common.Util;
using GameInterface.Policies;
using GameInterface.Services.Armies.Handlers;
using GameInterface.Services.Armies.Messages;
using GameInterface.Services.Armies.Patches;
using GameInterface.Services.Armies.Patches.Disable;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using GameInterface.Services.ObjectManager;
using GameInterface.Tests.Bootstrap;
using HarmonyLib;
using Moq;
using System;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors.AiBehaviors;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Siege;
using Xunit;

namespace GameInterface.Tests.Services.CampaignAuthority;

[Collection(ModInformationRoleCollection.Name)]
public class ArmyWorldCallbackRoleTests
{
    static ArmyWorldCallbackRoleTests() =>
        RuntimeHelpers.RunModuleConstructor(typeof(Coop.Tests.Mocks.TestNetwork).Module.ModuleHandle);

    private static int memberBodyReads, siegeBodyEntries;
    private static bool ArmyReadSpy(ref Army __result)
    {
        memberBodyReads++;
        __result = null!;
        return false;
    }
    private static bool SiegeBodySpy(bool __runOriginal)
    {
        if (__runOriginal) siegeBodyEntries++;
        return false;
    }
    private static bool InvokePrefix(string callback)
    {
        var type = callback == "Tick" ? typeof(ArmyDisablePatches) : typeof(DisableArmyHourlyTick);
        var method = AccessTools.Method(type, callback == "Tick" ? "DisableArmyTick" : "Prefix");
        Assert.NotNull(method);
        return (bool)method.Invoke(null, Array.Empty<object>())!;
    }

    [Theory]
    [InlineData("Tick", SessionMode.ListenAuthority, "A", "A", true)]
    [InlineData("HourlyTick", SessionMode.ListenAuthority, "A", "A", true)]
    [InlineData("Tick", SessionMode.ListenAuthority, "A", "B", false)]
    [InlineData("HourlyTick", SessionMode.ListenAuthority, "A", "B", false)]
    [InlineData("Tick", SessionMode.RemoteReplica, "B", "A", false)]
    [InlineData("HourlyTick", SessionMode.RemoteReplica, "B", "A", false)]
    [InlineData("Tick", SessionMode.RemoteReplica, "B", "B", true)]
    [InlineData("HourlyTick", SessionMode.RemoteReplica, "B", "B", true)]
    public void NativeCallbackPrefixUsesCurrentWriterRatherThanRoomRole(string callback,
        SessionMode mode, string local, string writer, bool expected)
    {
        using var fixture = new Fixture(mode, local, writer);
        Assert.Equal(expected, InvokePrefix(callback));
    }

    [Theory]
    [InlineData("Tick", SessionMode.ListenAuthority, "A")]
    [InlineData("HourlyTick", SessionMode.ListenAuthority, "A")]
    [InlineData("Tick", SessionMode.RemoteReplica, "B")]
    [InlineData("HourlyTick", SessionMode.RemoteReplica, "B")]
    public void PauseFreezeAndSupersededLeaseDoNotRunWorldCallback(string callback, SessionMode mode, string local)
    {
        using var fixture = new Fixture(mode, local, local);
        Assert.True(InvokePrefix(callback));
        var original = fixture.Session.CurrentLease;
        Assert.True(fixture.Session.TryFreezeLocalAuthority(original, 7));
        Assert.False(InvokePrefix(callback));
        var paused = new AuthorityLease("room", "paused", "", 3, true);
        Assert.True(fixture.Session.TryApply(paused, "A"));
        Assert.False(InvokePrefix(callback));
        var otherWriter = local == "A" ? "B" : "A";
        Assert.True(fixture.Session.TryApply(new AuthorityLease("room", "next", otherWriter, 4), "A"));
        Assert.False(fixture.Session.TryApply(original, "A"));
        Assert.False(InvokePrefix(callback));
    }

    [Theory]
    [InlineData(CampaignExecutionPhase.Replay)]
    [InlineData(CampaignExecutionPhase.Prediction)]
    [InlineData(CampaignExecutionPhase.CheckpointInstall)]
    [InlineData(CampaignExecutionPhase.LocalIntent)]
    [InlineData(CampaignExecutionPhase.Bootstrap)]
    public void RestrictedPhaseCannotStartEitherNativeWorldCallback(CampaignExecutionPhase phase)
    {
        using var fixture = new Fixture(SessionMode.ListenAuthority, "A", "A");
        Assert.True(InvokePrefix("Tick"));
        Assert.True(InvokePrefix("HourlyTick"));
        using (CampaignExecutionScope.Enter(phase))
        using (CampaignExecutionScope.Enter(CampaignExecutionPhase.WorldWrite))
        {
            Assert.False(InvokePrefix("Tick"));
            Assert.False(InvokePrefix("HourlyTick"));
        }
        Assert.True(InvokePrefix("Tick"));
        Assert.True(InvokePrefix("HourlyTick"));
    }

    [Theory]
    [InlineData(SessionMode.ListenAuthority, "A")]
    [InlineData(SessionMode.RemoteReplica, "B")]
    public void AllowedReceiveThreadDoesNotGrantEitherNativeSimulationCallback(SessionMode mode, string local)
    {
        using var fixture = new Fixture(mode, local, local);
        Assert.True(InvokePrefix("Tick"));
        Assert.True(InvokePrefix("HourlyTick"));
        using (new AllowedThread())
        {
            Assert.False(InvokePrefix("Tick"));
            Assert.False(InvokePrefix("HourlyTick"));
        }
    }

    [Theory]
    [InlineData(SessionMode.ListenAuthority, true)]
    [InlineData(SessionMode.DedicatedAuthority, true)]
    [InlineData(SessionMode.RemoteReplica, false)]
    public void UnconfirmedInitializationKeepsLegacyRoleButExplicitBootstrapCannotSimulate(SessionMode mode, bool legacy)
    {
        using var fixture = new Fixture(mode, "A", "A", confirmed: false);
        Assert.False(fixture.Session.IsConfirmed);
        Assert.Equal(legacy, InvokePrefix("Tick"));
        Assert.Equal(legacy, InvokePrefix("HourlyTick"));
        using (CampaignExecutionScope.Enter(CampaignExecutionPhase.Bootstrap))
        {
            Assert.False(InvokePrefix("Tick"));
            Assert.False(InvokePrefix("HourlyTick"));
        }
    }

    [Fact]
    public void NativeMemberListenersRegisterOnceDuringReplicaLoadAndSurvivePromotion()
    {
        GameBootStrap.Initialize();
        using var fixture = new Fixture(SessionMode.RemoteReplica, "B", "A");
        var harmony = new Harmony("Coop.Tests.ArmyCallbacks." + Guid.NewGuid().ToString("N"));
        var behavior = new AiArmyMemberBehavior();
        try
        {
            harmony.CreateClassProcessor(typeof(DisableAiArmyMemberBehavior)).Patch();
            harmony.Patch(AccessTools.PropertyGetter(typeof(MobileParty), nameof(MobileParty.Army)),
                prefix: new HarmonyMethod(typeof(ArmyWorldCallbackRoleTests), nameof(ArmyReadSpy)));
            harmony.Patch(AccessTools.Method(typeof(AiArmyMemberBehavior), "OnSiegeEventStarted"),
                prefix: new HarmonyMethod(typeof(ArmyWorldCallbackRoleTests), nameof(SiegeBodySpy)) { priority = Priority.Last });
            using (CampaignExecutionScope.Enter(CampaignExecutionPhase.Bootstrap)) behavior.RegisterEvents();
            var aiEvent = (MbEvent<MobileParty, PartyThinkParams>)CampaignEvents.AiHourlyTickEvent;
            Action<MobileParty, PartyThinkParams>? ai = null;
            int aiCount = 0;
            for (var node = aiEvent._nonSerializedListenerList; node != null; node = node.Next)
                if (ReferenceEquals(node.Owner, behavior)) { aiCount++; ai = node.Action; }
            var siegeEvent = (MbEvent<SiegeEvent>)CampaignEvents.OnSiegeEventStartedEvent;
            Action<SiegeEvent>? siege = null;
            int siegeCount = 0;
            for (var node = siegeEvent._nonSerializedListenerList; node != null; node = node.Next)
                if (ReferenceEquals(node.Owner, behavior)) { siegeCount++; siege = node.Action; }
            Assert.Equal(1, aiCount);
            Assert.Equal(1, siegeCount);
            Assert.NotNull(ai);
            Assert.NotNull(siege);
            memberBodyReads = siegeBodyEntries = 0;
            ai(null!, null!); siege(null!);
            Assert.Equal(0, memberBodyReads);
            Assert.Equal(0, siegeBodyEntries);
            Assert.True(fixture.Session.TryApply(new AuthorityLease("room", "promoted", "B", 3), "A"));
            ai(ObjectHelper.SkipConstructor<MobileParty>(), null!); siege(null!);
            Assert.Equal(1, memberBodyReads);
            Assert.Equal(1, siegeBodyEntries);
            using (new AllowedThread()) { ai(null!, null!); siege(null!); }
            using (CampaignExecutionScope.Enter(CampaignExecutionPhase.Replay)) { ai(null!, null!); siege(null!); }
            using (CampaignExecutionScope.Enter(CampaignExecutionPhase.Prediction)) { ai(null!, null!); siege(null!); }
            Assert.Equal(1, memberBodyReads);
            Assert.Equal(1, siegeBodyEntries);
            Assert.True(fixture.Session.TryFreezeLocalAuthority(fixture.Session.CurrentLease, 8));
            ai(null!, null!); siege(null!);
            Assert.True(fixture.Session.TryApply(new AuthorityLease("room", "paused", "", 4, true), "A"));
            ai(null!, null!); siege(null!);
            Assert.True(fixture.Session.TryApply(new AuthorityLease("room", "returned", "A", 5), "A"));
            ai(null!, null!); siege(null!);
            Assert.Equal(1, memberBodyReads);
            Assert.Equal(1, siegeBodyEntries);
            Assert.Same(ai, FindAi());
            int finalAiCount = 0, finalSiegeCount = 0;
            for (var node = aiEvent._nonSerializedListenerList; node != null; node = node.Next)
                if (ReferenceEquals(node.Owner, behavior)) finalAiCount++;
            for (var node = siegeEvent._nonSerializedListenerList; node != null; node = node.Next)
                if (ReferenceEquals(node.Owner, behavior)) finalSiegeCount++;
            Assert.Equal(1, finalAiCount);
            Assert.Equal(1, finalSiegeCount);

            Action<MobileParty, PartyThinkParams>? FindAi()
            {
                for (var node = aiEvent._nonSerializedListenerList; node != null; node = node.Next)
                    if (ReferenceEquals(node.Owner, behavior)) return node.Action;
                return null;
            }
        }
        finally
        {
            Campaign.Current.CampaignEvents.RemoveListeners(behavior);
            harmony.UnpatchAll(harmony.Id);
        }
    }

    [Theory]
    [InlineData("Postfix_HourlyTick", SessionMode.ListenAuthority, "A", false)]
    [InlineData("Postfix_BoostCohesionWithInfluence", SessionMode.ListenAuthority, "A", false)]
    [InlineData("Postfix_HourlyTick", SessionMode.RemoteReplica, "B", true)]
    [InlineData("Postfix_BoostCohesionWithInfluence", SessionMode.RemoteReplica, "B", true)]
    public void AppliedCohesionPublishesOnceOnlyFromWriterAndDoesNotEchoReplay(string postfix,
        SessionMode mode, string local, bool expected)
    {
        using var fixture = new Fixture(mode, local, "B");
        var army = ObjectHelper.SkipConstructor<Army>();
        army.Cohesion = 67;
        int published = 0;
        Action<MessagePayload<ArmyCohesionChanged>> subscriber = payload =>
        {
            Assert.Same(army, payload.What.Army);
            Assert.Equal(67, payload.What.Cohesion);
            published++;
        };
        MessageBroker.Instance.Subscribe(subscriber);
        try
        {
            void Invoke() => AccessTools.Method(typeof(ArmyCohesionPatch), postfix).Invoke(null, new object[] { army });
            Invoke();
            Assert.Equal(expected ? 1 : 0, published);
            using (new AllowedThread()) Invoke();
            using (CampaignExecutionScope.Enter(CampaignExecutionPhase.Replay)) Invoke();
            using (CampaignExecutionScope.Enter(CampaignExecutionPhase.Prediction)) Invoke();
            Assert.Equal(expected ? 1 : 0, published);
            Assert.True(fixture.Session.TryFreezeLocalAuthority(fixture.Session.CurrentLease, 9));
            Invoke();
            Assert.Equal(expected ? 1 : 0, published);
        }
        finally { MessageBroker.Instance.Unsubscribe(subscriber); }
    }

    [Fact]
    public void ActualIncomingCohesionResultAppliesWithoutPublishingOrSendingAnotherResult()
    {
        using var fixture = new Fixture(SessionMode.RemoteReplica, "B", "B");
        var army = ObjectHelper.SkipConstructor<Army>();
        army.Cohesion = 67;
        var objects = new Mock<IObjectManager>();
        objects.Setup(value => value.TryGetObjectWithLogging("army", out army)).Returns(true);
        var network = new Mock<INetwork>();
        using var broker = new MessageBroker();
        using var handler = new ArmyCohesionHandler(broker, objects.Object, network.Object);
        int published = 0;
        Action<MessagePayload<ArmyCohesionChanged>> subscriber = _ => published++;
        MessageBroker.Instance.Subscribe(subscriber);
        broker.Subscribe(subscriber);
        var harmony = new Harmony("Coop.Tests.ArmyCohesionReceive." + Guid.NewGuid().ToString("N"));
        try
        {
            harmony.CreateClassProcessor(typeof(ArmyCohesionPatch)).Patch();
            broker.Publish(this, new NetworkArmyCohesionChanged("army", 42));
            GameThread.Run(() => { }, blocking: true);
            Assert.Equal(42, army.Cohesion);
            Assert.Equal(0, published);
            network.Verify(value => value.SendAll(It.IsAny<IMessage>()), Times.Never);
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            MessageBroker.Instance.Unsubscribe(subscriber);
            broker.Unsubscribe(subscriber);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SessionMode previousMode = ModInformation.SessionMode;
        private readonly ILifetimeScope? previousContainer;
        private readonly IContainer container;
        internal readonly SessionIdentity Session = new SessionIdentity();

        internal Fixture(SessionMode mode, string local, string writer, bool confirmed = true)
        {
            ContainerProvider.TryGetContainer(out previousContainer);
            if (confirmed)
                Assert.True(Session.TryApply(new AuthorityLease("room", "current", writer, 2), "A"));
            var controller = new ControllerIdProvider();
            controller.SetControllerId(local);
            var builder = new ContainerBuilder();
            builder.RegisterInstance(new CampaignAuthorityContext(Session, controller)).As<ICampaignAuthorityContext>();
            builder.RegisterInstance(Mock.Of<ISyncPolicy>(value => value.AllowOriginal() == false)).As<ISyncPolicy>();
            container = builder.Build();
            ModInformation.SessionMode = mode;
            ContainerProvider.SetContainer(container);
        }

        public void Dispose()
        {
            ModInformation.SessionMode = previousMode;
            if (previousContainer != null) ContainerProvider.SetContainer(previousContainer);
            else ContainerProvider.Clear();
            container.Dispose();
        }
    }
}
