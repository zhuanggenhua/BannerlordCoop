using Common;
using GameInterface.Services.MapTracks.Interfaces;
using GameInterface.Services.CampaignAuthority;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;

namespace GameInterface.Services.MapTracks.Patches;

[HarmonyPatch(typeof(MapTracksCampaignBehavior))]
internal class MapTracksCampaignBehaviorPatches
{
    [HarmonyPatch(nameof(MapTracksCampaignBehavior.GameLoadFinished))]
    [HarmonyPrefix]
    public static bool GameLoadFinishedPrefix(MapTracksCampaignBehavior __instance)
    {
        if (ModInformation.IsServer) return true;
        // Observers receive visible tracks separately, but need the timer when promoted.
        if (__instance._quarterHourlyTick == null) __instance.AddEventHandler();
        return false;
    }

    [HarmonyPatch(nameof(MapTracksCampaignBehavior.OnHourlyTick))]
    [HarmonyPrefix]
    public static bool OnHourlyTickPrefix(MapTracksCampaignBehavior __instance)
    {
        if (!CampaignSimulationAuthorityPolicy.CanRunWorldSimulation()) return false;

        // Replace with interface method to run for all players
        ContainerProvider.TryResolve<IMapTracksCampaignBehaviorInterface>(out var mapTracksCampaignBehaviorInterface);

        mapTracksCampaignBehaviorInterface.OnHourlyTick(__instance);

        return false;
    }

    [HarmonyPatch(nameof(MapTracksCampaignBehavior.OnHourlyTickParty))]
    [HarmonyPrefix]
    public static bool OnHourlyTickPartyPrefix() => CampaignSimulationAuthorityPolicy.CanRunWorldSimulation();

    [HarmonyPatch(nameof(MapTracksCampaignBehavior.OnMobilePartyDestroyed))]
    [HarmonyPrefix]
    public static bool OnMobilePartyDestroyedPrefix() => CampaignSimulationAuthorityPolicy.CanRunWorldSimulation();

    [HarmonyPatch(nameof(MapTracksCampaignBehavior.QuarterHourlyTick))]
    [HarmonyPrefix]
    public static bool QuarterHourlyTickPrefix(MapTracksCampaignBehavior __instance)
    {
        if (!CampaignSimulationAuthorityPolicy.CanRunWorldSimulation()) return false;

        // Replace with interface method to run for all players
        ContainerProvider.TryResolve<IMapTracksCampaignBehaviorInterface>(out var mapTracksCampaignBehaviorInterface);

        mapTracksCampaignBehaviorInterface.QuarterHourlyTick(__instance);

        return false;
    }

    [HarmonyPatch(nameof(MapTracksCampaignBehavior.IsTrackDropped))]
    [HarmonyPrefix]
    public static bool IsTrackDroppedPrefix(MapTracksCampaignBehavior __instance, ref bool __result, MobileParty mobileParty)
    {
        if (!CampaignSimulationAuthorityPolicy.CanRunWorldSimulation()) return false;

        ContainerProvider.TryResolve<IMapTracksCampaignBehaviorInterface>(out var mapTracksCampaignBehaviorInterface);

        __result = mapTracksCampaignBehaviorInterface.IsTrackDropped(__instance, mobileParty);

        return false;
    }

    [HarmonyPatch(nameof(MapTracksCampaignBehavior.AddTrack))]
    [HarmonyPrefix]
    public static bool AddTrackPrefix(MapTracksCampaignBehavior __instance, MobileParty party, CampaignVec2 trackPosition, Vec2 trackDirection)
    {
        if (!CampaignSimulationAuthorityPolicy.CanRunWorldSimulation()) return false;

        ContainerProvider.TryResolve<IMapTracksCampaignBehaviorInterface>(out var mapTracksCampaignBehaviorInterface);

        mapTracksCampaignBehaviorInterface.AddTrack(__instance, party, trackPosition, trackDirection);

        return false;
    }

    /// <summary>
    /// Vanilla behavior already already saves _allTracks in it, need to add each track's source faction to that same record.
    /// Without it a restart reloads every track without a faction.
    /// </summary>
    [HarmonyPatch(nameof(MapTracksCampaignBehavior.SyncData))]
    [HarmonyPostfix]
    public static void SyncDataPostfix(MapTracksCampaignBehavior __instance, IDataStore dataStore)
    {
        if (!ContainerProvider.TryResolve<IMapTracksCampaignBehaviorInterface>(out var mapTracksCampaignBehaviorInterface)) return;

        mapTracksCampaignBehaviorInterface.SyncTrackMapFactions(__instance, dataStore);
        mapTracksCampaignBehaviorInterface.SyncPlayerDetectedTracks(__instance, dataStore);
        mapTracksCampaignBehaviorInterface.SyncTrackSourceParties(__instance, dataStore);
    }
}
