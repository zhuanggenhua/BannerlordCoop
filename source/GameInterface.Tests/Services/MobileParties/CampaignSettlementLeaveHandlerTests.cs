using Common;
using Common.Commands;
using Common.Messaging;
using Common.Network;
using Common.Network.MobileParties;
using Common.Tests.Utils;
using Common.Util;
using Coop.Tests.Mocks;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using GameInterface.Services.MobileParties;
using GameInterface.Services.MobileParties.Handlers;
using GameInterface.Services.MobileParties.Messages.Behavior;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using GameInterface.Services.Settlements.Interfaces;
using GameInterface.Services.SiegeEvents.Commands;
using LiteNetLib;
using Moq;
using Serilog;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using Xunit;

namespace GameInterface.Tests.Services.MobileParties;

[Collection(ModInformationRoleCollection.Name)]
public sealed class CampaignSettlementLeaveHandlerTests
{
    static CampaignSettlementLeaveHandlerTests() =>
        RuntimeHelpers.RunModuleConstructor(typeof(TestNetwork).Module.ModuleHandle);

    [Fact]
    public void LocalHostObserverRoutesToWriterWithoutLoopbackOrNativeWrite()
    {
        using var f = new Fixture("a", "b");
        Assert.True(f.Players.IsReservedController("a"));
        f.Broker.Publish(f, new EndSettlementEncounterAttempted(f.PartyA, f.Lease));
        f.Pump();
        var sent = Assert.Single(f.Sent);
        Assert.Same(f.PeerB, sent.Peer);
        var command = Assert.IsType<CampaignSettlementLeaveCommand>(sent.Message);
        Assert.Equal("a", command.Request.ControllerId);
        Assert.True(command.Request.Matches(f.Lease));
        Assert.Equal(0, f.NativeCalls);
        Assert.Same(f.Town, f.PartyA.CurrentSettlement);
    }

    [Fact]
    public void RemoteObserverRequestExecutesOnceOnHostWriterAndPublishesResultAfterNativeWrite()
    {
        using var f = new Fixture("a", "a");
        var request = f.Request("b", 2);
        f.Broker.Publish(f.PeerB, request);
        f.Broker.Publish(f.PeerB, request);
        f.Pump();
        Assert.Equal(1, f.NativeCalls);
        Assert.Null(f.PartyB.CurrentSettlement);
        Assert.IsType<CampaignSettlementLeaveApplied>(Assert.Single(f.Sent).Message);
        Assert.Same(f.PartyA, Campaign.Current.MainParty);
        Assert.Same(f.Town, f.PartyA.CurrentSettlement);
        Assert.Equal(0, f.EndEncounterCalls);
    }

    [Fact]
    public void HostCommandExecutesOnceAtRemoteWriterWithoutAllowedThreadOrIdentitySwitch()
    {
        using var f = new Fixture("b", "b");
        var request = f.Request("a", 1);
        f.Broker.Publish(f.PeerA, new CampaignSettlementLeaveCommand(request));
        f.Pump();
        Assert.Equal(1, f.NativeCalls);
        Assert.Null(f.PartyA.CurrentSettlement);
        Assert.Same(f.PartyB, Campaign.Current.MainParty);
        Assert.Same(f.Town, f.PartyB.CurrentSettlement);
        Assert.Equal(0, f.EndEncounterCalls);
        Assert.Equal(1, f.AppliedEvents);
        Assert.IsType<CampaignSettlementLeaveApplied>(Assert.Single(f.Sent).Message);
    }

    [Fact]
    public void HostCommandUsesLiveClientTransportWithoutInventedPlayerPeerBinding()
    {
        using var f = new Fixture("b", "b", bindHostPeer: false);
        Assert.False(f.Players.TryGetPeer("a", out _));
        Assert.False(f.Players.TryGetPlayer(f.PeerA, out _));
        f.Broker.Publish(f.PeerA, new CampaignSettlementLeaveCommand(f.Request("a", 1)));
        f.Pump();
        Assert.Equal(1, f.NativeCalls);
        Assert.Null(f.PartyA.CurrentSettlement);
        Assert.Same(f.PartyB, Campaign.Current.MainParty);
    }

    [Theory]
    [InlineData("stale_epoch")]
    [InlineData("wrong_party")]
    [InlineData("wrong_settlement")]
    [InlineData("zero_sequence")]
    [InlineData("unregistered_controller")]
    [InlineData("non_host_peer")]
    [InlineData("superseded_host_peer")]
    public void InvalidCommandCannotWriteOrConsumeValidCommandSequence(string fault)
    {
        using var f = new Fixture("b", "b");
        var lease = fault == "stale_epoch" ? new AuthorityLease("room", "world", "b", 4) : f.Lease;
        var request = new CampaignSettlementLeaveRequest(lease,
            fault == "unregistered_controller" ? "absent" : "a",
            fault == "zero_sequence" ? 0 : 1,
            fault == "wrong_party" ? 2u : 1u,
            fault == "wrong_settlement" ? 99u : 10u, "stream");
        var sender = fault == "non_host_peer" ? f.PeerB : f.PeerA;
        if (fault == "superseded_host_peer") f.RoomHostPeer = f.PeerPool.CreatePeer("127.0.0.3");
        f.Broker.Publish(sender, new CampaignSettlementLeaveCommand(request));
        f.Pump();
        Assert.Equal(0, f.NativeCalls);
        Assert.Empty(f.Sent);
        f.RoomHostPeer = f.PeerA;
        f.Broker.Publish(f.PeerA, new CampaignSettlementLeaveCommand(f.Request("a", 1)));
        f.Pump();
        Assert.Equal(1, f.NativeCalls);
    }

    [Fact]
    public void HostDisconnectWhileQueuedRejectsCommandAndReconnectCanReuseUnconsumedSequence()
    {
        using var f = new Fixture("b", "b");
        var command = new CampaignSettlementLeaveCommand(f.Request("a", 1));
        f.EnqueueBefore(() => f.Broker.Publish(f.PeerA, command), () => f.RoomHostPeer = null);
        f.Pump();
        Assert.Equal(0, f.NativeCalls);
        Assert.Empty(f.Sent);
        var replacement = f.PeerPool.CreatePeer();
        f.RoomHostPeer = replacement;
        f.Broker.Publish(f.PeerA, command);
        f.Pump();
        Assert.Equal(0, f.NativeCalls);
        f.Broker.Publish(replacement, command);
        f.Pump();
        Assert.Equal(1, f.NativeCalls);
        Assert.Null(f.PartyA.CurrentSettlement);
        Assert.Same(f.PartyB, Campaign.Current.MainParty);
    }

    [Fact]
    public void FreezeBeforeFifoExecutionRejectsCommandWithoutConsumingItsSequence()
    {
        using var f = new Fixture("b", "b");
        var command = new CampaignSettlementLeaveCommand(f.Request("a", 1));
        f.EnqueueBefore(() => f.Broker.Publish(f.PeerA, command),
            () => Assert.True(f.Identity.TryFreezeLocalAuthority(f.Lease, 33)));
        f.Pump();
        Assert.Equal(0, f.NativeCalls);
        Assert.True(f.Identity.TryCancelLocalAuthorityFreeze(f.Lease, 33));
        f.Broker.Publish(f.PeerA, command);
        f.Pump();
        Assert.Equal(1, f.NativeCalls);
    }

    [Fact]
    public void HandoffBeforeFifoExecutionRejectsOldEpochAndAcceptsNewEpochSequence()
    {
        using var f = new Fixture("b", "b");
        var next = new AuthorityLease("room", "world-6", "b", 6);
        f.EnqueueBefore(() => f.Broker.Publish(f.PeerA, new CampaignSettlementLeaveCommand(f.Request("a", 1))),
            () => Assert.True(f.Identity.TryApply(next, "a")));
        f.Pump();
        Assert.Equal(0, f.NativeCalls);
        f.Broker.Publish(f.PeerA, new CampaignSettlementLeaveCommand(
            new CampaignSettlementLeaveRequest(next, "a", 1, 1, 10, "stream")));
        f.Pump();
        Assert.Equal(1, f.NativeCalls);
    }

    [Fact]
    public void ReplayAfterReenterDoesNotLeaveAgainButNewSequenceDoes()
    {
        using var f = new Fixture("b", "b");
        var command = new CampaignSettlementLeaveCommand(f.Request("a", 1));
        f.Broker.Publish(f.PeerA, command);
        f.Pump();
        f.PartyA._currentSettlement = f.Town;
        f.Broker.Publish(f.PeerA, command);
        f.Pump();
        Assert.Equal(1, f.NativeCalls);
        Assert.Same(f.Town, f.PartyA.CurrentSettlement);
        f.Broker.Publish(f.PeerA, new CampaignSettlementLeaveCommand(f.Request("a", 1, sequence: 2)));
        f.Pump();
        Assert.Equal(2, f.NativeCalls);
    }

    [Fact]
    public void NewReconnectStreamCanRestartRequestSequenceWithoutReplayingOldStream()
    {
        using var f = new Fixture("a", "a");
        var old = f.Request("b", 2, sequence: 4);
        f.Broker.Publish(f.PeerB, old);
        f.Pump();
        f.PartyB._currentSettlement = f.Town;
        var newPeer = f.PeerPool.CreatePeer("127.0.0.3");
        f.Players.SetPeer("b", newPeer);
        f.Broker.Publish(f.PeerB, f.Request("b", 2, sequence: 5));
        f.Broker.Publish(newPeer, f.Request("b", 2, stream: "reconnected"));
        f.Pump();
        Assert.Equal(2, f.NativeCalls);
        f.PartyB._currentSettlement = f.Town;
        f.Broker.Publish(newPeer, old);
        f.Pump();
        Assert.Equal(2, f.NativeCalls);
        Assert.Same(f.Town, f.PartyB.CurrentSettlement);
    }

    [Fact]
    public void AuthorityResultClosesOnlyTheLocalOwnersEncounterAndAppliesOnce()
    {
        using var f = new Fixture("a", "b");
        var result = new CampaignSettlementLeaveApplied(f.Request("a", 1));
        f.Broker.Publish(f.PeerB, result);
        f.Broker.Publish(f.PeerB, result);
        f.Pump();
        Assert.Null(f.PartyA.CurrentSettlement);
        Assert.Same(f.Town, f.PartyB.CurrentSettlement);
        Assert.Equal(1, f.ReplicaCalls);
        Assert.Equal(1, f.EndEncounterCalls);
        Assert.Equal(0, f.NativeCalls);
        Assert.Same(f.PartyA, Campaign.Current.MainParty);
    }

    [Fact]
    public void StaleAuthorityResultDoesNotCloseNewEncounter()
    {
        using var f = new Fixture("a", "b");
        var result = new CampaignSettlementLeaveApplied(f.Request("a", 1));
        f.EnqueueBefore(() => f.Broker.Publish(f.PeerB, result),
            () => Assert.True(f.Identity.TryApply(new AuthorityLease("room", "world-6", "a", 6), "a")));
        f.Pump();
        Assert.Same(f.Town, f.PartyA.CurrentSettlement);
        Assert.Equal(0, f.ReplicaCalls);
        Assert.Equal(0, f.EndEncounterCalls);
    }

    [Fact]
    public void ResultPublishedInsideEnvelopeDrainAppliesBeforeFollowingLeaseChange()
    {
        using var f = new Fixture("a", "b");
        var result = new CampaignSettlementLeaveApplied(f.Request("a", 1));
        f.OnGame(() =>
        {
            f.Broker.Publish(f.PeerB, result);
            Assert.True(f.Identity.TryApply(new AuthorityLease("room", "world-6", "a", 6), "a"));
        });
        f.Pump();
        Assert.Equal(1, f.EndEncounterCalls);
        Assert.Null(f.PartyA.CurrentSettlement);
    }

    [Fact]
    public void SameEpochResultCannotClearAnotherSettlementEncounter()
    {
        using var f = new Fixture("a", "b");
        var anotherTown = ObjectHelper.SkipConstructor<Settlement>();
        f.PartyA._currentSettlement = anotherTown;
        f.Broker.Publish(f.PeerB, new CampaignSettlementLeaveApplied(f.Request("a", 1)));
        f.Pump();
        Assert.Same(anotherTown, f.PartyA.CurrentSettlement);
        Assert.Equal(0, f.ReplicaCalls);
        Assert.Equal(0, f.EndEncounterCalls);
    }

    [Fact]
    public void DisposedHandlerCannotExecuteAnAlreadyQueuedCommand()
    {
        using var f = new Fixture("b", "b");
        f.EnqueueBefore(() => f.Broker.Publish(f.PeerA, new CampaignSettlementLeaveCommand(f.Request("a", 1))),
            f.StopHandler);
        f.Pump();
        Assert.Equal(0, f.NativeCalls);
        Assert.Empty(f.Sent);
        Assert.Same(f.Town, f.PartyA.CurrentSettlement);
    }

    [Fact]
    public void NestedWireContractsPreserveLeaseControllerSequenceAndExpectedTown()
    {
        var lease = new AuthorityLease("room", "world", "b", 17);
        var request = new CampaignSettlementLeaveRequest(lease, "a", 42, 23, 99, "reconnect-stream");
        var command = ProtoBuf.Serializer.DeepClone(new CampaignSettlementLeaveCommand(request));
        var result = ProtoBuf.Serializer.DeepClone(new CampaignSettlementLeaveApplied(request));
        foreach (var copy in new[] { command.Request, result.Request })
        {
            Assert.True(copy.Matches(lease));
            Assert.False(copy.Matches(new AuthorityLease("room", "next-world", "a", 18)));
            Assert.Equal("a", copy.ControllerId);
            Assert.Equal(42, copy.Sequence);
            Assert.Equal(23u, copy.PartyId);
            Assert.Equal(99u, copy.SettlementId);
            Assert.Equal("reconnect-stream", copy.RequestStreamId);
        }
        Assert.IsAssignableFrom<ICampaignAuthorityScopedMessage>(result);
        Assert.IsAssignableFrom<IServerToClientCommand>(command);
    }

    [Fact]
    public void DebugCommandRegistryAllowsListenHostToReachLocalPartyValidation()
    {
        using var f = new Fixture("a", "b");
        f.PartyA._currentSettlement = null;
        var registry = new CoopCommandRegistry(new[] { new SiegeDebugCommand.LeaveSettlementCoopCommand() }, Mock.Of<ILogger>());
        var result = registry.ProcessCommand("coop.debug.siege.leave_settlement", new CoopCommandArgsFactory().FromValues(Array.Empty<string>()));
        Assert.False(result.Succeeded);
        Assert.NotEqual("command_wrong_side", result.ErrorCode);
        Assert.Contains("not in a settlement", result.Output);
    }

    [Fact]
    public void LocalWriterAttemptNeverRoutesOrExecutesAgain()
    {
        using var f = new Fixture("a", "a");
        f.Broker.Publish(f, new EndSettlementEncounterAttempted(f.PartyA, f.Lease));
        f.Pump();
        Assert.Equal(0, f.NativeCalls);
        Assert.Empty(f.Sent);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SessionMode priorMode = ModInformation.SessionMode;
        private readonly Campaign priorCampaign = Campaign.Current;
        private readonly CampaignSettlementLeaveHandler handler;
        public readonly TestNetwork PeerPool = new();
        public readonly TestMessageBroker Broker = new();
        public readonly SessionIdentity Identity = new();
        public readonly AuthorityLease Lease;
        public readonly PlayerManager Players;
        public readonly NetPeer PeerA, PeerB;
        public NetPeer RoomHostPeer;
        public readonly MobileParty PartyA = ObjectHelper.SkipConstructor<MobileParty>();
        public readonly MobileParty PartyB = ObjectHelper.SkipConstructor<MobileParty>();
        public readonly Settlement Town = ObjectHelper.SkipConstructor<Settlement>();
        public readonly List<(NetPeer? Peer, IMessage Message)> Sent = new();
        public int NativeCalls, ReplicaCalls, EndEncounterCalls, AppliedEvents;

        public Fixture(string local, string writer, bool bindHostPeer = false)
        {
            ModInformation.SessionMode = local == "a" ? SessionMode.ListenAuthority : SessionMode.RemoteReplica;
            var campaign = ObjectHelper.SkipConstructor<Campaign>();
            campaign.MainParty = local == "a" ? PartyA : PartyB;
            Campaign.Current = campaign;
            Lease = new AuthorityLease("room", "world", writer, 5);
            Assert.True(Identity.TryApply(Lease, "a"));
            var controller = new ControllerIdProvider();
            controller.SetControllerId(local);
            var objects = new ObjectManager(Mock.Of<ILogger>());
            Assert.True(objects.AddExisting("MobileParty_a", PartyA, 1));
            Assert.True(objects.AddExisting("MobileParty_b", PartyB, 2));
            Assert.True(objects.AddExisting("Settlement_town", Town, 10));
            PartyA._currentSettlement = Town;
            PartyB._currentSettlement = Town;
            Players = new PlayerManager(Mock.Of<ILogger>(), objects, controller);
            Assert.True(Players.AddPlayer(new Player("a", "", "MobileParty_a", "", "")));
            Assert.True(Players.AddPlayer(new Player("b", "", "MobileParty_b", "", "")));
            PeerA = PeerPool.CreatePeer();
            PeerB = PeerPool.CreatePeer("127.0.0.2");
            if (bindHostPeer) Players.SetPeer("a", PeerA);
            Players.SetPeer("b", PeerB);
            RoomHostPeer = local == "b" ? PeerA : null;
            var network = new Mock<INetwork>();
            network.As<IRoomHostPeerProvider>()
                .Setup(value => value.IsCurrentRoomHostPeer(It.IsAny<NetPeer>()))
                .Returns<NetPeer>(peer => ReferenceEquals(peer, RoomHostPeer));
            network.Setup(value => value.Send(It.IsAny<NetPeer>(), It.IsAny<IMessage>()))
                .Callback<NetPeer, IMessage>((peer, message) => Sent.Add((peer, message)));
            network.Setup(value => value.SendAll(It.IsAny<IMessage>()))
                .Callback<IMessage>(message =>
                {
                    if (message is CampaignSettlementLeaveApplied result)
                    {
                        Assert.Null(result.Request.PartyId == 1 ? PartyA.CurrentSettlement : PartyB.CurrentSettlement);
                        Assert.False(AllowedThread.IsThisThreadAllowed());
                    }
                    Sent.Add((null, message));
                });
            var leave = new Mock<ICampaignSettlementLeave>();
            leave.Setup(value => value.Apply(It.IsAny<MobileParty>())).Returns<MobileParty>(party =>
            {
                Assert.False(AllowedThread.IsThisThreadAllowed());
                Assert.Same(campaign.MainParty, Campaign.Current.MainParty);
                NativeCalls++;
                party._currentSettlement = null;
                return true;
            });
            var settlements = new Mock<ISettlementInterface>();
            settlements.Setup(value => value.PartyLeaveSettlement(It.IsAny<MobileParty>())).Callback<MobileParty>(party =>
            {
                Assert.True(AllowedThread.IsThisThreadAllowed());
                ReplicaCalls++;
                party._currentSettlement = null;
            });
            settlements.Setup(value => value.EndSettlementEncounter()).Callback(() =>
            {
                Assert.True(AllowedThread.IsThisThreadAllowed());
                EndEncounterCalls++;
            });
            Broker.Subscribe<SettlementEncounterLeaveApplied>(_ => AppliedEvents++);
            handler = new CampaignSettlementLeaveHandler(Broker, network.Object, objects, Players,
                new CampaignAuthorityContext(Identity, controller), Identity, controller, leave.Object, settlements.Object);
        }

        public CampaignSettlementLeaveRequest Request(string controller, uint party, long sequence = 1,
            string stream = "stream") => new(Lease, controller, sequence, party, 10, stream);

        public void StopHandler() => handler.Dispose();

        public void Pump() => GameThread.Run(() => { }, blocking: true);

        public void EnqueueBefore(Action receive, Action transition) => OnGame(() =>
        {
            System.Threading.Tasks.Task.Run(receive).GetAwaiter().GetResult();
            transition();
        });

        public void OnGame(Action action)
        {
            Exception? failure = null;
            GameThread.Run(() =>
            {
                try { action(); }
                catch (Exception ex) { failure = ex; }
            }, blocking: true);
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        public void Dispose()
        {
            handler.Dispose();
            PeerPool.Dispose();
            Campaign.Current = priorCampaign;
            ModInformation.SessionMode = priorMode;
        }
    }
}
