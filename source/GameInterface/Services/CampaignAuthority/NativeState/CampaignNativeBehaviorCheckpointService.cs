using Common;
using Common.Logging;
using GameInterface.Services.Kingdoms;
using Serilog;
using GameInterface.Services.ObjectManager;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using ProtoBuf;
using TaleWorlds.Core;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Extensions;

namespace GameInterface.Services.CampaignAuthority.NativeState;

public interface ICampaignNativeBehaviorCheckpointService
{
    bool TryCapture(out CampaignNativeBehaviorCheckpoint checkpoint);
    bool TryPrepare(CampaignNativeBehaviorCheckpoint checkpoint, out Action install);
    bool IsInstalled(CampaignNativeBehaviorCheckpoint checkpoint);
}

internal sealed partial class CampaignNativeBehaviorCheckpointService : ICampaignNativeBehaviorCheckpointService
{
    private readonly IObjectManager objects;
    private readonly IKingdomDecisionDataConverter decisionConverter;
    private static readonly ILogger Logger = LogManager.GetLogger<CampaignNativeBehaviorCheckpointService>();
    internal string LastRejectedReason { get; private set; }
    public CampaignNativeBehaviorCheckpointService(IObjectManager objects, IKingdomDecisionDataConverter decisionConverter = null)
    { this.objects = objects; this.decisionConverter = decisionConverter; }
    private bool Reject(string reason)
    {
        LastRejectedReason = reason;
        Logger.Warning("Native checkpoint rejected: {Reason}", reason);
        return false;
    }

    private bool RejectValue(FormattableString reason) => Reject(FormattableString.Invariant(reason));

    public bool TryCapture(out CampaignNativeBehaviorCheckpoint checkpoint)
    {
        checkpoint = null;
        LastRejectedReason = null;
        var campaign = Campaign.Current;
        if (campaign == null) return Reject("capture.campaign=null");
        if (decisionConverter == null) return Reject("capture.kingdom.converter=null");
        return TryCapture(campaign.GetCampaignBehavior<AgingCampaignBehavior>(),
            campaign.GetCampaignBehavior<CaravansCampaignBehavior>(), ItemCategories.All.ToArray(),
            campaign.GetCampaignBehavior<KingdomDecisionProposalBehavior>(), out checkpoint);
    }

    public bool TryPrepare(CampaignNativeBehaviorCheckpoint checkpoint, out Action install)
    {
        install = null;
        LastRejectedReason = null;
        var campaign = Campaign.Current;
        if (campaign == null) return Reject("prepare.campaign=null");
        if (decisionConverter == null) return Reject("prepare.kingdom.converter=null");
        return TryPrepare(checkpoint, campaign.GetCampaignBehavior<AgingCampaignBehavior>(),
            campaign.GetCampaignBehavior<CaravansCampaignBehavior>(), ItemCategories.All.ToArray(),
            campaign.GetCampaignBehavior<KingdomDecisionProposalBehavior>(), out install);
    }

    public bool IsInstalled(CampaignNativeBehaviorCheckpoint checkpoint) =>
        checkpoint != null && TryCapture(out var installed) && MatchesInstalled(checkpoint, installed);

    private bool MatchesInstalled(CampaignNativeBehaviorCheckpoint checkpoint, CampaignNativeBehaviorCheckpoint installed)
    {
        if (checkpoint == null || installed == null || checkpoint.Version != 3 || installed.Version != 3)
            return Reject("installed.header=null or invalid");
        using var expected = new MemoryStream();
        using var actual = new MemoryStream();
        Serializer.Serialize(expected, checkpoint);
        Serializer.Serialize(actual, installed);
        return expected.ToArray().SequenceEqual(actual.ToArray()) || Reject("installed.nativeState differs from checkpoint");
    }

    internal bool TryCapture(AgingCampaignBehavior aging, CaravansCampaignBehavior caravans, ItemCategory[] categories,
        KingdomDecisionProposalBehavior proposals, out CampaignNativeBehaviorCheckpoint checkpoint)
    {
        checkpoint = null;
        if (!TryCapture(aging, caravans, categories, out var finite) ||
            !TryCaptureKingdomProposals(proposals, out var history)) return false;
        checkpoint = new CampaignNativeBehaviorCheckpoint(finite.Aging, finite.Caravans, history);
        return true;
    }

    internal bool TryPrepare(CampaignNativeBehaviorCheckpoint checkpoint, AgingCampaignBehavior aging,
        CaravansCampaignBehavior caravans, ItemCategory[] categories, KingdomDecisionProposalBehavior proposals, out Action install)
    {
        install = null;
        if (!TryPrepare(checkpoint, aging, caravans, categories, out var installFinite) ||
            !TryPrepareKingdomProposals(checkpoint.KingdomProposals, proposals, out var installProposals)) return false;
        install = () =>
        {
            using (CampaignExecutionScope.Enter(CampaignExecutionPhase.CheckpointInstall))
            { installFinite(); installProposals(); }
        };
        return true;
    }

    internal bool TryCapture(AgingCampaignBehavior aging, CaravansCampaignBehavior caravans,
        ItemCategory[] categories, out CampaignNativeBehaviorCheckpoint checkpoint)
    {
        checkpoint = null;
        if (!TryCapture(aging, out var agingCheckpoint) ||
            !TryCaptureCaravans(caravans, categories, out var caravanState)) return false;
        checkpoint = new CampaignNativeBehaviorCheckpoint(agingCheckpoint.Aging, caravanState);
        return true;
    }

    internal bool TryPrepare(CampaignNativeBehaviorCheckpoint checkpoint, AgingCampaignBehavior aging,
        CaravansCampaignBehavior caravans, ItemCategory[] categories, out Action install)
    {
        install = null;
        if (!TryPrepare(checkpoint, aging, out var installAging) ||
            !TryPrepareCaravans(checkpoint.Caravans, caravans, categories, out var installCaravans)) return false;
        install = () =>
        {
            using (CampaignExecutionScope.Enter(CampaignExecutionPhase.CheckpointInstall))
            {
                installAging();
                installCaravans();
            }
        };
        return true;
    }

    internal bool TryCapture(AgingCampaignBehavior aging, out CampaignNativeBehaviorCheckpoint checkpoint)
    {
        checkpoint = null;
        if (aging == null || aging._gameStartDay < 0)
            return RejectValue($"capture.aging.behavior/gameStartDay={aging?._gameStartDay}");
        if (!TryCaptureHeroes(aging._extraLivesContainer, "extraLives", out var extraLives) ||
            !TryCaptureHeroes(aging._heroesYoungerThanHeroComesOfAge, "youngHeroes", out var youngHeroes)) return false;
        checkpoint = new CampaignNativeBehaviorCheckpoint(new CampaignAgingBehaviorState(
            aging._gameStartDay, extraLives, youngHeroes));
        return true;
    }

    internal bool TryPrepare(CampaignNativeBehaviorCheckpoint checkpoint, AgingCampaignBehavior aging, out Action install)
    {
        install = null;
        var state = checkpoint?.Aging;
        if (checkpoint?.Version != 3 || aging == null || state == null || state.GameStartDay < 0)
            return RejectValue($"prepare.aging.header version={checkpoint?.Version}, day={state?.GameStartDay}, behaviorNull={aging == null}");
        if (!TryPrepareHeroes(state.ExtraLives, "extraLives", out var extraLives) ||
            !TryPrepareHeroes(state.YoungHeroes, "youngHeroes", out var youngHeroes)) return false;
        install = () =>
        {
            using (CampaignExecutionScope.Enter(CampaignExecutionPhase.CheckpointInstall))
            {
                aging._extraLivesContainer = extraLives;
                aging._heroesYoungerThanHeroComesOfAge = youngHeroes;
                aging._gameStartDay = state.GameStartDay;
            }
        };
        return true;
    }

    private bool TryCaptureHeroes(Dictionary<Hero, int> source, string field, out CampaignHeroIntState[] states)
    {
        states = null;
        if (source == null) return RejectValue($"capture.aging.{field}=null");
        var values = new List<CampaignHeroIntState>();
        var seen = new HashSet<uint>();
        foreach (var pair in source)
        {
            if (pair.Value < 0 || !objects.TryGetHandle(pair.Key, out var handle) || handle == 0 || !seen.Add(handle) ||
                !objects.TryGetObject<Hero>(handle, out var hero) || !ReferenceEquals(hero, pair.Key))
                return RejectValue($"capture.aging.{field} hero={pair.Key?.StringId}, value={pair.Value}, invalid/duplicate registry");
            values.Add(new CampaignHeroIntState(handle, pair.Value));
        }
        states = values.ToArray();
        return true;
    }

    private bool TryPrepareHeroes(CampaignHeroIntState[] states, string field, out Dictionary<Hero, int> values)
    {
        values = new Dictionary<Hero, int>();
        if (states == null) return RejectValue($"prepare.aging.{field}=null");
        var seen = new HashSet<uint>();
        foreach (var entry in states)
        {
            if (entry.HeroHandle == 0 || entry.Value < 0 || !seen.Add(entry.HeroHandle) ||
                !objects.TryGetObject<Hero>(entry.HeroHandle, out var hero) || values.ContainsKey(hero))
                return RejectValue($"prepare.aging.{field} handle={entry.HeroHandle}, value={entry.Value}, invalid/duplicate reference");
            values.Add(hero, entry.Value);
        }
        return true;
    }
}
