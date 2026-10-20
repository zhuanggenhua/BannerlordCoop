using Common;
using Common.Messaging;
using Common.Session;
using GameInterface.Services.Entity;
using GameInterface.Services.MobileParties.Data;
using GameInterface.Services.MobileParties.Handlers;
using GameInterface.Services.MobileParties.Interfaces;
using GameInterface.Services.MobileParties.Messages.Behavior;
using GameInterface.Services.ObjectManager;
using Moq;
using System;
using System.Runtime.CompilerServices;
using Xunit;

namespace GameInterface.Tests.Services.CampaignAuthority.Apply;
public class MovementApplyCompletionTests
{
    static MovementApplyCompletionTests() => RuntimeHelpers.RunModuleConstructor(typeof(Coop.Tests.Mocks.TestNetwork).Module.ModuleHandle);
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MapperOrPartyResolutionFailureCannotCertifyCriticalMovement(bool mappingWorks)
    {
        using var broker = new MessageBroker(); var identity = new SessionIdentity();
        var lease = new AuthorityLease("room", "source", "b", 3); Assert.True(identity.TryApply(lease, "a"));
        CampaignApplyCompletion.Attach(broker, identity);
        var data = new PartyBehaviorUpdateData("missing", default, null, default, default, default, default, default);
        var wire = new Mock<IPartyBehaviorWireMapper>();
        wire.Setup(value => value.TryFromNetwork(It.IsAny<NetworkPartyBehaviorUpdateData>(), out data)).Returns(mappingWorks);
        var controller = new ControllerIdProvider(); controller.SetControllerId("a");
        using var handler = new MobilePartyBehaviorHandler(broker, controller, Mock.Of<IMobilePartyInterface>(), wire.Object,
            Mock.Of<IObjectManager>(), Mock.Of<IMobilePartyBehaviorSnapshot>());
        broker.Publish(this, new NetworkCampaignAuthorityPartyBehaviorDelta(default));
        GameThread.Run(() => { }, blocking: true);
        Assert.False(CampaignApplyCompletion.For(identity).CanAcknowledgeThrough(lease, out var reason));
        Assert.Contains(mappingWorks ? "missing party" : "wire references", reason);
    }
}
