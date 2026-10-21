using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Util;
using GameInterface.Services.MobileParties.Extensions;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.MobileParties.Messages;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.PartyComponents.Data;
using GameInterface.Services.PartyComponents.Messages;
using HarmonyLib;
using Serilog;
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace GameInterface.Services.PartyComponents.Handlers;

internal class PartyComponentHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<PartyComponentHandler>();

    private static readonly FieldInfo HomeSettlementField =
        AccessTools.Field(typeof(PatrolPartyComponent), "_homeSettlement");
    private static readonly MethodInfo PatrolInitializeProperties =
        AccessTools.Method(typeof(PatrolPartyComponent), "InitializePartyComponentProperties");
    private static readonly PropertyInfo GarrisonSettlementProperty =
        AccessTools.Property(typeof(GarrisonPartyComponent), nameof(GarrisonPartyComponent.Settlement));

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;

    private readonly Type[] partyTypes = new Type[]
    {
        typeof(BanditPartyComponent),    // 0
        typeof(CaravanPartyComponent),   // 1
        typeof(CustomPartyComponent),    // 2
        typeof(GarrisonPartyComponent),  // 3
        typeof(LordPartyComponent),      // 4
        typeof(MilitiaPartyComponent),   // 5
        typeof(PatrolPartyComponent),    // 6
        typeof(VillagerPartyComponent),  // 7
    };

    public PartyComponentHandler(IMessageBroker messageBroker, INetwork network, IObjectManager objectManager)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;
        messageBroker.Subscribe<PartyComponentCreated>(Handle);
        messageBroker.Subscribe<NetworkCreatePartyComponent>(Handle);

        messageBroker.Subscribe<PartyComponentMobilePartyUpdated>(Handle_PartyComponentMobilePartyUpdated);
        messageBroker.Subscribe<NetworkPartyComponentMobilePartyUpdated>(Handle_NetworkPartyComponentMobilePartyUpdated);

        messageBroker.Subscribe<PartyComponentLeaderChanged>(Handle_PartyComponentLeaderChanged);
        messageBroker.Subscribe<NetworkPartyComponentLeaderChanged>(Handle_NetworkPartyComponentLeaderChanged);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<PartyComponentCreated>(Handle);
        messageBroker.Unsubscribe<NetworkCreatePartyComponent>(Handle);

        messageBroker.Unsubscribe<PartyComponentMobilePartyUpdated>(Handle_PartyComponentMobilePartyUpdated);
        messageBroker.Unsubscribe<NetworkPartyComponentMobilePartyUpdated>(Handle_NetworkPartyComponentMobilePartyUpdated);

        messageBroker.Unsubscribe<PartyComponentLeaderChanged>(Handle_PartyComponentLeaderChanged);
        messageBroker.Unsubscribe<NetworkPartyComponentLeaderChanged>(Handle_NetworkPartyComponentLeaderChanged);
    }

    private void Handle(MessagePayload<PartyComponentCreated> payload)
    {
        if (!CampaignSimulationAuthorityPolicy.CanPublishAppliedWorldResult()) return;
        uint settlementHandle = 0;
        if (payload.What.SettlementId != null &&
            (!objectManager.TryGetObjectWithLogging<Settlement>(payload.What.SettlementId, out var settlement) ||
             !objectManager.TryGetHandleWithLogging(settlement, out settlementHandle))) return;

        if (!objectManager.AddNewObject(payload.What.Instance, out var id)) return;
        if (!objectManager.TryGetHandleWithLogging(payload.What.Instance, out var handle)) return;

        var typeIndex = partyTypes.IndexOf(payload.What.Instance.GetType());
        var data = new PartyComponentData(typeIndex, id, handle)
        {
            HomeSettlementId = settlementHandle,
            IsNaval = payload.What.IsNaval,
        };

        if (!CampaignSimulationAuthorityPolicy.CanSendWorldResult()) return;
        network.SendAll(new NetworkCreatePartyComponent(data));
    }

    private void Handle(MessagePayload<NetworkCreatePartyComponent> payload)
    {
        var data = payload.What.Data;

        GameThread.Run(() =>
        {
            using var replay = CampaignExecutionScope.Enter(CampaignExecutionPhase.Replay);
            try
            {
                var obj = CreatePartyComponent(data);

                switch (data.TypeIndex)
                {
                    case 0:
                        if (!objectManager.AddExisting(data.Id, obj, data.Handle))
                        { MessageApplyScope.Fail("Failed to register PartyComponent " + data.Id); return; }
                        break;
                    case 1:
                        if (!objectManager.AddExisting(data.Id, obj, data.Handle))
                        { MessageApplyScope.Fail("Failed to register PartyComponent " + data.Id); return; }
                        break;
                    case 2:
                        if (!objectManager.AddExisting(data.Id, obj, data.Handle))
                        { MessageApplyScope.Fail("Failed to register PartyComponent " + data.Id); return; }
                        break;
                    case 3:
                        if (!objectManager.AddExisting(data.Id, obj, data.Handle))
                        { MessageApplyScope.Fail("Failed to register PartyComponent " + data.Id); return; }

                        var garrisonComponent = (GarrisonPartyComponent)obj;

                        if (data.HomeSettlementId == 0) break;

                        // Reconstitute the Settlement link that is normally set in the constructor.
                        // It is bundled in the creation message to avoid any dependency on AutoSync
                        // field-update ordering — without it GarrisonPartyComponent.PartyOwner
                        // (Settlement.OwnerClan.Leader) throws and crashes the nameplate VM.
                        if (!objectManager.TryGetObject(data.HomeSettlementId, out Settlement garrisonSettlement))
                        {
                            MessageApplyScope.Fail("Missing garrison Settlement " + data.HomeSettlementId);
                            Logger.Warning(
                                "GarrisonPartyComponent {Id}: could not find Settlement '{SettlementId}' in ObjectManager; " +
                                "Settlement will not be set on client",
                                data.Id, data.HomeSettlementId);
                            break;
                        }

                        using (new AllowedThread())
                        {
                            GarrisonSettlementProperty.SetValue(garrisonComponent, garrisonSettlement);

                            // Vanilla OnInitialize sets this back-link so the town resolves its garrison.
                            if (garrisonSettlement.Town != null)
                            {
                                garrisonSettlement.Town.GarrisonPartyComponent = garrisonComponent;
                            }
                        }

                        break;
                    case 4:
                        if (!objectManager.AddExisting(data.Id, obj, data.Handle))
                        { MessageApplyScope.Fail("Failed to register PartyComponent " + data.Id); return; }
                        break;
                    case 5:
                        if (!objectManager.AddExisting(data.Id, obj, data.Handle))
                        { MessageApplyScope.Fail("Failed to register PartyComponent " + data.Id); return; }

                        var militiaComponent = (MilitiaPartyComponent)obj;

                        if (data.HomeSettlementId == 0) break;

                        // Reconstitute the Settlement link that is normally set in the constructor,
                        // bundled in the creation message like the garrison's. Without it the militia's
                        // PartyOwner and MapFaction stay null, and once the settlement is besieged
                        // vanilla's siege menu paths (Town.GetDefenderParties) throw on the party
                        // every frame, freezing every client at the siege menus.
                        if (!objectManager.TryGetObject(data.HomeSettlementId, out Settlement militiaSettlement))
                        {
                            MessageApplyScope.Fail("Missing militia Settlement " + data.HomeSettlementId);
                            Logger.Warning(
                                "MilitiaPartyComponent {Id}: could not find Settlement '{SettlementId}' in ObjectManager; " +
                                "Settlement will not be set on client",
                                data.Id, data.HomeSettlementId);
                            break;
                        }

                        using (new AllowedThread())
                        {
                            militiaComponent.Settlement = militiaSettlement;

                            // Vanilla OnInitialize sets this back-link so the settlement resolves its militia.
                            militiaSettlement.MilitiaPartyComponent = militiaComponent;
                        }

                        break;
                    case 6:
                        if (!objectManager.AddExisting(data.Id, obj, data.Handle))
                        { MessageApplyScope.Fail("Failed to register PartyComponent " + data.Id); return; }

                        var patrolComponent = (PatrolPartyComponent)obj;

                        if (data.HomeSettlementId == 0) break;

                        // Reconstitute fields that are normally set in the constructor and
                        // InitializePartyComponentProperties. These are bundled in the creation
                        // message to avoid any dependency on AutoSync field-update ordering.
                        if (!objectManager.TryGetObject(data.HomeSettlementId, out Settlement homeSettlement))
                        {
                            MessageApplyScope.Fail("Missing patrol Settlement " + data.HomeSettlementId);
                            Logger.Warning(
                                "PatrolPartyComponent {Id}: could not find Settlement '{SettlementId}' in ObjectManager; " +
                                "Settlement.PatrolParty will not be set on client",
                                data.Id, data.HomeSettlementId);
                            break;
                        }

                        using (new AllowedThread())
                        {
                            HomeSettlementField.SetValue(patrolComponent, homeSettlement);
                            // Reflect IsNaval through its private setter so that IsNaval is correct
                            // before InitializePartyComponentProperties checks it.
                            AccessTools.Property(typeof(PatrolPartyComponent), nameof(PatrolPartyComponent.IsNaval))
                                .SetValue(patrolComponent, data.IsNaval);
                            PatrolInitializeProperties.Invoke(patrolComponent, null);
                        }

                        break;
                    case 7:
                        if (!objectManager.AddExisting(data.Id, obj, data.Handle))
                        { MessageApplyScope.Fail("Failed to register PartyComponent " + data.Id); return; }
                        break;
                    default:
                        MessageApplyScope.Fail("Invalid PartyComponent type " + data.TypeIndex);
                        Logger.Error(
                            "Invalid TypeIndex {TypeIndex}. Valid range: [0..{MaxIndex}] (0=Bandit,1=Caravan,2=Custom,3=Garrison,4=Lord,5=Militia,6=Patrol,7=Villager). PacketId: {Id}",
                            data.TypeIndex,
                            partyTypes.Length - 1,
                            data.Id);
                        break;
                }
            }
            catch (Exception e)
            {
                MessageApplyScope.Fail(e.ToString());
                Logger.Error(e, "Failed to apply {Message}", nameof(NetworkCreatePartyComponent));
            }
        });
    }

    private PartyComponent CreatePartyComponent(PartyComponentData data)
    {
        if (data.TypeIndex != 0 || data.HomeSettlementId == 0)
        {
            return (PartyComponent)ObjectHelper.SkipConstructor(partyTypes[data.TypeIndex]);
        }

        if (!objectManager.TryGetObjectWithLogging(data.HomeSettlementId, out Settlement relatedSettlement))
        {
            MessageApplyScope.Fail("Missing bandit Settlement " + data.HomeSettlementId);
            return (PartyComponent)ObjectHelper.SkipConstructor(partyTypes[data.TypeIndex]);
        }

        using (new AllowedThread())
        {
            return new BanditPartyComponent(relatedSettlement, null);
        }
    }

    private void Handle_PartyComponentMobilePartyUpdated(MessagePayload<PartyComponentMobilePartyUpdated> payload)
    {
        if (!CampaignSimulationAuthorityPolicy.CanSendWorldResult()) return;
        var obj = payload.What;

        string partyComponentId = null;
        if (obj.Instance != null && !objectManager.TryGetIdWithLogging(obj.Instance, out partyComponentId))
            return;
        if (!objectManager.TryGetIdWithLogging(obj.MobileParty, out var partyId))
            return;

        var message = new NetworkPartyComponentMobilePartyUpdated(partyComponentId, partyId);
        network.SendAll(message);
    }

    private void Handle_NetworkPartyComponentMobilePartyUpdated(MessagePayload<NetworkPartyComponentMobilePartyUpdated> payload)
    {
        var obj = payload.What;

        // Re-resolve inside in case the object is removed concurrently.
        GameThread.Run(() =>
        {
            using var replay = CampaignExecutionScope.Enter(CampaignExecutionPhase.Replay);
            try
            {
                PartyComponent partyComponent = null;
                if (obj.InstanceId != null &&
                    !objectManager.TryGetObjectWithLogging(obj.InstanceId, out partyComponent))
                { MessageApplyScope.Fail("Missing PartyComponent " + obj.InstanceId); return; }

                if (!objectManager.TryGetObjectWithLogging<MobileParty>(obj.MobilePartyId, out var mobileParty))
                { MessageApplyScope.Fail("Missing MobileParty " + obj.MobilePartyId); return; }

                using (new AllowedThread())
                {
                    ApplyComponentBinding(Campaign.Current.CampaignObjectManager, mobileParty, partyComponent);
                }
            }
            catch (Exception e)
            {
                MessageApplyScope.Fail(e.ToString());
                Logger.Error(e, "Failed to apply {Message}", nameof(NetworkPartyComponentMobilePartyUpdated));
            }
        });
    }

    internal static void ApplyComponentBinding(CampaignObjectManager manager, MobileParty party, PartyComponent component)
    {
        if (component != null) component.MobileParty = party;
        if (ReferenceEquals(party._partyComponent, component)) return;
        bool published = manager.MobileParties.Any(value => ReferenceEquals(value, party));
        // Match SetPartyComponent's native partition bookkeeping without replaying
        // component Finish/Create/Initialize side effects on an observer. An unpublished
        // party is inserted into its partition by the original AddMobileParty, once.
        if (published) manager.BeforePartyComponentChanged(party);
        party._partyComponent = component;
        party.UpdatePartyComponentFlags();
        if (published) manager.AfterPartyComponentChanged(party);
        if (!published)
            Logger.Debug("Bound party component {PartyId} referenceHash {PartyReferenceHash} published {Published} component {ComponentType}",
                party.StringId, RuntimeHelpers.GetHashCode(party), published, component?.GetType().FullName);
    }

    private void Handle_PartyComponentLeaderChanged(MessagePayload<PartyComponentLeaderChanged> payload)
    {
        if (!CampaignSimulationAuthorityPolicy.CanSendWorldResult()) return;
        var obj = payload.What;

        var mobileParty = obj.Instance.MobileParty;
        if (mobileParty != null && mobileParty.IsPlayerParty())
        {
            messageBroker.Publish(this, new PartyLeaderChanged(mobileParty, obj.NewLeader));
            return;
        }

        if (!objectManager.TryGetIdWithLogging(obj.Instance, out var partyComponentId)) return;

        // NewLeader can be null (when a party is disbanded)
        string newLeaderId = null;
        if (obj.NewLeader != null && !objectManager.TryGetIdWithLogging(obj.NewLeader, out newLeaderId)) return;

        var message = new NetworkPartyComponentLeaderChanged(partyComponentId, newLeaderId);
        network.SendAll(message);
    }

    private void Handle_NetworkPartyComponentLeaderChanged(MessagePayload<NetworkPartyComponentLeaderChanged> payload)
    {
        var obj = payload.What;

        GameThread.Run(() =>
        {
            using var replay = CampaignExecutionScope.Enter(CampaignExecutionPhase.Replay);
            try
            {
                if (!objectManager.TryGetObjectWithLogging<PartyComponent>(obj.PartyComponentId, out var partyComponent))
                { MessageApplyScope.Fail("Missing PartyComponent " + obj.PartyComponentId); return; }

                Hero newLeader = null;
                if (obj.NewLeaderId != null && !objectManager.TryGetObjectWithLogging<Hero>(obj.NewLeaderId, out newLeader))
                { MessageApplyScope.Fail("Missing leader Hero " + obj.NewLeaderId); return; }

                using (new AllowedThread())
                {
                    partyComponent.OnChangePartyLeader(newLeader);
                    partyComponent.Party?.SetVisualAsDirty();
                }
            }
            catch (Exception e)
            {
                MessageApplyScope.Fail(e.ToString());
                Logger.Error(e, "Failed to apply {Message}", nameof(NetworkPartyComponentLeaderChanged));
            }
        });
    }
}
