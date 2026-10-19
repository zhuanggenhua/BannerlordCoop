#if DEBUG
using Autofac;
using Common;
using Common.Logging;
using Common.Messaging;
using Common.Util;
using GameInterface.AutoSync;
using GameInterface.AutoSync.Builders;
using GameInterface.Policies;
using GameInterface.Registry.Auto;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using GameInterface.Services.Heroes.Messages;
using GameInterface.Services.Heroes.Patches;
using GameInterface.Services.MapEvents;
using HarmonyLib;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Moq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using Xunit;

namespace GameInterface.Tests.Services.Heroes.Patches;

[Collection(nameof(ModInformationRoleCollection))]
public sealed class MissionHeroHealthNoopPatchTests : IDisposable
{
    private static readonly Lazy<Type> generated = new Lazy<Type>(GeneratePrefix);
    private static Hero mainHero = null!;
    private static Mission mission = null!;
    private static Campaign campaign = null!;
    private static Game game = null!;
    private static BasicCharacterObject player = null!;
    private static int generatedCalls, requestCalls;
    private static int mainHeroReads, ownerReads, diagnosticBefore, diagnosticAfter;
    private static int campaignReads, gameReads, playerReads;
    [ThreadStatic] private static bool simulateUninitialized;
    private static bool? diagnosticOriginalRan;
    private static CampaignMissionHealthDiagnostics.Observation? diagnosticRow;
    private static Exception? diagnosticException;
    private static Exception? injectedError;
    private readonly Harmony harmony = new Harmony("coop.tests.mission-health-noop." + Guid.NewGuid());
    private readonly SessionMode previousMode = ModInformation.SessionMode;
    private readonly Func<Hero, bool?> previousOwner = BattleSpawnGate.HeroAgentAuthorityProbe;
    private readonly ILifetimeScope? previousContainer;
    private readonly IContainer container;
    private readonly SessionIdentity identity = new SessionIdentity();
    private readonly Hero hero;
    private readonly List<string> logs = new List<string>();
    private readonly Action<string> capture;
    private readonly Action<MessagePayload<HeroHitPointsChangeRequested>> requested;
    private readonly List<HeroHitPointsChangeRequested> requests = new List<HeroHitPointsChangeRequested>();

    static MissionHeroHealthNoopPatchTests() =>
        RuntimeHelpers.RunModuleConstructor(typeof(Coop.Tests.Mocks.TestNetwork).Module.ModuleHandle);

    private static void OnGameThread(Action action)
    {
        int threadId = GameThread.Instance.GameThreadId;
        try
        {
            GameThread.Run(() =>
            {
                try { action(); }
                finally { simulateUninitialized = false; }
            }, blocking: true);
        }
        finally { Assert.Equal(threadId, GameThread.Instance.GameThreadId); }
    }

    public MissionHeroHealthNoopPatchTests()
    {
        ContainerProvider.TryGetContainer(out previousContainer);
        hero = (Hero)FormatterServices.GetUninitializedObject(typeof(Hero));
        AccessTools.Field(typeof(Hero), "_health").SetValue(hero, 100);
        mainHero = hero;
        mission = (Mission)FormatterServices.GetUninitializedObject(typeof(Mission));
        campaign = (Campaign)FormatterServices.GetUninitializedObject(typeof(Campaign));
        game = (Game)FormatterServices.GetUninitializedObject(typeof(Game));
        player = (CharacterObject)FormatterServices.GetUninitializedObject(typeof(CharacterObject));
        generatedCalls = requestCalls = 0; injectedError = null;
        mainHeroReads = ownerReads = diagnosticBefore = diagnosticAfter = 0;
        diagnosticOriginalRan = null; diagnosticRow = null; diagnosticException = null;
        campaignReads = gameReads = playerReads = 0;
        simulateUninitialized = false;
        ModInformation.SessionMode = SessionMode.RemoteReplica;
        BattleSpawnGate.HeroAgentAuthorityProbe = _ => { ownerReads++; return true; };
        var controller = new ControllerIdProvider(); controller.SetControllerId("B");
        identity.TryApply(new AuthorityLease("room", "paused", "", 6, true), "A");
        var builder = new ContainerBuilder();
        builder.RegisterInstance(Mock.Of<ISyncPolicy>(value => value.AllowOriginal() == false)).As<ISyncPolicy>();
        builder.RegisterInstance(controller).As<IControllerIdProvider>();
        builder.RegisterInstance(new CampaignAuthorityContext(identity, controller)).As<ICampaignAuthorityContext>();
        container = builder.Build(); ContainerProvider.SetContainer(container);
        harmony.Patch(AccessTools.PropertyGetter(typeof(Hero), nameof(Hero.MainHero)), prefix: new HarmonyMethod(typeof(MissionHeroHealthNoopPatchTests), nameof(MainHero)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(Mission), nameof(Mission.Current)), prefix: new HarmonyMethod(typeof(MissionHeroHealthNoopPatchTests), nameof(CurrentMission)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(Campaign), nameof(Campaign.Current)), prefix: new HarmonyMethod(typeof(MissionHeroHealthNoopPatchTests), nameof(CurrentCampaign)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(Game), nameof(Game.Current)), prefix: new HarmonyMethod(typeof(MissionHeroHealthNoopPatchTests), nameof(CurrentGame)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(Game), nameof(Game.PlayerTroop)), prefix: new HarmonyMethod(typeof(MissionHeroHealthNoopPatchTests), nameof(PlayerTroop)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(GameThread), nameof(GameThread.IsInitialized)), prefix: new HarmonyMethod(typeof(MissionHeroHealthNoopPatchTests), nameof(Initialized)));
        capture = message => logs.Add(message); OutputSinkManager.AddLogCallback(capture);
        requested = payload => requests.Add(payload.What); MessageBroker.Instance.Subscribe(requested);
    }

    private static bool MainHero(ref Hero __result) { mainHeroReads++; __result = mainHero; return false; }
    private static bool CurrentMission(ref Mission __result) { __result = mission; return false; }
    private static bool CurrentCampaign(ref Campaign __result) { campaignReads++; __result = campaign; return false; }
    private static bool CurrentGame(ref Game __result) { gameReads++; __result = game; return false; }
    private static bool PlayerTroop(ref BasicCharacterObject __result) { playerReads++; __result = player; return false; }
    private static bool Initialized(ref bool __result)
    { if (!simulateUninitialized) return true; __result = false; return false; }
    private static void GeneratedCalled() => generatedCalls++;
    private static void RequestCalled() => requestCalls++;
    private static void DiagnosticStarted() => diagnosticBefore++;
    private static void DiagnosticFinished(bool __2, Exception __3, CampaignMissionHealthDiagnostics.Observation __4)
    {
        diagnosticAfter++; diagnosticOriginalRan = __2;
        diagnosticException = __3; diagnosticRow = __4;
    }
    private static void ChangeEarlier(ref int value) => value = 99;
    private static void ThrowLater(ref Hero __instance) { if (injectedError != null) throw injectedError; }

    private void Wire(bool adapter)
    {
        harmony.Patch(AccessTools.Method(generated.Value, "HitPointsPrefix"), postfix: new HarmonyMethod(typeof(MissionHeroHealthNoopPatchTests), nameof(GeneratedCalled)));
        harmony.Patch(AccessTools.Method(typeof(HeroHitPointsRequestPatch), "Prefix"), postfix: new HarmonyMethod(typeof(MissionHeroHealthNoopPatchTests), nameof(RequestCalled)));
        harmony.Patch(AccessTools.Method(typeof(CampaignMissionHealthDiagnosticPatch), "Prefix"), postfix: new HarmonyMethod(typeof(MissionHeroHealthNoopPatchTests), nameof(DiagnosticStarted)));
        harmony.Patch(AccessTools.Method(typeof(CampaignMissionHealthDiagnosticPatch), "Finalizer"), postfix: new HarmonyMethod(typeof(MissionHeroHealthNoopPatchTests), nameof(DiagnosticFinished)));
        harmony.CreateClassProcessor(generated.Value).Patch();
        harmony.CreateClassProcessor(typeof(HeroHitPointsRequestPatch)).Patch();
        harmony.CreateClassProcessor(typeof(CampaignMissionHealthDiagnosticPatch)).Patch();
        if (adapter) harmony.CreateClassProcessor(typeof(MissionHeroHealthNoopPatch)).Patch();
    }

    [Fact]
    public void OriginalGeneratedPrefixReportsManagedWriteEvenWhenVanillaSetterDoesNothing()
    {
        OnGameThread(() =>
        {
            Wire(false); hero.HitPoints = 100;
            Assert.Equal(1, generatedCalls); Assert.Equal(1, requestCalls); Assert.Empty(requests);
            Assert.Single(logs.Where(text => text.Contains("Client updated managed") && text.Contains("Hero.HitPoints")));
            Assert.Equal(1, diagnosticBefore); Assert.Equal(1, diagnosticAfter); Assert.True(diagnosticOriginalRan);
            Assert.Equal(100, hero.HitPoints);
        });
    }

    [Fact]
    public void ExactOwnedNoopSkipsActualGeneratedPrefixButKeepsRequestAndDiagnosticFinalizer()
    {
        OnGameThread(() =>
        {
            Wire(true); hero.HitPoints = 100;
            Assert.Equal(0, generatedCalls); Assert.Equal(1, requestCalls); Assert.Empty(requests);
            Assert.DoesNotContain(logs, text => text.Contains("Client updated managed"));
            Assert.Equal(1, diagnosticBefore); Assert.Equal(1, diagnosticAfter); Assert.False(diagnosticOriginalRan);
            Assert.Equal(100, diagnosticRow!.OldHitPoints);
            Assert.Equal(100, diagnosticRow.RequestedHitPoints);
            Assert.Equal(100, diagnosticRow.FinalArgument);
            Assert.Equal(100, diagnosticRow.PostHitPoints);
            Assert.True(diagnosticRow.MainHero); Assert.Equal(6, diagnosticRow.Epoch);
            Assert.True(diagnosticRow.Paused); Assert.False(diagnosticRow.LocalWriter);
            Assert.False(diagnosticRow.OriginalAllowed); Assert.False(diagnosticRow.AllowedThread);
            Assert.Null(diagnosticRow.ObservationError); Assert.Null(diagnosticException);
            Assert.True(CallOriginalPolicy.IsClientMutationBlocked()); Assert.False(CallOriginalPolicy.IsOriginalAllowed());
            Assert.Equal(100, hero.HitPoints);
        });
    }

    [Theory]
    [InlineData("no-mission")]
    [InlineData("other-hero")]
    [InlineData("unowned")]
    [InlineData("writer")]
    [InlineData("host")]
    [InlineData("unknown-lease")]
    [InlineData("uninitialized-thread")]
    [InlineData("no-campaign")]
    [InlineData("no-game")]
    [InlineData("no-player")]
    [InlineData("non-character-player")]
    public void IneligibleSameValueFallsBackWithoutChangingHealth(string reason)
    {
        OnGameThread(() =>
        {
            if (reason == "no-mission") mission = null!;
            if (reason == "other-hero") mainHero = (Hero)FormatterServices.GetUninitializedObject(typeof(Hero));
            if (reason == "unowned") BattleSpawnGate.HeroAgentAuthorityProbe = _ => false;
            if (reason == "writer") Assert.True(identity.TryApply(new AuthorityLease("room", "writer", "B", 7), "A"));
            if (reason == "host") ModInformation.SessionMode = SessionMode.ListenAuthority;
            if (reason == "uninitialized-thread") simulateUninitialized = true;
            if (reason == "no-campaign") campaign = null!;
            if (reason == "no-game") game = null!;
            if (reason == "no-player") player = null!;
            if (reason == "non-character-player") player = (BasicCharacterObject)FormatterServices.GetUninitializedObject(typeof(BasicCharacterObject));
            if (reason == "unknown-lease")
        {
                var context = new Mock<ICampaignAuthorityContext>(); AuthorityLease missing = default;
                context.Setup(value => value.TryGetCurrentLease(out missing)).Returns(false);
                var builder = new ContainerBuilder(); builder.RegisterInstance(context.Object).As<ICampaignAuthorityContext>();
                builder.RegisterInstance(Mock.Of<ISyncPolicy>(value => value.AllowOriginal() == false)).As<ISyncPolicy>();
                using var unknown = builder.Build();
                ContainerProvider.SetContainer(unknown);
                Assert.True(MissionHeroHealthNoopPatch.Prefix(hero, 100));
                context.Verify(value => value.TryGetCurrentLease(out missing), Times.Once);
                ContainerProvider.SetContainer(container);
            }
            else Assert.True(MissionHeroHealthNoopPatch.Prefix(hero, 100));
            if (new[] { "unknown-lease", "no-campaign", "no-game", "no-player", "non-character-player" }.Contains(reason))
            { Assert.Equal(0, mainHeroReads); Assert.Equal(0, ownerReads); }
            if (reason == "unknown-lease")
            { Assert.Equal(0, campaignReads); Assert.Equal(0, gameReads); Assert.Equal(0, playerReads); }
            simulateUninitialized = false;
            Assert.Equal(100, hero.HitPoints);
        });
    }

    [Fact]
    public void NativeExactEqualityReturnsBeforeAnyAssignmentOrCallback()
    {
        OnGameThread(() =>
        {
            var setter = AccessTools.PropertySetter(typeof(Hero), nameof(Hero.HitPoints));
            byte[] il = setter.GetMethodBody()!.GetILAsByteArray()!;
            Assert.Equal(new byte[] { 0x02, 0x7b }, il.Take(2));
            Assert.Equal("_health", setter.Module.ResolveField(BitConverter.ToInt32(il, 2))!.Name);
            Assert.Equal(new byte[] { 0x03, 0x33, 0x01, 0x2a }, il.Skip(6).Take(4));
            Assert.Equal(new byte[] { 0x02, 0x7b }, AccessTools.PropertyGetter(typeof(Hero), nameof(Hero.HitPoints)).GetMethodBody()!.GetILAsByteArray()!.Take(2));
        });
    }

    [Fact]
    public async Task OffThreadAndNullHeroAlwaysKeepOriginalPath()
    {
        Assert.True(await Task.Run(() => MissionHeroHealthNoopPatch.Prefix(hero, 100)));
        OnGameThread(() => Assert.True(MissionHeroHealthNoopPatch.Prefix(null!, 100)));
        Assert.Equal(100, hero.HitPoints);
    }

    [Theory]
    [InlineData(99)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void ChangedArgumentsAreNeverClampedOrAuthorizedByAdapter(int requestedHealth)
    {
        OnGameThread(() =>
        {
            Assert.True(MissionHeroHealthNoopPatch.Prefix(hero, requestedHealth));
            Assert.Equal(100, hero.HitPoints);
        });
    }

    [Fact]
    public void ApprovedReceiveRetainsActualGeneratedPrefixAndOriginalSetter()
    {
        OnGameThread(() =>
        {
            Wire(true);
            using (new AllowedThread())
        {
                Assert.True(MissionHeroHealthNoopPatch.Prefix(hero, 100));
                hero.HitPoints = 100;
            }
            Assert.Equal(1, generatedCalls); Assert.Equal(1, requestCalls); Assert.Empty(requests);
            Assert.DoesNotContain(logs, text => text.Contains("Client updated managed"));
            Assert.True(diagnosticOriginalRan); Assert.Equal(1, diagnosticAfter);
            Assert.True(diagnosticRow!.OriginalAllowed); Assert.True(diagnosticRow.AllowedThread);
            Assert.False(AllowedThread.IsThisThreadAllowed());
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EarlierArgumentChangeUsesLiveValueAndPreservesOwnershipRequestAndException(bool owned)
    {
        OnGameThread(() =>
        {
            Wire(true); BattleSpawnGate.HeroAgentAuthorityProbe = _ => owned;
            var target = AccessTools.PropertySetter(typeof(Hero), nameof(Hero.HitPoints));
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(MissionHeroHealthNoopPatchTests), nameof(ChangeEarlier)) { priority = 700 });
            injectedError = new InvalidOperationException("later original pipeline error");
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(MissionHeroHealthNoopPatchTests), nameof(ThrowLater)) { priority = 0 });
            Assert.Same(injectedError, Assert.Throws<InvalidOperationException>(() => hero.HitPoints = 100));
            Assert.Equal(1, generatedCalls); Assert.Equal(1, requestCalls);
            if (owned) { Assert.Equal(99, Assert.Single(requests).HitPoints); Assert.Same(hero, requests[0].Hero); }
            else Assert.Empty(requests);
            Assert.Single(logs.Where(text => text.Contains("Client updated managed") && text.Contains("Hero.HitPoints")));
            Assert.Same(injectedError, diagnosticException); Assert.Equal(99, diagnosticRow!.FinalArgument);
            Assert.Equal(100, diagnosticRow.RequestedHitPoints);
            Assert.Equal(100, diagnosticRow.OldHitPoints); Assert.Equal(100, diagnosticRow.PostHitPoints);
            Assert.Equal(1, diagnosticAfter);
            Assert.Equal(100, hero.HitPoints);
            Assert.False(AllowedThread.IsThisThreadAllowed());
        });
    }

    private static Type GeneratePrefix()
    {
        var property = AccessTools.Property(typeof(Hero), nameof(Hero.HitPoints));
        var factory = new Mock<IAutoRegistryFactory>();
        var builder = new AutoSyncPropertyBuilder(factory.Object, new AutoSyncRegistry(), new AutoSyncConstantsBuilder());
        string prefix = builder.GetPrefix(new Debuggable<PropertyInfo>(property, false));
        string headers = "using GameInterface.Utils; using HarmonyLib; using Common.Messaging; using GameInterface.Policies; using System; using System.Collections.Generic; using TaleWorlds.CampaignSystem; namespace AutoSync;";
        var syntax = new[] { CSharpSyntaxTree.ParseText(headers + prefix), CSharpSyntaxTree.ParseText(AutoSyncUtils.GetLocalSetMessage(property)) };
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat(AppDomain.CurrentDomain.GetAssemblies().Where(value => !value.IsDynamic && !string.IsNullOrEmpty(value.Location)).Select(value => value.Location)).Distinct();
        var compilation = CSharpCompilation.Create("AutoSyncAsm.NoopTests." + Guid.NewGuid().ToString("N"), syntax,
            paths.Select(path => MetadataReference.CreateFromFile(path)), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var bytes = new MemoryStream(); var result = compilation.Emit(bytes);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return Assembly.Load(bytes.ToArray()).GetType("AutoSync.Hero_HitPoints_PropertySetPrefix", true)!;
    }

    public void Dispose()
    {
        harmony.UnpatchAll(harmony.Id); OutputSinkManager.RemoveLogCallback(capture); MessageBroker.Instance.Unsubscribe(requested);
        BattleSpawnGate.HeroAgentAuthorityProbe = previousOwner; ModInformation.SessionMode = previousMode;
        Assert.True(GameThread.Instance.IsInitialized);
        Assert.False(GameThread.Instance.IsGameThread);
        if (previousContainer != null) ContainerProvider.SetContainer(previousContainer); else ContainerProvider.Clear();
        container.Dispose(); mainHero = null!; mission = null!; campaign = null!; game = null!; player = null!; injectedError = null;
    }
}
#endif
