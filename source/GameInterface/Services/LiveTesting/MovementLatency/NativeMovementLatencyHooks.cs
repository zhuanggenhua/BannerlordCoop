#if DEBUG
using Common;
using Common.Logging;
using GameInterface.Services.MobileParties.Data;
using HarmonyLib;
using SandBox.View.Map;
using SandBox.View.Map.Visuals;
using Serilog;
using System;
using System.Collections.Generic;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Map;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;

namespace GameInterface.Services.LiveTesting.MovementLatency;

/// <summary>Installed only while armed; observer patches never skip or replace original behavior.</summary>
internal sealed class NativeMovementLatencyHooks : IMovementLatencyHooks
{
    private static readonly ILogger Logger = LogManager.GetLogger<NativeMovementLatencyHooks>();
    private static readonly object ownership = new object();
    private static MovementLatencyProbe active;
    private readonly Harmony harmony = new("Coop.Debug.MovementLatency." + Guid.NewGuid().ToString("N"));
    private MovementLatencyProbe owner;

    public NativeMovementLatencyHooks() { }

    internal static IReadOnlyDictionary<string, MethodBase> ResolveTargets() => new Dictionary<string, MethodBase>
    {
        ["input"] = Required(typeof(MapScreen), "HandleLeftMouseButtonClick", typeof(MapEntityVisual), typeof(CampaignVec2), typeof(PathFaceRecord), typeof(bool)),
        ["apply"] = Required(typeof(MobilePartyBehaviorSnapshot), nameof(MobilePartyBehaviorSnapshot.TryApply), typeof(MobileParty), typeof(PartyBehaviorUpdateData), typeof(IInteractablePoint).MakeByRefType()),
        ["parallelPosition"] = Required(typeof(MobileParty), "SetPositionParallel", typeof(CampaignVec2).MakeByRefType(), typeof(int).MakeByRefType(), typeof(MobileParty[]).MakeByRefType()),
        ["position"] = AccessTools.PropertySetter(typeof(MobileParty), nameof(MobileParty.Position)) ?? throw new MissingMethodException("MobileParty.Position setter"),
        ["visual"] = Required(typeof(MobilePartyVisual), "Tick", typeof(float), typeof(float), typeof(int).MakeByRefType(), typeof(MobilePartyVisual[]).MakeByRefType()),
        ["frame"] = Required(typeof(GameThread), nameof(GameThread.Update), typeof(TimeSpan)),
    };

    private static MethodInfo Required(Type type, string name, params Type[] args) =>
        AccessTools.DeclaredMethod(type, name, args) ?? throw new MissingMethodException(type.FullName, name);

    public void Start(MovementLatencyProbe probe)
    {
        lock (ownership)
        {
            if (active != null) throw new InvalidOperationException("Another movement probe owns the process hooks.");
            var targets = ResolveTargets();
            owner = active = probe;
            try
            {
                Patch(targets["input"], nameof(InputPrefix), nameof(InputPostfix), nameof(OriginalException));
                Patch(targets["apply"], nameof(ApplyPrefix), nameof(ApplyPostfix), nameof(OriginalException));
                Patch(targets["parallelPosition"], null, nameof(ParallelPositionPostfix), nameof(OriginalException));
                Patch(targets["position"], null, nameof(PositionPostfix), nameof(OriginalException));
                Patch(targets["visual"], null, nameof(VisualPostfix), nameof(OriginalException));
                Patch(targets["frame"], null, nameof(FramePostfix), nameof(OriginalException));
            }
            catch { harmony.UnpatchAll(harmony.Id); active = null; owner = null; throw; }
        }
    }

    public void Stop()
    {
        lock (ownership)
        {
            if (owner == null) return;
            if (ReferenceEquals(active, owner)) active = null;
            harmony.UnpatchAll(harmony.Id);
            owner = null;
        }
    }

    private void Patch(MethodBase original, string prefix, string postfix, string finalizer) => harmony.Patch(original,
        prefix == null ? null : new HarmonyMethod(typeof(NativeMovementLatencyHooks), prefix),
        postfix == null ? null : new HarmonyMethod(typeof(NativeMovementLatencyHooks), postfix),
        finalizer: finalizer == null ? null : new HarmonyMethod(typeof(NativeMovementLatencyHooks), finalizer));

    private static void Observe(Action<MovementLatencyProbe> action)
    {
        var probe = active;
        if (probe == null) return;
        try { action(probe); }
        catch (Exception ex)
        {
            probe.Fail(ex);
            Logger.Error(ex, "Movement latency diagnostics failed; trace is invalid");
        }
    }

    internal static void InputPrefix(CampaignVec2 __1, out long __state)
    {
        long id = 0;
        Observe(p => id = p.InputEnter(MobileParty.MainParty, __1.X, __1.Y));
        __state = id;
    }
    internal static void InputPostfix(long __state) => Observe(p => p.InputExit(MobileParty.MainParty, __state));
    internal static void ApplyPrefix(MobileParty __0, PartyBehaviorUpdateData __1, out long __state)
    {
        long id = 0;
        Observe(p => id = p.ApplyEnter(__0, __1.OriginControllerId, __1.MoveTargetPoint.X, __1.MoveTargetPoint.Y));
        __state = id;
    }
    internal static void ApplyPostfix(MobileParty __0, bool __result, long __state) => Observe(p => p.ApplyExit(__0, __state, __result));
    internal static void ParallelPositionPostfix(MobileParty __instance, bool __runOriginal)
    { if (__runOriginal) Observe(p => p.Observe(__instance, "native_position")); }
    internal static void PositionPostfix(MobileParty __instance, bool __runOriginal)
    { if (__runOriginal) Observe(p => p.Observe(__instance, "position_set")); }
    internal static void FramePostfix() => Observe(p => p.Frame());
    internal static void VisualPostfix(MobilePartyVisual __instance, bool __runOriginal)
    {
        if (!__runOriginal) return;
        Observe(p =>
        {
            var party = __instance?.MapEntity?.MobileParty;
            if (!p.Tracks(party)) return;
            if (__instance.StrategicEntity == null) throw new InvalidOperationException("Tracked party visual has no strategic entity.");
            var position = __instance.StrategicEntity.GetFrame().origin;
            p.Observe(party, "visual_frame", position.X, position.Y);
        });
    }
    // Return the exact original exception. A diagnostic adapter cannot convert a game failure to PASS.
    internal static Exception OriginalException(Exception __exception)
    {
        if (__exception != null) Observe(p => p.Fail(__exception));
        return __exception;
    }
}
#endif
