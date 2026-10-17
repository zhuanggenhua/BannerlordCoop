using Common;
using Common.Util;
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
