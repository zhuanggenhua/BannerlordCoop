using Common;
using Common.Messaging;
using Common.Network;
using Common.Session;
using GameInterface.Registry.Auto;
using GameInterface.Services.ObjectManager;
using Moq;
using Serilog;
using System;
using System.Runtime.CompilerServices;
using Xunit;

namespace GameInterface.Tests.Registry;

public class AutoRegistryApplyCompletionTests
{
    static AutoRegistryApplyCompletionTests() => RuntimeHelpers.RunModuleConstructor(typeof(Coop.Tests.Mocks.TestNetwork).Module.ModuleHandle);
    public sealed class Instance { }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void CreationRejectionAndCallbackFailureRemainVisibleToTheFinalBarrier(bool added, bool callbackThrows)
    {
        using var broker = new MessageBroker(); var identity = new SessionIdentity();
        var lease = new AuthorityLease("room", "writer", "a", 3);
        Assert.True(identity.TryApply(lease, "a")); CampaignApplyCompletion.Attach(broker, identity);
        var manager = new Mock<IObjectManager>();
        manager.Setup(value => value.AddExisting("Instance_created", It.IsAny<object>(), 7)).Returns(added);
        var registry = new Mock<AutoRegistryBase<Instance>>(Mock.Of<ILogger>(), Mock.Of<IAutoRegistryFactory>(), manager.Object);
        if (callbackThrows) registry.Setup(value => value.OnClientCreated(It.IsAny<Instance>(), "created"))
            .Throws(new InvalidOperationException("creation callback failure"));
        using var handler = new AutoRegistryHandler<Instance>(registry.Object, broker, Mock.Of<INetwork>(), manager.Object);
        broker.Publish(this, new NetworkCreateInstance<Instance>("created", 7));
        GameThread.Run(() => { }, blocking: true);
        Assert.Equal(added && !callbackThrows, CampaignApplyCompletion.For(identity).CanAcknowledgeThrough(lease, out _));
        registry.Verify(value => value.OnClientCreated(It.IsAny<Instance>(), "created"), added ? Times.Once() : Times.Never());
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void DestroyMissingIdentityAndCallbackFailureNeverBecomeSuccessfulAcknowledgements(bool found, bool callbackThrows)
    {
        using var broker = new MessageBroker(); var identity = new SessionIdentity();
        var lease = new AuthorityLease("room", "writer", "a", 3);
        Assert.True(identity.TryApply(lease, "a")); CampaignApplyCompletion.Attach(broker, identity);
        var manager = new Mock<IObjectManager>(); var instance = new Instance(); string id = "created";
        manager.Setup(value => value.TryGetObjectWithLogging(7, out instance)).Returns(found);
        manager.Setup(value => value.TryGetIdWithLogging(instance, out id)).Returns(found);
        var registry = new Mock<AutoRegistryBase<Instance>>(Mock.Of<ILogger>(), Mock.Of<IAutoRegistryFactory>(), manager.Object);
        if (callbackThrows) registry.Setup(value => value.OnClientDestroyed(instance, id))
            .Throws(new InvalidOperationException("destroy callback failure"));
        using var handler = new AutoRegistryHandler<Instance>(registry.Object, broker, Mock.Of<INetwork>(), manager.Object);
        broker.Publish(this, new NetworkDestroyInstance<Instance>(7));
        GameThread.Run(() => { }, blocking: true);
        Assert.Equal(found && !callbackThrows, CampaignApplyCompletion.For(identity).CanAcknowledgeThrough(lease, out _));
        // The original logged-catch cleanup behavior is retained, not replaced by rollback.
        manager.Verify(value => value.Remove(instance), found ? Times.Once() : Times.Never());
    }
}
