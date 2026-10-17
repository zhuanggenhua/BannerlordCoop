using Common;
using Common.Messaging;
using Common.Network;
using Coop.Core.Client.Services.TroopRosters.Handlers;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.TroopRosters.Interfaces;
using GameInterface.Services.TroopRosters.Messages;
using Moq;
using Xunit;

namespace Coop.Tests.Client.Services.TroopRosters;

public class ClientTroopRosterAuthorityCommandTests
{
    [Fact]
    public void CurrentAuthorityAppliesRoutedRecruitmentOnce()
    {
        var lease = new AuthorityLease("room-a", "authority-b", "controller-b", 5);
        var authority = new Mock<ICampaignAuthorityContext>();
        authority.Setup(context => context.TryGetLocalAuthorityLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease current) =>
            {
                current = lease;
                return true;
            });
        var player = new Player("controller-a", "hero-a", "party-a", "clan-a", "character-a");
        var players = new Mock<IPlayerManager>();
        players.Setup(manager => manager.TryGetPlayer("controller-a", out player)).Returns(true);
        var troopRoster = new Mock<ITroopRosterInterface>();
        using var handler = new ClientTroopRosterHandler(
            Mock.Of<IMessageBroker>(), Mock.Of<INetwork>(), Mock.Of<IObjectManager>(),
            authority.Object, players.Object, troopRoster.Object);
        var request = new NetworkCampaignAuthorityRecruitmentCommand(
            lease, 1, "controller-a", "party-a", new[] { new TroopInfo("hero-a", "troop-a", 0) });
        var payload = new MessagePayload<NetworkCampaignAuthorityRecruitmentCommand>(this, request);

        handler.HandleAuthorityRecruitmentCommand(payload);
        handler.HandleAuthorityRecruitmentCommand(payload);
        GameThread.Run(() => { }, blocking: true);

        troopRoster.Verify(roster => roster.HandleOnRecruitmentDone(
            "party-a", It.Is<TroopInfo[]>(troops => troops.Length == 1 && troops[0].CharacterObjectId == "troop-a")),
            Times.Once);
    }

    [Fact]
    public void CandidateRejectsRecruitmentCommandFromAnOldEpoch()
    {
        var currentLease = new AuthorityLease("room-a", "authority-b", "controller-b", 5);
        var authority = new Mock<ICampaignAuthorityContext>();
        authority.Setup(context => context.TryGetLocalAuthorityLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease current) =>
            {
                current = currentLease;
                return true;
            });
        var players = new Mock<IPlayerManager>();
        var troopRoster = new Mock<ITroopRosterInterface>();
        using var handler = new ClientTroopRosterHandler(
            Mock.Of<IMessageBroker>(), Mock.Of<INetwork>(), Mock.Of<IObjectManager>(),
            authority.Object, players.Object, troopRoster.Object);
        var stale = new NetworkCampaignAuthorityRecruitmentCommand(
            new AuthorityLease("room-a", "authority-a", "controller-a", 4),
            1,
            "controller-a",
            "party-a",
            new[] { new TroopInfo("hero-a", "troop-a", 0) });

        handler.HandleAuthorityRecruitmentCommand(
            new MessagePayload<NetworkCampaignAuthorityRecruitmentCommand>(this, stale));
        GameThread.Run(() => { }, blocking: true);

        troopRoster.Verify(roster => roster.HandleOnRecruitmentDone(
            It.IsAny<string>(), It.IsAny<TroopInfo[]>()), Times.Never);
    }
}
