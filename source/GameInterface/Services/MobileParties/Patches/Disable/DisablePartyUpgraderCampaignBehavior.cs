using Common;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.MobileParties.Extensions;
using HarmonyLib;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.ComponentInterfaces;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;

namespace GameInterface.Services.MobileParties.Patches.Disable;

[HarmonyPatch(typeof(PartyUpgraderCampaignBehavior))]
internal class DisablePartyUpgraderCampaignBehavior
{
    [HarmonyPatch(nameof(PartyUpgraderCampaignBehavior.RegisterEvents))]
    internal static bool Prefix() => true;
}

[HarmonyPatch(typeof(PartyUpgraderCampaignBehavior))]
internal class PartyUpgraderCampaignBehaviorPatches
{
    [HarmonyPatch(nameof(PartyUpgraderCampaignBehavior.DailyTickParty))]
    [HarmonyPrefix]
    internal static bool DailyTickPartyAuthorityPrefix()
        => CampaignSimulationAuthorityPolicy.CanRunWorldSimulation();

    [HarmonyPatch(nameof(PartyUpgraderCampaignBehavior.MapEventEnded))]
    [HarmonyPrefix]
    internal static bool MapEventEndedAuthorityPrefix()
        => CampaignSimulationAuthorityPolicy.CanRunWorldSimulation();

    [HarmonyPatch(nameof(PartyUpgraderCampaignBehavior.UpgradeReadyTroops))]
    [HarmonyPrefix]
    public static bool UpgradeReadyTroopsPrefix(PartyUpgraderCampaignBehavior __instance, PartyBase party)
    {
        if (!CampaignSimulationAuthorityPolicy.CanRunWorldSimulation()) return false;
        // Replace MainParty check
        if ((party.IsMobile && party.MobileParty.IsPlayerParty()) || !party.IsActive) return false;

        TroopRoster memberRoster = party.MemberRoster;
        PartyTroopUpgradeModel partyTroopUpgradeModel = Campaign.Current.Models.PartyTroopUpgradeModel;
        for (int i = 0; i < memberRoster.Count; i++)
        {
            TroopRosterElement elementCopyAtIndex = memberRoster.GetElementCopyAtIndex(i);
            if (!partyTroopUpgradeModel.IsTroopUpgradeable(party, elementCopyAtIndex.Character)) continue;

            List<PartyUpgraderCampaignBehavior.TroopUpgradeArgs> possibleUpgradeTargets = __instance.GetPossibleUpgradeTargets(party, elementCopyAtIndex);
            if (possibleUpgradeTargets.Count <= 0) continue;

            PartyUpgraderCampaignBehavior.TroopUpgradeArgs upgradeArgs = __instance.SelectPossibleUpgrade(possibleUpgradeTargets);
            __instance.UpgradeTroop(party, i, upgradeArgs);
        }

        return false;
    }
}