using Common;
using Common.Messaging;
using Coop.Core.Server;
using Coop.Core.Server.States;
using GameInterface.Registry;
using GameInterface.Services.GameState.Interfaces;
using GameInterface.Services.GameState.Messages;
using GameInterface.Services.MapEvents;
using GameInterface.Services.MobileParties;
using GameInterface.Services.MobileParties.Messages;
using GameInterface.Services.Modules;
using GameInterface.Services.Modules.Validators;
using GameInterface.Services.Players;
using GameInterface.Services.UI.Interfaces;
using Moq;
using Xunit;

namespace Coop.Tests.Server.States;

public class ListenAuthorityStartupTests
{
    [Fact]
    public void NativeWorldRegistersOnceWithoutDedicatedCleanupOrDebugAutoLoad()
    {
        var previous = ModInformation.SessionMode;
        ModInformation.SessionMode = SessionMode.ListenAuthority;
        try
        {
            var broker = new Mock<IMessageBroker>();
            var registry = new Mock<IRegistryManager>();
            var cleaner = new Mock<IMapEventLoadCleaner>();
            var local = new Mock<ILocalPlayerContext>();
            var game = new Mock<IGameStateInterface>();
            var logic = new Mock<IServerLogic>();
            using var state = new InitialServerState(logic.Object, broker.Object, registry.Object,
                cleaner.Object, Mock.Of<IEmptyLordPartyResetter>(), Mock.Of<IModuleInfoProvider>(),
                Mock.Of<IModuleValidator>(), game.Object, Mock.Of<ILoadingInterface>(), local.Object);
            state.Start();
            var ready = new MessagePayload<CampaignReady>(null, new CampaignReady());
            state.Handle_CampaignReady(ready);
            state.Handle_CampaignReady(ready);
            registry.Verify(r => r.RegisterAllGameObjects(), Times.Once);
            local.Verify(l => l.RegisterNativePlayer(), Times.Once);
            cleaner.Verify(c => c.FinalizePlayerMapEvents(), Times.Never);
            broker.Verify(b => b.Publish(It.IsAny<object>(), It.IsAny<RemoveMainParty>()), Times.Never);
            game.Verify(g => g.LoadGame(It.IsAny<string>()), Times.Never);
            logic.Verify(l => l.SetState<ServerRunningState>(), Times.Once);
        }
        finally { ModInformation.SessionMode = previous; }
    }
}
