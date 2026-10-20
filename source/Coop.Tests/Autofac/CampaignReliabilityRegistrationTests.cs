using Coop.Core.Client.Services.Session;
using Coop.Core.Server.Services.Session;
using Autofac;
using Autofac.Builder;
using Coop.Core.Client;
using Coop.Core.Server;
using GameInterface;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.CampaignAuthority.Lifecycle;
using GameInterface.Services.CampaignAuthority.Movement;
using GameInterface.Services.MobileParties;
using GameInterface.Services.ObjectReadiness;
using HarmonyLib;
using Xunit;

namespace Coop.Tests.Autofac;

public class CampaignReliabilityRegistrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProductionRolesInjectTheSameSessionCompletionServices(bool server)
    {
        var builder = new ContainerBuilder();
        if (server) builder.RegisterModule<ServerModule>();
        else builder.RegisterModule<ClientModule>();
        builder.RegisterModule<GameInterfaceModule>();
        using var container = builder.Build(ContainerBuildOptions.IgnoreStartableComponents);
        using var first = container.BeginLifetimeScope();
        using var second = container.BeginLifetimeScope();
        var registry = first.Resolve<MobilePartyRegistry>();
        var drain = first.Resolve<ICampaignAsyncTaskDrain>();
        var graph = first.Resolve<ICampaignObjectGraphReadiness>();
        var lifetime = first.Resolve<ICampaignLifetimeCompletionDrain>();
        var roster = first.Resolve<ICampaignRosterCheckpointService>();
        Assert.IsType<ReadyCampaignRosterCheckpointService>(roster);
        Assert.IsType<CampaignMovementFreezePreparation>(first.Resolve<ICampaignMovementFreezePreparation>());
        object freezeHandler = server ? first.Resolve<CampaignSimulationAvailabilityServerHandler>() :
            first.Resolve<CampaignAuthorityFreezeClientHandler>();
        Assert.IsType<CampaignMovementFreezePreparation>(Field(freezeHandler, "movementPreparation"));
        Assert.Same(graph, Field(registry, "graphReadiness"));
        Assert.Same(graph, Field(drain, "graphReadiness"));
        Assert.Same(graph, Field(roster, "graph"));
        Assert.Same(lifetime, Field(registry, "lifetimeCompletion"));
        Assert.Same(lifetime, Field(drain, "lifetimeCompletion"));
        Assert.Same(lifetime, Field(roster, "lifetime"));
        Assert.Same(first.Resolve<ICampaignMovementCheckpointService>(), first.Resolve<ICampaignMovementCheckpointService>());
        Assert.NotSame(graph, second.Resolve<ICampaignObjectGraphReadiness>());
        Assert.NotSame(lifetime, second.Resolve<ICampaignLifetimeCompletionDrain>());
        Assert.NotSame(first.Resolve<ICampaignMovementCheckpointService>(), second.Resolve<ICampaignMovementCheckpointService>());
    }

    private static object Field(object instance, string name) => AccessTools.Field(instance.GetType(), name).GetValue(instance);
}
