using Autofac;

namespace GameInterface.Services.CampaignAuthority.Movement;

public sealed class MovementModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<CampaignAiContinuation>().As<ICampaignAiContinuation>().InstancePerDependency();
        builder.RegisterType<CampaignMovementCheckpointService>().As<ICampaignMovementCheckpointService>().InstancePerLifetimeScope();
        builder.RegisterType<CampaignMovementFreezePreparation>().As<ICampaignMovementFreezePreparation>().InstancePerDependency();
    }
}
