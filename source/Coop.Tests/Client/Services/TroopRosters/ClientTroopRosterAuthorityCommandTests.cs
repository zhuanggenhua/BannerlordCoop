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
using System;
using Xunit;

namespace Coop.Tests.Client.Services.TroopRosters;

public class ClientTroopRosterAuthorityCommandTests
{
    [Fact]
    public void CurrentAuthorityAppliesRoutedRecruitmentOnce()
    {
        var lease = new AuthorityLease("room-a", "authority-b", "controller-b", 5);
        var authority = new Mock<ICampaignAuthorityContext>();
        authority.Setup(context => context.TryExecuteWithCurrentLease(lease, It.IsAny<Action>()))
            .Returns((AuthorityLease _, Action action) => { action(); return true; });
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

    [Fact]
    public void CanonicalPlayerPartyAcceptsCompactWireIdButRejectsAnotherPlayersParty()
    {
        var lease = new AuthorityLease("room-a", "authority-b", "controller-b", 5);
        var authority = new Mock<ICampaignAuthorityContext>();
        authority.Setup(value => value.TryGetLocalAuthorityLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease current) => { current = lease; return true; });
        authority.Setup(value => value.TryExecuteWithCurrentLease(lease, It.IsAny<Action>()))
            .Returns((AuthorityLease _, Action action) => { action(); return true; });
        var player = new Player("controller-a", "hero-a", "MobileParty_party-a", "clan-a", "character-a");
        var players = new Mock<IPlayerManager>();
        players.Setup(value => value.TryGetPlayer("controller-a", out player)).Returns(true);
        var roster = new Mock<ITroopRosterInterface>();
        using var handler = new ClientTroopRosterHandler(Mock.Of<IMessageBroker>(), Mock.Of<INetwork>(),
            Mock.Of<IObjectManager>(), authority.Object, players.Object, roster.Object);

        handler.HandleAuthorityRecruitmentCommand(new(this, new NetworkCampaignAuthorityRecruitmentCommand(
            lease, 9, "controller-a", "party-other", new[] { new TroopInfo("notable", "troop", 0) })));
        // Rejected ownership must not consume sequence 9.
        handler.HandleAuthorityRecruitmentCommand(new(this, new NetworkCampaignAuthorityRecruitmentCommand(
            lease, 9, "controller-a", "party-a", new[] { new TroopInfo("notable", "troop", 0) })));
        GameThread.Run(() => { }, blocking: true);

        roster.Verify(value => value.HandleOnRecruitmentDone("party-a", It.IsAny<TroopInfo[]>()), Times.Once);
        roster.Verify(value => value.HandleOnRecruitmentDone("party-other", It.IsAny<TroopInfo[]>()), Times.Never);
    }

    [Fact]
    public void FrozenLeaseCannotApplyCommandEvenIfItWasLocallyOwnedAtInitialCheck()
    {
        var lease = new AuthorityLease("room-a", "authority-b", "controller-b", 5);
        var authority = new Mock<ICampaignAuthorityContext>();
        authority.Setup(value => value.TryGetLocalAuthorityLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease current) => { current = lease; return true; });
        authority.Setup(value => value.TryExecuteWithCurrentLease(lease, It.IsAny<Action>())).Returns(false);
        var player = new Player("controller-a", "hero-a", "party-a", "clan-a", "character-a");
        var players = new Mock<IPlayerManager>();
        players.Setup(value => value.TryGetPlayer("controller-a", out player)).Returns(true);
        var roster = new Mock<ITroopRosterInterface>();
        using var handler = new ClientTroopRosterHandler(Mock.Of<IMessageBroker>(), Mock.Of<INetwork>(),
            Mock.Of<IObjectManager>(), authority.Object, players.Object, roster.Object);

        handler.HandleAuthorityRecruitmentCommand(new(this, new NetworkCampaignAuthorityRecruitmentCommand(
            lease, 1, "controller-a", "party-a", new[] { new TroopInfo("notable", "troop", 0) })));
        GameThread.Run(() => { }, blocking: true);

        roster.Verify(value => value.HandleOnRecruitmentDone(It.IsAny<string>(), It.IsAny<TroopInfo[]>()), Times.Never);
    }
}
