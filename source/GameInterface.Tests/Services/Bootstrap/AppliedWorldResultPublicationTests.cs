using Autofac;
using Common;
using Common.Messaging;
using Common.Network;
using Common.Util;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using GameInterface.Services.MapEvents;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Party;
using GameInterface.Services.Players;
using GameInterface.Services.TroopRosters;
using GameInterface.Services.TroopRosters.Handlers;
using GameInterface.Services.TroopRosters.Messages;
using GameInterface.Services.TroopRosters.Patches;
using HarmonyLib;
using Moq;
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Roster;
using Xunit;

namespace GameInterface.Tests.Services.Bootstrap;

[Collection(ModInformationRoleCollection.Name)]
public class AppliedWorldResultPublicationTests
{
    static AppliedWorldResultPublicationTests() =>
        RuntimeHelpers.RunModuleConstructor(typeof(Coop.Tests.Mocks.TestNetwork).Module.ModuleHandle);

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, true, false)]
    public void AllowedThreadPublishesOnlyRegisteredAlreadyAppliedWriterResult(
        bool legacy, bool writer, bool frozen, bool publishes)
    {
        WithContext(legacy, writer, frozen, (_, roster, character) =>
        {
            int published = 0;
            Action<MessagePayload<CountsAtIndexAdded>> subscriber = message =>
            {
                Assert.Same(roster, message.What.TroopRoster);
                Assert.Equal(-3, message.What.CountChange);
                published++;
            };
            MessageBroker.Instance.Subscribe(subscriber);
            try
            {
                using (new AllowedThread())
                {
                    Assert.False(CampaignSimulationAuthorityPolicy.CanRunWorldSimulation());
                    InvokePostfix(roster, character);
                }
                Assert.Equal(publishes ? 1 : 0, published);
            }
            finally { MessageBroker.Instance.Unsubscribe(subscriber); }
        });
    }

    [Theory]
    [InlineData(CampaignExecutionPhase.Replay)]
    [InlineData(CampaignExecutionPhase.CheckpointInstall)]
    [InlineData(CampaignExecutionPhase.Prediction)]
    [InlineData(CampaignExecutionPhase.LocalIntent)]
    [InlineData(CampaignExecutionPhase.Bootstrap)]
    public void RestrictedParentCannotPublishEvenWithNestedWorldWrite(CampaignExecutionPhase phase)
    {
        WithContext(false, true, false, (_, roster, character) =>
        {
            int published = 0;
            Action<MessagePayload<CountsAtIndexAdded>> subscriber = _ => published++;
            MessageBroker.Instance.Subscribe(subscriber);
            try
            {
                using (new AllowedThread())
                using (CampaignExecutionScope.Enter(phase))
                using (CampaignExecutionScope.Enter(CampaignExecutionPhase.WorldWrite))
                {
                    Assert.False(CampaignSimulationAuthorityPolicy.CanPublishAppliedWorldResult());
                    Assert.False(CampaignSimulationAuthorityPolicy.CanRunWorldSimulation());
                    InvokePostfix(roster, character);
                }
                Assert.Equal(0, published);
            }
            finally { MessageBroker.Instance.Unsubscribe(subscriber); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriterReceiveHandlerCarriesReplayAndDoesNotEcho(bool removeZeroCounts)
    {
        WithContext(false, true, false, (objects, roster, character) =>
        {
            objects.Setup(value => value.TryGetObjectWithLogging(1, out roster)).Returns(true);
            objects.Setup(value => value.TryGetObjectWithLogging(2, out character)).Returns(true);
            using var applied = new ManualResetEventSlim();
            bool replay = false, allowed = false, canPublish = true;
            int published = 0;
            Action<MessagePayload<CountsAtIndexAdded>> subscriber = _ => Interlocked.Increment(ref published);
            MessageBroker.Instance.Subscribe(subscriber);
            Action capture = () =>
            {
                replay = CampaignExecutionScope.Current == CampaignExecutionPhase.Replay;
                allowed = AllowedThread.IsThisThreadAllowed();
                canPublish = CampaignSimulationAuthorityPolicy.CanPublishAppliedWorldResult();
                InvokePostfix(roster, character);
            };
            var partyScreen = new RecordingPartyScreen(capture);
            var encounter = new RecordingEncounter(() => applied.Set());
            using var broker = new MessageBroker();
            using var handler = new TroopRosterDeltaHandler(broker, objects.Object, Mock.Of<INetwork>(),
                encounter, partyScreen, Mock.Of<IPlayerManager>(), Mock.Of<IPlayerTroopXpRelevance>());
            try
            {
                if (removeZeroCounts) broker.Publish(this, new NetworkTroopRosterRemoveZeroCounts(1));
                else broker.Publish(this, new NetworkTroopRosterElementBatch(1, 2,
                    new[] { TroopRosterElementOperation.AddCounts(-3, 0, 0, true) }));
                Assert.True(applied.Wait(TimeSpan.FromSeconds(10)), "receive did not complete on game thread");
                Assert.True(replay);
                Assert.True(allowed);
                Assert.False(canPublish);
                Assert.Equal(0, published);
            }
            finally { MessageBroker.Instance.Unsubscribe(subscriber); }
        });
    }

    private static void InvokePostfix(TroopRoster roster, CharacterObject character) =>
        AccessTools.Method(typeof(TroopRosterAddToCountsPatch), "Postfix")
            .Invoke(null, new object[] { roster, character, -3, 0, 0, true, true });

    private sealed class RecordingPartyScreen : IPartyScreenRosterRefresher
    {
        private readonly Action capture;
        public RecordingPartyScreen(Action capture) => this.capture = capture;
        public bool TryApply(TroopRoster roster, CharacterObject character, Action<TroopRoster, CharacterObject> apply)
        { capture(); return true; }
        public bool TryRemoveZeroCounts(TroopRoster roster) { capture(); return true; }
        public bool TryApply(ItemRoster roster, Action<ItemRoster> apply) => false;
    }

    private sealed class RecordingEncounter : IEncounterMenuConditionRefresher
    {
        private readonly Action completed;
        public RecordingEncounter(Action completed) => this.completed = completed;
        public void RefreshForRoster(TroopRoster roster) => completed();
        public void RefreshForMapEvent(TaleWorlds.CampaignSystem.MapEvents.MapEvent mapEvent) { }
    }

    private static void WithContext(bool legacy, bool writer, bool frozen,
        Action<Mock<IObjectManager>, TroopRoster, CharacterObject> action)
    {
        bool hadContainer = ContainerProvider.TryGetContainer(out var previous);
        var previousMode = ModInformation.SessionMode;
        var builder = new ContainerBuilder();
        if (!legacy)
        {
            var identity = new SessionIdentity();
            var lease = new AuthorityLease("room", "authority", "b", 4);
            Assert.True(identity.TryApply(lease, "a"));
            if (frozen) Assert.True(identity.TryFreezeLocalAuthority(lease, 1));
            var controller = new ControllerIdProvider();
            controller.SetControllerId(writer ? "b" : "a");
            builder.RegisterInstance(new CampaignAuthorityContext(identity, controller)).As<ICampaignAuthorityContext>();
        }
        var objects = new Mock<IObjectManager>();
        var roster = new TroopRoster();
        var character = new CharacterObject();
        string rosterId = "roster";
        objects.Setup(value => value.TryGetId(roster, out rosterId)).Returns(true);
        builder.RegisterInstance(objects.Object).As<IObjectManager>();
        using var container = builder.Build();
        try
        {
            ModInformation.SessionMode = legacy ? SessionMode.DedicatedAuthority : SessionMode.RemoteReplica;
            ContainerProvider.SetContainer(container);
            action(objects, roster, character);
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
            if (hadContainer) ContainerProvider.SetContainer(previous);
            else ContainerProvider.Clear();
        }
    }
}
