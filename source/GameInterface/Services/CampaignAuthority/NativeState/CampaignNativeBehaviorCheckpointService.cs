using Common;
using GameInterface.Services.ObjectManager;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.Core;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Extensions;

namespace GameInterface.Services.CampaignAuthority.NativeState;

public interface ICampaignNativeBehaviorCheckpointService
{
    bool TryCapture(out CampaignNativeBehaviorCheckpoint checkpoint);
    bool TryPrepare(CampaignNativeBehaviorCheckpoint checkpoint, out Action install);
}

internal sealed partial class CampaignNativeBehaviorCheckpointService : ICampaignNativeBehaviorCheckpointService
{
    private readonly IObjectManager objects;
    public CampaignNativeBehaviorCheckpointService(IObjectManager objects) { this.objects = objects; }

    public bool TryCapture(out CampaignNativeBehaviorCheckpoint checkpoint)
    {
        checkpoint = null;
        var campaign = Campaign.Current;
        return campaign != null && TryCapture(campaign.GetCampaignBehavior<AgingCampaignBehavior>(),
            campaign.GetCampaignBehavior<CaravansCampaignBehavior>(), ItemCategories.All.ToArray(), out checkpoint);
    }

    public bool TryPrepare(CampaignNativeBehaviorCheckpoint checkpoint, out Action install)
    {
        install = null;
        var campaign = Campaign.Current;
        return campaign != null && TryPrepare(checkpoint, campaign.GetCampaignBehavior<AgingCampaignBehavior>(),
            campaign.GetCampaignBehavior<CaravansCampaignBehavior>(), ItemCategories.All.ToArray(), out install);
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
        if (aging == null || aging._gameStartDay < 0 ||
            !TryCaptureHeroes(aging._extraLivesContainer, out var extraLives) ||
            !TryCaptureHeroes(aging._heroesYoungerThanHeroComesOfAge, out var youngHeroes)) return false;
        checkpoint = new CampaignNativeBehaviorCheckpoint(new CampaignAgingBehaviorState(
            aging._gameStartDay, extraLives, youngHeroes));
        return true;
    }

    internal bool TryPrepare(CampaignNativeBehaviorCheckpoint checkpoint, AgingCampaignBehavior aging, out Action install)
    {
        install = null;
        var state = checkpoint?.Aging;
        if (checkpoint?.Version != 2 || aging == null || state == null || state.GameStartDay < 0 ||
            !TryPrepareHeroes(state.ExtraLives, out var extraLives) ||
            !TryPrepareHeroes(state.YoungHeroes, out var youngHeroes)) return false;
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

    private bool TryCaptureHeroes(Dictionary<Hero, int> source, out CampaignHeroIntState[] states)
    {
        states = null;
        if (source == null) return false;
        var values = new List<CampaignHeroIntState>();
        var seen = new HashSet<uint>();
        foreach (var pair in source)
        {
            if (pair.Value < 0 || !objects.TryGetHandle(pair.Key, out var handle) || handle == 0 || !seen.Add(handle) ||
                !objects.TryGetObject<Hero>(handle, out var hero) || !ReferenceEquals(hero, pair.Key)) return false;
            values.Add(new CampaignHeroIntState(handle, pair.Value));
        }
        states = values.ToArray();
        return true;
    }

    private bool TryPrepareHeroes(CampaignHeroIntState[] states, out Dictionary<Hero, int> values)
    {
        values = new Dictionary<Hero, int>();
        if (states == null) return false;
        var seen = new HashSet<uint>();
        foreach (var entry in states)
        {
            if (entry.HeroHandle == 0 || entry.Value < 0 || !seen.Add(entry.HeroHandle) ||
                !objects.TryGetObject<Hero>(entry.HeroHandle, out var hero) || values.ContainsKey(hero)) return false;
            values.Add(hero, entry.Value);
        }
        return true;
    }
}
