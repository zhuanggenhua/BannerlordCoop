using Autofac;
using Common;
using Common.Util;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using HarmonyLib;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using Xunit;

namespace GameInterface.Tests.Services.CampaignAuthority;

[Collection(ModInformationRoleCollection.Name)]
public sealed class CampaignSimulationEvidenceTests
{
    [Theory]
    [InlineData(true, false, "writerNpc", false)]
    [InlineData(true, true, "writerPlayer", false)]
    [InlineData(false, true, "observerLocalNavigation", false)]
    [InlineData(false, false, "unqualified", true)]
    public void ActualPartyRegistryAndConfirmedLeaseDetermineRole(bool writer, bool player, string expected, bool unsafeWorld)
    {
        using var fixture = new Fixture(writer, player);
        var row = CampaignSimulationEvidencePatches.CaptureAi(fixture.Party.Ai);
        Assert.Equal(expected, row.Classification); Assert.Equal(unsafeWorld, row.UnexpectedWorld);
        Assert.Same(fixture.Party, row.ActualParty); Assert.Same(fixture.Party.Ai, row.ActualAi);
    }

    [Theory]
    [InlineData("frozen")]
    [InlineData("unconfirmed")]
    [InlineData("stale-ai")]
    [InlineData("wrong-controller")]
    [InlineData("remote-player")]
    [InlineData("unregistered")]
    [InlineData("paused")]
    public void UnprovenOrFrozenObserverIsNeverNavigation(string invalid)
    {
        using var fixture = new Fixture(false, true);
        var ai = fixture.Party.Ai;
        if (invalid == "frozen") Assert.True(fixture.Identity.TryFreezeLocalAuthority(fixture.Identity.CurrentLease, 1));
        if (invalid == "unconfirmed") fixture.Identity = new SessionIdentity();
        if (invalid == "paused") Assert.True(fixture.Identity.TryApply(new AuthorityLease("room", "paused-world", "", 2, true)));
        if (invalid == "stale-ai") fixture.Party.Ai = new MobilePartyAi(fixture.Party);
        if (invalid == "wrong-controller") fixture.MarkerOwner = "other";
        if (invalid == "remote-player") fixture.Owner = fixture.MarkerOwner = "other";
        if (invalid == "unregistered") fixture.Registered = false;
        fixture.RegisterContainer();
        var row = CampaignSimulationEvidencePatches.CaptureAi(ai);
        Assert.NotEqual("observerLocalNavigation", row.Classification); Assert.True(row.UnexpectedWorld);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    public void ReceivedApplyPreservesUnsafeNpcAndActualRole(bool writer, bool player, bool unsafeWorld)
    {
        using var fixture = new Fixture(writer, player);
        using var allowed = new AllowedThread();
        var row = CampaignSimulationEvidencePatches.CaptureAi(fixture.Party.Ai);
        Assert.Equal("receiveApply", row.Classification); Assert.Equal(unsafeWorld, row.UnexpectedWorld);
        Assert.Equal(player ? "localPlayer" : "npc", row.PartyRole);
        Assert.Equal(writer ? "writer" : "observer", row.Role);
    }

    [Fact]
    public void FrozenOldWriterAndMissingRegistryEvidenceAreExplicitAndNeverObserverNavigation()
    {
        using var fixture = new Fixture(true, true);
        Assert.True(fixture.Identity.TryFreezeLocalAuthority(fixture.Identity.CurrentLease, 1));
        var frozen = CampaignSimulationEvidencePatches.CaptureAi(fixture.Party.Ai);
        Assert.Equal("unqualified", frozen.Classification);Assert.True(frozen.Frozen);Assert.True(frozen.UnexpectedWorld);
        ContainerProvider.Clear();
        var missing = CampaignSimulationEvidencePatches.CaptureAi(fixture.Party.Ai);
        Assert.Equal("unknown", missing.Classification);Assert.True(missing.UnexpectedWorld);Assert.NotEmpty(missing.Reason);
    }

    [Fact]
    public void DiagnosticReadFailureRetainsCauseAndBoundedFirstEvidenceCannotOverflowSilently()
    {
        using var fixture = new Fixture(true, false);
        fixture.FailRead = true;fixture.RegisterContainer();
        var entry = CampaignSimulationEvidencePatches.CaptureAi(fixture.Party.Ai);
        Assert.Equal("unknown", entry.Classification);Assert.True(entry.UnexpectedWorld);
        Assert.Contains("registry probe failure", entry.DiagnosticError);
        var collector = new CampaignCycleDiagnostics(1);
        collector.AiTick(entry, entry.Copy(), true, null);
        var other = entry.Copy();other.Classification = "unqualified";
        collector.AiTick(other, other.Copy(), true, null);
        Assert.True(collector.Overflowed);Assert.Single(collector.SimulationEvidenceSnapshot().FirstEntries);
        Assert.Equal(2, collector.SimulationEvidenceSnapshot().Counts["MobilePartyAi.Tick.observed"]);
    }

    [Fact]
    public void EntryRoleIsPreservedWhileEpochReferenceAndExitDiagnosticChangesRemainUnsafe()
    {
        using var fixture = new Fixture(false, true);
        var collector = new CampaignCycleDiagnostics();
        var entry = CampaignSimulationEvidencePatches.CaptureAi(fixture.Party.Ai);
        var exit = entry.Copy();exit.ActualParty = new object();exit.Reason = "exit registration lost";
        exit.DiagnosticError = "exit probe failure";exit.Classification = "unknown";
        exit.PartyId = "replacement-id";exit.ObjectController = "replacement-owner";exit.AiIdentity = 81;
        collector.AiTick(entry, exit, true, null);
        var snapshot = collector.SimulationEvidenceSnapshot();
        Assert.Equal(1, snapshot.Counts["MobilePartyAi.Tick.observerLocalNavigation.completed"]);
        Assert.Equal(1, snapshot.Counts["MobilePartyAi.Tick.unexpectedWorld.completed"]);
        var evidence = Assert.Single(snapshot.FirstEntries);
        Assert.True(evidence.ContextChanged);Assert.Equal(exit.DiagnosticError, evidence.ExitDiagnosticError);
        Assert.Equal(exit.Reason, evidence.ExitReason);Assert.Equal("unknown", evidence.ExitClassification);
        Assert.Equal(exit.PartyId, evidence.ExitPartyId);Assert.Equal(exit.ObjectController, evidence.ExitObjectController);Assert.Equal(81, evidence.ExitAiIdentity);
    }

    [Fact]
    public void AtomicPartitionIncludesFailuresSkippedOriginalAndRealCoverageWithoutChangingLegacySnapshot()
    {
        var collector = new CampaignCycleDiagnostics();
        Assert.Empty(collector.SimulationSnapshot());
        Assert.All(collector.SimulationEvidenceSnapshot().Counts.Values, value => Assert.Equal(0, value));
        Parallel.For(0, 300, i =>
        {
            var row = new CampaignAiTickEvidence { Classification = "unknown" };
            collector.AiTick(row, row.Copy(), i % 3 != 2, i % 3 == 1 ? new InvalidOperationException("original") : null);
            var snapshot = collector.SimulationEvidenceSnapshot().Counts;
            foreach (string outcome in CampaignAiTickEvidence.Outcomes)
                Assert.Equal(snapshot["MobilePartyAi.Tick." + outcome], CampaignAiTickEvidence.Classes.Sum(kind => snapshot["MobilePartyAi.Tick." + kind + "." + outcome]));
            Assert.Equal(snapshot["MobilePartyAi.Tick.observed"], CampaignAiTickEvidence.Outcomes.Sum(outcome => snapshot["MobilePartyAi.Tick." + outcome]));
        });
        var final = collector.SimulationEvidenceSnapshot();
        Assert.Equal(300, final.Counts["MobilePartyAi.Tick.observed"]);
        Assert.Equal(100, final.Counts["MobilePartyAi.Tick.unknown.failed"]);
        Assert.Equal(100, final.Counts["MobilePartyAi.Tick.unknown.skipped"]);
        Assert.Equal(3, final.FirstEntries.Length);
    }

    [Fact]
    public void FinalizerReturnsOriginalExceptionAndSkippedOriginalCannotBecomeCompleted()
    {
        using var fixture = new Fixture(true, false);
        var collector = new CampaignCycleDiagnostics();var original = new InvalidOperationException("original failure");
        var state = new CampaignSimulationEvidencePatches.AiEntry { Collector = collector, Evidence = CampaignSimulationEvidencePatches.CaptureAi(fixture.Party.Ai) };
        var result = AccessTools.Method(typeof(CampaignSimulationEvidencePatches), "Finalizer").Invoke(null,
            new object[] { fixture.Party.Ai, AccessTools.Method(typeof(MobilePartyAi), "Tick"), false, original, state });
        Assert.Same(original, result);
        var snapshot = collector.SimulationEvidenceSnapshot().Counts;
        Assert.Equal(1, snapshot["MobilePartyAi.Tick.writerNpc.skipped"]);
        Assert.Equal(0, snapshot["MobilePartyAi.Tick.completed"]);
        Assert.Equal(1, snapshot["MobilePartyAi.Tick.beforeOriginal.failed"]);
    }

    [Fact]
    public void WaitParentIdentityPreservesEveryInstructionAndOnlyRebuildsAtSuccessfulClassEnd()
    {
        var parent = AccessTools.Method(typeof(Campaign), "RealTick");
        var instructions = PatchProcessor.GetOriginalInstructions(parent);
        var identity = AccessTools.Method(typeof(CampaignSimulationEvidencePatches), "RealTickIdentity");
        Assert.Same(instructions, identity.Invoke(null, new object[] { instructions }));
        var cleanup = AccessTools.Method(typeof(CampaignSimulationEvidencePatches), "Cleanup");
        var harmony = new Harmony("coop.tests.ai-evidence-wait-parent");var error = new Exception("patch failure");
        Assert.Same(error, cleanup.Invoke(null, new object[] { null, error, harmony }));
        Assert.Null(cleanup.Invoke(null, new object[] { parent, null, harmony }));
        try
        {
            Assert.Null(cleanup.Invoke(null, new object[] { null, null, harmony }));
            Assert.Single(Harmony.GetPatchInfo(parent).Transpilers.Where(patch => patch.owner == harmony.Id));
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Campaign previousCampaign = Campaign.Current;
        private readonly SessionMode previousMode = ModInformation.SessionMode;
        private readonly Campaign campaign = (Campaign)RuntimeHelpers.GetUninitializedObject(typeof(Campaign));
        private readonly ControllerIdProvider controller = new();
        private readonly Mock<IObjectManager> objects = new();
        private readonly Mock<IPlayerManager> players = new();
        private readonly bool player;
        private ILifetimeScope scope;
        internal SessionIdentity Identity = new();
        internal readonly MobileParty Party = (MobileParty)RuntimeHelpers.GetUninitializedObject(typeof(MobileParty));
        internal bool Registered = true, FailRead;
        internal string Owner = "local", MarkerOwner = "local";
        private static ConditionalWeakTable<object, ControlledObjectInfo> Markers =>
            (ConditionalWeakTable<object, ControlledObjectInfo>)AccessTools.Field(typeof(PlayerManager), "PlayerObjects").GetValue(null);
        internal Fixture(bool writer, bool player)
        {
            this.player = player;ModInformation.SessionMode = SessionMode.RemoteReplica;
            controller.SetControllerId("local");Party.StringId = "actual_party";Party.Ai = new MobilePartyAi(Party);
            campaign.MainParty = player ? Party : (MobileParty)RuntimeHelpers.GetUninitializedObject(typeof(MobileParty));
            Campaign.Current = campaign;
            Assert.True(Identity.TryApply(new AuthorityLease("room", "world", writer ? "local" : "remote", 1)));
            RegisterContainer();
        }
        internal void RegisterContainer()
        {
            scope?.Dispose();Markers.Remove(Party);
            if (player) Markers.Add(Party, new ControlledObjectInfo(MarkerOwner, controller));
            string id = "party-id";MobileParty registered = Party;
            objects.Setup(value => value.TryGetId(Party, out id)).Returns(Registered);
            if (FailRead) objects.Setup(value => value.TryGetId(Party, out id)).Throws(new InvalidOperationException("registry probe failure"));
            objects.Setup(value => value.TryGetObject<MobileParty>(id, out registered)).Returns(Registered);
            players.SetupGet(value => value.Players).Returns(player ? new[] { new Player(Owner, "hero", id, "clan", "char") } : Array.Empty<Player>());
            var builder = new ContainerBuilder();builder.RegisterInstance(controller).As<IControllerIdProvider>();
            builder.RegisterInstance(Identity).As<ISessionIdentity>();builder.RegisterInstance(new CampaignAuthorityContext(Identity, controller)).As<ICampaignAuthorityContext>();
            builder.RegisterInstance(objects.Object).As<IObjectManager>();builder.RegisterInstance(players.Object).As<IPlayerManager>();
            scope = builder.Build();ContainerProvider.SetContainer(scope);
        }
        public void Dispose()
        {
            ContainerProvider.Clear();scope.Dispose();Markers.Remove(Party);Campaign.Current = previousCampaign;ModInformation.SessionMode = previousMode;
        }
    }
}
