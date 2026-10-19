using Common.Messaging;
using Common.Network;
using Coop.Core.Server.Services.ItemRosters.Handlers;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.ItemObjects;
using GameInterface.Services.ObjectManager;

namespace Coop.Core.Client.Services.ItemRosters.Handlers;

public sealed class CampaignItemRosterAuthorityHandler : ItemRosterMessageHandler
{
    public CampaignItemRosterAuthorityHandler(IMessageBroker broker, INetwork network,
        IObjectManager objectManager, ItemObjectRegistry itemObjectRegistry, ICampaignAuthorityContext authorityContext)
        : base(broker, network, objectManager, null, itemObjectRegistry, authorityContext)
    {
    }
}
