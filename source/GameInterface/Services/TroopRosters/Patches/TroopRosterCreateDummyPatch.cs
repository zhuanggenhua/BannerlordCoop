using Common;
using Common.Messaging;
using Common.Util;
using GameInterface.Policies;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Registry.Auto;
using HarmonyLib;
using System;
using System.Reflection;
using TaleWorlds.CampaignSystem.Roster;

namespace GameInterface.Services.TroopRosters.Patches;

[HarmonyPatch(typeof(TroopRoster))]
internal class TroopRosterCreateDummyPatch
{
    private static readonly ConstructorInfo TroopRosterCtor = AccessTools.Constructor(typeof(TroopRoster), Type.EmptyTypes);

    [HarmonyPatch(nameof(TroopRoster.CreateDummyTroopRoster))]
    [HarmonyPrefix]
    internal static bool Prefix_CreateDummyTroopRoster(ref TroopRoster __result)
    {
        if (CallOriginalPolicy.IsOriginalAllowed())
            return true;

        if (!CampaignSimulationAuthorityPolicy.CanRunWorldSimulation())
        {
            // Party, tooltip, battle-tally, and other presentation flows create local scratch rosters on
            // observers. They deliberately have no network identity; authority-created managed rosters arrive
            // through the object registry instead of this factory.
            return true;
        }

        __result = ObjectHelper.SkipConstructor<TroopRoster>();

        MessageBroker.Instance.Publish(null, new InstanceCreated<TroopRoster>(__result));

        TroopRosterCtor.Invoke(__result, Array.Empty<object>());

        return false;
    }
}
