using Common;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using Moq;
using Serilog;
using System;
using System.Collections.Generic;
using Xunit;
using ObjectManagerService = GameInterface.Services.ObjectManager.ObjectManager;

namespace GameInterface.Tests.Services;

public class ObjectManagerAllocationCheckpointTests
{
    [Fact]
    public void AToBToAContinuesAfterRemovedObjectIdentities()
    {
        var authorityA = CreateManager();
        var first = new Payload();
        Assert.True(authorityA.AddNewObject(first, out var firstId));
        for (int index = 0; index < 2; index++)
        {
            var removed = new Payload();
            Assert.True(authorityA.AddNewObject(removed, out _));
            Assert.True(authorityA.Remove(removed));
        }

        var authorityB = CreateManager();
        Assert.True(authorityB.AddExisting(firstId, new Payload(), 1));
        Assert.True(authorityB.TryApplyAllocationCheckpoint(authorityA.CaptureAllocationCheckpoint()));
        var createdByB = new Payload();
        Assert.True(authorityB.AddNewObject(createdByB, out var idOnB));
        Assert.Equal("Payload_4", idOnB);
        Assert.True(authorityB.TryGetHandle(createdByB, out var handleOnB));
        Assert.Equal(4u, handleOnB);

        Assert.True(authorityA.TryApplyAllocationCheckpoint(authorityB.CaptureAllocationCheckpoint()));
        var createdAfterReturn = new Payload();
        Assert.True(authorityA.AddNewObject(createdAfterReturn, out var idAfterReturn));
        Assert.Equal("Payload_5", idAfterReturn);
        Assert.True(authorityA.TryGetHandle(createdAfterReturn, out var handleAfterReturn));
        Assert.Equal(5u, handleAfterReturn);
        Assert.Single(authorityB.GetHandleMap(), pair => pair.Key == firstId);
    }

    [Fact]
    public void OlderCheckpointCannotRewindEitherAllocator()
    {
        var manager = CreateManager();
        var older = manager.CaptureAllocationCheckpoint();
        var instance = new Payload();
        Assert.True(manager.AddNewObject(instance, out _));
        Assert.True(manager.TryApplyAllocationCheckpoint(older));

        var second = new Payload();
        Assert.True(manager.AddNewObject(second, out var id));
        Assert.Equal("Payload_2", id);
        Assert.True(manager.TryGetHandle(second, out var handle));
        Assert.Equal(2u, handle);
    }

    [Fact]
    public void InvalidCounterRejectsEntireCheckpoint()
    {
        var manager = CreateManager();
        var invalid = new RegistryAllocationCheckpoint(400, new Dictionary<string, int>
        {
            [typeof(Payload).AssemblyQualifiedName] = 300,
            ["invalid"] = -1,
        });
        Assert.False(manager.TryApplyAllocationCheckpoint(invalid));
        Assert.False(manager.TryApplyAllocationCheckpoint(null));
        var instance = new Payload();
        Assert.True(manager.AddNewObject(instance, out var id));
        Assert.Equal("Payload_1", id);
        Assert.True(manager.TryGetHandle(instance, out var handle));
        Assert.Equal(1u, handle);
    }

    [Fact]
    public void ExhaustedHandleSpaceStaysExhaustedAfterExplicitRegistrationAndReplay()
    {
        var manager = CreateManager();
        Assert.True(manager.TryApplyAllocationCheckpoint(new RegistryAllocationCheckpoint(0, new Dictionary<string, int>())));
        Assert.True(manager.AddExisting("received", new Payload(), 7));
        Assert.True(manager.TryApplyAllocationCheckpoint(new RegistryAllocationCheckpoint(50, new Dictionary<string, int>())));
        Assert.Equal(0u, manager.CaptureAllocationCheckpoint().NextHandle);
        Assert.Throws<InvalidOperationException>(() => manager.AddExisting("new", new Payload()));
        var fresh = new Payload();
        Assert.Throws<InvalidOperationException>(() => manager.AddNewObject(fresh, out _));
        Assert.False(manager.Contains(fresh));
    }

    [Fact]
    public void ExhaustedTypeCounterDoesNotWrapIntoNegativeIds()
    {
        var manager = CreateManager();
        Assert.True(manager.TryApplyAllocationCheckpoint(new RegistryAllocationCheckpoint(1, new Dictionary<string, int>
        {
            [typeof(Payload).AssemblyQualifiedName] = int.MaxValue,
        })));
        Assert.Throws<OverflowException>(() => manager.GetUniqueTypeId(new Payload()));
    }

    private static ObjectManagerService CreateManager()
    {
        var identity = new SessionIdentity();
        Assert.True(identity.TryApply(new AuthorityLease("room", "authority", "writer", 1), "writer"));
        var controller = new ControllerIdProvider();
        controller.SetControllerId("writer");
        return new ObjectManagerService(Mock.Of<ILogger>(), new CampaignAuthorityContext(identity, controller));
    }

    private sealed class Payload { }
}
