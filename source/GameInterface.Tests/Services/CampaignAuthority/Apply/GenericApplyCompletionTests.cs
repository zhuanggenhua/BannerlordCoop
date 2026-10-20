using Common;
using Common.Messaging;
using Common.Network;
using Common.Session;
using GameInterface.Services.ObjectManager;
using GameInterface.Utils;
using GameInterface.Utils.NetworkEvents;
using Moq;
using System;
using System.Runtime.CompilerServices;
using Xunit;

namespace GameInterface.Tests.Services.CampaignAuthority.Apply;

public class GenericApplyCompletionTests
{
    static GenericApplyCompletionTests() => RuntimeHelpers.RunModuleConstructor(typeof(Coop.Tests.Mocks.TestNetwork).Module.ModuleHandle);
    private sealed class Instance { }
    private sealed class Reference { }
    private sealed record Result : GenericNetworkReferenceEvent<Instance, Reference>
    {
        public override uint InstanceId { get; set; }
        public override uint ValueId { get; set; }
        public Result(uint instance, uint value) : base(instance, value) { InstanceId = instance; ValueId = value; }
    }
    // Open generic fixture must not enter production handler discovery.
    private sealed class Handler<T> : GenericHandler<Handler<T>, Instance>
    {
        public Handler(IMessageBroker broker, IObjectManager manager, Action applied) : base(broker, manager, Mock.Of<INetwork>()) =>
            SubscribeNetworkReference<Reference, Result>((_, _, _) => applied());
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MissingReferenceOrInstanceCannotAcknowledgeWhileResolvedApplyCan(bool instanceFound, bool referenceFound)
    {
        using var broker = new MessageBroker();
        var identity = new SessionIdentity(); var lease = new AuthorityLease("room", "writer", "a", 3);
        Assert.True(identity.TryApply(lease, "a"));
        CampaignApplyCompletion.Attach(broker, identity);
        var manager = new Mock<IObjectManager>();
        var instance = new Instance(); var reference = new Reference();
        manager.Setup(value => value.TryGetObjectWithLogging(1, out instance)).Returns(instanceFound);
        manager.Setup(value => value.TryGetObjectWithLogging(2, out reference)).Returns(referenceFound);
        bool applied = false;
        using var handler = new Handler<object>(broker, manager.Object, () => applied = true);
        broker.Publish(this, new Result(1, 2));
        GameThread.Run(() => { }, blocking: true);
        Assert.Equal(instanceFound && referenceFound, applied);
        Assert.Equal(applied, CampaignApplyCompletion.For(identity).CanAcknowledgeThrough(lease, out _));
    }
}
