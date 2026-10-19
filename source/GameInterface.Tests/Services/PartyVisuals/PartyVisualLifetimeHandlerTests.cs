using Common;
using Common.Util;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.PartyVisuals.Handlers;
using Moq;
using SandBox.View.Map.Visuals;
using Serilog;
using System.Collections.Generic;
using Xunit;

namespace GameInterface.Tests.Services.PartyVisuals;

public class PartyVisualLifetimeHandlerTests
{
    [Fact]
    public void OlderVisualReceiptsNeverRewindOrRebindExistingAllocations()
    {
        var (objects, visual) = RegisteredVisual();
        Assert.True(objects.TryApplyAllocationCheckpoint(new RegistryAllocationCheckpoint(100, new Dictionary<string, int>())));
        Assert.False(PartyVisualLifetimeHandler.TryRegisterNetworkVisual(objects, "MobilePartyVisual_42", visual, 50));
        Assert.False(PartyVisualLifetimeHandler.TryRegisterNetworkVisual(objects, "MobilePartyVisual_9", visual, 8));
        var checkpoint = objects.CaptureAllocationCheckpoint();
        Assert.Equal(100u, checkpoint.NextHandle);
        Assert.Equal(42, checkpoint.TypeCounters[typeof(MobilePartyVisual).AssemblyQualifiedName]);
        Assert.True(objects.TryGetHandle(visual, out var original));
        Assert.Equal(5u, original);
        Assert.Single(objects.GetHandleMap());
    }

    [Theory]
    [InlineData("MobilePartyVisual_Created_9000")]
    [InlineData("MobilePartyVisual_-1")]
    [InlineData("MobilePartyVisual_+99")]
    [InlineData("MobilePartyVisual_2147483648")]
    [InlineData("ItemRoster_9000")]
    public void OnlyGeneratedIdsOfTheActualLocalVisualTypeAdvanceItsCounter(string authorityId)
    {
        var (objects, visual) = RegisteredVisual();
        Assert.False(PartyVisualLifetimeHandler.TryRegisterNetworkVisual(objects, authorityId, visual, 50));
        var checkpoint = objects.CaptureAllocationCheckpoint();
        Assert.Equal(51u, checkpoint.NextHandle);
        Assert.Equal(1, checkpoint.TypeCounters[typeof(MobilePartyVisual).AssemblyQualifiedName]);
        Assert.Single(checkpoint.TypeCounters);
        Assert.Single(objects.GetHandleMap());
    }

    [Fact]
    public void ReservedZeroHandleDoesNotAlterEitherAllocationFloor()
    {
        var (objects, visual) = RegisteredVisual();
        var before = objects.CaptureAllocationCheckpoint();
        Assert.False(PartyVisualLifetimeHandler.TryRegisterNetworkVisual(objects, "MobilePartyVisual_9000", visual, 0));
        var after = objects.CaptureAllocationCheckpoint();
        Assert.Equal(before.NextHandle, after.NextHandle);
        Assert.Equal(before.TypeCounters[typeof(MobilePartyVisual).AssemblyQualifiedName], after.TypeCounters[typeof(MobilePartyVisual).AssemblyQualifiedName]);
    }

    [Fact]
    public void FinalAuthorityHandleExhaustsInsteadOfWrappingOnReplay()
    {
        var (objects, visual) = RegisteredVisual();
        Assert.False(PartyVisualLifetimeHandler.TryRegisterNetworkVisual(objects, "MobilePartyVisual_42", visual, uint.MaxValue));
        Assert.False(PartyVisualLifetimeHandler.TryRegisterNetworkVisual(objects, "MobilePartyVisual_9", visual, 8));
        Assert.Equal(0u, objects.CaptureAllocationCheckpoint().NextHandle);
        Assert.True(objects.TryGetHandle(visual, out var original));
        Assert.Equal(5u, original);
    }

    private static (ObjectManager Objects, MobilePartyVisual Visual) RegisteredVisual()
    {
        var objects = new ObjectManager(Mock.Of<ILogger>());
        var visual = ObjectHelper.SkipConstructor<MobilePartyVisual>();
        Assert.True(objects.AddExisting("MobilePartyVisual_testclient1", visual, 5));
        return (objects, visual);
    }

    [Fact]
    public void ReusedNativeVisualRetainsBindingAndObservesTheAuthorityAllocationFloor()
    {
        var identity = new SessionIdentity();
        Assert.True(identity.TryApply(new AuthorityLease("room", "writer-a", "a", 1), "a"));
        var controller = new ControllerIdProvider();
        controller.SetControllerId("b");
        var objects = new ObjectManager(Mock.Of<ILogger>(), new CampaignAuthorityContext(identity, controller));
        var visual = ObjectHelper.SkipConstructor<MobilePartyVisual>();
        Assert.True(objects.AddExisting("MobilePartyVisual_testclient1", visual, 100));
        Assert.True(objects.TryApplyAllocationCheckpoint(new RegistryAllocationCheckpoint(45028, new Dictionary<string, int>
        {
            [typeof(MobilePartyVisual).AssemblyQualifiedName] = 2901,
        })));

        Assert.False(PartyVisualLifetimeHandler.TryRegisterNetworkVisual(objects, "MobilePartyVisual_2902", visual, 45028));

        var checkpoint = objects.CaptureAllocationCheckpoint();
        Assert.Equal(45029u, checkpoint.NextHandle);
        Assert.Equal(2902, checkpoint.TypeCounters[typeof(MobilePartyVisual).AssemblyQualifiedName]);
        Assert.True(objects.TryGetId(visual, out var localId));
        Assert.Equal("MobilePartyVisual_testclient1", localId);
        Assert.True(objects.TryGetHandle(visual, out var localHandle));
        Assert.Equal(100u, localHandle);
        Assert.False(objects.TryGetObject<MobilePartyVisual>(45028, out _));
        Assert.Single(objects.GetHandleMap());

        Assert.True(identity.TryApply(new AuthorityLease("room", "writer-b", "b", 2), "a"));
        var createdAfterHandoff = ObjectHelper.SkipConstructor<MobilePartyVisual>();
        Assert.True(objects.AddNewObject(createdAfterHandoff, out var nextId));
        Assert.Equal("MobilePartyVisual_2903", nextId);
        Assert.True(objects.TryGetHandle(createdAfterHandoff, out var nextHandle));
        Assert.Equal(45029u, nextHandle);
    }

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
        objectManager.Setup(manager => manager.TryApplyAllocationCheckpoint(It.Is<RegistryAllocationCheckpoint>(
            checkpoint => checkpoint.NextHandle == authorityHandle + 1 && checkpoint.TypeCounters.Count == 0))).Returns(true);

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
