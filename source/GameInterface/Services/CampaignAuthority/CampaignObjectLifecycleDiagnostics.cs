#if DEBUG
using Common.Logging;
using GameInterface.Services.MobileParties.Extensions;
using GameInterface.Services.ObjectManager;
using HarmonyLib;
using Serilog;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using RegistryObjectManager = GameInterface.Services.ObjectManager.ObjectManager;

namespace GameInterface.Services.CampaignAuthority;

// Diagnostic-only adapter. Never changes registry entries, original results or exceptions.
internal static class CampaignObjectLifecycleDiagnostics
{
    internal static readonly ILogger Logger = LogManager.GetLogger(typeof(CampaignObjectLifecycleDiagnostics));

    private static long observationFailures;
    private static long loggingFailures;
    internal static long ObservationFailures => Interlocked.Read(ref observationFailures);
    internal static long LoggingFailures => Interlocked.Read(ref loggingFailures);

    internal sealed class Removal
    {
        internal string Identity;
        internal string Type;
        internal string Id;
        internal uint Handle;
        internal bool HadId;
        internal bool HadHandle;
        internal string Stack;
        internal string ObservationError;
    }

    internal static bool Includes(object obj) => obj is Army || obj is PartyBase;
    internal static string Identity(object obj) => $"reference:{RuntimeHelpers.GetHashCode(obj):x8}";

    internal static Removal Capture(IObjectManager manager, object obj, ILogger logger)
    {
        if (!Includes(obj)) return null;
        var state = new Removal { Identity = Identity(obj), Type = obj.GetType().FullName, Stack = Environment.StackTrace };
        try
        {
            state.HadId = manager.TryGetId(obj, out state.Id);
            state.HadHandle = manager.TryGetHandle(obj, out state.Handle);
        }
        catch (Exception error)
        {
            // This is only a failed diagnostic read, not an exception from the original Remove.
            state.ObservationError = error.ToString();
            RecordFailure("observation");
        }
        Write(logger, "[LifecycleProbe] Remove.before {ObjectType} {ObjectIdentity} id={RegisteredId} handle={RegisteredHandle} hadId={HadId} hadHandle={HadHandle} observationError={ObservationError}\n{CallStack}",
            state.Type, state.Identity, state.Id, state.Handle, state.HadId, state.HadHandle, state.ObservationError, state.Stack);
        return state;
    }

    internal static Exception Complete(Removal state, bool ranOriginal, bool result, Exception exception, ILogger logger)
    {
        if (state != null)
            Write(logger, "[LifecycleProbe] Remove.after {ObjectType} {ObjectIdentity} id={RegisteredId} handle={RegisteredHandle} ranOriginal={RanOriginal} result={RemoveResult} originalException={OriginalException} observationError={ObservationError}",
                state.Type, state.Identity, state.Id, state.Handle, ranOriginal, result, exception?.ToString(), state.ObservationError);
        return exception;
    }

    internal static void LookupMiss(object obj, bool result, bool ranOriginal, ILogger logger)
    {
        if (!ranOriginal || result || !Includes(obj)) return;
        // TryGetId also has legitimate misses. This is DEBUG context, never a new ERR.
        Write(logger, "[LifecycleProbe] TryGetId.miss {ObjectType} {ObjectIdentity}\n{CallStack}", obj.GetType().FullName, Identity(obj), Environment.StackTrace);
    }

    internal static void NullParty(MobileParty party, MethodBase method, ILogger logger)
    {
        if (party != null) return;
        Write(logger, "[LifecycleProbe] NullParty {OriginalMethod}\n{CallStack}", method.Name, Environment.StackTrace);
    }

    internal static void RecordFailure(string kind, CampaignCycleDiagnostics collector = null)
    {
        if (kind == "observation") Interlocked.Increment(ref observationFailures);
        else Interlocked.Increment(ref loggingFailures);
        try
        {
            if (collector == null && Campaign.Current != null) collector = CampaignCycleDispatchPatches.For(Campaign.Current);
            collector?.Simulation("CampaignLifecycleProbe." + kind + ".failed");
        }
        catch { /* The process-level counter remains available if even the diagnostic collector is unavailable. */ }
    }

    private static void Write(ILogger logger, string template, params object[] values)
    {
        try { logger.Write(LogEventLevel.Debug, template, values); }
        catch (Exception diagnosticError)
        {
            RecordFailure("logging");
            // Logging itself must not replace an original engine/registry result or exception.
            // The fallback explicitly identifies unavailable probe evidence; no game error is caught here.
            try { System.Diagnostics.Trace.WriteLine("[LifecycleProbe] diagnostic output unavailable: " + diagnosticError); }
            catch { /* A broken diagnostic listener cannot be allowed to change game execution. */ }
        }
    }
}

[HarmonyPatch(typeof(RegistryObjectManager), nameof(RegistryObjectManager.Remove), new[] { typeof(object) })]
internal static class CampaignObjectRemovalDiagnosticPatches
{
    [HarmonyPrefix]
    internal static void Prefix(RegistryObjectManager __instance, object obj, out CampaignObjectLifecycleDiagnostics.Removal __state) =>
        __state = CampaignObjectLifecycleDiagnostics.Capture(__instance, obj, CampaignObjectLifecycleDiagnostics.Logger);

    [HarmonyFinalizer]
    internal static Exception Finalizer(bool __runOriginal, bool __result, Exception __exception, CampaignObjectLifecycleDiagnostics.Removal __state) =>
        CampaignObjectLifecycleDiagnostics.Complete(__state, __runOriginal, __result, __exception, CampaignObjectLifecycleDiagnostics.Logger);
}

[HarmonyPatch]
internal static class CampaignObjectLookupDiagnosticPatches
{
    internal static MethodBase TargetMethod() => AccessTools.Method(typeof(RegistryObjectManager), nameof(RegistryObjectManager.TryGetId),
        new[] { typeof(object), typeof(string).MakeByRefType() });

    [HarmonyPostfix]
    internal static void Postfix(object obj, bool __result, bool __runOriginal) =>
        CampaignObjectLifecycleDiagnostics.LookupMiss(obj, __result, __runOriginal, CampaignObjectLifecycleDiagnostics.Logger);
}

[HarmonyPatch]
internal static class CampaignNullPartyDiagnosticPatches
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(MobilePartyExtensions), nameof(MobilePartyExtensions.IsPlayerParty), new[] { typeof(MobileParty) });
        yield return AccessTools.Method(typeof(MobilePartyExtensions), nameof(MobilePartyExtensions.IsControlledByThisInstance), new[] { typeof(MobileParty) });
    }

    [HarmonyPrefix]
    internal static void Prefix(MobileParty party, MethodBase __originalMethod) =>
        CampaignObjectLifecycleDiagnostics.NullParty(party, __originalMethod, CampaignObjectLifecycleDiagnostics.Logger);
}
#endif
