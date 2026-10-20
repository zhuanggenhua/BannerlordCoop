using Autofac;
using Common;
using Common.Extensions;
using Common.Messaging;
using Common.Network;
using Common.Session;
using Common.Util;
using GameInterface.Registry.Auto;
using GameInterface.Services.ObjectManager;
using GameInterface.Tests.Bootstrap;
using Moq;
using Serilog;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace GameInterface.Tests.Registry;

[Collection(ModInformationRoleCollection.Name)]
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
        var registry = new ReceiptRegistry<Instance>(manager.Object) { CallbackThrows = callbackThrows };
        using var handler = new AutoRegistryHandler<Instance>(registry, broker, Mock.Of<INetwork>(), manager.Object);
        broker.Publish(this, new NetworkCreateInstance<Instance>("created", 7));
        GameThread.Run(() => { }, blocking: true);
        Assert.Equal(added && !callbackThrows, CampaignApplyCompletion.For(identity).CanAcknowledgeThrough(lease, out _));
        Assert.Equal(added ? 1 : 0, registry.CreatedCount);
        Assert.Equal(added ? "created" : null, registry.CreatedId);
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
        var registry = new ReceiptRegistry<Instance>(manager.Object) { CallbackThrows = callbackThrows };
        using var handler = new AutoRegistryHandler<Instance>(registry, broker, Mock.Of<INetwork>(), manager.Object);
        broker.Publish(this, new NetworkDestroyInstance<Instance>(7));
        GameThread.Run(() => { }, blocking: true);
        Assert.Equal(found && !callbackThrows, CampaignApplyCompletion.For(identity).CanAcknowledgeThrough(lease, out _));
        Assert.Equal(found ? 1 : 0, registry.DestroyedCount);
        Assert.Equal(found ? id : null, registry.DestroyedId);
        Assert.Same(found ? instance : null, registry.DestroyedInstance);
        // The original logged-catch cleanup behavior is retained, not replaced by rollback.
        manager.Verify(value => value.Remove(instance), found ? Times.Once() : Times.Never());
    }

    [Fact]
    public void ReceiptFixtureThenPatchBootstrapBuildInTheSameProcessKeepsRegistryDiscoveryValid()
    {
        CreationRejectionAndCallbackFailureRemainVisibleToTheFinalBarrier(false, false);
        CreationRejectionAndCallbackFailureRemainVisibleToTheFinalBarrier(true, true);
        CreationRejectionAndCallbackFailureRemainVisibleToTheFinalBarrier(true, false);
        DestroyMissingIdentityAndCallbackFailureNeverBecomeSuccessfulAcknowledgements(false, false);
        DestroyMissingIdentityAndCallbackFailureNeverBecomeSuccessfulAcknowledgements(true, true);
        DestroyMissingIdentityAndCallbackFailureNeverBecomeSuccessfulAcknowledgements(true, false);

        // Assembly.GetTypes returns this open definition; the existing discovery rule
        // excludes generic registries. Do not generate a concrete Castle registry proxy.
        Assert.Contains(typeof(ReceiptRegistry<>), AppDomain.CurrentDomain.GetDomainTypes());
        Assert.False(typeof(ReceiptRegistry<>).IsConcrete());
        Assert.False(typeof(ReceiptRegistry<Instance>).IsConcrete());
        bool hadContainer = ContainerProvider.TryGetContainer(out var previousContainer);
        try
        {
            using var bootstrap = new PatchBootstrap();
            Assert.NotNull(bootstrap.Container.Resolve<IObjectManager>());
            Assert.True(ContainerProvider.TryGetContainer(out var currentContainer));
            Assert.Same(bootstrap.Container, currentContainer);
        }
        finally
        {
            if (hadContainer) ContainerProvider.SetContainer(previousContainer);
            else ContainerProvider.Clear();
        }
    }

    private sealed class ReceiptRegistry<T> : AutoRegistryBase<T> where T : class
    {
        public ReceiptRegistry(IObjectManager manager)
            : base(Mock.Of<ILogger>(), Mock.Of<IAutoRegistryFactory>(), manager) { }

        public bool CallbackThrows { get; init; }
        public int CreatedCount { get; private set; }
        public int DestroyedCount { get; private set; }
        public string? CreatedId { get; private set; }
        public string? DestroyedId { get; private set; }
        public T? DestroyedInstance { get; private set; }
        public override IEnumerable<MethodBase> Constructors => Array.Empty<MethodBase>();
        public override IEnumerable<MethodBase> DestroyMethods => Array.Empty<MethodBase>();
        public override void RegisterAllObjects() { }
        public override void OnServerCreated(T obj, string id) { }
        public override void OnServerDestroyed(T obj, string id) { }
        public override void OnClientCreated(T obj, string id)
        {
            CreatedCount++;
            CreatedId = id;
            if (CallbackThrows) throw new InvalidOperationException("creation callback failure");
        }
        public override void OnClientDestroyed(T obj, string id)
        {
            DestroyedCount++;
            DestroyedId = id;
            DestroyedInstance = obj;
            if (CallbackThrows) throw new InvalidOperationException("destroy callback failure");
        }
    }
}
