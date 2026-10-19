using Common;
using Common.Messaging;
using Coop.Core.Server;
using Coop.Core.Server.States;
using Coop.Core.Server.States.Messages;
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
using System;
using System.Collections.Generic;
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
            var emptyLordParties = new Mock<IEmptyLordPartyResetter>();
            var game = new Mock<IGameStateInterface>();
            var logic = new Mock<IServerLogic>();
            var calls = new List<string>();
            registry.Setup(r => r.RegisterAllGameObjects()).Callback(() => calls.Add("register"));
            registry.Setup(r => r.PatchLifetimes()).Callback(() => calls.Add("patch"));
            local.Setup(l => l.RegisterNativePlayer()).Callback(() => calls.Add("bind-native-player"));
            emptyLordParties.Setup(r => r.ResetEmptyLordParties()).Callback(() => calls.Add("reset-empty-lords"));
            broker.Setup(b => b.Publish(It.IsAny<object>(), It.IsAny<CampaignInitializationCompleted>()))
                .Callback(() => calls.Add("initialization-complete"));
            logic.Setup(l => l.SetState<ServerRunningState>()).Callback(() => calls.Add("running"));
            using var state = new InitialServerState(logic.Object, broker.Object, registry.Object,
                cleaner.Object, emptyLordParties.Object, Mock.Of<IModuleInfoProvider>(),
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
            Assert.Equal(new[] { "register", "patch", "bind-native-player", "reset-empty-lords", "initialization-complete", "running" }, calls);
        }
        finally { ModInformation.SessionMode = previous; }
    }

    [Fact]
    public void RegistrationFailureDoesNotPublishInitializationCompleteOrStartRunning()
    {
        var previous = ModInformation.SessionMode;
        ModInformation.SessionMode = SessionMode.ListenAuthority;
        try
        {
            var broker = new Mock<IMessageBroker>();
            var registry = new Mock<IRegistryManager>();
            registry.Setup(r => r.RegisterAllGameObjects()).Throws(new InvalidOperationException("registry failure"));
            var local = new Mock<ILocalPlayerContext>();
            var logic = new Mock<IServerLogic>();
            using var state = new InitialServerState(logic.Object, broker.Object, registry.Object,
                Mock.Of<IMapEventLoadCleaner>(), Mock.Of<IEmptyLordPartyResetter>(), Mock.Of<IModuleInfoProvider>(),
                Mock.Of<IModuleValidator>(), Mock.Of<IGameStateInterface>(), Mock.Of<ILoadingInterface>(), local.Object);

            Assert.Throws<InvalidOperationException>(() => state.Handle_CampaignReady(
                new MessagePayload<CampaignReady>(null, new CampaignReady())));

            broker.Verify(b => b.Publish(It.IsAny<object>(), It.IsAny<CampaignInitializationCompleted>()), Times.Never);
            local.Verify(l => l.RegisterNativePlayer(), Times.Never);
            logic.Verify(l => l.SetState<ServerRunningState>(), Times.Never);
        }
        finally { ModInformation.SessionMode = previous; }
    }
}
