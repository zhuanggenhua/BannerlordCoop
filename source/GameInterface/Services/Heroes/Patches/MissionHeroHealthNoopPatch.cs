using Common;
using GameInterface.Policies;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Heroes.Extensions;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace GameInterface.Services.Heroes.Patches;

[HarmonyPatch(typeof(Hero), nameof(Hero.HitPoints), MethodType.Setter)]
internal static class MissionHeroHealthNoopPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(600)]
    internal static bool Prefix(Hero __instance, int value)
    {
        if (__instance == null || !ModInformation.IsClient ||
            !GameThread.Instance.IsInitialized || !GameThread.Instance.IsGameThread)
            return true;

        if (Mission.Current == null || __instance.HitPoints != value)
            return true;

        if (CallOriginalPolicy.IsOriginalAllowed() ||
            !ContainerProvider.TryResolve<ICampaignAuthorityContext>(out var context) ||
            !context.TryGetCurrentLease(out _) || context.IsLocalCampaignAuthority)
            return true;

        if (Campaign.Current == null || Game.Current == null ||
            Game.Current.PlayerTroop is not CharacterObject || __instance != Hero.MainHero)
            return true;

        if (!__instance.IsHealthControlledByThisInstance()) return true;

        // Vanilla returns before any effect for exact equality; keep the generated ref prefix from diagnosing a write.
        return false;
    }
}
