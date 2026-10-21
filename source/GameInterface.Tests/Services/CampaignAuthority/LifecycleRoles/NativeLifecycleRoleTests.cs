using Autofac;
using Common;
using Common.Messaging;
using Common.Util;
using GameInterface.Policies;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.PartyComponents.Messages;
using GameInterface.Services.PartyComponents.Handlers;
using Common.Network;
using GameInterface.Services.PartyComponents.Data;
using GameInterface.Services.MobilePartyAIs.Messages;
using ProtoBuf.Meta;
using System.IO;
using GameInterface.Services.PartyComponents.Patches.Lifetime;
using GameInterface.Services.PartyComponents.Patches;
using GameInterface.Services.Settlements.Patches;
using GameInterface.Services.Settlements.Messages;
using HarmonyLib;
using Moq;
using System;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using TaleWorlds.CampaignSystem.Settlements;
using Xunit;

namespace GameInterface.Tests.Services.CampaignAuthority.LifecycleRoles;

[Collection(ModInformationRoleCollection.Name)]
public class NativeLifecycleRoleTests
{
    [Fact]
    public void OriginalLifecyclePayloadsRoundTripWithoutNewFieldsAndCarryAuthorityScope()
    {
        IMessage[] messages =
        {
            new NetworkCreateMobilePartyAi("ai", 102, 103), new NetworkDestroyMobilePartyAi(102),
            new NetworkCreatePartyComponent(new PartyComponentData(1, "component", 101)),
            new NetworkPartyComponentMobilePartyUpdated("component", "party"),
            new NetworkPartyComponentLeaderChanged("component", "leader")
        };
        foreach (var message in messages)
        {
            Assert.IsAssignableFrom<ICampaignAuthorityScopedMessage>(message);
            using var stream = new MemoryStream();
            RuntimeTypeModel.Default.Serialize(stream, message);
            var bytes = stream.ToArray();
            stream.Position = 0;
            var restored = RuntimeTypeModel.Default.Deserialize(stream, null, message.GetType());
            using var second = new MemoryStream();
            RuntimeTypeModel.Default.Serialize(second, restored);
            Assert.Equal(bytes, second.ToArray());
        }
    }

    [Fact]
    public void MissingAuthorityContextCannotSendClassifiedResultsEvenForLegacyServerRole()
    {
        bool hadPrevious = ContainerProvider.TryGetContainer(out var previous);
        var mode = ModInformation.SessionMode;
        try
        {
            ContainerProvider.Clear();
            ModInformation.SessionMode = SessionMode.DedicatedAuthority;
            Assert.True(CampaignSimulationAuthorityPolicy.CanPublishAppliedWorldResult());
            Assert.False(CampaignSimulationAuthorityPolicy.CanSendWorldResult());
        }
        finally { ModInformation.SessionMode = mode; if (hadPrevious) ContainerProvider.SetContainer(previous); }
    }

    [Fact]
    public void BWriterRunsOriginalCaravanConstructorAndAnnouncesExactlyOnce()
    {
        WithRole(true, false, objects =>
        {
            var settlement = ObjectHelper.SkipConstructor<Settlement>();
            var owner = ObjectHelper.SkipConstructor<Hero>();
            var leader = ObjectHelper.SkipConstructor<Hero>();
            var changes = 0;
            string id = "component";
            uint handle = 101;
            objects.Setup(value => value.AddNewObject(It.IsAny<CaravanPartyComponent>(), out id)).Returns(true);
            objects.Setup(value => value.TryGetHandleWithLogging(It.IsAny<PartyComponent>(), out handle)).Returns(true);
            var network = new Mock<INetwork>();
            using var handler = new PartyComponentHandler(MessageBroker.Instance, network.Object, objects.Object);
            Action<MessagePayload<PartyComponentCreated>> capture = _ => changes++;
            MessageBroker.Instance.Subscribe(capture);
            var harmony = new Harmony("test.lifecycle.caravan");
            var ctor = AccessTools.GetDeclaredConstructors(typeof(CaravanPartyComponent)).Single();
            harmony.Patch(ctor, prefix: new HarmonyMethod(AccessTools.Method(typeof(CaravanPartyComponentLifetimePatches), "Prefix")));
            try
            {
                var caravan = (CaravanPartyComponent)ctor.Invoke(new object[] { settlement, owner, leader, true, null });
                Assert.Same(owner, caravan.Owner);
                Assert.Same(leader, caravan.Leader);
                Assert.Same(settlement, caravan.Settlement);
                Assert.True(caravan.IsElite);
                Assert.Equal(1, changes);
                objects.Verify(value => value.AddNewObject(caravan, out id), Times.Once);
                network.Verify(value => value.SendAll(It.IsAny<NetworkCreatePartyComponent>()), Times.Once);
            }
            finally { harmony.UnpatchAll(harmony.Id); MessageBroker.Instance.Unsubscribe(capture); }
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ObserverOrFrozenWriterCannotRunCaravanConstructorOrRegister(bool writer, bool frozen)
    {
        WithRole(writer, frozen, objects =>
        {
            int created = 0;
            Action<MessagePayload<PartyComponentCreated>> capture = _ => created++;
            MessageBroker.Instance.Subscribe(capture);
            var harmony = new Harmony("test.lifecycle.caravan.rejected");
            var ctor = AccessTools.GetDeclaredConstructors(typeof(CaravanPartyComponent)).Single();
            harmony.Patch(ctor, prefix: new HarmonyMethod(AccessTools.Method(typeof(CaravanPartyComponentLifetimePatches), "Prefix")));
            try
            {
                var caravan = (CaravanPartyComponent)ctor.Invoke(new object[] { null, ObjectHelper.SkipConstructor<Hero>(), null, true, null });
                Assert.Null(caravan.Owner);
                Assert.False(caravan.IsElite);
                Assert.Equal(0, created);
                objects.Verify(value => value.AddNewObject(It.IsAny<object>(), out It.Ref<string>.IsAny), Times.Never);
            }
            finally { harmony.UnpatchAll(harmony.Id); MessageBroker.Instance.Unsubscribe(capture); }
        });
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void NativeComponentBindingUsesWriterRoleAndExplicitReplayDoesNotEcho(bool writer, bool frozen)
    {
        WithRole(writer, frozen, objects =>
        {
            var component = ObjectHelper.SkipConstructor<CaravanPartyComponent>();
            var party = ObjectHelper.SkipConstructor<MobileParty>();
            int published = 0;
            Action<MessagePayload<PartyComponentMobilePartyUpdated>> capture = _ => published++;
            MessageBroker.Instance.Subscribe(capture);
            var harmony = new Harmony("test.lifecycle.component.binding");
            harmony.CreateClassProcessor(typeof(PartyComponentPatches)).Patch();
            try
            {
                component.MobileParty = party;
                Assert.Equal(writer && !frozen, ReferenceEquals(party, component.MobileParty));
                Assert.Equal(writer && !frozen ? 1 : 0, published);
                using (CampaignExecutionScope.Enter(CampaignExecutionPhase.Replay))
                using (new AllowedThread()) component.MobileParty = party;
                Assert.Same(party, component.MobileParty);
                Assert.Equal(writer && !frozen ? 1 : 0, published);
            }
            finally { harmony.UnpatchAll(harmony.Id); MessageBroker.Instance.Unsubscribe(capture); }
        });
    }

    [Theory]
    [InlineData(CampaignExecutionPhase.Bootstrap, false, true)]
    [InlineData(CampaignExecutionPhase.CheckpointInstall, false, true)]
    [InlineData(CampaignExecutionPhase.Bootstrap, true, false)]
    [InlineData(CampaignExecutionPhase.CheckpointInstall, true, false)]
    [InlineData(CampaignExecutionPhase.WorldWrite, false, false)]
    [InlineData(CampaignExecutionPhase.Replay, false, false)]
    [InlineData(CampaignExecutionPhase.Prediction, false, false)]
    public void NativeOwnerInitializationIsPhaseAndRegistrationBounded(CampaignExecutionPhase phase, bool registered, bool applies)
    {
        WithRole(false, false, objects =>
        {
            var pair = CreateOwnerPair();
            string id = "town-component";
            objects.Setup(value => value.TryGetId(pair.town, out id)).Returns(registered);
            int published = 0;
            Action<MessagePayload<SettlementComponentOwnerChanged>> capture = _ => published++;
            MessageBroker.Instance.Subscribe(capture);
            var harmony = new Harmony("test.lifecycle.owner.initialization");
            harmony.CreateClassProcessor(typeof(OwnerSettlementComponentPatch)).Patch();
            try
            {
                using (CampaignExecutionScope.Enter(phase)) pair.town.Owner = pair.owner;
                Assert.Equal(applies, ReferenceEquals(pair.owner, pair.town.Owner));
                Assert.Equal(0, published);
            }
            finally { harmony.UnpatchAll(harmony.Id); MessageBroker.Instance.Unsubscribe(capture); }
        });
    }

    [Fact]
    public void WriterOwnerResultIsPublishedOnlyAfterNativeApplyAndReceiveDoesNotEcho()
    {
        WithRole(true, false, objects =>
        {
            var pair = CreateOwnerPair();
            int published = 0;
            Action<MessagePayload<SettlementComponentOwnerChanged>> capture = _ =>
            { Assert.Same(pair.owner, pair.town.Owner); published++; };
            MessageBroker.Instance.Subscribe(capture);
            var harmony = new Harmony("test.lifecycle.owner.applied");
            harmony.CreateClassProcessor(typeof(OwnerSettlementComponentPatch)).Patch();
            try
            {
                pair.town.Owner = pair.owner;
                Assert.Equal(1, published);
                using (CampaignExecutionScope.Enter(CampaignExecutionPhase.Replay))
                using (new AllowedThread()) pair.town.Owner = null;
                Assert.Null(pair.town.Owner);
                Assert.Equal(1, published);
            }
            finally { harmony.UnpatchAll(harmony.Id); MessageBroker.Instance.Unsubscribe(capture); }
        });
    }

    [Fact]
    public void BWriterRunsNativeGoldAndOwnerFlagSettersBeforePublishingTheirResults()
    {
        WithRole(true, false, objects =>
        {
            var town = ObjectHelper.SkipConstructor<Town>();
            int goldResults = 0, flagResults = 0;
            Action<MessagePayload<SettlementComponentGoldChanged>> gold = payload =>
            { Assert.Equal(42, town.Gold); Assert.Equal(town.Gold, payload.What.Gold); goldResults++; };
            Action<MessagePayload<SettlementComponentIsOwnerUnassignedChanged>> flag = payload =>
            { Assert.True(town.IsOwnerUnassigned); Assert.True(payload.What.IsOwnerUnassigned); flagResults++; };
            MessageBroker.Instance.Subscribe(gold); MessageBroker.Instance.Subscribe(flag);
            var harmony = new Harmony("test.lifecycle.settlement.property.family");
            harmony.CreateClassProcessor(typeof(GoldSettlementComponentPatch)).Patch();
            harmony.CreateClassProcessor(typeof(IsOwnerUnassignedSettlementComponentPatch)).Patch();
            try
            {
                town.Gold = 42;
                town.IsOwnerUnassigned = true;
                Assert.Equal(1, goldResults); Assert.Equal(1, flagResults);
                using (CampaignExecutionScope.Enter(CampaignExecutionPhase.Replay))
                using (new AllowedThread()) { town.Gold = 9; town.IsOwnerUnassigned = false; }
                Assert.Equal(9, town.Gold); Assert.False(town.IsOwnerUnassigned);
                Assert.Equal(1, goldResults); Assert.Equal(1, flagResults);
            }
            finally { harmony.UnpatchAll(harmony.Id); MessageBroker.Instance.Unsubscribe(gold); MessageBroker.Instance.Unsubscribe(flag); }
        });
    }

    private static (Town town, PartyBase owner) CreateOwnerPair()
    {
        var town = ObjectHelper.SkipConstructor<Town>();
        var owner = ObjectHelper.SkipConstructor<PartyBase>();
        var settlement = ObjectHelper.SkipConstructor<Settlement>();
        settlement.StringId = "owner-town";
        AccessTools.GetDeclaredFields(typeof(PartyBase)).Single(field => field.FieldType == typeof(Settlement)).SetValue(owner, settlement);
        AccessTools.GetDeclaredFields(typeof(PartyBase)).Single(field => field.FieldType == typeof(TaleWorlds.CampaignSystem.Roster.ItemRoster))
            .SetValue(owner, new TaleWorlds.CampaignSystem.Roster.ItemRoster());
        return (town, owner);
    }

    [Fact]
    public void BWriterRunsRealOwnerSetterInsteadOfStaticClientRejection()
    {
        WithRole(true, false, objects =>
        {
            var town = ObjectHelper.SkipConstructor<Town>();
            var owner = ObjectHelper.SkipConstructor<PartyBase>();
            var settlement = ObjectHelper.SkipConstructor<Settlement>();
            settlement.StringId = "owner-town";
            AccessTools.GetDeclaredFields(typeof(PartyBase)).Single(field => field.FieldType == typeof(Settlement)).SetValue(owner, settlement);
            AccessTools.GetDeclaredFields(typeof(PartyBase)).Single(field => field.FieldType == typeof(TaleWorlds.CampaignSystem.Roster.ItemRoster))
                .SetValue(owner, new TaleWorlds.CampaignSystem.Roster.ItemRoster());
            var harmony = new Harmony("test.lifecycle.owner");
            harmony.CreateClassProcessor(typeof(OwnerSettlementComponentPatch)).Patch();
            try { town.Owner = owner; Assert.Same(owner, town.Owner); }
            finally { harmony.UnpatchAll(harmony.Id); }
        });
    }

    internal static void WithRole(bool writer, bool frozen, Action<Mock<IObjectManager>> action)
    {
        var identity = new SessionIdentity();
        var lease = new AuthorityLease("room", "authority", writer ? "b" : "a", 5);
        Assert.True(identity.TryApply(lease, "a"));
        if (frozen) Assert.True(identity.TryFreezeLocalAuthority(lease, 1));
        var controller = new ControllerIdProvider();
        controller.SetControllerId("b");
        var objects = new Mock<IObjectManager>();
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new CampaignAuthorityContext(identity, controller)).As<ICampaignAuthorityContext>();
        builder.RegisterInstance(Mock.Of<ISyncPolicy>(policy => policy.AllowOriginal() == false)).As<ISyncPolicy>();
        builder.RegisterInstance(objects.Object).As<IObjectManager>();
        bool hadPrevious = ContainerProvider.TryGetContainer(out var previous);
        var previousMode = ModInformation.SessionMode;
        using var container = builder.Build();
        try { ContainerProvider.SetContainer(container); ModInformation.SessionMode = SessionMode.RemoteReplica; action(objects); }
        finally { ModInformation.SessionMode = previousMode; if (hadPrevious) ContainerProvider.SetContainer(previous); else ContainerProvider.Clear(); }
    }
}
