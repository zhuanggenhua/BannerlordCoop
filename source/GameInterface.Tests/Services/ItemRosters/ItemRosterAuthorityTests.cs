using Autofac;
using Common;
using Common.Messaging;
using Common.Util;
using GameInterface.Policies;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using GameInterface.Services.ItemRosters.Messages;
using GameInterface.Services.ItemRosters.Patches;
using GameInterface.Services.ItemRosters.Handlers;
using GameInterface.Services.ObjectManager;
using Moq;
using Serilog;
using System;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;
using TaleWorlds.Core;
using Xunit;

namespace GameInterface.Tests.Services.ItemRosters;

[Collection(ModInformationRoleCollection.Name)]
public sealed class ItemRosterAuthorityTests
{
    [Fact]
    public void ReceivedCreationInitializesNativeRosterAndRetainsAssignedHandle()
    {
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof(Coop.Tests.Mocks.TestNetwork).Module.ModuleHandle);
        var objects = new ObjectManager(Mock.Of<ILogger>());
        var broker = new MessageBroker();
        using var handler = new ItemRosterLifetimeHandler(broker, objects, Mock.Of<Common.Network.INetwork>());
        broker.Publish(this, new NetworkCreateItemRoster("ItemRoster_Created_8", 51));
        GameThread.Run(() => { }, blocking: true);
        Assert.True(objects.TryGetObject<ItemRoster>(51, out var roster));
        Assert.Empty(roster);
        var element = new EquipmentElement(new ItemObject());
        roster.AddToCounts(element, 2);
        Assert.Equal(2, roster.GetElementNumber(0));
        Assert.True(objects.TryGetHandle(roster, out var handle));
        Assert.Equal(51u, handle);
    }

    [Theory]
    [InlineData("b")]
    [InlineData("a")]
    public void LocalMarketStockRemainsLocalForWriterAndObserver(string writer)
    {
        var previous = Campaign.Current;
        using var fixture = new Fixture(false, "b", writer, false, false);
        try
        {
            var settlement = ObjectHelper.SkipConstructor<Settlement>();
            settlement.Party = ObjectHelper.SkipConstructor<PartyBase>();
            settlement.Party.ItemRoster = fixture.Roster;
            var campaign = ObjectHelper.SkipConstructor<Campaign>();
            campaign.CampaignObjectManager = new CampaignObjectManager
            {
                Settlements = new MBReadOnlyList<Settlement>(new System.Collections.Generic.List<Settlement> { settlement })
            };
            Campaign.Current = campaign;
            var result = 0;
            var element = new EquipmentElement(new ItemObject());
            Assert.True(ItemRosterPatch.AddToCountsPrefix(fixture.Roster, ref result, element, 1));
            ItemRosterPatch.AddToCountsPostfix(fixture.Roster, ref result, element, 1);
            Assert.True(ItemRosterPatch.ClearPrefix(fixture.Roster));
            Assert.Equal(0, fixture.Updates + fixture.Clears);
        }
        finally { Campaign.Current = previous; }
    }

    [Fact]
    public void AbsoluteReceivedCountReappliesWithoutDuplicatingNativeInventory()
    {
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof(Coop.Tests.Mocks.TestNetwork).Module.ModuleHandle);
        using var fixture = new Fixture(true, "a", "b", false, false);
        var element = new EquipmentElement(new ItemObject());
        fixture.Roster.AddToCounts(element, 3);
        GameThread.Run(() =>
        {
            ItemRosterPatch.SetCountOverride(fixture.Roster, element, 7);
            ItemRosterPatch.SetCountOverride(fixture.Roster, element, 7);
        }, blocking: true);
        Assert.Equal(7, fixture.Roster.GetElementNumber(fixture.Roster.FindIndexOfElement(element)));
        Assert.Equal(0, fixture.Updates);
    }

    [Theory]
    [InlineData(true, "a", "a", false, false, true)]
    [InlineData(false, "b", "b", false, false, true)]
    [InlineData(true, "a", "b", false, false, false)]
    [InlineData(false, "b", "a", false, false, false)]
    [InlineData(true, "a", "a", false, true, false)]
    [InlineData(false, "b", "b", false, true, false)]
    [InlineData(true, "a", "", true, false, false)]
    [InlineData(false, "b", "", true, false, false)]
    public void OnlyConfirmedUnfrozenWriterMutatesAndPublishesSharedRoster(
        bool host, string local, string writer, bool paused, bool frozen, bool expected)
    {
        using var fixture = new Fixture(host, local, writer, paused, frozen);
        var result = 0;
        var element = new EquipmentElement(ObjectHelper.SkipConstructor<ItemObject>());
        var allowed = ItemRosterPatch.AddToCountsPrefix(fixture.Roster, ref result, element, 1);
        Assert.Equal(expected, allowed);
        if (allowed) ItemRosterPatch.AddToCountsPostfix(fixture.Roster, ref result, element, 1);
        Assert.Equal(expected ? 1 : 0, fixture.Updates);
        Assert.Equal(expected, ItemRosterPatch.ClearPrefix(fixture.Roster));
        Assert.Equal(expected ? 1 : 0, fixture.Clears);
    }

    [Fact]
    public void ReceivedApplyRunsNativelyWithoutPublishing()
    {
        using var fixture = new Fixture(true, "a", "b", false, false);
        var result = 0;
        var element = new EquipmentElement(ObjectHelper.SkipConstructor<ItemObject>());
        using (new AllowedThread())
        {
            Assert.True(ItemRosterPatch.AddToCountsPrefix(fixture.Roster, ref result, element, 1));
            ItemRosterPatch.AddToCountsPostfix(fixture.Roster, ref result, element, 1);
            Assert.True(ItemRosterPatch.ClearPrefix(fixture.Roster));
        }
        Assert.Equal(0, fixture.Updates + fixture.Clears);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SessionMode mode = ModInformation.SessionMode;
        private readonly bool hadContainer;
        private readonly ILifetimeScope previous;
        private readonly IContainer container;
        private readonly Action<MessagePayload<ItemRosterUpdated>> updated;
        private readonly Action<MessagePayload<ItemRosterCleared>> cleared;
        public readonly ItemRoster Roster = new();
        public int Updates;
        public int Clears;
        public Fixture(bool host, string local, string writer, bool paused, bool frozen)
        {
            hadContainer = ContainerProvider.TryGetContainer(out previous);
            ModInformation.SessionMode = host ? SessionMode.ListenAuthority : SessionMode.RemoteReplica;
            var identity = new SessionIdentity();
            var lease = new AuthorityLease("room", "world", writer, 7, paused);
            Assert.True(identity.TryApply(lease, "a"));
            if (frozen) Assert.True(identity.TryFreezeLocalAuthority(lease, 9));
            var controller = new ControllerIdProvider();
            controller.SetControllerId(local);
            var objects = new Mock<IObjectManager>();
            string id = "inventory";
            objects.Setup(value => value.TryGetId(Roster, out id)).Returns(true);
            var policy = new Mock<ISyncPolicy>();
            policy.Setup(value => value.AllowOriginal()).Returns(false);
            var builder = new ContainerBuilder();
            builder.RegisterInstance(objects.Object).As<IObjectManager>();
            builder.RegisterInstance(policy.Object).As<ISyncPolicy>();
            builder.RegisterInstance(new CampaignAuthorityContext(identity, controller)).As<ICampaignAuthorityContext>();
            container = builder.Build();
            ContainerProvider.SetContainer(container);
            updated = _ => Updates++;
            cleared = _ => Clears++;
            MessageBroker.Instance.Subscribe(updated);
            MessageBroker.Instance.Subscribe(cleared);
        }
        public void Dispose()
        {
            MessageBroker.Instance.Unsubscribe(updated);
            MessageBroker.Instance.Unsubscribe(cleared);
            ModInformation.SessionMode = mode;
            if (hadContainer) ContainerProvider.SetContainer(previous); else ContainerProvider.Clear();
            container.Dispose();
        }
    }
}
