using Autofac;
using Common;
using Common.Messaging;
using GameInterface.Policies;
using GameInterface.Registry.Auto;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using GameInterface.Services.TroopRosters.Messages;
using GameInterface.Services.TroopRosters.Patches;
using Moq;
using System;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem.Roster;
using Xunit;

namespace GameInterface.Tests.Services.TroopRosters;

[Collection(ModInformationRoleCollection.Name)]
public class TroopRosterAuthorityCreationTests
{
    static TroopRosterAuthorityCreationTests() =>
        RuntimeHelpers.RunModuleConstructor(typeof(Coop.Tests.Mocks.TestNetwork).Module.ModuleHandle);

    [Theory]
    [InlineData("a", "a", false, false, true)]
    [InlineData("b", "b", false, false, true)]
    [InlineData("a", "b", false, false, false)]
    [InlineData("b", "a", false, false, false)]
    [InlineData("b", "b", false, true, false)]
    [InlineData("a", "", true, false, false)]
    public void OnlyUnfrozenWriterAnnouncesNativeDummyAndPartyRosterCreation(
        string local, string writer, bool paused, bool frozen, bool creates)
    {
        var identity = new SessionIdentity();
        var lease = new AuthorityLease("room", "authority", writer, 3, paused);
        Assert.True(identity.TryApply(lease, "a"));
        if (frozen) Assert.True(identity.TryFreezeLocalAuthority(lease, 1));
        var controller = new ControllerIdProvider();
        controller.SetControllerId(local);
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new CampaignAuthorityContext(identity, controller)).As<ICampaignAuthorityContext>();
        builder.RegisterInstance(Mock.Of<ISyncPolicy>(policy => policy.AllowOriginal() == false)).As<ISyncPolicy>();
        using var container = builder.Build();
        bool hadContainer = ContainerProvider.TryGetContainer(out var previous);
        var oldMode = ModInformation.SessionMode;
        int created = 0, owners = 0;
        Action<MessagePayload<InstanceCreated<TroopRoster>>> onCreate = _ => created++;
        Action<MessagePayload<PartyOwnerSet>> onOwner = _ => owners++;
        var broker = MessageBroker.Instance;
        broker.Subscribe(onCreate);
        broker.Subscribe(onOwner);
        try
        {
            ContainerProvider.SetContainer(container);
            ModInformation.SessionMode = local == "a" ? SessionMode.ListenAuthority : SessionMode.RemoteReplica;
            TroopRoster dummy = null!;
            Assert.Equal(!creates, TroopRosterCreateDummyPatch.Prefix_CreateDummyTroopRoster(ref dummy));
            if (creates) { Assert.NotNull(dummy); Assert.Empty(dummy.GetTroopRoster()); }
            TroopRosterOwnerPartyRegistrationPatch.Postfix(new TroopRoster());
            Assert.Equal(creates ? 2 : 0, created);
            Assert.Equal(creates ? 1 : 0, owners);
        }
        finally
        {
            broker.Unsubscribe(onCreate);
            broker.Unsubscribe(onOwner);
            ModInformation.SessionMode = oldMode;
            if (hadContainer) ContainerProvider.SetContainer(previous);
            else ContainerProvider.Clear();
        }
    }
}
