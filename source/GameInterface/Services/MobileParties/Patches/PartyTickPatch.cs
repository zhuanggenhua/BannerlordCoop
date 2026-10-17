using Common;
using HarmonyLib;
using GameInterface.Services.CampaignAuthority;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.MobileParties.Patches;

[HarmonyPatch]
internal class PartyTickPatch
{
    [HarmonyPatch(typeof(CampaignPeriodicEventManager), nameof(CampaignPeriodicEventManager.TickPeriodicEvents))]
    [HarmonyPrefix]
    static bool Prefix_TickPeriodicEvents() => CampaignSimulationAuthorityPolicy.CanRunWorldSimulation();

    [HarmonyPatch(typeof(CampaignPeriodicEventManager), nameof(CampaignPeriodicEventManager.MobilePartyHourlyTick))]
    [HarmonyPrefix]
    static bool Prefix_MobilePartyHourlyTick() => CampaignSimulationAuthorityPolicy.CanRunWorldSimulation();


    [HarmonyPatch(typeof(MobileParty), nameof(MobileParty.HourlyTick))]
    [HarmonyPrefix]
    static bool Prefix_HourlyTick() => CampaignSimulationAuthorityPolicy.CanRunWorldSimulation();
}
