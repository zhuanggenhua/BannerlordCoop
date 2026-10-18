using Autofac;
using Common;
using Common.Messaging;
using GameInterface.Policies;
using GameInterface.Registry.Auto;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using Moq;
using System;
using Xunit;

namespace GameInterface.Tests.Registry;

[Collection(ModInformationRoleCollection.Name)]
public class LifetimeAuthorityTests
{
    [Theory]
    [InlineData(SessionMode.RemoteReplica, "guest", 1)]
    [InlineData(SessionMode.ListenAuthority, "host", 0)]
    public void LifecyclePublishesOnlyFromTheCampaignLeaseHolder(
        SessionMode mode, string localControllerId, int expectedEvents)
    {
        var identity = new SessionIdentity();
        Assert.True(identity.TryApply(new AuthorityLease("room", "authority-guest", "guest", 3), "host"));
        var controller = new ControllerIdProvider();
        controller.SetControllerId(localControllerId);
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new CampaignAuthorityContext(identity, controller)).As<ICampaignAuthorityContext>();
        builder.RegisterInstance(Mock.Of<ISyncPolicy>(policy => policy.AllowOriginal() == false));
        using var container = builder.Build();
        var previousMode = ModInformation.SessionMode;
        bool hadContainer = ContainerProvider.TryGetContainer(out var previousContainer);
        int created = 0;
        int destroyed = 0;
        Action<MessagePayload<InstanceCreated<RegistryProbe>>> captureCreate = _ => created++;
        Action<MessagePayload<InstanceDestroyed<RegistryProbe>>> captureDestroy = _ => destroyed++;
        MessageBroker.Instance.Subscribe(captureCreate);
        MessageBroker.Instance.Subscribe(captureDestroy);
        try
        {
            ModInformation.SessionMode = mode;
            ContainerProvider.SetContainer(container);
            var instance = new RegistryProbe();
            LifetimePatches<RegistryProbe>.CreatePrefix(ref instance);
            LifetimePatches<RegistryProbe>.DestroyPostfix(ref instance, __runOriginal: true);

            Assert.Equal(expectedEvents, created);
            Assert.Equal(expectedEvents, destroyed);
        }
        finally
        {
            MessageBroker.Instance.Unsubscribe(captureCreate);
            MessageBroker.Instance.Unsubscribe(captureDestroy);
            ModInformation.SessionMode = previousMode;
            if (hadContainer) ContainerProvider.SetContainer(previousContainer);
            else ContainerProvider.Clear();
        }
    }

    private sealed class RegistryProbe { }
}
