using Common;
using GameInterface.Services.CampaignAuthority;
using Moq;
using System;

namespace Coop.Tests.Mocks;

internal static class CampaignHandoffTestServices
{
    public static CampaignSchedulerCheckpoint Checkpoint() => new(0, 50, 0,
        Array.Empty<CampaignPeriodicTickerState>(), Array.Empty<CampaignPeriodicEventState>());

    public static ICampaignSchedulerCheckpointService Scheduler()
    {
        var service = new Mock<ICampaignSchedulerCheckpointService>();
        var checkpoint = Checkpoint();
        service.Setup(value => value.TryCapture(out checkpoint)).Returns(true);
        service.Setup(value => value.TryApply(It.IsAny<CampaignSchedulerCheckpoint>())).Returns(true);
        return service.Object;
    }

    public static ICampaignAsyncTaskDrain Drained() => Mock.Of<ICampaignAsyncTaskDrain>(value => value.TryDrain());
}
