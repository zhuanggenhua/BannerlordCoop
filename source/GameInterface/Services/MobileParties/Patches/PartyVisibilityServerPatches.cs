using Common;
using HarmonyLib;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.MobileParties.Patches;

/// <summary>
/// Dedicated servers and debug replicas can reveal active parties. Listen authority always
/// preserves native local visibility, including in DEBUG builds.
/// </summary>
[HarmonyPatch(typeof(MobileParty), nameof(MobileParty.IsSpotted))]
internal class PartyIsSpottedServerPatch
{
    internal static void Postfix(MobileParty __instance, ref bool __result)
    {
        if (ModInformation.IsDedicatedAuthority || (!ModInformation.IsListenAuthority && DebugPartyVisibility.ForceAllVisible))
        {
            // Match the IsVisible patch below: only live parties are force-spotted. A destroyed
            // party must read as unspotted, or the map nameplate machinery keeps re-creating its
            // banner (with 0 troops) after the destruction replicates.
            __result = __instance.IsActive;
        }
    }
}

[HarmonyPatch(typeof(MobileParty))]
internal class PartyVisibilityOnServerPatch
{
    [HarmonyPatch(nameof(MobileParty.IsVisible), MethodType.Setter)]
    [HarmonyPrefix]
    internal static void PrefixIsVisible(MobileParty __instance, ref bool value)
    {
#if DEBUG
        // Restore the exact captured fixture state through the native visibility setter.
        if (global::GameInterface.Services.SiegeEvents.Commands.DefenderSiegeFixtureCommands.IsRestoringCapturedVisibility(__instance))
            return;
#endif
        if (ModInformation.IsDedicatedAuthority || (!ModInformation.IsListenAuthority && DebugPartyVisibility.ForceAllVisible))
        {
            value = __instance.IsActive;
        }
    }

    [HarmonyPatch(nameof(MobileParty.IsInspected), MethodType.Setter)]
    [HarmonyPrefix]
    internal static void PrefixIsInspected(ref bool value)
    {
        if (ModInformation.IsDedicatedAuthority || (!ModInformation.IsListenAuthority && DebugPartyVisibility.ForceAllVisible))
        {
            value = true;
        }
    }
}
