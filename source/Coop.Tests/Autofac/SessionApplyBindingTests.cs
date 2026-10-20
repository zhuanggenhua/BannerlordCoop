using Autofac;
using Common;
using Common.Messaging;
using Common.Session;
using System;
using Coop.Core.Client;
using Coop.Core.Client.Services.Session;
using Coop.Core.Server;
using Coop.Core.Server.Services.Session;
using GameInterface;
using Xunit;

namespace Coop.Tests.Autofac;

[Collection(ModInformationRoleCollection.Name)]
public class SessionApplyBindingTests
{
    private static IContainer Build(bool server)
    {
        var builder = new ContainerBuilder();
        if (server) builder.RegisterModule<ServerModule>();
        else builder.RegisterModule<ClientModule>();
        builder.RegisterModule<GameInterfaceModule>();
        return builder.Build();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProductionContainerDisposalAllowsAnotherSessionOnTheSharedBroker(bool server)
    {
        ISessionIdentity previous;
        using (var first = Build(server)) previous = first.Resolve<ISessionIdentity>();
        using var next = Build(server);
        Assert.NotSame(previous, next.Resolve<ISessionIdentity>());
        Assert.Same(MessageBroker.Instance, next.Resolve<IMessageBroker>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IdleProductionContainersCanCoexistAndChildHandlersShareTheirRoomSession(bool server)
    {
        using var first = Build(server);
        using var second = Build(server);
        Assert.NotSame(first.Resolve<ISessionIdentity>(), second.Resolve<ISessionIdentity>());
        using var child = first.BeginLifetimeScope();
        Assert.Same(first.Resolve<ISessionIdentity>(), child.Resolve<ISessionIdentity>());
        if (server) child.Resolve<CampaignSimulationAvailabilityServerHandler>();
        else child.Resolve<CampaignAuthorityFreezeClientHandler>();
    }
    private sealed record Result : ICampaignAuthorityScopedMessage;
    [Fact]
    public void ProductionSessionReplacementRejectsOldQueuedApplyWithoutFaultingTheNewSession()
    {
        IContainer next = null;
        ISessionIdentity previousIdentity = null, nextIdentity = null;
        var lease = new AuthorityLease("room", "writer", "a", 3);
        var broker = MessageBroker.Instance;
        int applied = 0;
        void Handle(MessagePayload<Result> _) => GameThread.EnqueueSafe(() => applied++);
        broker.Subscribe<Result>(Handle);
        try
        {
            GameThread.Run(() =>
            {
                using (var previous = Build(false))
                {
                    previousIdentity = previous.Resolve<ISessionIdentity>();
                    Assert.True(previousIdentity.TryApply(lease, "a"));
                    broker.Publish(this, new Result());
                }
                next = Build(false);
                nextIdentity = next.Resolve<ISessionIdentity>();
                Assert.True(nextIdentity.TryApply(lease, "a"));
            }, blocking: true);
            GameThread.Run(() => { }, blocking: true);
            Assert.Equal(0, applied);
            Assert.False(CampaignApplyCompletion.For(previousIdentity).CanAcknowledgeThrough(lease, out _));
            Assert.True(CampaignApplyCompletion.For(nextIdentity).CanAcknowledgeThrough(lease, out _));
        }
        finally
        {
            next?.Dispose();
            broker.Unsubscribe<Result>(Handle);
        }
        GC.KeepAlive((Action<MessagePayload<Result>>)Handle);
    }

}
