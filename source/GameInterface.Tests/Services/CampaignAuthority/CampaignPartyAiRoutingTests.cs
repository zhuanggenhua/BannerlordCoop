using Autofac;
using Common;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using GameInterface.Services.MobilePartyAIs;
using GameInterface.Services.MobilePartyAIs.Patches;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using Xunit;

namespace GameInterface.Tests.Services.CampaignAuthority;

[Collection(ModInformationRoleCollection.Name)]
public class CampaignPartyAiRoutingTests
{
    [Theory]
    [InlineData("a", "a", false, false, true)]
    [InlineData("b", "b", false, false, true)]
    [InlineData("a", "b", false, false, false)]
    [InlineData("b", "a", false, false, false)]
    [InlineData("b", "b", false, true, false)]
    [InlineData("a", "", true, false, false)]
    public void OnlyConfirmedUnfrozenWriterRunsWorldPartyAi(
        string local, string writer, bool paused, bool frozen, bool runs)
    {
        var identity = new SessionIdentity();
        var lease = new AuthorityLease("room", "authority", writer, 3, paused);
        Assert.True(identity.TryApply(lease, "a"));
        if (frozen) Assert.True(identity.TryFreezeLocalAuthority(lease, 1));
        var controller = new ControllerIdProvider();
        controller.SetControllerId(local);
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new CampaignAuthorityContext(identity, controller)).As<ICampaignAuthorityContext>();
        using var container = builder.Build();
        bool hadContainer = ContainerProvider.TryGetContainer(out var previous);
        var oldMode = ModInformation.SessionMode;
        var previousRunner = PartiesThinkPatch.BoundRunner;
        var runner = new RecordingRunner();
        var campaign = (Campaign)RuntimeHelpers.GetUninitializedObject(typeof(Campaign));
        try
        {
            ContainerProvider.SetContainer(container);
            ModInformation.SessionMode = local == "a" ? SessionMode.ListenAuthority : SessionMode.RemoteReplica;
            PartiesThinkPatch.Bind(runner);
            float dt = .016f;
            Assert.False(PartiesThinkPatch.PartiesThinkPrefix(campaign, ref dt));
            Assert.Equal(runs ? 1 : 0, runner.Calls);
            if (runs) Assert.Same(campaign, runner.Campaign);
        }
        finally
        {
            PartiesThinkPatch.Unbind(runner);
            if (previousRunner != null) PartiesThinkPatch.Bind(previousRunner);
            ModInformation.SessionMode = oldMode;
            if (hadContainer) ContainerProvider.SetContainer(previous);
            else ContainerProvider.Clear();
        }
    }

    private sealed class RecordingRunner : IPartyAiBatchRunner
    {
        public int Calls;
        public Campaign Campaign;
        public void TickBatch(Campaign campaign, float dt)
        { Assert.Equal(.016f, dt); Calls++; Campaign = campaign; }
    }
}
