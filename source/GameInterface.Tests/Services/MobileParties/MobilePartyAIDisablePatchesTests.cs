using Common;
using Common.Util;
using Autofac;
using GameInterface.Services.Entity;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.MobilePartyAIs.Patches;
using GameInterface.Services.Players;
using GameInterface.Tests;
using HarmonyLib;
using System;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem.Party;
using Xunit;

namespace GameInterface.Tests.Services.MobileParties;

/// <summary>
/// Tests the authority boundary for campaign-map AI decisions.
/// </summary>
[Collection(ModInformationRoleCollection.Name)]
public class MobilePartyAIDisablePatchesTests : IDisposable
{
    private readonly bool wasServer = ModInformation.IsServer;
    private readonly ConditionalWeakTable<object, ControlledObjectInfo> playerObjects =
        (ConditionalWeakTable<object, ControlledObjectInfo>)AccessTools
            .Field(typeof(PlayerManager), "PlayerObjects")
            .GetValue(null)!;
    private MobileParty? registeredParty;

    [Theory]
    [InlineData("b", "b", "a", false, false, true)]
    [InlineData("a", "a", "b", false, false, true)]
    [InlineData("a", null, "b", false, false, false)]
    [InlineData("b", "a", "a", false, false, false)]
    [InlineData("b", "b", "b", false, true, false)]
    [InlineData("a", "a", "", true, false, false)]
    [InlineData("b", null, "b", false, false, true)]
    public void ConfirmedLeaseAllowsOnlyWorldAuthorityOrAnObserversRegisteredLocalParty(
        string local, string? owner, string writer, bool paused, bool frozen, bool expected)
    {
        var party = owner == null ? ObjectHelper.SkipConstructor<MobileParty>() : RegisterPlayerParty(owner, local);
        var identity = new SessionIdentity();
        var lease = new AuthorityLease("room", "authority", writer, 3, paused);
        Assert.True(identity.TryApply(lease, "a"));
        if (frozen) Assert.True(identity.TryFreezeLocalAuthority(lease, 1));
        var controller = new ControllerIdProvider();
        controller.SetControllerId(local);
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new CampaignAuthorityContext(identity, controller)).As<ICampaignAuthorityContext>();
        using var container = builder.Build();
        bool hadPrevious = ContainerProvider.TryGetContainer(out var previous);
        var previousMode = ModInformation.SessionMode;
        try
        {
            ModInformation.SessionMode = local == "a" ? SessionMode.ListenAuthority : SessionMode.RemoteReplica;
            ContainerProvider.SetContainer(container);
            Assert.Equal(expected, MobilePartyAIDisablePatches.IsTickAuthority(party));
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
            if (hadPrevious) ContainerProvider.SetContainer(previous);
            else ContainerProvider.Clear();
        }
    }

    public void Dispose()
    {
        ModInformation.IsServer = wasServer;

        if (registeredParty != null)
        {
            playerObjects.Remove(registeredParty);
        }
    }

    [Fact]
    public void IsTickAuthority_ServerPlayerParty_ReturnsFalse()
    {
        var party = RegisterPlayerParty("PlayerOne", "Server");
        ModInformation.IsServer = true;

        bool result = MobilePartyAIDisablePatches.IsTickAuthority(party);

        Assert.False(result);
    }

    [Fact]
    public void IsTickAuthority_ServerAiParty_ReturnsTrue()
    {
        var party = ObjectHelper.SkipConstructor<MobileParty>();
        ModInformation.IsServer = true;

        bool result = MobilePartyAIDisablePatches.IsTickAuthority(party);

        Assert.True(result);
    }

    [Fact]
    public void IsTickAuthority_ListenHostOwnParty_ReturnsTrue()
    {
        var party = RegisterPlayerParty("PlayerOne", "PlayerOne");
        ModInformation.SessionMode = SessionMode.ListenAuthority;
        Assert.True(MobilePartyAIDisablePatches.IsTickAuthority(party));
    }

    [Fact]
    public void IsTickAuthority_ListenHostRemoteParty_ReturnsFalse()
    {
        var party = RegisterPlayerParty("PlayerTwo", "PlayerOne");
        ModInformation.SessionMode = SessionMode.ListenAuthority;
        Assert.False(MobilePartyAIDisablePatches.IsTickAuthority(party));
    }

    [Fact]
    public void IsTickAuthority_OwningClientParty_ReturnsTrue()
    {
        var party = RegisterPlayerParty("PlayerOne", "PlayerOne");
        ModInformation.IsServer = false;

        bool result = MobilePartyAIDisablePatches.IsTickAuthority(party);

        Assert.True(result);
    }

    [Fact]
    public void IsTickAuthority_RemoteClientParty_ReturnsFalse()
    {
        var party = RegisterPlayerParty("PlayerOne", "PlayerTwo");
        ModInformation.IsServer = false;

        bool result = MobilePartyAIDisablePatches.IsTickAuthority(party);

        Assert.False(result);
    }

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(false, false, true, true)]
    [InlineData(false, false, false, false)]
    public void ShouldTickParty_ConfirmedLeaseOverridesLegacyPartyOwnership(
        bool authorityKnown,
        bool localAuthority,
        bool legacyPartyControl,
        bool expected)
    {
        Assert.Equal(expected, CampaignSimulationAuthorityPolicy.ShouldTickParty(
            authorityKnown,
            localAuthority,
            legacyPartyControl));
    }

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(false, false, true, true)]
    [InlineData(false, false, false, false)]
    public void ShouldRunWorldSimulation_ConfirmedLeaseOverridesLegacyRole(
        bool authorityKnown,
        bool localAuthority,
        bool legacyIsServer,
        bool expected)
    {
        Assert.Equal(expected, CampaignSimulationAuthorityPolicy.ShouldRunWorldSimulation(
            authorityKnown,
            localAuthority,
            legacyIsServer));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void ShouldAllowBehaviorUpdate_PreservesPlayerControlAndAuthorityAi(
        bool localSimulationAuthority,
        bool locallyControlled,
        bool expected)
    {
        Assert.Equal(expected, PartyBehaviorPatch.ShouldAllowBehaviorUpdate(
            localSimulationAuthority,
            locallyControlled));
    }

    private MobileParty RegisterPlayerParty(string ownerControllerId, string localControllerId)
    {
        var controllerIdProvider = new ControllerIdProvider();
        controllerIdProvider.SetControllerId(localControllerId);

        registeredParty = ObjectHelper.SkipConstructor<MobileParty>();
        playerObjects.Add(
            registeredParty,
            new ControlledObjectInfo(ownerControllerId, controllerIdProvider));

        return registeredParty;
    }
}
