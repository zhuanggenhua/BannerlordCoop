using Common.Messaging;
using Common.Network;
using Common.Util;
using Coop.Core.Server.Services.MobileParties;
using Coop.Core.Server.Services.MobileParties.Messages;
using GameInterface.Services.Heroes.Enum;
using GameInterface.Services.Heroes.Interaces;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.MobileParties.Data;
using GameInterface.Services.Time.Interfaces;
using LiteNetLib;
using Moq;
using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;
using Xunit;

namespace Coop.Tests.Server.Services.MobileParties;

/// <summary>
/// Serializes tests that replace the global current campaign.
/// </summary>
[CollectionDefinition(nameof(CampaignCurrentCollection), DisableParallelization = true)]
public sealed class CampaignCurrentCollection
{
}

/// <summary>
/// Tests authoritative mobile-party join-baseline capture.
/// </summary>
[Collection(nameof(CampaignCurrentCollection))]
public class JoinCampaignBaselineSenderTests
{
    [Fact]
    public void Send_InactiveParty_CapturesOnlyActiveParty()
    {
        Campaign previousCampaign = Campaign.Current;
        try
        {
            var activeParty = ObjectHelper.SkipConstructor<MobileParty>();
            activeParty.IsActive = true;
            var inactiveParty = ObjectHelper.SkipConstructor<MobileParty>();
            inactiveParty.IsActive = false;
            var campaignObjectManager = new CampaignObjectManager
            {
                Settlements = new MBReadOnlyList<Settlement>(new List<Settlement>()),
            };
            campaignObjectManager._mobileParties.Add(activeParty);
            campaignObjectManager._mobileParties.Add(inactiveParty);
            var campaign = ObjectHelper.SkipConstructor<Campaign>();
            campaign.CampaignObjectManager = campaignObjectManager;
            Campaign.Current = campaign;

            var network = new Mock<INetwork>();
            var mapTimeTracker = new Mock<IMapTimeTrackerInterface>();
            long serverTicks = 123L;
            mapTimeTracker
                .Setup(tracker => tracker.TryGetCurrentTicks(out serverTicks))
                .Returns(true);
            var snapshot = new Mock<IMobilePartyBehaviorSnapshot>();
            var state = new MobilePartyJoinState();
            string failure = null!;
            snapshot
                .Setup(service => service.TryCreateJoinState(
                    activeParty,
                    It.Is<ISet<MobileParty>>(parties =>
                        parties.Count == 1 && parties.Contains(activeParty)),
                    It.IsAny<ISet<Settlement>>(),
                    out state,
                    out failure))
                .Returns(true);
            var timeControl = new Mock<ITimeControlInterface>();
            timeControl.Setup(service => service.GetTimeControl()).Returns(TimeControlEnum.Pause);
            var authorityContext = new Mock<ICampaignAuthorityContext>();
            var troopXpBaselineProvider = new Mock<IPlayerPartyTroopXpBaselineProvider>();
            var partyBehaviorWireMapper = new Mock<IPartyBehaviorWireMapper>();
            NetworkPartyBehaviorUpdateData networkBehavior = default;
            partyBehaviorWireMapper
                .Setup(service => service.TryToNetwork(It.IsAny<PartyBehaviorUpdateData>(), out networkBehavior))
                .Returns(true);
            TroopRosterXpBaseline[] troopXpBaselines = Array.Empty<TroopRosterXpBaseline>();
            troopXpBaselineProvider
                .Setup(service => service.TryCapture(
                    It.IsAny<NetPeer>(),
                    out troopXpBaselines))
                .Returns(true);
            NetworkJoinCampaignBaseline sent = default;
            network
                .Setup(service => service.SendImmediate(
                    It.IsAny<NetPeer>(),
                    It.IsAny<IMessage>()))
                .Callback<NetPeer, IMessage>((_, message) =>
                    sent = Assert.IsType<NetworkJoinCampaignBaseline>(message));
            var sender = new JoinCampaignBaselineSender(
                network.Object,
                mapTimeTracker.Object,
                snapshot.Object,
                timeControl.Object,
                troopXpBaselineProvider.Object,
                partyBehaviorWireMapper.Object,
                authorityContext.Object);

            sender.Send(null!);

            Assert.True(sent.IsComplete);
            Assert.False(sent.HasCommittedPausedCheckpointLease);
            Assert.Single(sent.PartyStates!);
            snapshot.Verify(service => service.TryCreateJoinState(
                inactiveParty,
                It.IsAny<ISet<MobileParty>>(),
                It.IsAny<ISet<Settlement>>(),
                out It.Ref<MobilePartyJoinState>.IsAny,
                out It.Ref<string>.IsAny), Times.Never);
        }
        finally
        {
            Campaign.Current = previousCampaign;
        }
    }

    [Fact]
    public void Send_CompleteBaseline_StampsCurrentPausedCheckpointLease()
    {
        const string requestId = "d4388b2b0f8b4f939c6f86ebcb9c1251";
        Campaign previousCampaign = Campaign.Current;
        try
        {
            var campaignObjectManager = new CampaignObjectManager
            {
                Settlements = new MBReadOnlyList<Settlement>(new List<Settlement>()),
            };
            var campaign = ObjectHelper.SkipConstructor<Campaign>();
            campaign.CampaignObjectManager = campaignObjectManager;
            Campaign.Current = campaign;

            var network = new Mock<INetwork>();
            var mapTimeTracker = new Mock<IMapTimeTrackerInterface>();
            long serverTicks = 123L;
            mapTimeTracker.Setup(tracker => tracker.TryGetCurrentTicks(out serverTicks)).Returns(true);
            var snapshot = new Mock<IMobilePartyBehaviorSnapshot>();
            var timeControl = new Mock<ITimeControlInterface>();
            timeControl.Setup(service => service.GetTimeControl()).Returns(TimeControlEnum.Pause);
            var troopXpBaselineProvider = new Mock<IPlayerPartyTroopXpBaselineProvider>();
            TroopRosterXpBaseline[] troopXpBaselines = Array.Empty<TroopRosterXpBaseline>();
            troopXpBaselineProvider
                .Setup(service => service.TryCapture(It.IsAny<NetPeer>(), out troopXpBaselines))
                .Returns(true);
            var partyBehaviorWireMapper = new Mock<IPartyBehaviorWireMapper>();
            var lease = new AuthorityLease("room", "authority", string.Empty, 4, isPaused: true);
            var authorityContext = new Mock<ICampaignAuthorityContext>();
            authorityContext.Setup(context => context.TryGetCurrentLease(out lease)).Returns(true);
            NetworkJoinCampaignBaseline sent = default;
            network.Setup(service => service.SendImmediate(It.IsAny<NetPeer>(), It.IsAny<IMessage>()))
                .Callback<NetPeer, IMessage>((_, message) =>
                    sent = Assert.IsType<NetworkJoinCampaignBaseline>(message));

            var sender = new JoinCampaignBaselineSender(
                network.Object,
                mapTimeTracker.Object,
                snapshot.Object,
                timeControl.Object,
                troopXpBaselineProvider.Object,
                partyBehaviorWireMapper.Object,
                authorityContext.Object);

            sender.Send(null!, requestId);

            Assert.True(sent.IsComplete);
            Assert.True(sent.TryGetCommittedPausedCheckpointLease(out var stampedLease));
            Assert.Equal(lease, stampedLease);
            Assert.Equal(requestId, sent.BaselineRequestId);
        }
        finally
        {
            Campaign.Current = previousCampaign;
        }
    }
}
