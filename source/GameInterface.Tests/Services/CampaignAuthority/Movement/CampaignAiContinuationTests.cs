using Autofac;
using Common.Network.Session;
using Common.Util;
using GameInterface.Services.CampaignAuthority.Movement;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.MobileParties.Data;
using HarmonyLib;
using Moq;
using ProtoBuf;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.ComponentInterfaces;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;
using Xunit;

namespace GameInterface.Tests.Services.CampaignAuthority.Movement;

public partial class CampaignMovementCheckpointServiceTests
{
    private static int randomCalls;

    [Fact]
    public void NativeDecisionLeavingFleeHasMissingLeafBeforeInstallAndClearsActualLeafAfterInstall()
    {
        var party = Party(1);
        var model = new Mock<MobilePartyAIModel>();
        model.Setup(m => m.ShouldPartyCheckInitiativeBehavior(party)).Returns(true);
        campaign._gameModels = ObjectHelper.SkipConstructor<GameModels>();
        campaign._gameModels.MobilePartyAIModel = model.Object;
        campaign.GameStarted = true;
        party.Ai._fleeingData.ShouldFleeClockWise = true;
        party.Ai._fleeingData.CcwFleeDirectionIsBlocked = true;
        party.Ai.HourCounter = 73;
        party.ShortTermBehavior = AiBehavior.FleeToPoint;
        Assert.True(service.TryCapture(source, 777, out var checkpoint));
        var replica = IncrementalReplica(party);
        Assert.Null(replica._fleeingData);
        var missingLeaf = Assert.Throws<NullReferenceException>(() => replica.GetBehaviors(out _, out _, out _));
        Assert.Contains(nameof(MobilePartyAi.GetBehaviors), missingLeaf.StackTrace);
        replica._fleeingData = new MobilePartyAi.FleeingData();
        replica.GetBehaviors(out _, out _, out _);
        replica._fleeingData = null;
        Assert.True(service.TryPrepare(checkpoint, source, 777, out var apply));
        Assert.True(apply());
        Assert.True(replica._fleeingData.ShouldFleeClockWise);
        Assert.True(replica._fleeingData.CcwFleeDirectionIsBlocked);
        Assert.Equal(73, replica.HourCounter);
        replica.GetBehaviors(out var behavior, out var target, out _);
        Assert.Equal(AiBehavior.Hold, behavior);
        Assert.Null(target);
        Assert.False(replica._fleeingData.AlreadyReachedTheDestinationWhileFleeing);
        Assert.False(replica._fleeingData.ShouldFleeClockWise);
        Assert.False(replica._fleeingData.CwFleeDirectionIsBlocked);
        Assert.False(replica._fleeingData.CcwFleeDirectionIsBlocked);
        model.Verify(m => m.ShouldPartyCheckInitiativeBehavior(party), Times.Exactly(3));
        Assert.Same(replica, party.Ai);
    }

    [Fact]
    public void EveryFiniteSavedFieldRoundTripsWithoutConstructorRandomOrIdentityAllocation()
    {
        var party = Party(1);
        var ai = party.Ai;
        ai._fleeingData.AlreadyReachedTheDestinationWhileFleeing = true;
        ai._fleeingData.ShouldFleeClockWise = true;
        ai._fleeingData.CwFleeDirectionIsBlocked = true;
        ai._fleeingData.CcwFleeDirectionIsBlocked = true;
        ai.HourCounter = 73;
        ai._isDisabled = true;
        ai.RethinkAtNextHourlyTick = true;
        ai.DoNotMakeNewDecisions = true;
        ai.IsAlerted = true;
        ai.DefaultBehaviorNeedsUpdate = true;
        ai._attackInitiative = 0.25f;
        ai._avoidInitiative = 2.5f;
        ai._initiativeRestoreTime = new CampaignTime(-10);
        ai._nextAiCheckTime = new CampaignTime(101);
        ai._enableAgainAtHour = new CampaignTime(102);
        ai.DoNotAttackMainPartyUntil = new CampaignTime(long.MaxValue);
        Assert.True(service.TryCapture(source, 777, out var checkpoint));
        var expected = Decode(checkpoint).AiContinuation.States.Single();
        var replica = IncrementalReplica(party);
        uint next = registry.CaptureAllocationCheckpoint().NextHandle;
        randomCalls = 0;
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(MBRandom)).Where(m =>
            m.Name == nameof(MBRandom.RandomInt) || m.Name == "get_RandomFloat"))
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(CampaignMovementCheckpointServiceTests), nameof(CountRandom)));
        Assert.True(service.TryPrepare(checkpoint, source, 777, out var apply));
        Assert.Null(replica._fleeingData);
        Assert.False(service.IsInstalled(checkpoint));
        Assert.True(apply());
        Assert.True(service.IsInstalled(checkpoint));
        Assert.True(service.TryCapture(source, 777, out var recaptured));
        Assert.Equal(checkpoint.Payload, recaptured.Payload);
        Assert.Equal(expected.HourCounter, replica.HourCounter);
        Assert.Equal(expected.AttackInitiative, replica._attackInitiative);
        Assert.Equal(expected.AvoidInitiative, replica._avoidInitiative);
        Assert.Equal(expected.RestoreTicks, replica._initiativeRestoreTime.NumTicks);
        Assert.Equal(expected.NextCheckTicks, replica._nextAiCheckTime.NumTicks);
        Assert.Equal(expected.EnableTicks, replica._enableAgainAtHour.NumTicks);
        Assert.Equal(expected.NoAttackTicks, replica.DoNotAttackMainPartyUntil.NumTicks);
        Assert.True(replica._isDisabled && replica.RethinkAtNextHourlyTick && replica.DoNotMakeNewDecisions && replica.IsAlerted);
        Assert.True(replica.DefaultBehaviorNeedsUpdate);
        Assert.Same(replica, party.Ai);
        Assert.True(registry.TryGetObject<MobilePartyAi>(1001, out var resolved));
        Assert.Same(replica, resolved);
        Assert.Equal(next, registry.CaptureAllocationCheckpoint().NextHandle);
        Assert.Equal(0, randomCalls);
    }

    [Theory]
    [InlineData("missing-section")]
    [InlineData("missing-record")]
    [InlineData("missing-version")]
    [InlineData("future-version")]
    [InlineData("duplicate-ai")]
    [InlineData("duplicate-party")]
    [InlineData("wrong-party")]
    [InlineData("wrong-ai")]
    [InlineData("swapped-order")]
    [InlineData("nan")]
    [InlineData("infinite")]
    [InlineData("missing-ai-registry")]
    [InlineData("wrong-owner")]
    public void InvalidContinuationRefusesBeforeMovementOrAiWrites(string fault)
    {
        var first = Party(1);
        var second = Party(2);
        Assert.True(service.TryCapture(source, 777, out var original));
        var payload = Decode(original);
        switch (fault)
        {
            case "missing-section": payload.AiContinuation = null; break;
            case "missing-record": payload.AiContinuation.States = payload.AiContinuation.States.Take(1).ToArray(); break;
            case "missing-version": payload.AiContinuation.Version = 0; break;
            case "future-version": payload.AiContinuation.Version = 2; break;
            case "duplicate-ai": payload.AiContinuation.States[1].AiId = payload.AiContinuation.States[0].AiId; break;
            case "duplicate-party": payload.AiContinuation.States[1].PartyId = payload.AiContinuation.States[0].PartyId; break;
            case "wrong-party": payload.AiContinuation.States[1].PartyId = 123; break;
            case "wrong-ai": payload.AiContinuation.States[1].AiId = 123; break;
            case "swapped-order": Array.Reverse(payload.AiContinuation.States); break;
            case "nan": payload.AiContinuation.States[1].AttackInitiative = float.NaN; break;
            case "infinite": payload.AiContinuation.States[1].AvoidInitiative = float.PositiveInfinity; break;
            case "missing-ai-registry": Assert.True(registry.Remove(second.Ai)); break;
            case "wrong-owner": AccessTools.Field(typeof(MobilePartyAi), nameof(MobilePartyAi._mobileParty)).SetValue(second.Ai, first); break;
        }
        first._position = Point(900, 901);
        first.Ai.HourCounter = 999;
        var malformed = Reencode(payload);
        Assert.False(service.TryPrepare(malformed, source, 777, out _));
        Assert.False(service.IsInstalled(malformed));
        Assert.Equal(Point(900, 901), first.Position);
        Assert.Equal(999, first.Ai.HourCounter);
        Assert.Null(service.LastApplied);
    }

    [Fact]
    public void MissingSourceFleeLeafOrAiRegistrationRefusesCapture()
    {
        var party = Party(1);
        var flee = party.Ai._fleeingData;
        party.Ai._fleeingData = null;
        Assert.False(service.TryCapture(source, 777, out _));
        party.Ai._fleeingData = flee;
        Assert.True(registry.Remove(party.Ai));
        Assert.False(service.TryCapture(source, 777, out _));
    }

    [Fact]
    public void DuplicateDoesNotRepairChangedAiStateOrReplaceLiveLeaf()
    {
        var party = Party(1);
        Assert.True(service.TryCapture(source, 777, out var checkpoint));
        Assert.True(service.TryPrepare(checkpoint, source, 777, out var apply));
        Assert.True(apply());
        var leaf = party.Ai._fleeingData;
        party.Ai.HourCounter++;
        Assert.True(service.TryPrepare(checkpoint, source, 777, out var duplicate));
        Assert.False(duplicate());
        Assert.Same(leaf, party.Ai._fleeingData);
        Assert.False(service.IsInstalled(checkpoint));
    }

    [Fact]
    public void LeafInstallRechecksEveryBindingBeforeChangingEarlierAi()
    {
        var first = Party(1);
        var second = Party(2);
        var leaf = new CampaignAiContinuation(registry);
        Assert.True(leaf.TryCapture(out var captured));
        Assert.True(leaf.TryPrepare(captured, new uint[] { 1, 2 }, out var apply));
        first.Ai.HourCounter = 999;
        Assert.True(registry.Remove(second.Ai));
        Assert.False(apply());
        Assert.Equal(999, first.Ai.HourCounter);
        Assert.False(apply());
    }

    [Fact]
    public void ExplicitEmptyContinuationSupportsEmptyWorldButMissingSectionDoesNot()
    {
        Assert.True(service.TryCapture(source, 777, out var checkpoint));
        var payload = Decode(checkpoint);
        Assert.NotNull(payload.AiContinuation);
        Assert.Equal(1, payload.AiContinuation.Version);
        Assert.Empty(payload.AiContinuation.States);
        Assert.True(service.TryPrepare(checkpoint, source, 777, out var apply));
        Assert.True(apply());
        Assert.True(service.IsInstalled(checkpoint));
        payload.AiContinuation = null;
        Assert.False(service.TryPrepare(Reencode(payload), source, 777, out _));
    }

    [Fact]
    public void RecaptureDetectsEachChangedContinuationFieldBeforeAcknowledgement()
    {
        var party = Party(1);
        Assert.True(service.TryCapture(source, 777, out var checkpoint));
        foreach (var member in typeof(CampaignAiContinuationState).GetProperties().Where(p =>
            p.Name != nameof(CampaignAiContinuationState.PartyId) && p.Name != nameof(CampaignAiContinuationState.AiId)))
        {
            var payload = Decode(checkpoint);
            var state = payload.AiContinuation.States.Single();
            object value = member.GetValue(state);
            object changed = value is bool flag ? !flag : value is float f ? f + 0.125f :
                value is long ticks ? ticks + 1 : (object)((int)value + 1);
            member.SetValue(state, changed);
            var altered = Reencode(payload);
            Assert.False(service.IsInstalled(altered), member.Name);
            var installer = new CampaignMovementCheckpointService(new MobilePartyBehaviorSnapshot(registry),
                new PartyBehaviorWireMapper(registry), new CampaignAiContinuation(registry));
            Assert.True(installer.TryPrepare(altered, source, 777, out var apply), member.Name);
            Assert.True(apply(), member.Name);
            Assert.True(service.IsInstalled(altered), member.Name);
            var fresh = new CampaignMovementCheckpointService(new MobilePartyBehaviorSnapshot(registry),
                new PartyBehaviorWireMapper(registry), new CampaignAiContinuation(registry));
            Assert.True(fresh.TryPrepare(checkpoint, source, 777, out var restore), member.Name);
            Assert.True(restore(), member.Name);
        }
        Assert.Same(party.Ai, registry.TryGetObject<MobilePartyAi>(1001, out var ai) ? ai : null);
    }

    [Fact]
    public void MovementModuleRegistersOneTransientLeafContract()
    {
        var builder = new ContainerBuilder();
        builder.RegisterInstance(registry).As<IObjectManager>();
        builder.RegisterModule<MovementModule>();
        using var container = builder.Build();
        Assert.IsType<CampaignAiContinuation>(container.Resolve<ICampaignAiContinuation>());
        Assert.NotSame(container.Resolve<ICampaignAiContinuation>(), container.Resolve<ICampaignAiContinuation>());
    }

    private MobilePartyAi IncrementalReplica(MobileParty party)
    {
        Assert.True(registry.TryGetHandle(party.Ai, out var handle));
        Assert.True(registry.TryGetId(party.Ai, out var id));
        Assert.True(registry.Remove(party.Ai));
        var replica = ObjectHelper.SkipConstructor<MobilePartyAi>();
        AccessTools.Field(typeof(MobilePartyAi), nameof(MobilePartyAi._mobileParty)).SetValue(replica, party);
        Assert.True(registry.AddExisting(id, replica, handle));
        party.Ai = replica;
        return replica;
    }

    private static CampaignMovementCheckpointService.MovementPayload Decode(CampaignMovementCheckpoint checkpoint)
    {
        using var stream = new MemoryStream(checkpoint.Payload);
        return Serializer.Deserialize<CampaignMovementCheckpointService.MovementPayload>(stream);
    }

    private CampaignMovementCheckpoint Reencode(CampaignMovementCheckpointService.MovementPayload payload)
    {
        using var stream = new MemoryStream();
        Serializer.Serialize(stream, payload);
        var bytes = stream.ToArray();
        using var sha = SHA256.Create();
        string hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        return new CampaignMovementCheckpoint(source, 777, payload.States.Length, bytes, hash);
    }

    private static void CountRandom() => randomCalls++;
}
