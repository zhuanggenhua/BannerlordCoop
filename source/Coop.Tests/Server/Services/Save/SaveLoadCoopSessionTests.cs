using Autofac;
using Common;
using Common.Network;
using Coop.Core.Common;
using GameInterface.Services.Heroes.Messages;
using Common.Tests.Utils;
using Coop.Core.Server.Services.Save.Handlers;
using Coop.Core.Server.Services.Shutdown;
using GameInterface.CoopSessionData;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using Moq;
using System.Reflection;
using Common.Logging;
using Coop.Core.Server.Services.Save;
using GameInterface.CoopSessionData.Save.Data;
using GameInterface.Services.Alleys;
using GameInterface.Services.Caravans;
using GameInterface.Services.Heroes;
using GameInterface.Services.Inventory;
using GameInterface.Services.Heroes;
using System.Collections.Generic;
using GameInterface.Services.Inventory.TradeSkills;
using GameInterface.Services.MobileParties;
using GameInterface.Services.Players.Data;
using GameInterface.Services.Smithing;
using GameInterface.Services.Workshops;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace Coop.Tests.Server.Services.Save
{
    public class SaveLoadCoopSessionTests
    {
        private const string SAVE_PATH = "./";

        private readonly ITestOutputHelper output;
        private readonly ServerTestComponent serverComponent;
        private readonly IContainer container;

        public SaveLoadCoopSessionTests(ITestOutputHelper output)
        {
            this.output = output;

            serverComponent = new ServerTestComponent(output);

            container = serverComponent.Container; ;
        }

        [Fact]
        public void SaveSession()
        {
            // Setup
            var saveManager = container.Resolve<ICoopSaveManager>();

            var players = new Player[]
            {
                new Player("MyPlayer1", "MyHero1","MyParty1", "MyClan1", "MyCharacter1"),
                new Player("MyPlayer2", "MyHero2","MyParty2", "MyClan2", "MyCharacter2"),
            };

            var interactionsPlayerData = new InteractionsPlayerData(new(), new(), new(), new(), new(), new(), new(), new(), new(), new(), new(), new());
            interactionsPlayerData.PlayerAlreadySneakedSettlements[players[0].HeroId] = new() { "settlement1Id", "settlement2Id" };
            interactionsPlayerData.PlayerAlreadySneakedSettlements[players[1].HeroId] = new() { "settlement2Id", "settlement3Id" };
            interactionsPlayerData.PlayerOrderedDrinkThisDayInSettlement[players[0].HeroId] = "settlement1Id";
            interactionsPlayerData.PlayerOrderedDrinkThisDayInSettlement[players[1].HeroId] = "settlement2Id";
            interactionsPlayerData.PlayerHasBoughtTunToParty[players[0].HeroId] = true;
            interactionsPlayerData.PlayerHasBoughtTunToParty[players[1].HeroId] = false;
            interactionsPlayerData.PlayerHasMetRansomBroker[players[0].HeroId] = false;
            interactionsPlayerData.PlayerHasMetRansomBroker[players[1].HeroId] = true;
            interactionsPlayerData.PlayerHasMetHermit[players[0].HeroId] = true;
            interactionsPlayerData.PlayerHasMetHermit[players[1].HeroId] = false;

            var tradePlayerData = new TradePlayerData(new(), new(), new(), new());
            tradePlayerData.PlayerSettlementBribePaid[players[0].HeroId] = new() { ["settlement1Id"] = 0, ["settlement2Id"] = 1000 };
            tradePlayerData.PlayerSettlementBribePaid[players[1].HeroId] = new() { ["settlement2Id"] = 3200, ["settlement3Id"] = 123 };

            ICoopSession sessionData = new CoopSession(
                "SaveManagerTest",
                players,
                new CraftingPlayerData(new(), new(), new()),
                new WorkshopPlayerData(new()),
                new CaravansPlayerData(new(), new()),
                new AlleyPlayerData(new()),
                interactionsPlayerData,
                tradePlayerData,
                new InventoryPlayerData(new(), new()),
                new HeroMeetingData(new()),
                new AgingPlayerData(new()));

            string saveFile = sessionData.UniqueGameId;

            string savePath = saveManager.DefaultPath + saveFile;

            if (File.Exists(savePath + ".json"))
            {
                // Ensure file does not exist before testing
                File.Delete(savePath + ".json");
            }

            Assert.False(File.Exists(savePath));

            // Execution
            saveManager.SaveCoopSession(saveFile, sessionData);

            // Verification
            Assert.True(File.Exists(savePath + saveManager.FileType));
        }

        [Fact]
        public void SaveLoadSession()
        {
            // Setup
            var saveManager = container.Resolve<ICoopSaveManager>();

            var players = new Player[]
            {
                new Player("MyPlayer1", "MyHero1","MyParty1", "MyClan1", "MyCharacter1"),
                new Player("MyPlayer2", "MyHero2","MyParty2", "MyClan2", "MyCharacter2"),
            };

            var interactionsPlayerData = new InteractionsPlayerData(new(), new(), new(), new(), new(), new(), new(), new(), new(), new(), new(), new());
            interactionsPlayerData.PlayerAlreadySneakedSettlements[players[0].HeroId] = new() { "settlement1Id", "settlement2Id" };
            interactionsPlayerData.PlayerAlreadySneakedSettlements[players[1].HeroId] = new() { "settlement2Id", "settlement3Id" };
            interactionsPlayerData.PlayerOrderedDrinkThisDayInSettlement[players[0].HeroId] = "settlement1Id";
            interactionsPlayerData.PlayerOrderedDrinkThisDayInSettlement[players[1].HeroId] = "settlement2Id";
            interactionsPlayerData.PlayerHasBoughtTunToParty[players[0].HeroId] = true;
            interactionsPlayerData.PlayerHasBoughtTunToParty[players[1].HeroId] = false;
            interactionsPlayerData.PlayerHasMetRansomBroker[players[0].HeroId] = false;
            interactionsPlayerData.PlayerHasMetRansomBroker[players[1].HeroId] = true;
            interactionsPlayerData.PlayerHasMetHermit[players[0].HeroId] = true;
            interactionsPlayerData.PlayerHasMetHermit[players[1].HeroId] = false;

            var tradePlayerData = new TradePlayerData(new(), new(), new(), new());
            tradePlayerData.PlayerSettlementBribePaid[players[0].HeroId] = new() { ["settlement1Id"] = 0, ["settlement2Id"] = 1000 };
            tradePlayerData.PlayerSettlementBribePaid[players[1].HeroId] = new() { ["settlement2Id"] = 3200, ["settlement3Id"] = 123 };

            var meetingTimes = new Dictionary<string, Dictionary<string, long>>
            {
                ["MyHero1"] = new Dictionary<string, long>
                {
                    ["lord_6_1"] = 1351,
                },
            };

            ICoopSession sessionData = new CoopSession(
                "SaveManagerTest",
                players,
                new CraftingPlayerData(new(), new(), new()),
                new WorkshopPlayerData(new()),
                new CaravansPlayerData(new(), new()),
                new AlleyPlayerData(new()),
                interactionsPlayerData,
                tradePlayerData,
                new InventoryPlayerData(new(), new()),
                new HeroMeetingData(meetingTimes),
                new AgingPlayerData(new()));

            string saveFile = SAVE_PATH + sessionData.UniqueGameId;

            // Execution
            saveManager.SaveCoopSession(saveFile, sessionData);

            ICoopSession savedSession = saveManager.LoadCoopSession(saveFile);

            // Verification
            // CoopSession/Player are plain data classes without value equality, so the round-tripped
            // session is compared field-by-field rather than via whole-object Assert.Equal.
            Assert.NotNull(savedSession);
            Assert.Equal(sessionData.UniqueGameId, savedSession.UniqueGameId);
            Assert.Equal(sessionData.Players.Length, savedSession.Players.Length);
            for (int i = 0; i < sessionData.Players.Length; i++)
            {
                Assert.Equal(sessionData.Players[i].ControllerId, savedSession.Players[i].ControllerId);
                Assert.Equal(sessionData.Players[i].HeroId, savedSession.Players[i].HeroId);
                Assert.Equal(sessionData.Players[i].MobilePartyId, savedSession.Players[i].MobilePartyId);
                Assert.Equal(sessionData.Players[i].ClanId, savedSession.Players[i].ClanId);

                var playerHeroId = sessionData.Players[i].HeroId;

                Assert.Equal(sessionData.InteractionsPlayerData.PlayerAlreadySneakedSettlements[playerHeroId], savedSession.InteractionsPlayerData.PlayerAlreadySneakedSettlements[playerHeroId]);
                Assert.Equal(sessionData.InteractionsPlayerData.PlayerOrderedDrinkThisDayInSettlement[playerHeroId], savedSession.InteractionsPlayerData.PlayerOrderedDrinkThisDayInSettlement[playerHeroId]);
                Assert.Equal(sessionData.InteractionsPlayerData.PlayerHasBoughtTunToParty[playerHeroId], savedSession.InteractionsPlayerData.PlayerHasBoughtTunToParty[playerHeroId]);
                Assert.Equal(sessionData.InteractionsPlayerData.PlayerHasMetRansomBroker[playerHeroId], savedSession.InteractionsPlayerData.PlayerHasMetRansomBroker[playerHeroId]);
                Assert.Equal(sessionData.InteractionsPlayerData.PlayerHasMetHermit[playerHeroId], savedSession.InteractionsPlayerData.PlayerHasMetHermit[playerHeroId]);

                Assert.Equal(sessionData.TradePlayerData.PlayerSettlementBribePaid[playerHeroId], savedSession.TradePlayerData.PlayerSettlementBribePaid[playerHeroId]);
            }
            Assert.Equal(1351, savedSession.HeroMeetingData.PlayerLastMeetingTimes["MyHero1"]["lord_6_1"]);
        }

        [Fact]
        public void LoadSession_NoFile()
        {
            // Setup
            var saveManager = container.Resolve<ICoopSaveManager>();
            string saveFile = SAVE_PATH + "IDontExist.json";

            // Execution
            ICoopSession savedSession = saveManager.LoadCoopSession(saveFile);

            // Verification
            Assert.Null(savedSession);
        }

        [Fact]
        public void LoadSession_NoFile_LogsNothing()
        {
            var saveManager = container.Resolve<ICoopSaveManager>();
            string saveName = NewSaveName();

            ICoopSession? session = null;
            var logs = CaptureLogs(saveName, () => session = saveManager.LoadCoopSession(saveName));

            Assert.Null(session);
            Assert.Empty(logs);
        }

        [Fact]
        public void LoadSession_TruncatedFile_LogsErrorAndWritesNoFile()
        {
            var saveManager = container.Resolve<ICoopSaveManager>();
            string saveName = NewSaveName();

            try
            {
                byte[] truncated = Encoding.UTF8.GetBytes("{\"UniqueGameId\": \"" + saveName + "\", \"Players\": [");
                string path = WriteSessionFile(saveManager, saveName, truncated);

                ICoopSession? session = null;
                var logs = CaptureLogs(saveName, () => session = saveManager.LoadCoopSession(saveName));

                Assert.Null(session);
                Assert.Contains(logs, log => log.Contains("could not be read") && log.Contains("JsonException"));
                Assert.Single(Directory.GetFiles(saveManager.DefaultPath, saveName + "*"));
                Assert.Equal(truncated, File.ReadAllBytes(path));
            }
            finally
            {
                DeleteSessionFiles(saveManager, saveName);
            }
        }

        [Fact]
        public void LoadSession_EmptyFile_LogsError()
        {
            var saveManager = container.Resolve<ICoopSaveManager>();
            string saveName = NewSaveName();

            try
            {
                WriteSessionFile(saveManager, saveName, Array.Empty<byte>());

                ICoopSession? session = null;
                var logs = CaptureLogs(saveName, () => session = saveManager.LoadCoopSession(saveName));

                Assert.Null(session);
                Assert.Contains(logs, log => log.Contains("could not be read"));
            }
            finally
            {
                DeleteSessionFiles(saveManager, saveName);
            }
        }

        [Fact]
        public void LoadSession_JsonNull_LogsError()
        {
            var saveManager = container.Resolve<ICoopSaveManager>();
            string saveName = NewSaveName();

            try
            {
                WriteSessionFile(saveManager, saveName, Encoding.UTF8.GetBytes("null"));

                ICoopSession? session = null;
                var logs = CaptureLogs(saveName, () => session = saveManager.LoadCoopSession(saveName));

                Assert.Null(session);
                Assert.Contains(logs, log => log.Contains("contains only null"));
            }
            finally
            {
                DeleteSessionFiles(saveManager, saveName);
            }
        }

        [Fact]
        public void LoadSession_OldSchemaWithoutPlayers_WarnsAndReturnsSession()
        {
            var saveManager = container.Resolve<ICoopSaveManager>();
            string saveName = NewSaveName();

            try
            {
                // Shape written by mod builds that stored ControlledEntityMap instead of Players
                WriteSessionFile(saveManager, saveName, Encoding.UTF8.GetBytes("{\"UniqueGameId\": \"" + saveName + "\", \"ControlledEntityMap\": {}}"));

                ICoopSession? session = null;
                var logs = CaptureLogs(saveName, () => session = saveManager.LoadCoopSession(saveName));

                Assert.NotNull(session);
                Assert.Null(session.Players);
                Assert.Contains(logs, log => log.Contains("has no Players list"));
            }
            finally
            {
                DeleteSessionFiles(saveManager, saveName);
            }
        }

        [Fact]
        public void LoadSession_EmptyPlayersArray_LogsLoadedLine()
        {
            var saveManager = container.Resolve<ICoopSaveManager>();
            string saveName = NewSaveName();

            try
            {
                saveManager.SaveCoopSession(saveName, NewSession(saveName));

                ICoopSession? session = null;
                var logs = CaptureLogs(saveName, () => session = saveManager.LoadCoopSession(saveName));

                Assert.NotNull(session);
                Assert.Empty(session.Players);
                Assert.Contains(logs, log => log.Contains("0 saved player registrations"));
                Assert.DoesNotContain(logs, log => log.Contains("has no Players list"));
            }
            finally
            {
                DeleteSessionFiles(saveManager, saveName);
            }
        }

        [Fact]
        public void LoadSession_FileLockedByAnotherHandle_LogsErrorWithoutThrowing()
        {
            var saveManager = container.Resolve<ICoopSaveManager>();
            string saveName = NewSaveName();

            try
            {
                string path = WriteSessionFile(saveManager, saveName, Encoding.UTF8.GetBytes("{\"Players\": []}"));

                ICoopSession? session = null;
                string[] logs;
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    logs = CaptureLogs(saveName, () => session = saveManager.LoadCoopSession(saveName));
                }

                Assert.Null(session);
                Assert.Contains(logs, log => log.Contains("could not be read") && log.Contains("IOException"));
            }
            finally
            {
                DeleteSessionFiles(saveManager, saveName);
            }
        }

        [Fact]
        public void LoadSession_ValidFile_LogsLoadedLineWithPlayerCount()
        {
            var saveManager = container.Resolve<ICoopSaveManager>();
            string saveName = NewSaveName();

            try
            {
                var session = NewSession(saveName,
                    new Player("MyPlayer1", "MyHero1", "MyParty1", "MyClan1", "MyCharacter1"),
                    null,
                    new Player("MyPlayer2", "MyHero2", "MyParty2", "MyClan2", "MyCharacter2"));
                saveManager.SaveCoopSession(saveName, session);

                ICoopSession? loaded = null;
                var logs = CaptureLogs(saveName, () => loaded = saveManager.LoadCoopSession(saveName));

                Assert.NotNull(loaded);
                Assert.Contains(logs, log => log.Contains("loaded from") && log.Contains("2 saved player registrations"));
            }
            finally
            {
                DeleteSessionFiles(saveManager, saveName);
            }
        }

        private static string NewSaveName() => "SessionLoadTest_" + Guid.NewGuid().ToString("N");

        private static ICoopSession NewSession(string saveName, params Player?[] players)
        {
            var empty = CoopSession.Empty;
            return new CoopSession(
                saveName,
                players,
                empty.CraftingPlayerData,
                empty.WorkshopPlayerData,
                empty.CaravansPlayerData,
                empty.AlleyPlayerData,
                empty.InteractionsPlayerData,
                empty.TradePlayerData,
                empty.InventoryPlayerData,
                empty.HeroMeetingData,
                empty.AgingPlayerData);
        }

        private static string WriteSessionFile(ICoopSaveManager saveManager, string saveName, byte[] contents)
        {
            Directory.CreateDirectory(saveManager.DefaultPath);
            string path = saveManager.DefaultPath + saveName + saveManager.FileType;
            File.WriteAllBytes(path, contents);
            return path;
        }

        // Other test classes log in parallel, so only lines naming this test's file are kept
        private static string[] CaptureLogs(string marker, Action action)
        {
            var messages = new ConcurrentQueue<string>();
            Action<string> callback = message =>
            {
                if (message.Contains(marker)) messages.Enqueue(message);
            };

            OutputSinkManager.AddLogCallback(callback);
            try
            {
                action();
            }
            finally
            {
                OutputSinkManager.RemoveLogCallback(callback);
            }

            return messages.ToArray();
        }

        private static void DeleteSessionFiles(ICoopSaveManager saveManager, string saveName)
        {
            if (Directory.Exists(saveManager.DefaultPath) == false) return;

            foreach (var file in Directory.GetFiles(saveManager.DefaultPath, saveName + "*"))
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }
        }
    }

    public sealed class BattlePreparationSaveCopyTests
    {
        [Fact]
        public void BattleHistorySurvivesActualWriteLoadAndLeaseCopyWithoutChangingOriginalLifetime()
        {
            var saveManager = new CoopSaveManager();var saveName = "BattleHistoryCopy_" + Guid.NewGuid().ToString("N");
            var party = new BattleObjectReference("party", 12);
            var key = new BattlePreparationKey("original-room", "native-campaign", new BattleObjectReference("event", 11), "room-claim", 7);
            var row = new BattlePreparationRecord(key, new BattlePreparationInput(party, new[] { new BattleCohortMember("A", party, "reservation") }),
                BattlePreparationPhase.Retired, 3, new BattlePreparedPayload(Array.Empty<BattlePreparedParty>(), Array.Empty<BattlePreparedSide>(), new byte[] { 1, 2, 3 }), null);
            var e = CoopSession.Empty;
            var state = new BattlePreparationState(records: new[] { row });
            var provider = new CoopSessionProvider { CoopSession = new CoopSession(saveName, e.Players, e.CraftingPlayerData, e.WorkshopPlayerData,
                e.CaravansPlayerData, e.AlleyPlayerData, e.InteractionsPlayerData, e.TradePlayerData, e.InventoryPlayerData,
                e.HeroMeetingData, e.AgingPlayerData, battlePreparations: state) };
            var broker = new TestMessageBroker();var players = new Mock<IPlayerManager>();players.SetupGet(value => value.Players).Returns(Array.Empty<Player>());
            using var handler = new SaveGameHandler(broker, saveManager, provider, players.Object, Mock.Of<IPlayerPartyRestorer>(), Mock.Of<INetwork>(), Mock.Of<IObjectManager>());
            try
            {
                broker.Publish(this, new GameSaved(saveName));
                Assert.NotSame(state, provider.CoopSession.BattlePreparations);
                Assert.Equal(7, Assert.Single(saveManager.LoadCoopSession(saveName).BattlePreparations.Records).Key.ClaimGeneration);
                provider.CoopSession = CoopSession.Empty;
                broker.Publish(this, new GameLoaded(saveName));
                Assert.Equal("original-room", Assert.Single(provider.CoopSession.BattlePreparations.Records).Key.RoomId);
                var copied = (ICoopSession)typeof(SaveGameHandler).GetMethod("CopyWithLease", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null,
                    new object[] { provider.CoopSession, new AuthorityLease("fresh-room", "next", "A", 9), "A" });
                Assert.Equal("fresh-room", copied.RoomId);Assert.Equal("original-room", Assert.Single(copied.BattlePreparations.Records).Key.RoomId);
                Assert.NotSame(provider.CoopSession.BattlePreparations, copied.BattlePreparations);
            }
            finally { File.Delete(Path.Combine(saveManager.DefaultPath, saveName + saveManager.FileType)); }
        }

        [Fact]
        public void ActiveBattleLoadRetainsSavedEvidenceAndRefusesUnsupportedRestore()
        {
            var e = CoopSession.Empty;var saveManager = new CoopSaveManager();var finalizer = new Mock<ICoopFinalizer>();
            var saveName = "UnsupportedBattleLoad_" + Guid.NewGuid().ToString("N");
            var party = new BattleObjectReference("party", 12);
            var row = new BattlePreparationRecord(new BattlePreparationKey("room", "campaign", new BattleObjectReference("event", 11), "claim", 7),
                new BattlePreparationInput(party, new[] { new BattleCohortMember("A", party, "reservation") }), BattlePreparationPhase.Unknown, 3, null, "partial");
            var saved = new CoopSession("save", e.Players, e.CraftingPlayerData, e.WorkshopPlayerData, e.CaravansPlayerData, e.AlleyPlayerData,
                e.InteractionsPlayerData, e.TradePlayerData, e.InventoryPlayerData, e.HeroMeetingData, e.AgingPlayerData,
                battlePreparations: new BattlePreparationState(records: new[] { row }));
            saveManager.SaveCoopSession(saveName, saved);
            var provider = new CoopSessionProvider();var original = provider.CoopSession;var broker = new TestMessageBroker();
            using var handler = new SaveGameHandler(broker, saveManager, provider, Mock.Of<IPlayerManager>(), Mock.Of<IPlayerPartyRestorer>(),
                Mock.Of<INetwork>(), Mock.Of<IObjectManager>(), finalizer: finalizer.Object);
            try
            {
                broker.Publish(this, new GameLoaded(saveName));
                Assert.Same(original, provider.CoopSession);Assert.Equal("partial", Assert.Single(saved.BattlePreparations.Records).Failure);
                Assert.Equal("partial", Assert.Single(saveManager.LoadCoopSession(saveName).BattlePreparations.Records).Failure);
                finalizer.Verify(value => value.Finalize(It.Is<string>(text => text.Contains("NOT_IMPLEMENTED"))), Times.Once);
            }
            finally { File.Delete(Path.Combine(saveManager.DefaultPath, saveName + saveManager.FileType)); }
        }

    }
}
