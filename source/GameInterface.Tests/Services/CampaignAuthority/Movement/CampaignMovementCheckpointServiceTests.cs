using Common;
using Common.Network.Session;
using Common.Util;
using GameInterface.Services.CampaignAuthority.Movement;
using GameInterface.Services.MobileParties.Data;
using GameInterface.Services.ObjectManager;
using GameInterface.Surrogates;
using GameInterface.Tests.Services.SiegeEvents;
using HarmonyLib;
using Moq;
using Serilog;
using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Map;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;
using Xunit;

namespace GameInterface.Tests.Services.CampaignAuthority.Movement;

[Collection(nameof(CampaignCurrentCollection))]
public class CampaignMovementCheckpointServiceTests : IDisposable
{
    private readonly Campaign previous = Campaign.Current;
    private readonly Campaign campaign = ObjectHelper.SkipConstructor<Campaign>();
    private readonly ObjectManager registry = new(Mock.Of<ILogger>());
    private readonly Harmony harmony = new($"movement-checkpoint.{Guid.NewGuid():N}");
    private readonly CampaignMovementCheckpointService service;
    private readonly AuthorityLease source = new("room", "authority-b", "b", 5);

    public CampaignMovementCheckpointServiceTests()
    {
        _ = new SurrogateCollection();
        campaign.CampaignObjectManager = new CampaignObjectManager
        {
            Settlements = new MBReadOnlyList<Settlement>(new List<Settlement>()),
        };
        var scene = new Mock<IMapScene>();
        scene.Setup(s => s.GetFaceIndex(in It.Ref<CampaignVec2>.IsAny)).Returns(PathFaceRecord.NullFaceRecord);
        campaign._mapSceneWrapper = scene.Object;
        Campaign.Current = campaign;
        harmony.Patch(AccessTools.PropertyGetter(typeof(MobileParty), nameof(MobileParty.NavigationCapability)),
            prefix: new HarmonyMethod(typeof(CampaignMovementCheckpointServiceTests), nameof(Navigation)));
        service = new CampaignMovementCheckpointService(new MobilePartyBehaviorSnapshot(registry), new PartyBehaviorWireMapper(registry));
    }

    public void Dispose()
    {
        harmony.UnpatchAll(harmony.Id);
        Campaign.Current = previous;
    }

    private static bool Navigation(ref MobileParty.NavigationType __result)
    {
        __result = MobileParty.NavigationType.Default;
        return false;
    }

    [Fact]
    public void FinalCheckpointRepairsPhysicalPositionAndNavigationThroughOriginalSnapshotPath()
    {
        var party = Party(1);
        var target = Party(2);
        party.PartyMoveMode = MoveModeType.Party;
        party.MoveTargetParty = target;
        party.Bearing = new Vec2(0.2f, 0.4f);
        party.EventPositionAdder = new Vec2(1, 2);
        Assert.True(service.TryCapture(source, 777, out var checkpoint));
        var expected = party.Position;
        party._position = Point(100, 200);
        party.Bearing = new Vec2(9, 8);
        party.EventPositionAdder = new Vec2(0, 0);
        party._pathMode = true;
        party.PathBegin = 8;
        uint nextHandle = registry.CaptureAllocationCheckpoint().NextHandle;

        Assert.True(service.TryPrepare(checkpoint, source, 777, out var apply));
        Assert.Equal(Point(100, 200), party.Position);
        Assert.True(apply());
        Assert.Equal(expected, party.Position);
        Assert.Equal(new Vec2(0.2f, 0.4f), party.Bearing);
        Assert.Equal(new Vec2(1, 2), party.EventPositionAdder);
        Assert.Equal(MoveModeType.Party, party.PartyMoveMode);
        Assert.Same(target, party.MoveTargetParty);
        Assert.False(party._pathMode);
        Assert.Equal(0, party.PathBegin);
        Assert.Equal(nextHandle, registry.CaptureAllocationCheckpoint().NextHandle);
        Assert.True(service.IsInstalled(checkpoint));
        Assert.False(apply());
    }

    [Fact]
    public void MissingLaterPartyRejectsBeforeMutatingEarlierParty()
    {
        var first = Party(1);
        var second = Party(2);
        Assert.True(service.TryCapture(source, 777, out var checkpoint));
        first._position = Point(100, 200);
        registry.Remove(second);
        Assert.False(service.TryPrepare(checkpoint, source, 777, out _));
        Assert.Equal(Point(100, 200), first.Position);
        Assert.Null(service.LastApplied);
    }

    [Fact]
    public void AdditionalActivePartyRejectsIncompleteCoverage()
    {
        Party(1);
        Assert.True(service.TryCapture(source, 777, out var checkpoint));
        Party(2);
        Assert.False(service.TryPrepare(checkpoint, source, 777, out _));
    }

    [Fact]
    public void StaleEpochOrClockCannotPrepareOrApply()
    {
        Party(1);
        Assert.True(service.TryCapture(source, 777, out var checkpoint));
        Assert.False(service.TryPrepare(checkpoint, new AuthorityLease("room", "authority-b", "b", 6), 777, out _));
        Assert.False(service.TryPrepare(checkpoint, source, 778, out _));
        Assert.False(checkpoint.Precedes(new AuthorityLease("room", "paused", "", 7, true)));
        Assert.True(checkpoint.Precedes(new AuthorityLease("room", "paused", "", 6, true)));
    }

    [Fact]
    public void CorruptedPayloadIsRejectedBeforeInstall()
    {
        var party = Party(1);
        Assert.True(service.TryCapture(source, 777, out var checkpoint));
        var original = party.Position;
        checkpoint.Payload[0] ^= 1;
        Assert.False(service.TryPrepare(checkpoint, source, 777, out _));
        Assert.Equal(original, party.Position);
    }

    [Fact]
    public void DuplicateCheckpointVerifiesWithoutRewritingPosition()
    {
        var party = Party(1);
        Assert.True(service.TryCapture(source, 777, out var checkpoint));
        Assert.True(service.TryPrepare(checkpoint, source, 777, out var apply));
        Assert.True(apply());
        party._position = Point(100, 200);
        Assert.True(service.TryPrepare(checkpoint, source, 777, out var duplicate));
        Assert.False(duplicate());
        Assert.Equal(Point(100, 200), party.Position);
    }

    [Fact]
    public void HandoffRejectsStaleTargetInsteadOfJoinNormalization()
    {
        var party = Party(1);
        var removed = Party(2);
        party.MoveTargetParty = removed;
        party.PartyMoveMode = MoveModeType.Party;
        campaign.CampaignObjectManager._mobileParties.Remove(removed);
        Assert.False(service.TryCapture(source, 777, out _));
        Assert.Same(removed, party.MoveTargetParty);
        Assert.Equal(MoveModeType.Party, party.PartyMoveMode);
    }

    private MobileParty Party(uint handle)
    {
        var party = ObjectHelper.SkipConstructor<MobileParty>();
        party.StringId = "npc_" + handle;
        party.IsActive = true;
        party.Ai = new MobilePartyAi(party);
        party.Party = ObjectHelper.SkipConstructor<PartyBase>();
        party.Party.MobileParty = party;
        party._position = Point(handle * 10, handle * 20);
        party.MoveTargetPoint = Point(300, 400);
        party.PartyMoveMode = MoveModeType.Point;
        party._defaultBehavior = AiBehavior.Hold;
        party.ShortTermBehavior = AiBehavior.Hold;
        campaign.CampaignObjectManager._mobileParties.Add(party);
        Assert.True(registry.AddExisting("MobileParty_Created_" + handle, party, handle));
        return party;
    }

    private static CampaignVec2 Point(float x, float y) => new(new Vec2(x, y), true);
}
