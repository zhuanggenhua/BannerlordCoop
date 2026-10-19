#if DEBUG
using Common;
using Common.Commands;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Party.Commands;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using Moq;
using Xunit;

namespace GameInterface.Tests.Services.Party;

[Collection(ModInformationRoleCollection.Name)]
public sealed class PrisonerSaleDebugCommandTests
{
    [Theory]
    [InlineData("town_ES1", "town", false, true)]
    [InlineData("town_ES1", "town_backstreet", false, true)]
    [InlineData("town_ES1", "town", true, false)]
    [InlineData("town_ES1", "town_outside", false, false)]
    [InlineData("town_ES2", "town", false, false)]
    [InlineData("", "town", false, false)]
    public void OpenBackstreet_RequiresDanusticaTownMenuAndNoMission(
        string settlementId, string currentMenu, bool missionActive, bool expected)
    {
        Assert.Equal(expected, PrisonerSaleDebugCommands.CanOpenBackstreet(settlementId, currentMenu, missionActive));
    }

    [Fact]
    public void OpenBackstreetCommand_UsesClientLocalCommandSide()
    {
        var command = new PrisonerSaleDebugCommands.OpenBackstreetCommand(
            new Mock<IObjectManager>(MockBehavior.Strict).Object,
            new Mock<IPlayerManager>(MockBehavior.Strict).Object,
            new Mock<ICampaignAuthorityContext>(MockBehavior.Strict).Object,
            new ControllerIdProvider());

        Assert.Equal("coop.debug.prisoner_sale", command.Prefix);
        Assert.Equal("open_backstreet", command.Name);
        Assert.Equal(CoopCommandSide.Both, command.Side);
    }

    [Fact]
    public void OpenBackstreet_RejectsDedicatedAuthorityBeforeResolvingPlayerObjects()
    {
        var previousMode = ModInformation.SessionMode;
        try
        {
            ModInformation.SessionMode = SessionMode.DedicatedAuthority;
            var objects = new Mock<IObjectManager>(MockBehavior.Strict);
            var players = new Mock<IPlayerManager>(MockBehavior.Strict);
            var authority = new Mock<ICampaignAuthorityContext>(MockBehavior.Strict);
            var controller = new ControllerIdProvider();
            controller.SetControllerId("controller-a");

            var result = new PrisonerSaleDebugCommands.OpenBackstreetCommand(
                    objects.Object, players.Object, authority.Object, controller)
                .ProcessCommand(Args("controller-a"));

            Assert.False(result.Succeeded);
            Assert.Equal("p2p_only", result.ErrorCode);
            Assert.Empty(objects.Invocations);
            Assert.Empty(players.Invocations);
            Assert.Empty(authority.Invocations);
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
        }
    }

    [Fact]
    public void OpenBackstreet_RejectsMissingLocalActorBeforeLeaseOrCampaignAccess()
    {
        var previousMode = ModInformation.SessionMode;
        try
        {
            ModInformation.SessionMode = SessionMode.RemoteReplica;
            var controller = new ControllerIdProvider();
            controller.SetControllerId("controller-a");
            var objects = new Mock<IObjectManager>(MockBehavior.Strict);
            var players = new Mock<IPlayerManager>(MockBehavior.Strict);
            players.Setup(value => value.TryGetPlayer("controller-a", out It.Ref<Player>.IsAny))
                .Returns(false);
            var authority = new Mock<ICampaignAuthorityContext>(MockBehavior.Strict);

            var result = new PrisonerSaleDebugCommands.OpenBackstreetCommand(
                    objects.Object, players.Object, authority.Object, controller)
                .ProcessCommand(Args("controller-a"));

            Assert.False(result.Succeeded);
            Assert.Equal("local_actor_unavailable", result.ErrorCode);
            Assert.Empty(objects.Invocations);
            Assert.Single(players.Invocations);
            Assert.Empty(authority.Invocations);
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
        }
    }

    [Fact]
    public void Prepare_RejectsDedicatedAuthorityBeforeResolvingPlayerObjects()
    {
        var previousMode = ModInformation.SessionMode;
        try
        {
            ModInformation.SessionMode = SessionMode.DedicatedAuthority;
            var objects = new Mock<IObjectManager>(MockBehavior.Strict);
            var players = new Mock<IPlayerManager>(MockBehavior.Strict);
            var authority = new Mock<ICampaignAuthorityContext>(MockBehavior.Strict);
            var controller = new ControllerIdProvider();
            controller.SetControllerId("controller-a");

            var result = new PrisonerSaleDebugCommands.PrepareCommand(
                    objects.Object, players.Object, authority.Object, controller)
                .ProcessCommand(Args("controller-b"));

            Assert.False(result.Succeeded);
            Assert.Equal("p2p_only", result.ErrorCode);
            Assert.Empty(objects.Invocations);
            Assert.Empty(players.Invocations);
            Assert.Empty(authority.Invocations);
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
        }
    }

    [Fact]
    public void Prepare_RejectsReplicaEvenWhenItHasAConfirmedRunningLease()
    {
        var previousMode = ModInformation.SessionMode;
        try
        {
            ModInformation.SessionMode = SessionMode.RemoteReplica;
            var controller = new ControllerIdProvider();
            controller.SetControllerId("controller-a");
            var identity = new SessionIdentity();
            var lease = new AuthorityLease("room", "authority", "controller-b", 3);
            Assert.True(identity.TryApply(lease));
            var authority = new CampaignAuthorityContext(identity, controller);
            var objects = new Mock<IObjectManager>(MockBehavior.Strict);
            var players = new Mock<IPlayerManager>(MockBehavior.Strict);

            var result = new PrisonerSaleDebugCommands.PrepareCommand(
                    objects.Object, players.Object, authority, controller)
                .ProcessCommand(Args("controller-b"));

            Assert.False(result.Succeeded);
            Assert.Equal("not_world_writer", result.ErrorCode);
            Assert.Empty(objects.Invocations);
            Assert.Empty(players.Invocations);
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
        }
    }

    [Fact]
    public void Submit_RejectsMissingLocalActorBeforeReadingNativeCampaignState()
    {
        var previousMode = ModInformation.SessionMode;
        try
        {
            ModInformation.SessionMode = SessionMode.RemoteReplica;
            var controller = new ControllerIdProvider();
            controller.SetControllerId("controller-a");
            var objects = new Mock<IObjectManager>(MockBehavior.Strict);
            var players = new Mock<IPlayerManager>(MockBehavior.Strict);
            players.Setup(value => value.TryGetPlayer("controller-a", out It.Ref<Player>.IsAny))
                .Returns(false);
            var authority = new Mock<ICampaignAuthorityContext>(MockBehavior.Strict);

            var result = new PrisonerSaleDebugCommands.SubmitCommand(
                    objects.Object, players.Object, authority.Object, controller)
                .ProcessCommand(Args("controller-a"));

            Assert.False(result.Succeeded);
            Assert.Equal("local_actor_unavailable", result.ErrorCode);
            Assert.Empty(objects.Invocations);
            Assert.Single(players.Invocations);
            Assert.Empty(authority.Invocations);
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
        }
    }

    private static ICoopCommandArgs Args(params string[] values) =>
        new CoopCommandArgsFactory().FromValues(values);
}
#endif
