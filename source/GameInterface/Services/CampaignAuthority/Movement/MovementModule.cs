using Autofac;

namespace GameInterface.Services.CampaignAuthority.Movement;

public sealed class MovementModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<CampaignMovementCheckpointService>().As<ICampaignMovementCheckpointService>().InstancePerLifetimeScope();
        builder.RegisterType<CampaignMovementFreezePreparation>().As<ICampaignMovementFreezePreparation>().InstancePerDependency();
    }
}
