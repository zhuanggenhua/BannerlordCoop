using GameInterface.Services.ObjectManager;
using GameInterface.Services.PartyVisuals.Handlers;
using Moq;
using Xunit;

namespace GameInterface.Tests.Services.PartyVisuals;

public class PartyVisualLifetimeHandlerTests
{
    [Fact]
    public void TryRegisterNetworkVisual_ReusesAlreadyRegisteredLocalVisual()
    {
        var objectManager = new Mock<IObjectManager>(MockBehavior.Strict);
        var visual = new object();
        const string localId = "MobilePartyVisual_Player";
        const string authorityId = "MobilePartyVisual_Created_1";
        const uint authorityHandle = 42;
        string existingId = localId;
        objectManager.Setup(manager => manager.TryGetId(visual, out existingId)).Returns(true);

        bool registered = PartyVisualLifetimeHandler.TryRegisterNetworkVisual(
            objectManager.Object,
            authorityId,
            visual,
            authorityHandle);

        Assert.False(registered);
        objectManager.Verify(
            manager => manager.AddExisting(authorityId, visual, authorityHandle),
            Times.Never);
    }

    [Fact]
    public void TryRegisterNetworkVisual_AddsUnregisteredVisualWithAuthorityIdentity()
    {
        var objectManager = new Mock<IObjectManager>(MockBehavior.Strict);
        var visual = new object();
        const string authorityId = "MobilePartyVisual_Created_1";
        const uint authorityHandle = 42;
        string existingId = null;
        objectManager.Setup(manager => manager.TryGetId(visual, out existingId)).Returns(false);
        objectManager.Setup(manager => manager.AddExisting(authorityId, visual, authorityHandle))
            .Returns(true);

        bool registered = PartyVisualLifetimeHandler.TryRegisterNetworkVisual(
            objectManager.Object,
            authorityId,
            visual,
            authorityHandle);

        Assert.True(registered);
        objectManager.Verify(
            manager => manager.AddExisting(authorityId, visual, authorityHandle),
            Times.Once);
    }
}
