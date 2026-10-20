using Autofac;
using Autofac.Builder;
using Common;
using Common.Util;
using Coop.Core.Client;
using Coop.Core.Server;
using GameInterface.Services.CampaignAuthority.NativeState;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Kingdoms;
using GameInterface.Services.Kingdoms.Data;
using GameInterface.Services.ObjectManager;
using HarmonyLib;
using Moq;
using ProtoBuf;
using Serilog;
using System.IO;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Election;
using Xunit;
using NativeObjects = GameInterface.Services.ObjectManager.ObjectManager;

namespace GameInterface.Tests.Services.CampaignAuthority.NativeState;

public class CampaignKingdomProposalCheckpointTests
{
    [Fact]
    public void ResolvedHistoryRoundtripsInOrderWithoutPublishingOrRequiringANativeCampaign()
    {
        var source = new Fixture(); var target = new Fixture();
        var acceptValue = source.Accept();
        source.Behavior._kingdomDecisionsList.Add(source.War());
        source.Behavior._kingdomDecisionsList.Add(acceptValue);
        source.Behavior._kingdomDecisionsList.Add(source.War());
        Assert.True(source.Service.TryCaptureKingdomProposals(source.Behavior, out var state));
        using var stream = new MemoryStream();
        Serializer.Serialize(stream, new CampaignNativeBehaviorCheckpoint(new CampaignAgingBehaviorState(0, new CampaignHeroIntState[0], new CampaignHeroIntState[0]), null, state));
        stream.Position = 0;
        var copy = Serializer.Deserialize<CampaignNativeBehaviorCheckpoint>(stream);
        Assert.Equal(3, copy.Version);
        Assert.True(target.Service.TryPrepareKingdomProposals(copy.KingdomProposals, target.Behavior, out var install));
        Assert.Empty(target.Behavior._kingdomDecisionsList);
        install();
        Assert.Equal(3, target.Behavior._kingdomDecisionsList.Count);
        var war = Assert.IsType<DeclareWarDecision>(target.Behavior._kingdomDecisionsList[0]);
        Assert.Same(target.Clan, war.ProposerClan); Assert.Same(target.Kingdom, war.Kingdom);
        Assert.Same(target.Target, war.FactionToDeclareWarOn); Assert.Equal(1234, war.TriggerTime.NumTicks);
        var accept = Assert.IsType<AcceptCallToWarAgreementDecision>(target.Behavior._kingdomDecisionsList[1]);
        Assert.Same(target.Calling, accept.CallingKingdom); Assert.Same(target.Target, accept.KingdomToCallToWarAgainst);
        Assert.Equal(718, accept.CallToWarCost); Assert.Equal(5678, accept.TriggerTime.NumTicks);
        Assert.True(accept.IsEnforced); Assert.True(accept.PlayerExamined);
        Assert.IsType<DeclareWarDecision>(target.Behavior._kingdomDecisionsList[2]);
        Assert.Null(accept._allianceCampaignBehavior); // lazy local cache, not copied from A
        Assert.Equal(CampaignExecutionPhase.Unspecified, CampaignExecutionScope.Current);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("count")]
    [InlineData("payload")]
    [InlineData("negative")]
    public void InvalidHistoryRefusesBeforeReplacingExistingIndex(string failure)
    {
        var fixture = new Fixture();
        fixture.Behavior._kingdomDecisionsList.Add(fixture.War());
        var original = fixture.Behavior._kingdomDecisionsList;
        var data = new DeclareWarDecisionData("clan", "kingdom", failure == "negative" ? -1 : 123,
            false, false, false, failure == "missing" ? "unknown" : "target");
        using var stream = new MemoryStream();
        Serializer.Serialize(stream, new CampaignNativeBehaviorCheckpointService.ProposalHistoryPayload { Decisions = new KingdomDecisionData[] { data } });
        var state = new CampaignKingdomProposalState(failure == "count" ? 2 : 1,
            failure == "payload" ? new byte[] { 255 } : stream.ToArray());
        Assert.False(fixture.Service.TryPrepareKingdomProposals(state, fixture.Behavior, out var install));
        Assert.Null(install); Assert.Same(original, fixture.Behavior._kingdomDecisionsList);
        Assert.Single(original); Assert.NotNull(fixture.Service.LastRejectedReason);
    }

    [Fact]
    public void MissingCaptureReferenceRefusesRatherThanDroppingHistory()
    {
        var f = new Fixture(); var war = f.War();
        AccessTools.Field(typeof(DeclareWarDecision), nameof(DeclareWarDecision.FactionToDeclareWarOn)).SetValue(war, ObjectHelper.SkipConstructor<Kingdom>());
        f.Behavior._kingdomDecisionsList.Add(war);
        Assert.False(f.Service.TryCaptureKingdomProposals(f.Behavior, out var state));
        Assert.Null(state); Assert.Single(f.Behavior._kingdomDecisionsList);
        Assert.Contains("kingdom.history", f.Service.LastRejectedReason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ActualProductionModulesInjectMatureConverterAndNativeCheckpoint(bool server)
    {
        var builder = new ContainerBuilder();
        if (server) builder.RegisterModule<ServerModule>(); else builder.RegisterModule<ClientModule>();
        builder.RegisterModule<GameInterfaceModule>();
        using var container = builder.Build(ContainerBuildOptions.IgnoreStartableComponents);
        using var scope = container.BeginLifetimeScope();
        var service = Assert.IsType<CampaignNativeBehaviorCheckpointService>(scope.Resolve<ICampaignNativeBehaviorCheckpointService>());
        Assert.IsType<KingdomDecisionDataConverter>(AccessTools.Field(service.GetType(), "decisionConverter").GetValue(service));
        Assert.NotSame(service, scope.Resolve<ICampaignNativeBehaviorCheckpointService>());
        var session = scope.Resolve<ICampaignSessionCheckpointService>();
        Assert.IsType<CampaignNativeBehaviorCheckpointService>(AccessTools.Field(session.GetType(), "nativeState").GetValue(session));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullPrepareRequiresCompleteHistoryBeforeAnyOtherDomainCanMutate(bool corrupt)
    {
        var f = new Fixture(); var aging = new AgingCampaignBehavior(); var caravans = new CaravansCampaignBehavior();
        aging._gameStartDay = 15;
        Assert.True(f.Service.TryCapture(aging, caravans, System.Array.Empty<TaleWorlds.Core.ItemCategory>(), f.Behavior, out var good));
        var history = corrupt ? new CampaignKingdomProposalState(2, good.KingdomProposals.Payload) : null;
        var incomplete = new CampaignNativeBehaviorCheckpoint(good.Aging, good.Caravans, history);
        aging._gameStartDay = 99;
        var visits = caravans._caravanLastHomeTownVisitTime;
        Assert.False(f.Service.TryPrepare(incomplete, aging, caravans, System.Array.Empty<TaleWorlds.Core.ItemCategory>(), f.Behavior, out var install));
        Assert.Null(install); Assert.Equal(99, aging._gameStartDay);
        Assert.Same(visits, caravans._caravanLastHomeTownVisitTime);
        Assert.Contains("kingdom", f.Service.LastRejectedReason);
    }

    [Fact]
    public void RejectionsIdentifyExactNativeDistanceAndUnregisteredLootHistory()
    {
        var f = new Fixture(); var aging = new AgingCampaignBehavior(); var caravans = new CaravansCampaignBehavior();
        caravans._navalCaravanVeryFarCache = float.PositiveInfinity;
        Assert.False(f.Service.TryCapture(aging, caravans, System.Array.Empty<TaleWorlds.Core.ItemCategory>(), out _));
        Assert.Contains("capture.caravan.distance naval=Infinity", f.Service.LastRejectedReason);
        caravans._navalCaravanVeryFarCache = -1;
        var party = ObjectHelper.SkipConstructor<TaleWorlds.CampaignSystem.Party.MobileParty>();
        party.StringId = "retired_caravan";
        caravans._lootedCaravans[party] = new CampaignTime(710);
        Assert.False(f.Service.TryCapture(aging, caravans, System.Array.Empty<TaleWorlds.Core.ItemCategory>(), out _));
        Assert.Contains("capture.caravan.looted party=retired_caravan, ticks=710", f.Service.LastRejectedReason);
        Assert.Single(caravans._lootedCaravans);
    }

    private sealed class Fixture
    {
        internal readonly Clan Clan = ObjectHelper.SkipConstructor<Clan>();
        internal readonly Kingdom Kingdom = ObjectHelper.SkipConstructor<Kingdom>();
        internal readonly Kingdom Calling = ObjectHelper.SkipConstructor<Kingdom>();
        internal readonly Kingdom Target = ObjectHelper.SkipConstructor<Kingdom>();
        internal readonly KingdomDecisionProposalBehavior Behavior = new();
        internal readonly NativeObjects Objects = new(Mock.Of<ILogger>());
        internal readonly CampaignNativeBehaviorCheckpointService Service;
        internal Fixture()
        {
            Assert.True(Objects.AddExisting("clan", Clan, 1)); Assert.True(Objects.AddExisting("kingdom", Kingdom, 2));
            Assert.True(Objects.AddExisting("calling", Calling, 3)); Assert.True(Objects.AddExisting("target", Target, 4));
            Service = new(Objects, new KingdomDecisionDataConverter(Objects));
        }
        internal DeclareWarDecision War()
        {
            var value = ObjectHelper.SkipConstructor<DeclareWarDecision>();
            value.ProposerClan = Clan; value._kingdom = Kingdom; value.TriggerTime = new CampaignTime(1234);
            AccessTools.Field(typeof(DeclareWarDecision), nameof(DeclareWarDecision.FactionToDeclareWarOn)).SetValue(value, Target); return value;
        }
        internal AcceptCallToWarAgreementDecision Accept()
        {
            var data = new AcceptCallToWarAgreementDecisionData("clan", "kingdom", 5678, true, false, true, "calling", "target", 718);
            using var stream = new MemoryStream();
            Serializer.Serialize(stream, new CampaignNativeBehaviorCheckpointService.ProposalHistoryPayload { Decisions = new KingdomDecisionData[] { data } });
            Assert.True(Service.TryPrepareKingdomProposals(new CampaignKingdomProposalState(1, stream.ToArray()), Behavior, out var install));
            install(); var value = Assert.IsType<AcceptCallToWarAgreementDecision>(Behavior._kingdomDecisionsList[0]);
            Behavior._kingdomDecisionsList.Clear(); return value;
        }
    }
}
