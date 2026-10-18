using Common;
using Common.Logging;
using Common.Messaging;
using GameInterface.CoopSessionData.Messages;
using GameInterface.Services.Alleys.Messages;
using GameInterface.Services.Caravans.Messages;
using GameInterface.Services.Heroes.Messages;
using GameInterface.Services.Inventory.Messages;
using GameInterface.Services.Inventory.TradeSkills.Messages;
using GameInterface.Services.MobileParties.Messages;
using GameInterface.Services.Smithing.Messages;
using GameInterface.Services.Workshops.Messages;
using Serilog;

namespace GameInterface.CoopSessionData;

internal class CoopSessionUpdateHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<CoopSessionUpdateHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly ICoopSessionProvider coopSessionProvider;

    public CoopSessionUpdateHandler(
        IMessageBroker messageBroker,
        ICoopSessionProvider coopSessionProvider)
    {
        this.messageBroker = messageBroker;
        this.coopSessionProvider = coopSessionProvider;

        messageBroker.Subscribe<NetworkUpdateCoopSession>(Handle_NetworkUpdateCoopSession);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<NetworkUpdateCoopSession>(Handle_NetworkUpdateCoopSession);
    }

    private void Handle_NetworkUpdateCoopSession(MessagePayload<NetworkUpdateCoopSession> obj)
    {
        if (ModInformation.IsServer) return;

        var session = obj.What.UpdatedSession;
        InitializeSessionReaders(messageBroker, this, session);
    }

    internal static void InitializeSessionReaders(IMessageBroker messageBroker, object source, Save.Data.ICoopSession session)
    {
        // Load updated session data
        messageBroker.Publish(source, new InitializeClientCraftingData(session.CraftingPlayerData));
        messageBroker.Publish(source, new InitializeClientWorkshopData(session.WorkshopPlayerData));
        messageBroker.Publish(source, new InitializeClientCaravansData(session.CaravansPlayerData));
        messageBroker.Publish(source, new InitializeClientAlleyData(session.AlleyPlayerData));
        messageBroker.Publish(source, new InitializeClientInteractionsData(session.InteractionsPlayerData));
        messageBroker.Publish(source, new InitializeClientTradeData(session.TradePlayerData));
        messageBroker.Publish(source, new InitializeClientInventoryData(session.InventoryPlayerData));
        messageBroker.Publish(source, new InitializeClientHeroMeetingData(session.HeroMeetingData));
        messageBroker.Publish(source, new InitializeClientAgingData(session.AgingPlayerData));
    }
}
