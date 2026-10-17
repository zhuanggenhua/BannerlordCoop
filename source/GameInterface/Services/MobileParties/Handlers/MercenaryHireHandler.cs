using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using GameInterface.Services.MobileParties.Messages;
using GameInterface.Services.MobileParties.Patches;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using LiteNetLib;
using Serilog;
using System.Collections.Generic;
using System.Threading;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;

namespace GameInterface.Services.MobileParties.Handlers;

/// <summary>
/// Routes a tavern mercenary hire through the room host to the active campaign authority, which
/// validates shared stock and applies the troop add and gold cost exactly once.
/// </summary>
internal class MercenaryHireHandler : IHandler
{
    private static readonly ILogger logger = LogManager.GetLogger<MercenaryHireHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly IObjectManager objectManager;
    private readonly INetwork network;
    private readonly ICampaignAuthorityContext authorityContext;
    private readonly IPlayerManager players;
    private readonly object sequenceGate = new object();
    private readonly Dictionary<string, (NetPeer Peer, long Sequence)> requestSequences = new();
    private AuthorityLease commandLease;
    private long nextAuthorityCommandSequence;
    private AuthorityLease lastAuthorityCommandLease;
    private long lastAuthorityCommandSequence;
    private long nextRequestSequence;

    public MercenaryHireHandler(
        IMessageBroker messageBroker,
        IObjectManager objectManager,
        INetwork network,
        ICampaignAuthorityContext authorityContext,
        IPlayerManager players)
    {
        this.messageBroker = messageBroker;
        this.objectManager = objectManager;
        this.network = network;
        this.authorityContext = authorityContext;
        this.players = players;

        messageBroker.Subscribe<MercenariesHired>(Handle_MercenariesHired);
        messageBroker.Subscribe<HireMercenaries>(Handle_HireMercenaries);
        messageBroker.Subscribe<NetworkCampaignAuthorityMercenaryHireCommand>(Handle_AuthorityHireCommand);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<MercenariesHired>(Handle_MercenariesHired);
        messageBroker.Unsubscribe<HireMercenaries>(Handle_HireMercenaries);
        messageBroker.Unsubscribe<NetworkCampaignAuthorityMercenaryHireCommand>(Handle_AuthorityHireCommand);
        lock (sequenceGate)
        {
            requestSequences.Clear();
            commandLease = default;
            nextAuthorityCommandSequence = 0;
            lastAuthorityCommandLease = default;
            lastAuthorityCommandSequence = 0;
        }
    }

    internal void Handle_MercenariesHired(MessagePayload<MercenariesHired> obj)
    {
        if (!objectManager.TryGetIdWithLogging(obj.What.MainHero, out var mainHeroId)) return;
        if (!objectManager.TryGetIdWithLogging(obj.What.MainParty, out var mainPartyId)) return;
        if (!objectManager.TryGetIdWithLogging(obj.What.Town, out var townId)) return;
        if (!objectManager.TryGetIdWithLogging(obj.What.MercenaryTroop, out var mercenaryTroopId)) return;

        AuthorityLease lease = default;
        if (authorityContext.TryGetCurrentLease(out var currentLease) && currentLease.IsValid)
        {
            if (currentLease.IsPaused) return;
            lease = currentLease;
        }

        network.SendAll(new HireMercenaries(
            mainHeroId,
            mainPartyId,
            townId,
            mercenaryTroopId,
            obj.What.Count,
            obj.What.GoldAmount,
            obj.What.MainHero.Gold,
            Interlocked.Increment(ref nextRequestSequence),
            lease));
    }

    internal void Handle_HireMercenaries(MessagePayload<HireMercenaries> obj)
    {
        var data = obj.What;
        if (obj.Who is not NetPeer peer) return;

        GameThread.RunSafe(() => HandleHireRequest(data, peer), context: nameof(MercenaryHireHandler));
    }

    private void HandleHireRequest(HireMercenaries data, NetPeer peer)
    {
        if (!players.ValidateCurrentPartySender(peer, data.MainPartyId) ||
            !players.TryGetPlayer(peer, out var player) || !PlayerOwnsRequest(player, data))
            return;

        bool authorityKnown = authorityContext.IsAuthorityKnown;
        if (authorityKnown)
        {
            if (!authorityContext.TryGetCurrentLease(out var lease) || !data.MatchesLease(lease) ||
                !TryAdvanceRequestSequence(player.ControllerId, peer, data.RequestSequence, authorityKnown: true))
                return;

            if (authorityContext.TryGetLocalAuthorityLease(out var localLease) && localLease == lease)
            {
                ApplyHireMercenaries(data, peer);
                return;
            }

            RouteToRemoteAuthority(data, player.ControllerId, lease);
            return;
        }

        if (!TryAdvanceRequestSequence(player.ControllerId, peer, data.RequestSequence, authorityKnown: false))
            return;

        ApplyHireMercenaries(data, peer);
    }

    private void Handle_AuthorityHireCommand(MessagePayload<NetworkCampaignAuthorityMercenaryHireCommand> payload)
    {
        var command = payload.What;
        if (!authorityContext.TryGetLocalAuthorityLease(out var lease) || !command.Matches(lease) ||
            !players.TryGetPlayer(command.OriginControllerId, out var player) ||
            !PlayerOwnsRequest(player, command.Request) ||
            !TryAdvanceAuthorityCommandSequence(lease, command.Sequence))
            return;

        GameThread.RunSafe(() =>
        {
            // Revalidate after queuing so an old lease or changed player registration cannot apply.
            if (!authorityContext.TryGetLocalAuthorityLease(out var currentLease) || currentLease != lease ||
                !players.TryGetPlayer(command.OriginControllerId, out var currentPlayer) ||
                !PlayerOwnsRequest(currentPlayer, command.Request))
                return;

            ApplyHireMercenaries(command.Request, peer: null);
        }, context: nameof(MercenaryHireHandler));
    }

    private void RouteToRemoteAuthority(HireMercenaries request, string originControllerId, AuthorityLease lease)
    {
        if (!players.TryGetPeer(lease.AuthorityControllerId, out var authorityPeer) || authorityPeer == null)
            return;

        long sequence;
        lock (sequenceGate)
        {
            if (commandLease != lease)
            {
                commandLease = lease;
                nextAuthorityCommandSequence = 0;
            }

            if (nextAuthorityCommandSequence == long.MaxValue)
                throw new System.InvalidOperationException("Campaign authority mercenary-hire sequence space is exhausted.");

            sequence = ++nextAuthorityCommandSequence;
        }

        network.Send(authorityPeer, new NetworkCampaignAuthorityMercenaryHireCommand(
            lease,
            sequence,
            originControllerId,
            request));
    }

    private bool TryAdvanceRequestSequence(string controllerId, NetPeer peer, long sequence, bool authorityKnown)
    {
        if (sequence <= 0) return !authorityKnown;

        lock (sequenceGate)
        {
            if (requestSequences.TryGetValue(controllerId, out var previous) &&
                ReferenceEquals(previous.Peer, peer) && sequence <= previous.Sequence)
                return false;

            requestSequences[controllerId] = (peer, sequence);
            return true;
        }
    }

    private bool TryAdvanceAuthorityCommandSequence(AuthorityLease lease, long sequence)
    {
        lock (sequenceGate)
        {
            if (lastAuthorityCommandLease != lease)
            {
                lastAuthorityCommandLease = lease;
                lastAuthorityCommandSequence = 0;
            }

            if (sequence <= lastAuthorityCommandSequence) return false;
            lastAuthorityCommandSequence = sequence;
            return true;
        }
    }

    private bool PlayerOwnsRequest(Player player, HireMercenaries request) =>
        player != null && !players.IsReservedController(player.ControllerId) &&
        player.HeroId == request.MainHeroId && player.MobilePartyId == request.MainPartyId;

    private void ApplyHireMercenaries(HireMercenaries data, NetPeer? peer)
    {
        if (!objectManager.TryGetObjectWithLogging<Hero>(data.MainHeroId, out var mainHero)) return;
        if (!objectManager.TryGetObjectWithLogging<MobileParty>(data.MainPartyId, out var mainParty)) return;
        if (!objectManager.TryGetObjectWithLogging<Town>(data.TownId, out var town)) return;
        if (!objectManager.TryGetObjectWithLogging<CharacterObject>(data.MercenaryTroopId, out var mercenaryTroop)) return;
        if (town.Settlement == null || !town.Settlement.IsTown) return;

        var recruitmentBehavior = Campaign.Current?.GetCampaignBehavior<RecruitmentCampaignBehavior>();
        var mercenaryData = recruitmentBehavior?.GetMercenaryData(town);
        int unitPrice = mercenaryData?.TroopType == null
            ? 0
            : Campaign.Current.Models.PartyWageModel.GetTroopRecruitmentCost(mercenaryData.TroopType, mainHero).RoundedResultNumber;
        int goldAmount = GetMercenaryHireGoldAmount(data.Count, unitPrice);
        bool availableTroopMatches = mercenaryData?.TroopType == mercenaryTroop;
        int availableCount = mercenaryData?.Number ?? 0;

        // Authority stock may tick while a player is still in the tavern conversation. Reject stale
        // requests against the current stock, then publish the latest stock back below.
        if (mercenaryData == null ||
            !CanApplyMercenaryHire(
                data.Count,
                goldAmount,
                mainHero.Gold,
                unitPrice,
                availableTroopMatches,
                availableCount))
        {
            var availableTroopId = "unresolved";
            if (mercenaryData?.TroopType != null &&
                objectManager.TryGetIdWithLogging(mercenaryData.TroopType, out var resolvedAvailableTroopId))
            {
                availableTroopId = resolvedAvailableTroopId;
            }

            logger.Warning(
                "Rejected town mercenary hire for {TownId}: requested {RequestedTroopId} x{RequestedCount}, available {AvailableTroop} x{AvailableCount}, client hero gold {ClientHeroGold}, authority hero gold {AuthorityHeroGold}, client cost {ClientGoldAmount}, authority cost {AuthorityGoldAmount}",
                data.TownId,
                data.MercenaryTroopId,
                data.Count,
                availableTroopId,
                availableCount,
                data.HeroGold,
                mainHero.Gold,
                data.GoldAmount,
                goldAmount);

            SendMercenaryStock(peer, town, mercenaryData?.TroopType, mercenaryData?.Number ?? 0);

            return;
        }

        // Apply with patches LIVE (no AllowedThread): the roster add replicates via the
        // TroopRoster patches and the gold change via the Hero.Gold sync.
        mainParty.AddElementToMemberRoster(mercenaryTroop, data.Count);
        GiveGoldAction.ApplyBetweenCharacters(mainHero, null, goldAmount, false);

        // The recruitment side effects of the hire. This is the host-safe equivalent of
        // vanilla CampaignEventDispatcher.OnUnitRecruited, whose listener reads Hero.MainHero /
        // MobileParty.MainParty (neither of which the dedicated host has); it runs against the
        // resolved hero/party instead, with patches live so the troop XP and the hero's
        // recruitment skill XP replicate to every client.
        if (mainHero.GetPerkValue(DefaultPerks.Leadership.FamousCommander))
        {
            mainParty.MemberRoster.AddXpToTroop(mercenaryTroop, (int)DefaultPerks.Leadership.FamousCommander.SecondaryBonus * data.Count);
        }
        SkillLevelingManager.OnTroopRecruited(mainHero, data.Count, mercenaryTroop.Tier);
        if (mercenaryTroop.Occupation == Occupation.Bandit)
        {
            SkillLevelingManager.OnBanditsRecruited(mainParty, mercenaryTroop, data.Count);
        }

        mercenaryData.ChangeMercenaryCount(-data.Count);
        RecruitmentCampaignBehaviorPatch.PublishMercenaryStock(recruitmentBehavior, town);
    }

    private void SendMercenaryStock(NetPeer? peer, Town town, CharacterObject troopType, int number)
    {
        if (!objectManager.TryGetIdWithLogging(town, out var townId)) return;

        string troopTypeId = null;
        if (troopType != null && !objectManager.TryGetId(troopType, out troopTypeId)) return;

        var update = new NetworkUpdateMercenaryStock(townId, troopTypeId, number);
        if (peer == null)
            network.SendAll(update);
        else
            network.Send(peer, update);
    }

    internal static int GetMercenaryHireGoldAmount(int count, int unitPrice)
    {
        if (count <= 0 || unitPrice <= 0)
            return 0;

        return count * unitPrice;
    }

    internal static bool CanApplyMercenaryHire(
        int count,
        int goldAmount,
        int serverHeroGold,
        int unitPrice,
        bool availableTroopMatches,
        int availableMercenaries)
    {
        return count > 0 &&
               goldAmount > 0 &&
               serverHeroGold >= goldAmount &&
               unitPrice > 0 &&
               availableTroopMatches &&
               availableMercenaries >= count;
    }
}
