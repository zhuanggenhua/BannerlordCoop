using Common;
#if DEBUG
using Common.Commands;
using GameInterface.Services.ObjectManager;
using Newtonsoft.Json;
using System;
using System.Linq;
#endif
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.MobileParties.Extensions;
using HarmonyLib;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.Core;

namespace GameInterface.Services.MobileParties.Patches.Disable;

[HarmonyPatch(typeof(MobilePartyTrainingBehavior))]
internal class DisableMobilePartyTrainingBehavior
{
    [HarmonyPatch(nameof(MobilePartyTrainingBehavior.RegisterEvents))]
    [HarmonyPrefix]
    internal static bool PrefixRegisterEvents() => true;

    // Both peers keep their native listeners; changing writer never registers them again.
    [HarmonyPatch(nameof(MobilePartyTrainingBehavior.HourlyTickParty))]
    [HarmonyPrefix]
    internal static bool PrefixHourlyTickParty() => CampaignSimulationAuthorityPolicy.CanRunWorldSimulation();

    [HarmonyPatch(nameof(MobilePartyTrainingBehavior.OnDailyTickParty))]
    [HarmonyPrefix]
    internal static bool PrefixDailyTickParty() => CampaignSimulationAuthorityPolicy.CanRunWorldSimulation();

    [HarmonyPatch(nameof(MobilePartyTrainingBehavior.OnPlayerUpgradedTroops))]
    [HarmonyPrefix]
    internal static bool PrefixPlayerUpgradedTroops() => CampaignSimulationAuthorityPolicy.CanRunWorldSimulation();

#if DEBUG
    public sealed class TrainingDailyCoopCommand : ICoopCommand
    {
        private readonly IObjectManager objects;
        public TrainingDailyCoopCommand(IObjectManager objects) => this.objects = objects;
        public string Prefix => "coop.debug.authority";
        public string Name => "training_daily";
        public string Description => "Runs one native NPC daily-training callback on the current campaign writer and reports roster XP.";
        public CoopCommandSide Side => CoopCommandSide.Both;
        public IExpectedArgs[] ExpectedArgs { get; } = { new ExpectedArgs("party_id", "The registered NPC party id.") };

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            if (!CampaignSimulationAuthorityPolicy.CanRunWorldSimulation())
                return new(false, "The current unfrozen campaign writer is required", "not_world_writer");
            if (Campaign.Current == null)
                return new(false, "A loaded campaign is required", "campaign_missing");
            if (!objects.TryGetObject(args[0], out MobileParty party) || party.MemberRoster == null)
                return new(false, "The NPC party is unavailable", "party_missing");
            if (party.IsPlayerParty())
                return new(false, "This probe accepts only NPC parties", "npc_required");
            var training = Campaign.Current.GetCampaignBehavior<MobilePartyTrainingBehavior>();
            if (training == null)
                return new(false, "The native training behavior is unavailable", "behavior_missing");

            var before = CaptureXp(party);
            training.OnDailyTickParty(party);
            return new(true, JsonConvert.SerializeObject(new
            {
                partyId = args[0],
                nativeCallback = nameof(MobilePartyTrainingBehavior.OnDailyTickParty),
                worldTicks = Campaign.Current.CurrentTickCount,
                before,
                after = CaptureXp(party),
            }));
        }

        private static object[] CaptureXp(MobileParty party) => party.MemberRoster.GetTroopRoster()
            .Where(element => !element.Character.IsHero)
            .Select(element => (object)new { id = element.Character.StringId, number = element.Number, xp = element.Xp })
            .ToArray();
    }
#endif
}

// OnPlayerUpgradedTroops already replaced in PartyDoneLogicHandler.ApplyUpgradedTroopHistory
[HarmonyPatch(typeof(MobilePartyTrainingBehavior))]
internal class MobilePartyTrainingBehaviorPatches
{
    [HarmonyPatch(nameof(MobilePartyTrainingBehavior.CheckScouting))]
    [HarmonyPrefix]
    public static bool CheckScoutingPrefix(MobileParty mobileParty)
    {
        if (mobileParty.EffectiveScout != null && !mobileParty.IsCurrentlyAtSea)
        {
            TerrainType faceTerrainType = Campaign.Current.MapSceneWrapper.GetFaceTerrainType(mobileParty.CurrentNavigationFace);
            if (!mobileParty.IsPlayerParty()) // Replace MainParty check
            {
                SkillLevelingManager.OnAIPartiesTravel(mobileParty.EffectiveScout, mobileParty.IsCaravan, faceTerrainType);
            }
            SkillLevelingManager.OnTraverseTerrain(mobileParty, faceTerrainType);
        }

        return false;
    }

    [HarmonyPatch(nameof(MobilePartyTrainingBehavior.CheckMovementSkills))]
    [HarmonyPrefix]
    public static bool CheckMovementSkillsPrefix(MobileParty mobileParty)
    {
        // Only need to patch first block as mobileParty == MobileParty.MainParty won't be true on the server
        if (mobileParty.IsPlayerParty())
        {
            if (mobileParty.IsCurrentlyAtSea)
            {
                SkillLevelingManager.OnTravelOnWater(mobileParty, mobileParty._lastCalculatedSpeed);
                return false;
            }
            using (List<TroopRosterElement>.Enumerator enumerator = mobileParty.MemberRoster.GetTroopRoster().GetEnumerator())
            {
                while (enumerator.MoveNext())
                {
                    TroopRosterElement troopRosterElement = enumerator.Current;
                    if (troopRosterElement.Character.IsHero)
                    {
                        if (troopRosterElement.Character.Equipment.Horse.IsEmpty)
                        {
                            SkillLevelingManager.OnTravelOnFoot(troopRosterElement.Character.HeroObject, mobileParty._lastCalculatedSpeed);
                        }
                        else
                        {
                            SkillLevelingManager.OnTravelOnHorse(troopRosterElement.Character.HeroObject, mobileParty._lastCalculatedSpeed);
                        }
                    }
                }
                return false;
            }
        }

        return true;
    }
}