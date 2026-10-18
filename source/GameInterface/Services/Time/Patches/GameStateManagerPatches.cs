using HarmonyLib;
using SandBox.View.Map;
using Common;
using Common.Messaging;
using GameInterface.Services.Time.Messages;
using System.Diagnostics;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace GameInterface.Services.Time.Patches
{
    [HarmonyPatch(typeof(GameStateManager))]
    class GameStateManagerPatches
    {
        private static GameStateManager observedStateManager;
        private static bool? lastMapReadiness;
        private static long lastReadinessPublishTimestamp;

        internal static void ResetReadinessObservation()
        {
#if DEBUG
            Services.CampaignAuthority.CampaignAuthorityDebugCommands.YieldMapAuthority = false;
#endif
            observedStateManager = null;
            lastMapReadiness = null;
            lastReadinessPublishTimestamp = 0;
        }

        // Prevents pausing in menus without their own game state (such as the encyclopedia)
        [HarmonyPatch(nameof(GameStateManager.RegisterActiveStateDisableRequest))]
        static bool Prefix() => ModInformation.IsListenAuthority;

        // Prevents pausing in menus with their own game states (such as the banner editor, party screen, clan screen, etc.)
        [HarmonyPatch(nameof(GameStateManager.OnTick))]
        static void Prefix(ref GameStateManager __instance, float dt)
        {
#if DEBUG
            ListenAuthorityTickEvidence.BeginFrame();
#endif
            // P1-A keeps native local menus/mission scheduling. No second world driver.
            if (ModInformation.IsListenAuthority) return;
            if (!(__instance.ActiveState is MapState))
            {
                MapState mapState = __instance.LastOrDefault<MapState>();
                if (mapState == null) return;

                // Co-op keeps the (now backgrounded) map ticking so the world keeps simulating
                // without pausing while another screen (clan, kingdom, inventory, crafting, etc.) is on top.
                // Unlike vanilla, that forced tick also runs the inactive map handler's UI/input callbacks.
                // Those callbacks read stale input and can take focus from the active screen, which triggers
                // map hotkeys while typing and immediately clears fields such as the blacksmith weapon name.
                // Temporarily detach the handler so campaign simulation keeps ticking without the inactive
                // map UI. Always restore it in finally: if OnTick throws and the handler stays null, later map
                // input and lifecycle callbacks would remain broken for the rest of the session.
                var handler = mapState.Handler;
                mapState.Handler = null;
                try
                {
                    mapState.OnTick(dt);
                }
                finally
                {
                    mapState.Handler = handler;
                }
            }
        }

        [HarmonyPatch(nameof(GameStateManager.OnTick))]
        [HarmonyPostfix]
        static void ReadinessPostfix(GameStateManager __instance)
        {
            var states = __instance.GameStates;
            bool mapReady = Campaign.Current != null &&
                states.Any(state => state is MapState) &&
                !states.Any(state => state is MissionState);
#if DEBUG
            mapReady &= !Services.CampaignAuthority.CampaignAuthorityDebugCommands.YieldMapAuthority;
#endif
            long timestamp = Stopwatch.GetTimestamp();
            bool stateManagerChanged = !ReferenceEquals(observedStateManager, __instance);
            bool readinessChanged = !lastMapReadiness.HasValue || lastMapReadiness.Value != mapReady;
            bool heartbeatDue = timestamp - lastReadinessPublishTimestamp >= Stopwatch.Frequency;
            if (!stateManagerChanged && !readinessChanged && !heartbeatDue) return;

            observedStateManager = __instance;
            lastMapReadiness = mapReady;
            lastReadinessPublishTimestamp = timestamp;
            MessageBroker.Instance.Publish(__instance, new CampaignMapSimulationReadinessChanged(mapReady));
        }
    }

    /// <summary>
    /// Prevents the inactive campaign map camera from replacing a mission's global audio listener.
    /// </summary>
    [HarmonyPatch(typeof(MapCameraView), nameof(MapCameraView.OnBeforeTick))]
    internal static class MapCameraViewPatches
    {
        [HarmonyPrefix]
        private static bool OnBeforeTickPrefix()
        {
            return ShouldTickMapCamera(GameStateManager.Current?.ActiveState);
        }

        internal static bool ShouldTickMapCamera(TaleWorlds.Core.GameState activeState)
        {
            // The map camera writes the global audio listener, which the active mission owns.
            return activeState is not MissionState;
        }
    }
}
