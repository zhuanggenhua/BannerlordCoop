using Autofac;
using Common;
using Common.Util;
using GameInterface.Policies;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using GameInterface.Services.MobileParties.Handlers;
using GameInterface.Services.MobileParties.Patches;
using Moq;
using System;
using System.Reflection;
using TaleWorlds.CampaignSystem.Party;
using Xunit;

namespace GameInterface.Tests.Services.MobileParties;

[Collection(ModInformationRoleCollection.Name)]
public class CampaignMovementRoleAdapterTests
{
    [Theory]
    [InlineData(SessionMode.ListenAuthority, "a", "a", true, false)]
    [InlineData(SessionMode.ListenAuthority, "a", "b", false, true)]
    [InlineData(SessionMode.RemoteReplica, "b", "b", true, false)]
    [InlineData(SessionMode.RemoteReplica, "b", "a", false, true)]
    public void NativeCommandCaptureAndObserverCorrectionFollowLease(
        SessionMode mode, string local, string writer, bool captures, bool corrects)
    {
        InRole(mode, local, writer, () =>
        {
            Assert.Equal(captures, CaptureOuter());
            Assert.Equal(corrects, MobilePartyBehaviorHandler.ShouldApplyObserverCorrections());
            // An inner native movement call must not produce a second publication.
            MobilePartyMovementStatePatches.Prefix(out var outer);
            try { Assert.False(CaptureOuter()); }
            finally { Complete(outer); }
            Assert.Equal(captures, outer);
            using (new AllowedThread()) Assert.False(CaptureOuter());
            using (CampaignExecutionScope.Enter(CampaignExecutionPhase.Prediction))
                Assert.False(CaptureOuter());
        });
    }

    private static bool CaptureOuter()
    {
        MobilePartyMovementStatePatches.Prefix(out var captured);
        Complete(captured);
        return captured;
    }

    private static void Complete(bool captured) => MobilePartyMovementStatePatches.Finalizer(null,
        typeof(MobileParty).GetMethod(nameof(MobileParty.RecalculateShortTermBehavior),
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), captured, null);

    private static void InRole(SessionMode mode, string local, string writer, Action action)
    {
        bool hadContainer = ContainerProvider.TryGetContainer(out var oldContainer);
        var previousMode = ModInformation.SessionMode;
        var identity = new SessionIdentity();
        Assert.True(identity.TryApply(new AuthorityLease("room", "authority", writer, 3), "a"));
        var controller = new ControllerIdProvider(); controller.SetControllerId(local);
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new CampaignAuthorityContext(identity, controller)).As<ICampaignAuthorityContext>();
        builder.RegisterInstance(Mock.Of<ISyncPolicy>(p => p.AllowOriginal() == false)).As<ISyncPolicy>();
        using var container = builder.Build();
        try { ModInformation.SessionMode = mode; ContainerProvider.SetContainer(container); action(); }
        finally
        {
            ModInformation.SessionMode = previousMode;
            if (hadContainer) ContainerProvider.SetContainer(oldContainer); else ContainerProvider.Clear();
        }
    }
}
