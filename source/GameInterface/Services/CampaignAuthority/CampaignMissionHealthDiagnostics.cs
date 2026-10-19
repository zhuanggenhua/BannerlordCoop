#if DEBUG
using Common;
using Common.Logging;
using GameInterface.Policies;
using GameInterface.Services.Entity;
using HarmonyLib;
using Serilog;
using Serilog.Events;
using System;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.MountAndBlade;

namespace GameInterface.Services.CampaignAuthority;

// Observes native Mission health writes without changing health, requests or permission.
internal static class CampaignMissionHealthDiagnostics
{
    internal static readonly ILogger Logger = LogManager.GetLogger(typeof(CampaignMissionHealthDiagnostics));

    internal sealed class Observation
    {
        internal string HeroId;
        internal string Reference;
        internal bool? MainHero;
        internal int? OldHitPoints;
        internal int RequestedHitPoints;
        internal int? FinalArgument;
        internal int? PostHitPoints;
        internal string Controller;
        internal string AuthorityController;
        internal long? Epoch;
        internal bool? Paused;
        internal bool? LocalWriter;
        internal bool? OriginalAllowed;
        internal bool? AllowedThread;
        internal string ObservationError;
    }

    internal static Observation Capture(Func<Observation, bool> read, ILogger logger)
    {
        var state = new Observation();
        try
        {
            if (!read(state)) return null;
        }
        catch (Exception error) { ReadFailed(state, error); }
        Write(logger, "[MissionHealthProbe] before hero={Hero} reference={Reference} mainHero={MainHero} oldHP={OldHP} requestedHP={RequestedHP} controller={Controller} authorityController={AuthorityController} epoch={Epoch} paused={Paused} localWriter={LocalWriter} callOriginalAllowed={CallOriginalAllowed} allowedThread={AllowedThread} observationError={ObservationError}",
            state.HeroId, state.Reference, state.MainHero, state.OldHitPoints, state.RequestedHitPoints,
            state.Controller, state.AuthorityController, state.Epoch, state.Paused, state.LocalWriter,
            state.OriginalAllowed, state.AllowedThread, state.ObservationError);
        return state;
    }

    internal static bool Read(Hero hero, int requested, Observation state)
    {
        if (Mission.Current == null) return false;
        state.RequestedHitPoints = requested;
        state.Reference = "reference:" + RuntimeHelpers.GetHashCode(hero).ToString("x8");
        state.HeroId = hero.StringId;
        state.MainHero = hero == Hero.MainHero;
        state.OldHitPoints = hero.HitPoints;
        ContainerProvider.TryResolve<IControllerIdProvider>(out var controller);
        state.Controller = controller?.ControllerId;
        if (ContainerProvider.TryResolve<ICampaignAuthorityContext>(out var context))
        {
            if (context.TryGetCurrentLease(out var lease))
            {
                state.Epoch = lease.Epoch;
                state.Paused = lease.IsPaused;
                state.AuthorityController = lease.AuthorityControllerId;
            }
            state.LocalWriter = context.IsLocalCampaignAuthority;
        }
        state.AllowedThread = Common.Util.AllowedThread.IsThisThreadAllowed();
        state.OriginalAllowed = CallOriginalPolicy.IsOriginalAllowed();
        return true;
    }

    internal static Exception Complete(Observation state, Func<int> readPostHealth, int finalArgument,
        bool ranOriginal, Exception exception, ILogger logger)
    {
        if (state == null) return exception;
        state.FinalArgument = finalArgument;
        try { state.PostHitPoints = readPostHealth(); }
        catch (Exception error) { ReadFailed(state, error); }
        Write(logger, "[MissionHealthProbe] after hero={Hero} reference={Reference} mainHero={MainHero} oldHP={OldHP} requestedHP={RequestedHP} finalArgumentHP={FinalArgumentHP} postHP={PostHP} originalRan={OriginalRan} originalException={OriginalException} controller={Controller} authorityController={AuthorityController} epoch={Epoch} paused={Paused} localWriter={LocalWriter} callOriginalAllowed={CallOriginalAllowed} allowedThread={AllowedThread} observationError={ObservationError}",
            state.HeroId, state.Reference, state.MainHero, state.OldHitPoints, state.RequestedHitPoints,
            state.FinalArgument, state.PostHitPoints, ranOriginal, ExceptionText(exception), state.Controller,
            state.AuthorityController, state.Epoch, state.Paused, state.LocalWriter,
            state.OriginalAllowed, state.AllowedThread, state.ObservationError);
        return exception;
    }

    private static void ReadFailed(Observation state, Exception error)
    {
        CampaignObjectLifecycleDiagnostics.RecordFailure("observation");
        state.ObservationError = (state.ObservationError == null ? "" : state.ObservationError + " | ") + ExceptionText(error);
    }

    private static string ExceptionText(Exception error)
    {
        if (error == null) return null;
        try { return error.ToString(); }
        catch
        {
            CampaignObjectLifecycleDiagnostics.RecordFailure("observation");
            return "<exception formatting unavailable: " + error.GetType().FullName + ">";
        }
    }

    private static void Write(ILogger logger, string template, params object[] values)
    {
        try { logger.Write(LogEventLevel.Debug, template, values); }
        catch { CampaignObjectLifecycleDiagnostics.RecordFailure("logging"); }
    }
}

[HarmonyPatch(typeof(Hero), nameof(Hero.HitPoints), MethodType.Setter)]
internal static class CampaignMissionHealthDiagnosticPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    internal static void Prefix(Hero __instance, int value, out CampaignMissionHealthDiagnostics.Observation __state) =>
        __state = CampaignMissionHealthDiagnostics.Capture(state => CampaignMissionHealthDiagnostics.Read(__instance, value, state), CampaignMissionHealthDiagnostics.Logger);

    [HarmonyFinalizer]
    internal static Exception Finalizer(Hero __instance, int value, bool __runOriginal, Exception __exception,
        CampaignMissionHealthDiagnostics.Observation __state) =>
        CampaignMissionHealthDiagnostics.Complete(__state, () => __instance.HitPoints, value, __runOriginal, __exception, CampaignMissionHealthDiagnostics.Logger);
}
#endif
