using Common;
using Common.Messaging;
using Common.Network;
using Common.Util;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.TroopRosters;
using GameInterface.Services.TroopRosters.Handlers;
using GameInterface.Services.TroopRosters.Messages;
using HarmonyLib;
using LiteNetLib;
using Moq;
using System;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using Xunit;

namespace GameInterface.Tests.Services.TroopRosters;

[Collection(ModInformationRoleCollection.Name)]
public class TroopRosterMigrationRoutingTests
{
    static TroopRosterMigrationRoutingTests() =>
        RuntimeHelpers.RunModuleConstructor(typeof(Coop.Tests.Mocks.TestNetwork).Module.ModuleHandle);

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void NpcXpReachesAllPotentialWritersAndFrozenWriterCannotPublish(bool active, int sends)
    {
        var previous = ModInformation.SessionMode;
        try
        {
            ModInformation.SessionMode = SessionMode.RemoteReplica;
            var party = ObjectHelper.SkipConstructor<MobileParty>();
            var owner = ObjectHelper.SkipConstructor<PartyBase>();
            AccessTools.Field(typeof(PartyBase), "<MobileParty>k__BackingField").SetValue(owner, party);
            var roster = new TroopRoster { OwnerParty = owner };
            var character = new CharacterObject();
            var objects = new Mock<IObjectManager>();
            uint rosterHandle = 1, characterHandle = 2;
            objects.Setup(value => value.TryGetHandle(roster, out rosterHandle)).Returns(true);
            objects.Setup(value => value.TryGetHandleWithLogging(character, out characterHandle)).Returns(true);
            var context = new Mock<ICampaignAuthorityContext>();
            context.SetupGet(value => value.IsAuthorityKnown).Returns(true);
            context.Setup(value => value.TryGetLocalAuthorityLease(out It.Ref<AuthorityLease>.IsAny))
                .Returns((out AuthorityLease lease) => { lease = new AuthorityLease("room", "writer", "b", 5); return active; });
            var relevance = new Mock<IPlayerTroopXpRelevance>();
            relevance.Setup(value => value.GetConnectedPeers(party)).Returns(Array.Empty<NetPeer>());
            var network = new Mock<INetwork>();
            using var broker = new MessageBroker();
            using var handler = new TroopRosterDeltaHandler(broker, objects.Object, network.Object,
                null!, null!,
                Mock.Of<IPlayerManager>(), relevance.Object, authorityContext: context.Object);
            broker.Publish(this, new ElementXpSet(roster, character, 321));
            network.Verify(value => value.SendAll(It.Is<NetworkTroopRosterElementBatch>(message =>
                message.RosterId == 1 && message.CharacterId == 2 && message.Operations.Length == 1 &&
                message.Operations[0].Xp == 321)), Times.Exactly(sends));
        }
        finally { ModInformation.SessionMode = previous; }
    }
}
