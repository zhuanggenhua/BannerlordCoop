using Coop.Core.Common.Session;
using Xunit;

namespace Coop.Tests.Session;

public class CampaignAuthorityAvailabilityCoordinatorTests
{
    [Fact]
    public void SelectPrefersReadyRoomHost()
    {
        var coordinator = CreateCoordinator("host");
        Assert.True(coordinator.Report("guest", CampaignSimulationAvailability.MapReady, 1));
        Assert.True(coordinator.Report("host", CampaignSimulationAvailability.MapReady, 1));

        var selection = coordinator.Select();

        Assert.False(selection.IsPaused);
        Assert.Equal("host", selection.ControllerId);
    }

    [Fact]
    public void SelectUsesDeterministicReadyFallbackWhenHostIsUnavailable()
    {
        var coordinator = CreateCoordinator("host");
        Assert.True(coordinator.Report("controller-c", CampaignSimulationAvailability.MapReady, 1));
        Assert.True(coordinator.Report("host", CampaignSimulationAvailability.Unavailable, 1));
        Assert.True(coordinator.Report("controller-b", CampaignSimulationAvailability.MapReady, 1));

        var selection = coordinator.Select();

        Assert.False(selection.IsPaused);
        Assert.Equal("controller-b", selection.ControllerId);
    }

    [Fact]
    public void SelectPausesWhenNoControllerIsMapReady()
    {
        var coordinator = CreateCoordinator("host");
        Assert.True(coordinator.Report("host", CampaignSimulationAvailability.Unavailable, 1));
        Assert.True(coordinator.Report("guest", CampaignSimulationAvailability.Unknown, 1));

        var selection = coordinator.Select();

        Assert.True(selection.IsPaused);
        Assert.Equal(string.Empty, selection.ControllerId);
    }

    [Fact]
    public void ReportRejectsStaleAndConflictingRevisions()
    {
        var coordinator = CreateCoordinator("host");
        Assert.True(coordinator.Report("host", CampaignSimulationAvailability.MapReady, 2));

        Assert.True(coordinator.Report("host", CampaignSimulationAvailability.MapReady, 2));
        Assert.False(coordinator.Report("host", CampaignSimulationAvailability.Unavailable, 2));
        Assert.False(coordinator.Report("host", CampaignSimulationAvailability.Unavailable, 1));
        Assert.True(coordinator.Report("host", CampaignSimulationAvailability.Unavailable, 3));

        Assert.True(coordinator.Select().IsPaused);
    }

    [Fact]
    public void RemoveMakesControllerIneligibleAndSnapshotIsOrdered()
    {
        var coordinator = CreateCoordinator("host");
        Assert.True(coordinator.Report("controller-c", CampaignSimulationAvailability.MapReady, 1));
        Assert.True(coordinator.Report("host", CampaignSimulationAvailability.MapReady, 1));
        Assert.True(coordinator.Report("controller-b", CampaignSimulationAvailability.MapReady, 1));

        Assert.Equal(new[] { "controller-b", "controller-c", "host" },
            System.Linq.Enumerable.Select(coordinator.Snapshot(), entry => entry.ControllerId));

        Assert.True(coordinator.Remove("host"));
        Assert.Equal("controller-b", coordinator.Select().ControllerId);
        Assert.False(coordinator.Remove("host"));
    }

    [Fact]
    public void ReportRejectsInvalidControllerAndRevision()
    {
        var coordinator = CreateCoordinator("host");

        Assert.False(coordinator.Report(string.Empty, CampaignSimulationAvailability.MapReady, 1));
        Assert.False(coordinator.Report("host", CampaignSimulationAvailability.MapReady, 0));
        Assert.False(coordinator.Report("host", (CampaignSimulationAvailability)99, 1));
        Assert.Empty(coordinator.Snapshot());
    }

    private static ICampaignAuthorityAvailabilityCoordinator CreateCoordinator(string roomHostControllerId)
    {
        var identity = new SessionIdentity();
        Assert.True(identity.TrySetRoomHostControllerId(roomHostControllerId));
        return new CampaignAuthorityAvailabilityCoordinator(identity, new CampaignAuthorityCandidateSelector());
    }
}
