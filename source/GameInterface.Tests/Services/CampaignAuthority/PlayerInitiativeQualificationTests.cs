using Autofac;
using Common;
using Common.Util;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using GameInterface.Services.MobilePartyAIs.Patches;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using HarmonyLib;
using Moq;
using Serilog;
using System;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.CampaignSystem.Party;
using Xunit;
using NativeObjects = GameInterface.Services.ObjectManager.ObjectManager;

namespace GameInterface.Tests.Services.CampaignAuthority;

[Collection(ModInformationRoleCollection.Name)]
public class PlayerInitiativeQualificationTests
{
    static PlayerInitiativeQualificationTests() =>
        RuntimeHelpers.RunModuleConstructor(typeof(Coop.Tests.Mocks.TestNetwork).Module.ModuleHandle);

    [Fact]
    public void SameRegisteredPlayerKeepsNativeMainPartyInitiativeExemptionOnTheOtherWriter()
    {
        using var fixture = new Fixture();
        fixture.SetLocal("B");
        Assert.False(fixture.Model.ShouldPartyCheckInitiativeBehavior(fixture.B));
        fixture.SetLocal("A");
        Assert.True(fixture.Model.ShouldPartyCheckInitiativeBehavior(fixture.B));
        fixture.RegisterB();
        fixture.Patch();

        Assert.False(fixture.Model.ShouldPartyCheckInitiativeBehavior(fixture.B));
        fixture.SetLocal("B");
        Assert.False(fixture.Model.ShouldPartyCheckInitiativeBehavior(fixture.B));
    }

    [Fact]
    public void RealRegistrationRemovalAndRejoinChangeOnlyPlayerInitiativeEligibility()
    {
        using var fixture = new Fixture();
        fixture.Patch();
        Assert.True(fixture.Model.ShouldPartyCheckInitiativeBehavior(fixture.B));
        fixture.RegisterB();
        Assert.False(fixture.Model.ShouldPartyCheckInitiativeBehavior(fixture.B));
        Assert.True(fixture.Players.RemovePlayer(fixture.Player));
        Assert.False(PlayerManager.TryGetControlledObjectInfo(fixture.B, out _));
        Assert.True(fixture.Model.ShouldPartyCheckInitiativeBehavior(fixture.B));
        fixture.RegisterB();
        Assert.False(fixture.Model.ShouldPartyCheckInitiativeBehavior(fixture.B));
    }

    [Fact]
    public void OriginalNpcAndUnregisteredNativeMainPartyResultsStayUnchanged()
    {
        using var fixture = new Fixture();
        fixture.Patch();
        Assert.True(fixture.Model.ShouldPartyCheckInitiativeBehavior(fixture.Npc));
        Assert.False(fixture.Model.ShouldPartyCheckInitiativeBehavior(fixture.A));
        fixture.SetLocal("B");
        Assert.True(fixture.Model.ShouldPartyCheckInitiativeBehavior(fixture.Npc));
        Assert.False(fixture.Model.ShouldPartyCheckInitiativeBehavior(fixture.B));
    }

    [Fact]
    public void OriginalGetterNullFailureIsNotConvertedToAPlayerRestriction()
    {
        using var fixture = new Fixture();
        fixture.Patch();
        Assert.Throws<NullReferenceException>(() => fixture.Model.ShouldPartyCheckInitiativeBehavior(null!));
    }

    [Fact]
    public void ReceiveThreadRestrictionNeverAuthorizesNavigationOrWorldSimulation()
    {
        using var fixture = new Fixture();
        fixture.RegisterB();
        fixture.Patch();
        using (new AllowedThread())
        {
            Assert.False(fixture.Model.ShouldPartyCheckInitiativeBehavior(fixture.B));
            Assert.False(CampaignSimulationAuthorityPolicy.CanRunWorldSimulation());
            Assert.False(CampaignSimulationAuthorityPolicy.CanTickParty(fixture.B));
        }
    }

    [Theory]
    [InlineData("dedicated")]
    [InlineData("room-host-unregistered")]
    [InlineData("unknown-session")]
    [InlineData("missing-party")]
    [InlineData("contradictory-mapping")]
    public void LegacyUnknownOrInconsistentRegistrationKeepsTheOriginalNativeResult(string mode)
    {
        using var fixture = new Fixture();
        fixture.RegisterB();
        fixture.Patch();
        if (mode == "dedicated") ModInformation.SessionMode = SessionMode.DedicatedAuthority;
        if (mode == "room-host-unregistered") Assert.True(fixture.Players.RemovePlayer(fixture.HostPlayer));
        if (mode == "unknown-session") ContainerProvider.Clear();
        if (mode == "missing-party" || mode == "contradictory-mapping") Assert.True(fixture.Objects.Remove(fixture.B));
        if (mode == "contradictory-mapping")
            Assert.True(fixture.Objects.AddExisting("partyB", ObjectHelper.SkipConstructor<MobileParty>(), 2));

        Assert.True(fixture.Model.ShouldPartyCheckInitiativeBehavior(fixture.B));
    }

    [Theory]
    [InlineData("A", "A", false, false, CampaignExecutionPhase.Unspecified)]
    [InlineData("B", "B", false, false, CampaignExecutionPhase.Unspecified)]
    [InlineData("A", "B", false, false, CampaignExecutionPhase.Unspecified)]
    [InlineData("A", "A", false, true, CampaignExecutionPhase.Unspecified)]
    [InlineData("A", "", true, false, CampaignExecutionPhase.Unspecified)]
    [InlineData("A", "A", false, false, CampaignExecutionPhase.Replay)]
    [InlineData("A", "A", false, false, CampaignExecutionPhase.Bootstrap)]
    public void InitiativeRestrictionDoesNotGrantWorldOrNavigationPermission(
        string local, string writer, bool paused, bool frozen, CampaignExecutionPhase phase)
    {
        using var fixture = new Fixture();
        fixture.RegisterB();
        fixture.SetLocal(local);
        fixture.SetLease(writer, 3, paused, frozen);
        using (CampaignExecutionScope.Enter(phase))
        {
            bool worldBefore = CampaignSimulationAuthorityPolicy.CanRunWorldSimulation();
            bool navigationBefore = CampaignSimulationAuthorityPolicy.CanTickParty(fixture.B);
            fixture.Patch();
            Assert.False(fixture.Model.ShouldPartyCheckInitiativeBehavior(fixture.B));
            Assert.Equal(worldBefore, CampaignSimulationAuthorityPolicy.CanRunWorldSimulation());
            Assert.Equal(navigationBefore, CampaignSimulationAuthorityPolicy.CanTickParty(fixture.B));
        }
    }

    [Fact]
    public void SamePlayerOperationIdentityDoesNotDependOnCampaignEpochOrLocalOwnership()
    {
        using var fixture = new Fixture();
        fixture.RegisterB();
        fixture.Patch();
        foreach (var (local, writer, epoch) in new[] { ("A", "A", 3), ("B", "B", 5), ("A", "A", 7) })
        {
            fixture.SetLocal(local);
            fixture.SetLease(writer, epoch);
            Assert.False(fixture.Model.ShouldPartyCheckInitiativeBehavior(fixture.B));
            Assert.True(PlayerManager.TryGetControlledObjectInfo(fixture.B, out var control));
            Assert.Equal("B", control.ObjectControllerId);
            Assert.True(CampaignSimulationAuthorityPolicy.CanTickParty(fixture.B));
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Campaign previousCampaign = Campaign.Current;
        private readonly SessionMode previousMode = ModInformation.SessionMode;
        private readonly bool hadPrevious;
        private readonly ILifetimeScope previous;
        private readonly IContainer container;
        private readonly Campaign campaign = ObjectHelper.SkipConstructor<Campaign>();
        private readonly ControllerIdProvider controller = new();
        private readonly SessionIdentity identity = new();
        private readonly Harmony harmony = new("test.player-native-initiative");
        internal readonly NativeObjects Objects = new(Mock.Of<ILogger>());
        internal readonly MobileParty A = ObjectHelper.SkipConstructor<MobileParty>();
        internal readonly MobileParty B = ObjectHelper.SkipConstructor<MobileParty>();
        internal readonly MobileParty Npc = ObjectHelper.SkipConstructor<MobileParty>();
        internal readonly DefaultMobilePartyAIModel Model = ObjectHelper.SkipConstructor<DefaultMobilePartyAIModel>();
        internal readonly PlayerManager Players;
        internal readonly Player Player = new("B", "", "partyB", "", "");
        internal readonly Player HostPlayer = new("A", "", "partyA", "", "");
        internal Fixture()
        {
            hadPrevious = ContainerProvider.TryGetContainer(out previous);
            Assert.True(Objects.AddExisting("partyA", A, 1));
            Assert.True(Objects.AddExisting("partyB", B, 2));
            Assert.True(Objects.AddExisting("npc", Npc, 3));
            Players = new PlayerManager(Mock.Of<ILogger>(), Objects, controller);
            var builder = new ContainerBuilder();
            builder.RegisterInstance(new CampaignAuthorityContext(identity, controller)).As<ICampaignAuthorityContext>();
            builder.RegisterInstance(identity).As<ISessionIdentity>();
            builder.RegisterInstance(Players).As<IPlayerManager>();
            builder.RegisterInstance(Objects).As<global::GameInterface.Services.ObjectManager.IObjectManager>();
            container = builder.Build();
            ContainerProvider.SetContainer(container);
            Campaign.Current = campaign;
            SetLocal("A");
            SetLease("A", 1);
            Assert.True(Players.AddPlayer(HostPlayer));
        }
        internal void RegisterB() => Assert.True(Players.AddPlayer(Player));
        internal void SetLocal(string local)
        {
            controller.SetControllerId(local);
            campaign.MainParty = local == "A" ? A : B;
            ModInformation.SessionMode = local == "A" ? SessionMode.ListenAuthority : SessionMode.RemoteReplica;
        }
        internal void SetLease(string writer, long epoch, bool paused = false, bool frozen = false)
        {
            var lease = new AuthorityLease("room", "authority" + epoch, writer, epoch, paused);
            Assert.True(identity.TryApply(lease, "A"));
            if (frozen) Assert.True(identity.TryFreezeLocalAuthority(lease, 1));
        }
        internal void Patch()
        {
            var postfix = AccessTools.Method(typeof(DefaultMobilePartyAIModelPatches), "ShouldPartyCheckInitiativeBehavior_Postfix");
            if (postfix != null)
                harmony.Patch(AccessTools.Method(typeof(DefaultMobilePartyAIModel), nameof(DefaultMobilePartyAIModel.ShouldPartyCheckInitiativeBehavior)),
                    postfix: new HarmonyMethod(postfix));
        }
        public void Dispose()
        {
            harmony.UnpatchAll(harmony.Id);
            Players.RemovePlayer(Player);
            Players.RemovePlayer(HostPlayer);
            Campaign.Current = previousCampaign;
            ModInformation.SessionMode = previousMode;
            if (hadPrevious) ContainerProvider.SetContainer(previous);
            else ContainerProvider.Clear();
            container.Dispose();
        }
    }
}
