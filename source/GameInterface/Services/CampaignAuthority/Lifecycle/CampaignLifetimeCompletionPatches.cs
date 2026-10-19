using Common;
using GameInterface.Policies;
using GameInterface.Registry.Auto;
using HarmonyLib;
using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.CampaignAuthority.Lifecycle;

[HarmonyPatch(typeof(MobileParty), nameof(MobileParty.Army), MethodType.Setter)]
internal static class ArmySetterCompletionPatch
{
    [HarmonyPrefix, HarmonyPriority(Priority.First + 1)]
    private static void Prefix(MobileParty __instance, Army value, out CampaignLifetimeCompletion.ArmyScope __state)
    {
        __state = null;
        if (CallOriginalPolicy.IsOriginalAllowed() || __instance.Army == value) return;
        if (ContainerProvider.TryResolve<CampaignLifetimeCompletion>(out var completion))
            __state = completion.EnterArmy(__instance.Army);
    }

    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception Finalizer(CampaignLifetimeCompletion.ArmyScope __state, Exception __exception)
    {
        if (__state != null && ContainerProvider.TryResolve<CampaignLifetimeCompletion>(out var completion))
            completion.LeaveArmy(__state, false, false, __exception);
        return __exception;
    }
}

[HarmonyPatch(typeof(Army), nameof(Army.DisperseInternal))]
internal static class ArmyDisperseCompletionPatch
{
    [HarmonyPrefix, HarmonyPriority(Priority.First + 1)]
    private static bool Prefix(Army __instance, out CampaignLifetimeCompletion.ArmyScope __state)
    {
        __state = null;
        if (CallOriginalPolicy.IsOriginalAllowed()) return true;
        if (!CampaignSimulationAuthorityPolicy.CanRunWorldSimulation()) return false;
        if (!ContainerProvider.TryResolve<CampaignLifetimeCompletion>(out var completion))
            throw new InvalidOperationException("Army completion adapter is not registered");
        __state = completion.EnterArmy(__instance);
        return completion.MayDisperse(__state);
    }

    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception Finalizer(Army __instance, CampaignLifetimeCompletion.ArmyScope __state, bool __runOriginal, Exception __exception)
    {
        if (__state != null && ContainerProvider.TryResolve<CampaignLifetimeCompletion>(out var completion))
            completion.LeaveArmy(__state, true, __runOriginal, __exception);
        else if (__exception == null)
            LifetimePatches<Army>.DestroyPostfix(ref __instance, __runOriginal);
        return __exception;
    }
}

[HarmonyPatch(typeof(Army), nameof(Army.OnRemovePartyInternal))]
internal static class ArmyMemberRemovalCompletionPatch
{
    [HarmonyPrefix, HarmonyPriority(Priority.First + 1)]
    private static void Prefix(Army __instance, out CampaignLifetimeCompletion.ArmyScope __state)
    {
        __state = null;
        if (CallOriginalPolicy.IsOriginalAllowed()) return;
        if (ContainerProvider.TryResolve<CampaignLifetimeCompletion>(out var completion))
            __state = completion.EnterArmy(__instance);
    }

    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception Finalizer(CampaignLifetimeCompletion.ArmyScope __state, Exception __exception)
    {
        if (__state != null && ContainerProvider.TryResolve<CampaignLifetimeCompletion>(out var completion))
            completion.LeaveArmy(__state, false, false, __exception);
        return __exception;
    }
}

[HarmonyPatch(typeof(MapEvent), nameof(MapEvent.FinalizeEventAux))]
internal static class MapEventFinalizationCompletionPatch
{
    [HarmonyPrefix, HarmonyPriority(Priority.First + 1)]
    private static void Prefix(MapEvent __instance, out CampaignLifetimeCompletion.MapScope __state)
    {
        __state = null;
        if (CallOriginalPolicy.IsOriginalAllowed()) return;
        if (ContainerProvider.TryResolve<CampaignLifetimeCompletion>(out var completion))
            __state = completion.EnterFinalization(__instance);
    }

    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception Finalizer(CampaignLifetimeCompletion.MapScope __state, bool __runOriginal, Exception __exception)
    {
        if (__state != null && ContainerProvider.TryResolve<CampaignLifetimeCompletion>(out var completion))
            completion.LeaveFinalization(__state, __runOriginal, __exception);
        return __exception;
    }
}
