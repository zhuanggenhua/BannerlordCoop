using Common.Serialization;
using GameInterface.CoopSessionData.Save.Data;
using GameInterface.Services.Caravans;
using GameInterface.Services.Heroes.Data;
using GameInterface.Services.MobileParties;
using GameInterface.Services.Players.Data;
using GameInterface.Services.Smithing;
using GameInterface.Services.Workshops;
using GameInterface.Services.Alleys;
using Xunit;
using Xunit.Abstractions;
using GameInterface.Services.Inventory.TradeSkills;
using GameInterface.Services.Inventory;
using GameInterface.Services.Heroes;

namespace Coop.Tests.Server.Services.Save
{
    public class JSONSessionTests
    {
        private const string SAVE_PATH = "./saves/";

        private readonly ITestOutputHelper output;

        public JSONSessionTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact]
        public void SaveLoadSessions()
        {
            var gameObjectGuids = new GameObjectGuids(new string[] { "Random STR" });

            var players = new Player[]
            {
                new Player("MyPlayer", "MyHero", "MyParty", "MyClan", "MyCharacter")
            };

            var sessionData = new CoopSession(
                "TestId",
                players,
                new CraftingPlayerData(new(), new(), new()),
                new WorkshopPlayerData(new()),
                new CaravansPlayerData(new(), new()),
                new AlleyPlayerData(new()),
                new InteractionsPlayerData(new(), new(), new(), new(), new(), new(), new(), new(), new(), new(), new(), new()),
                new TradePlayerData(new(), new(), new(), new()),
                new InventoryPlayerData(new(), new()),
                new HeroMeetingData(new()),
                new AgingPlayerData(new()),
                roomId: "room-a",
                authorityId: "authority-a",
                authorityControllerId: "controller-a",
                authorityEpoch: 3,
                roomHostControllerId: "room-host-a",
                authorityPaused: true);

            string saveFile = SAVE_PATH + sessionData.UniqueGameId + ".json";

            var fileIO = new JsonFileIO();

            fileIO.WriteToFile(saveFile, sessionData);

            var resolvedSessions = fileIO.ReadFromFile<CoopSession>(saveFile);

            Assert.NotNull(resolvedSessions);
            Assert.Equal(sessionData.UniqueGameId, resolvedSessions.UniqueGameId);
            Assert.Equal(sessionData.RoomId, resolvedSessions.RoomId);
            Assert.Equal(sessionData.AuthorityId, resolvedSessions.AuthorityId);
            Assert.Equal(sessionData.AuthorityControllerId, resolvedSessions.AuthorityControllerId);
            Assert.Equal(sessionData.AuthorityEpoch, resolvedSessions.AuthorityEpoch);
            Assert.Equal(sessionData.RoomHostControllerId, resolvedSessions.RoomHostControllerId);
            Assert.True(resolvedSessions.AuthorityPaused);
        }

        [Fact]
        public void LegacySessionWithoutAuthorityFieldsStillLoads()
        {
            var filePath = SAVE_PATH + "LegacyAuthoritySession.json";
            System.IO.Directory.CreateDirectory(SAVE_PATH);
            System.IO.File.WriteAllText(filePath, "{\"UniqueGameId\":\"legacy\"}");

            try
            {
                var resolved = new JsonFileIO().ReadFromFile<CoopSession>(filePath);

                Assert.NotNull(resolved);
                Assert.Equal("legacy", resolved.UniqueGameId);
                Assert.Equal(string.Empty, resolved.RoomId);
                Assert.Equal(string.Empty, resolved.AuthorityId);
                Assert.Equal(0, resolved.AuthorityEpoch);
                Assert.Equal(string.Empty, resolved.RoomHostControllerId);
                Assert.False(resolved.AuthorityPaused);
            }
            finally
            {
                if (System.IO.File.Exists(filePath)) System.IO.File.Delete(filePath);
            }
        }
    }
}
