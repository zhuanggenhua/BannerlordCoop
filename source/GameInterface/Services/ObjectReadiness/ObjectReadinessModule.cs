using Autofac;

namespace GameInterface.Services.ObjectReadiness;

internal sealed class ObjectReadinessModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<CampaignObjectGraphReadiness>().As<ICampaignObjectGraphReadiness>().InstancePerLifetimeScope();
    }
}
