using Common.Logging;
using Common.Messaging;
using Common;
using GameInterface.Registry;
using GameInterface.Services.GameState.Interfaces;
using GameInterface.Services.GameState.Messages;
using GameInterface.Services.MapEvents;
using GameInterface.Services.MobileParties;
using GameInterface.Services.MobileParties.Messages;
using GameInterface.Services.Modules;
using GameInterface.Services.Modules.Validators;
using GameInterface.Services.UI.Interfaces;
using Serilog;
using GameInterface.Services.Players;
using Coop.Core.Server.States.Messages;

namespace Coop.Core.Server.States;

/// <summary>
/// State represting the server has just started
/// </summary>
public class InitialServerState : ServerStateBase
{
    private static readonly ILogger Logger = LogManager.GetLogger<InitialServerState>();

    private readonly IMessageBroker messageBroker;
    private readonly IRegistryManager registryManager;
    private readonly IMapEventLoadCleaner mapEventLoadCleaner;
    private readonly IEmptyLordPartyResetter emptyLordPartyResetter;
    private readonly IModuleValidator moduleValidator;
    private readonly IGameStateInterface gameStateInterface;
    private readonly ILoadingInterface loadingInterface;
    private readonly IModuleInfoProvider moduleInfoProvider;
    private readonly ILocalPlayerContext localPlayer;
    private bool campaignRegistered;

    public InitialServerState(
        IServerLogic context,
        IMessageBroker messageBroker,
        IRegistryManager registryManager,
        IMapEventLoadCleaner mapEventLoadCleaner,
        IEmptyLordPartyResetter emptyLordPartyResetter,
        IModuleInfoProvider moduleInfoProvider,
        IModuleValidator moduleValidator,
        IGameStateInterface gameStateInterface,
        ILoadingInterface loadingInterface,
        ILocalPlayerContext localPlayer = null) :
        base(context)
    {
        this.messageBroker = messageBroker;
        this.registryManager = registryManager;
        this.mapEventLoadCleaner = mapEventLoadCleaner;
        this.emptyLordPartyResetter = emptyLordPartyResetter;
        this.moduleValidator = moduleValidator;
        this.gameStateInterface = gameStateInterface;
        this.loadingInterface = loadingInterface;
        this.moduleInfoProvider = moduleInfoProvider;
        this.localPlayer = localPlayer;
        messageBroker.Subscribe<CampaignReady>(Handle_CampaignReady);
    }

    public override void Dispose()
    {
        messageBroker.Unsubscribe<CampaignReady>(Handle_CampaignReady);
    }

    internal void Handle_CampaignReady(MessagePayload<CampaignReady> payload)
    {
        if (campaignRegistered) return;
        // Coop does not support DLC. Warn the host if any is enabled before the server becomes
        // joinable; clients with DLC enabled are rejected separately during module validation.
        if (!moduleValidator.ValidateNoDlc(moduleInfoProvider.GetModuleInfos(), out var dlcError))
        {
            Logger.Error("Hosting with unsupported modules enabled. {error}", dlcError);
        }

        // Dedicated worlds discard the placeholder player; listen worlds keep the native actor.
        if (ModInformation.IsDedicatedAuthority)
            messageBroker.Publish(this, new RemoveMainParty());

        loadingInterface.SetLoadingMessage("Hosting Coop Server", "Registering campaign objects...");

        // Register the existing world before binding its local actor.
        registryManager.RegisterAllGameObjects();

        loadingInterface.SetLoadingMessage("Hosting Coop Server", "Applying synced object lifetimes...");
        registryManager.PatchLifetimes();
        if (ModInformation.IsListenAuthority)
            localPlayer.RegisterNativePlayer();
        else
            mapEventLoadCleaner.FinalizePlayerMapEvents();
        emptyLordPartyResetter.ResetEmptyLordParties();
        campaignRegistered = true;
        messageBroker.Publish(this, new CampaignInitializationCompleted());
        Logic.SetState<ServerRunningState>();
    }

    public override void Start()
    {
#if DEBUG
        if (ModInformation.IsListenAuthority) return;
        loadingInterface.ShowLoadingScreen();
        gameStateInterface.LoadGame("MP");
#endif
    }

    public override void Stop()
    {
    }
}
