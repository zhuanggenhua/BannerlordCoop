using Common;
using HarmonyLib;

namespace GameInterface.Services.ObjectReadiness;

[HarmonyPatch(typeof(GameThread), nameof(GameThread.Update))]
internal static class ObjectGraphReadinessUpdatePatch
{
    [HarmonyPostfix]
    internal static void Postfix()
    {
        // AutoSync may write fields directly, so inspect the graph after the original queue drain.
        if (GameThread.Instance.IsGameThread && ContainerProvider.TryResolve<ICampaignObjectGraphReadiness>(out var readiness))
            readiness.ReferencesApplied();
    }
}
