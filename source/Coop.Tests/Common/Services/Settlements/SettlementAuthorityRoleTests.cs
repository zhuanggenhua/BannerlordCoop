using Autofac;
using Common;
using Common.Messaging;
using Common.Network;
using Common.Network.Coalescing;
using Common.Session;
using Common.PacketHandlers;
using Common.Serialization;
using Common.Util;
using Coop.Core.Client.Services.Settlements.Handlers;
using Coop.Core.Client;
using Coop.Core.Server.Services.Settlements.Handlers;
using Coop.Core.Server.Services.Settlements.Messages;
using GameInterface;
using GameInterface.Policies;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Settlements.Handlers;
using GameInterface.Services.Settlements.Messages;
using GameInterface.Services.Settlements.Patches;
using HarmonyLib;
using Moq;
using LiteNetLib;
using System;
using System.Linq;
using System.IO;
using ProtoBuf.Meta;
using System.Threading;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using Xunit;

namespace Coop.Tests.Services.Settlements;

[Collection(ModInformationRoleCollection.Name)]
public class SettlementAuthorityRoleTests
{
    [Fact]
    public void OriginalSettlementPayloadFieldsRemainWireCompatibleAndAuthorityScoped()
    {
        var cases = new (IMessage message, string hex)[]
        {
            (new NetworkChangeSettlementComponentOwner("town", "owner"), "0A04746F776E12056F776E6572"),
            (new NetworkChangeSettlementComponentGold("town", 42), "0A04746F776E102A"),
            (new NetworkChangeSettlementComponentIsOwnerUnassigned("town", true), "0A04746F776E1001")
        };
        foreach (var item in cases)
        {
            Assert.IsAssignableFrom<ICampaignAuthorityScopedMessage>(item.message);
            using var stream = new MemoryStream();
            RuntimeTypeModel.Default.Serialize(stream, item.message);
            Assert.Equal(item.hex, Convert.ToHexString(stream.ToArray()));
            stream.Position = 0;
            Assert.Equal(item.message, RuntimeTypeModel.Default.Deserialize(stream, null, item.message.GetType()));
        }
    }

    [Fact]
    public void ClientTickUsesTheInjectedOriginalCoalescerOnTheGameThread()
    {
        var coalescer = new Mock<ISendCoalescer>();
        coalescer.SetupGet(value => value.HasPending).Returns(true);
        using var cancellation = new CancellationTokenSource();
        using var client = new CoopClient(Mock.Of<INetworkConfig>(), Mock.Of<IMessageBroker>(),
            Mock.Of<IPacketManager>(), Mock.Of<IMessagePacketHandler>(), Mock.Of<ICommonSerializer>(),
            Mock.Of<IReliableMessageBatcher<NetPeer>>(), cancellation, sendCoalescer: coalescer.Object);
        using var flushed = new ManualResetEventSlim();
        coalescer.Setup(value => value.Flush(client)).Callback(() =>
        { Assert.True(GameThread.Instance.IsGameThread); flushed.Set(); });
        client.Update(TimeSpan.Zero);
        Assert.True(flushed.Wait(TimeSpan.FromSeconds(10)));
        coalescer.Verify(value => value.Flush(client), Times.Once);
    }

    [Theory]
    [InlineData(true, true, CampaignExecutionPhase.WorldWrite, true)]
    [InlineData(false, true, CampaignExecutionPhase.WorldWrite, false)]
    [InlineData(true, false, CampaignExecutionPhase.WorldWrite, false)]
    [InlineData(true, true, CampaignExecutionPhase.Replay, false)]
    [InlineData(true, true, CampaignExecutionPhase.CheckpointInstall, false)]
    public void OriginalThreeConversionsPublishOnlyConfirmedWriterResults(bool writer, bool confirmed, CampaignExecutionPhase phase, bool sends)
    {
        WithRole(writer, confirmed, (identity, objects) =>
        {
            using var broker = new MessageBroker();
            var network = new Mock<INetwork>();
            var coalescer = new SendCoalescer();
            var town = ObjectHelper.SkipConstructor<Town>();
            string componentId = "town";
            objects.Setup(value => value.TryGetId(town, out componentId)).Returns(true);
            using var publisher = new ServerSettlementComponentHandler(broker, network.Object, objects.Object, coalescer);
            using (CampaignExecutionScope.Enter(phase))
            {
                broker.Publish(this, new SettlementComponentOwnerChanged(town, "owner"));
                broker.Publish(this, new SettlementComponentIsOwnerUnassignedChanged(town, true));
                broker.Publish(this, new SettlementComponentGoldChanged(town, 10));
                broker.Publish(this, new SettlementComponentGoldChanged(town, 42));
            }
            Assert.Equal(sends, coalescer.HasPending);
            coalescer.Flush(network.Object);
            network.Verify(value => value.SendAll(It.IsAny<NetworkChangeSettlementComponentOwner>()), sends ? Times.Once() : Times.Never());
            network.Verify(value => value.SendAll(It.IsAny<NetworkChangeSettlementComponentIsOwnerUnassigned>()), sends ? Times.Once() : Times.Never());
            network.Verify(value => value.SendAll(It.Is<NetworkChangeSettlementComponentGold>(x => x.Gold == 42)), sends ? Times.Once() : Times.Never());
            network.Verify(value => value.SendAll(It.Is<NetworkChangeSettlementComponentGold>(x => x.Gold == 10)), Times.Never());
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ScopedReceiveWaitsForNativeApplyAndMissingReferenceCannotAcknowledge(int missingReference)
    {
        WithRole(true, true, (identity, objects) =>
        {
            using var broker = new MessageBroker();
            using var binding = CampaignApplyCompletion.Attach(broker, identity);
            var completion = CampaignApplyCompletion.For(identity);
            var network = new Mock<INetwork>();
            using var converter = new ClientSettlementComponentHandler(broker, network.Object);
            using var native = new SettlementComponentHandler(broker, objects.Object);
            var town = ObjectHelper.SkipConstructor<Town>();
            var settlement = ObjectHelper.SkipConstructor<Settlement>();
            var owner = ObjectHelper.SkipConstructor<PartyBase>();
            AccessTools.GetDeclaredFields(typeof(Settlement)).Single(x => x.FieldType == typeof(PartyBase)).SetValue(settlement, owner);
            AccessTools.GetDeclaredFields(typeof(PartyBase)).Single(x => x.FieldType == typeof(TaleWorlds.CampaignSystem.Roster.ItemRoster))
                .SetValue(owner, new TaleWorlds.CampaignSystem.Roster.ItemRoster());
            SettlementComponent component = town;
            objects.Setup(value => value.TryGetObject<SettlementComponent>("town", out component)).Returns(missingReference != 1);
            objects.Setup(value => value.TryGetObject<Settlement>("owner", out settlement)).Returns(missingReference != 2);
            var harmony = new Harmony("test.scoped.settlement.receive");
            harmony.CreateClassProcessor(typeof(OwnerSettlementComponentPatch)).Patch();
            harmony.CreateClassProcessor(typeof(GoldSettlementComponentPatch)).Patch();
            harmony.CreateClassProcessor(typeof(IsOwnerUnassignedSettlementComponentPatch)).Patch();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            int echoes = 0;
            Action<MessagePayload<SettlementComponentOwnerChanged>> ownerEcho = _ => Interlocked.Increment(ref echoes);
            Action<MessagePayload<SettlementComponentGoldChanged>> goldEcho = _ => Interlocked.Increment(ref echoes);
            Action<MessagePayload<SettlementComponentIsOwnerUnassignedChanged>> flagEcho = _ => Interlocked.Increment(ref echoes);
            MessageBroker.Instance.Subscribe(ownerEcho); MessageBroker.Instance.Subscribe(goldEcho); MessageBroker.Instance.Subscribe(flagEcho);
            try
            {
                GameThread.Run(() => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); });
                Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
                broker.Publish(this, new NetworkChangeSettlementComponentOwner("town", "owner"));
                broker.Publish(this, new NetworkChangeSettlementComponentGold("town", 42));
                broker.Publish(this, new NetworkChangeSettlementComponentIsOwnerUnassigned("town", true));
                Assert.Equal(CampaignApplyStatus.Pending, completion.GetStatusThrough(identity.CurrentLease, out _));
                Assert.Equal(0, town.Gold);
                release.Set();
                GameThread.Run(() => { }, blocking: true);
                Assert.Equal(missingReference != 0 ? CampaignApplyStatus.Faulted : CampaignApplyStatus.Ready,
                    completion.GetStatusThrough(identity.CurrentLease, out var reason));
                if (missingReference == 1) Assert.Contains("Missing SettlementComponent town", reason);
                else if (missingReference == 2) Assert.Contains("Missing SettlementComponent owner PartyBase owner", reason);
                else { Assert.Equal(42, town.Gold); Assert.True(town.IsOwnerUnassigned); Assert.Same(owner, town.Owner); }
                Assert.Equal(0, echoes);
                network.Verify(value => value.SendAll(It.IsAny<IMessage>()), Times.Never());
            }
            finally
            {
                release.Set();
                harmony.UnpatchAll(harmony.Id);
                MessageBroker.Instance.Unsubscribe(ownerEcho); MessageBroker.Instance.Unsubscribe(goldEcho); MessageBroker.Instance.Unsubscribe(flagEcho);
            }
        });
    }

    private static void WithRole(bool writer, bool confirmed, Action<SessionIdentity, Mock<IObjectManager>> test)
    {
        var identity = new SessionIdentity();
        if (confirmed) Assert.True(identity.TryApply(new AuthorityLease("room", "authority", writer ? "b" : "a", 5), "a"));
        var controller = new ControllerIdProvider(); controller.SetControllerId("b");
        var objects = new Mock<IObjectManager>();
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new CampaignAuthorityContext(identity, controller)).As<ICampaignAuthorityContext>();
        builder.RegisterInstance(Mock.Of<ISyncPolicy>(x => x.AllowOriginal() == false)).As<ISyncPolicy>();
        builder.RegisterInstance(objects.Object).As<IObjectManager>();
        bool hadPrevious = ContainerProvider.TryGetContainer(out var previous);
        var mode = ModInformation.SessionMode;
        using var container = builder.Build();
        try { ContainerProvider.SetContainer(container); ModInformation.SessionMode = SessionMode.RemoteReplica; test(identity, objects); }
        finally { ModInformation.SessionMode = mode; if (hadPrevious) ContainerProvider.SetContainer(previous); else ContainerProvider.Clear(); }
    }
}
