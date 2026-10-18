using Common;
using Common.Tests.Utils;
using GameInterface.CoopSessionData;
using GameInterface.CoopSessionData.Save.Data;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Inventory.Messages;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using Moq;
using Newtonsoft.Json.Linq;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using Xunit;
using ObjectManagerService = GameInterface.Services.ObjectManager.ObjectManager;

namespace GameInterface.Tests.Services.CampaignAuthority;

public class CampaignSessionCheckpointServiceTests
{
    [Fact]
    public void TransfersDetachedSharedDataWithoutReplacingActorsOrLocalPreferences()
    {
        var source = new Fixture();
        var target = new Fixture();
        source.Provider.CoopSession.AgingPlayerData.PlayerIsIllDays["Hero_B"] = 3;
        source.Provider.CoopSession.CraftingPlayerData.PlayerOpenNewPartXpDictionary["Hero_B"] = new();
        source.Provider.CoopSession.HeroMeetingData.PlayerLastMeetingTimes["Hero_B"] = new() { ["Hero_A"] = 17 };
        source.Provider.CoopSession.TradePlayerData.PlayerItemsTradeData["Hero_B"] = new() { ["item"] = Tuple.Create(9.5f, 2) };
        source.Registrations[1].CrimeRatings["kingdom"] = 14;
        target.Registrations[1].MapTrackedControllerIds.Add("A");
        target.Registrations[1].PlatformName = "local name";
        var inventory = target.Provider.CoopSession.InventoryPlayerData;
        inventory.PlayerInventoryLocks["Hero_B"] = new List<string> { "sword" };
        inventory.PlayerInventorySortPreferences["Hero_B"] = new() { [1] = Tuple.Create(2, 3) };
        int initializationCount = 0;
        target.Broker.Subscribe<InitializeClientInventoryData>(payload =>
        {
            Assert.Same(inventory, payload.What.InventoryPlayerData);
            initializationCount++;
        });

        Assert.True(source.Service.TryCapture("campaign", out var checkpoint));
        source.Provider.CoopSession.AgingPlayerData.PlayerIsIllDays["Hero_B"] = 99;
        source.Registrations[1].CrimeRatings["kingdom"] = 99;
        Assert.True(target.Service.TryApply(checkpoint, "campaign"));

        Assert.Equal(3, target.Provider.CoopSession.AgingPlayerData.PlayerIsIllDays["Hero_B"]);
        Assert.Equal(17, target.Provider.CoopSession.HeroMeetingData.PlayerLastMeetingTimes["Hero_B"]["Hero_A"]);
        Assert.Equal(Tuple.Create(9.5f, 2), target.Provider.CoopSession.TradePlayerData.PlayerItemsTradeData["Hero_B"]["item"]);
        Assert.Empty(target.Provider.CoopSession.CraftingPlayerData.PlayerOpenNewPartXpDictionary["Hero_B"]);
        Assert.Equal(14, target.Registrations[1].CrimeRatings["kingdom"]);
        Assert.Equal("local name", target.Registrations[1].PlatformName);
        Assert.Equal(new[] { "A" }, target.Registrations[1].MapTrackedControllerIds);
        Assert.Same(inventory, target.Provider.CoopSession.InventoryPlayerData);
        Assert.Equal(new[] { "sword" }, inventory.PlayerInventoryLocks["Hero_B"]);
        Assert.Equal(Tuple.Create(2, 3), inventory.PlayerInventorySortPreferences["Hero_B"][1]);
        Assert.Equal(1, initializationCount);
        Assert.All(target.Provider.CoopSession.Players, player => Assert.Same(
            target.Registrations.Single(existing => existing.ControllerId == player.ControllerId), player));
        target.Players.Verify(value => value.AddPlayer(It.IsAny<Player>()), Times.Never);
        target.Players.Verify(value => value.RemovePlayer(It.IsAny<Player>()), Times.Never);
        target.Players.Verify(value => value.ReplacePlayer(It.IsAny<Player>(), It.IsAny<Player>()), Times.Never);
    }

    [Theory]
    [InlineData("campaign")]
    [InlineData("lease")]
    [InlineData("mapping")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("json")]
    [InlineData("data")]
    public void InvalidCheckpointCannotPartiallyChangePlayerOrSessionState(string failure)
    {
        var source = new Fixture();
        var target = new Fixture();
        source.Registrations[0].CrimeRatings["kingdom"] = 100;
        Assert.True(source.Service.TryCapture("campaign", out var checkpoint));
        var document = JObject.Parse(checkpoint.SessionJson);
        if (failure == "lease") document["AuthorityEpoch"] = 999;
        if (failure == "mapping") document["Players"][1]["HeroId"] = "Hero_other";
        if (failure == "missing") ((JArray)document["Players"]).RemoveAt(1);
        if (failure == "duplicate") document["Players"][1] = document["Players"][0].DeepClone();
        if (failure == "data") document["AgingPlayerData"] = null;
        checkpoint = new CampaignSessionCheckpoint(failure == "campaign" ? "other" : "campaign",
            failure == "json" ? "{" : document.ToString());
        var originalSession = target.Provider.CoopSession;
        Assert.False(target.Service.TryApply(checkpoint, "campaign"));
        Assert.Same(originalSession, target.Provider.CoopSession);
        Assert.Empty(target.Registrations[0].CrimeRatings);
    }

    [Fact]
    public void CaptureRejectsAnUnregisteredPlayerObject()
    {
        var fixture = new Fixture();
        Assert.True(fixture.Objects.TryGetObject<MobileParty>("MobileParty_B", out var party));
        Assert.True(fixture.Objects.Remove(party));
        Assert.False(fixture.Service.TryCapture("campaign", out var checkpoint));
        Assert.Null(checkpoint);
    }

    private sealed class Fixture
    {
        internal readonly ObjectManagerService Objects = new(Mock.Of<ILogger>());
        internal readonly Mock<IPlayerManager> Players = new();
        internal readonly CoopSessionProvider Provider = new();
        internal readonly TestMessageBroker Broker = new();
        internal readonly Player[] Registrations;
        internal readonly CampaignSessionCheckpointService Service;

        internal Fixture()
        {
            Registrations = new[] { CreatePlayer("A", 1), CreatePlayer("B", 5) };
            Players.SetupGet(value => value.Players).Returns(Registrations);
            var identity = new SessionIdentity();
            Assert.True(identity.TryApply(new AuthorityLease("room", "paused", "", 3, isPaused: true), "A"));
            Service = new CampaignSessionCheckpointService(Objects, Players.Object, Provider, identity, Broker);
        }

        private Player CreatePlayer(string controller, uint handle)
        {
            var hero = Empty<Hero>();
            var character = Empty<CharacterObject>();
            hero._characterObject = character;
            Assert.True(Objects.AddExisting("Hero_" + controller, hero, handle));
            Assert.True(Objects.AddExisting("MobileParty_" + controller, Empty<MobileParty>(), handle + 1));
            Assert.True(Objects.AddExisting("Clan_" + controller, Empty<Clan>(), handle + 2));
            Assert.True(Objects.AddExisting("CharacterObject_" + controller, character, handle + 3));
            return new Player(controller, "Hero_" + controller, "MobileParty_" + controller, "Clan_" + controller,
                "CharacterObject_" + controller);
        }
        private static T Empty<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));
    }
}
