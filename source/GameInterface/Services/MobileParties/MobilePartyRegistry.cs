using Common;
using Common.Messaging;
using Common.Network.Coalescing;
using Common.Util;
using GameInterface.Registry.Auto;
using GameInterface.Services.Entity;
using GameInterface.Services.CampaignAuthority.Lifecycle;
using GameInterface.Services.MobileParties.Messages;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.ObjectReadiness;
using static GameInterface.Services.ObjectManager.ObjectManager;
using HarmonyLib;
using Serilog;
using System;
using System.Collections.Generic;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Naval;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.ObjectSystem;

namespace GameInterface.Services.MobileParties;

/// <summary>
/// Registry for <see cref="MobileParty"/> objects
/// </summary>
internal class MobilePartyRegistry : AutoRegistryBase<MobileParty>
{
    public override bool Debug => false;

    private readonly IControllerIdProvider controllerIdProvider;
    private readonly IMessageBroker messageBroker;
    private readonly ISendCoalescer coalescer;
    private readonly CampaignLifetimeCompletion lifetimeCompletion;
    private readonly ICampaignObjectGraphReadiness graphReadiness;

    public MobilePartyRegistry(
        IControllerIdProvider controllerIdProvider,
        IMessageBroker messageBroker,
        ILogger logger,
        IAutoRegistryFactory autoRegistryFactory,
        IObjectManager objectManager,
        ISendCoalescer coalescer = null,
        CampaignLifetimeCompletion lifetimeCompletion = null,
        ICampaignObjectGraphReadiness graphReadiness = null)
        : base(logger, autoRegistryFactory, objectManager)
    {
        this.controllerIdProvider = controllerIdProvider;
        this.messageBroker = messageBroker;
        this.coalescer = coalescer;
        this.lifetimeCompletion = lifetimeCompletion;
        this.graphReadiness = graphReadiness;
    }

    public override IEnumerable<MethodBase> Constructors => AccessTools.GetDeclaredConstructors(typeof(MobileParty));

    public override IEnumerable<MethodBase> DestroyMethods => new MethodBase[]
    {
        AccessTools.Method(typeof(MobileParty), nameof(MobileParty.RemoveParty))
    };


    public override void RegisterAllObjects()
    {
        foreach (var party in MobileParty.All)
        {
            RegisterExistingObject(party.StringId, party);
        }
    }

    public override void OnClientCreated(MobileParty obj, string id)
    {
        graphReadiness?.BeginReplicaCreation(obj);
        using (new AllowedThread())
        {
            obj._isVisible = false;
            obj.IsActive = true;
            obj._isCurrentlyUsedByAQuest = false;
            obj.Party = new PartyBase(obj);
            obj.Anchor = new AnchorPoint(obj);
            obj.InitMembers();
            obj.InitCached();
            obj.Initialize();
            obj.HasLandNavigationCapability = true;
        }

        GameThread.RunSafe(() =>
        {
            void Publish()
            {
                var campaignObjects = Campaign.Current?.CampaignObjectManager;
                var nativeObjects = MBObjectManager.Instance;
                if (campaignObjects == null || nativeObjects == null)
                    throw new InvalidOperationException("Replica world publication requires the active native object managers");
                // Deferred publication is still received state, including on an observing ListenHost.
                using (new AllowedThread())
                {
                    campaignObjects.AddMobileParty(obj);
                    nativeObjects.RegisterObjectInternalWithoutTypeId(obj, presumed: false, out _);
                }
            }
            if (graphReadiness == null) Publish();
            else graphReadiness.PublishReplicaWhenReady(obj, Publish);
        }, context: "MobilePartyRegistry.OnClientCreated");
    }

    public override void OnClientDestroyed(MobileParty obj, string id)
    {
        graphReadiness?.CancelReplicaPublication(obj);
        GameThread.RunSafe(() =>
        {
            using (new AllowedThread())
            {
                try
                {
                    // A destroy that reaches the client only as this registry message never ran the
                    // vanilla DestroyPartyAction locally: a server-side destroy inside an AllowedThread
                    // scope skips publishing DestroyPartyApplied, while its RemoveParty (deferred out of
                    // that scope by DestroyPartyActionPatches.ApplyInternalPrefix) still publishes
                    // InstanceDestroyed. Vanilla map UI tears down on MobilePartyDestroyed — the party
                    // nameplate only removes plates on that event or a visibility change — so without it
                    // the dead party's plate leaks and forever renders its post-teardown name, which for
                    // bandits (ActualClan nulled in OnRemoveParty) is "NameFailed - BanditPartyPatch".
                    // Fire the events vanilla ApplyInternal would have. Membership in the world list
                    // discriminates: vanilla OnRemoveParty removes the party from CampaignObjectManager,
                    // so a party still listed here never had its local vanilla destroy, and a party that
                    // did is not double-notified.
                    if (Campaign.Current.MobileParties.Contains(obj))
                    {
                        CampaignEventDispatcher.Instance.OnMobilePartyDestroyed(obj, null);
                        CampaignEventDispatcher.Instance.OnMapInteractableDestroyed(obj.Party);
                    }

                    Campaign.Current.MobilePartyLocator.RemoveLocatable(obj);
                    Campaign.Current.VisualTrackerManager.RemoveTrackedObject(obj, true);
                    CampaignEventDispatcher.Instance.OnPartyRemoved(obj.Party);
                    Campaign.Current.CampaignObjectManager.RemoveMobileParty(obj);
                    graphReadiness?.CompleteReplicaRemoval(obj);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Failed to remove party");
                }
            }
        }, context: "MobilePartyRegistry.OnClientDestroyed");
    }

    public override void OnServerCreated(MobileParty obj, string id)
    {
    }

    public override void OnServerDestroyed(MobileParty obj, string id)
    {
        try
        {
            obj.MemberRoster.Clear();
            obj.PrisonRoster.Clear();
            obj.Party.SetVisualAsDirty();

            messageBroker.Publish(this, new MobilePartyDestroyed(obj));

            objectManager.TryGetHandleWithLogging(obj.ItemRoster, out var itemRosterId);
            objectManager.TryGetHandleWithLogging(obj.MemberRoster, out var memberRosterId);
            objectManager.TryGetHandleWithLogging(obj.PrisonRoster, out var prisonerRosterId);

            coalescer?.DropInstance(itemRosterId);
            coalescer?.DropInstance(memberRosterId);
            coalescer?.DropInstance(prisonerRosterId);

            // These attachments have no destroy hooks, so their lifetime ends with their party.
            messageBroker.Publish(this, new InstanceDestroyed<ItemRoster>(obj.ItemRoster));
            messageBroker.Publish(this, new InstanceDestroyed<TroopRoster>(obj.MemberRoster));
            messageBroker.Publish(this, new InstanceDestroyed<TroopRoster>(obj.PrisonRoster));
            if (lifetimeCompletion?.TryHoldGarrison(obj) != true)
                messageBroker.Publish(this, new InstanceDestroyed<PartyBase>(obj.Party));
        }
        finally
        {
            // AutoRegistryHandler sends the MobileParty destroy after this callback. Drop any state
            // queued before or during teardown so no behavior update can follow that destroy.
            // AutoSync keys use full registry ids; behavior packets use compact wire ids.
            coalescer?.DropInstance(id);
            if (objectManager.TryGetHandle(obj, out var handle))
                coalescer?.DropInstance(handle);
        }
    }
}
