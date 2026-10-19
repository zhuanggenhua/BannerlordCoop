using Common.Logging;
using HarmonyLib;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace GameInterface.AutoSync;

public interface IAutoSyncPatchCollector : IDisposable
{
    void AddPrefix(MethodBase patchMethod, MethodInfo patch);
    void AddPostfix(MethodBase patchMethod, MethodInfo patch);
    void AddTranspiler(MethodBase patchMethod, MethodInfo patch);
    void PatchAll();
    void UnpatchAll();
}

class AutoSyncPatchCollector : IAutoSyncPatchCollector
{
    private static readonly ILogger Logger = LogManager.GetLogger<AutoSyncPatchCollector>();
    private static readonly object PatchGate = new();

    private static readonly HashSet<(string HarmonyId, MethodBase Method, MethodInfo Patch, HarmonyPatchType Type)> PatchedMethods = new();

    private readonly Harmony harmony;

    private readonly List<(MethodBase, MethodInfo)> transpilers = new();
    private readonly List<(MethodBase, MethodInfo)> prefixes = new();
    private readonly List<(MethodBase, MethodInfo)> postfixes = new();
    public AutoSyncPatchCollector(Harmony harmony)
    {
        this.harmony = harmony;
    }

    public void AddPrefix(MethodBase patchMethod, MethodInfo patch)
    {
        if (patchMethod == null) throw new ArgumentNullException(nameof(patchMethod));
        if (patch == null) throw new ArgumentNullException(nameof(patch));

        prefixes.Add((patchMethod, patch));
    }
    public void AddPostfix(MethodBase patchMethod, MethodInfo patch)
    {
        if (patchMethod == null) throw new ArgumentNullException(nameof(patchMethod));
        if (patch == null) throw new ArgumentNullException(nameof(patch));

        postfixes.Add((patchMethod, patch));
    }
    public void AddTranspiler(MethodBase patchMethod, MethodInfo patch)
    {
        if (patchMethod == null) throw new ArgumentNullException(nameof(patchMethod));
        if (patch == null) throw new ArgumentNullException(nameof(patch));

        transpilers.Add((patchMethod, patch));
    }

    public void PatchAll()
    {
        lock (PatchGate)
        {
            PatchAll(transpilers, HarmonyPatchType.Transpiler);
            PatchAll(prefixes, HarmonyPatchType.Prefix);
            PatchAll(postfixes, HarmonyPatchType.Postfix);
        }
    }

    private void PatchAll(List<(MethodBase, MethodInfo)> patches, HarmonyPatchType patchType)
    {
        foreach (var (method, patch) in patches)
            Patch(method, patch, patchType);
    }

    private void Patch(MethodBase method, MethodInfo patch, HarmonyPatchType patchType)
    {
        var key = (harmony.Id, method, patch, patchType);

        try
        {
            // Harmony metadata is the source of truth. The static set can outlive a
            // container, while an external unpatch can remove Harmony's live entry.
            var ownedCount = CountInstalledPatches(method, patch, patchType, out var otherOwner, out var hasUntrackedGenericPatch);
            if (ownedCount > 1)
            {
                Logger.Error("Harmony patch {PatchDeclaringType}.{PatchName} has {PatchCount} live registrations for {DeclaringType}.{MethodName} owned by {HarmonyId}",
                    patch.DeclaringType, patch.Name, ownedCount, method.DeclaringType, method.Name, harmony.Id);
                return;
            }

            if (otherOwner != null)
            {
                Logger.Error("Method {DeclaringType}.{MethodName} already has patch {PatchDeclaringType}.{PatchName} owned by {OtherHarmonyId}; refusing duplicate under {HarmonyId}",
                    method.DeclaringType, method.Name, patch.DeclaringType, patch.Name, otherOwner, harmony.Id);
                return;
            }

            if (hasUntrackedGenericPatch)
            {
                Logger.Error("Cannot verify the closed generic identity for Harmony patch {PatchDeclaringType}.{PatchName} on {DeclaringType}.{MethodName} owned by {HarmonyId}; refusing to install a duplicate",
                    patch.DeclaringType, patch.Name, method.DeclaringType, method.Name, harmony.Id);
                return;
            }

            if (ownedCount == 1)
            {
                PatchedMethods.Add(key);
                return;
            }

            if (PatchedMethods.Remove(key))
                Logger.Warning("Harmony patch {PatchDeclaringType}.{PatchName} for {DeclaringType}.{MethodName} owned by {HarmonyId} was missing and will be reinstalled",
                    patch.DeclaringType, patch.Name, method.DeclaringType, method.Name, harmony.Id);

            // Harmony 2.4.2 stores generic patch methods by module/token. Its PatchMethod getter
            // resolves that token without a declaring-type context, so live metadata reports the
            // open generic definition. Record the exact closed request while installing so that
            // a later collector can safely associate that metadata with this target and owner.
            if (!PatchedMethods.Add(key))
            {
                Logger.Error("Harmony patch bookkeeping for {PatchDeclaringType}.{PatchName} on {DeclaringType}.{MethodName} owned by {HarmonyId} changed during installation",
                    patch.DeclaringType, patch.Name, method.DeclaringType, method.Name, harmony.Id);
                return;
            }

            var harmonyMethod = new HarmonyMethod(patch);
            try
            {
                switch (patchType)
                {
                    case HarmonyPatchType.Transpiler:
                        harmony.Patch(method, transpiler: harmonyMethod);
                        break;
                    case HarmonyPatchType.Prefix:
                        harmony.Patch(method, prefix: harmonyMethod);
                        break;
                    case HarmonyPatchType.Postfix:
                        harmony.Patch(method, postfix: harmonyMethod);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(patchType), patchType, "Unsupported Harmony patch type.");
                }

                ownedCount = CountInstalledPatches(method, patch, patchType, out otherOwner, out hasUntrackedGenericPatch);
                if (otherOwner != null)
                {
                    Logger.Error("Harmony patch {PatchDeclaringType}.{PatchName} for {DeclaringType}.{MethodName} is also owned by {OtherHarmonyId} after installation under {HarmonyId}",
                        patch.DeclaringType, patch.Name, method.DeclaringType, method.Name, otherOwner, harmony.Id);
                    PatchedMethods.Remove(key);
                    return;
                }

                if (ownedCount != 1)
                {
                    Logger.Error("Harmony did not register exactly one patch {PatchDeclaringType}.{PatchName} for {DeclaringType}.{MethodName} owned by {HarmonyId}; found {PatchCount}",
                        patch.DeclaringType, patch.Name, method.DeclaringType, method.Name, harmony.Id, ownedCount);
                    PatchedMethods.Remove(key);
                }
            }
            catch
            {
                PatchedMethods.Remove(key);
                throw;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to patch {DeclaringType}.{MethodName} with patch {PatchDeclaringType}.{PatchName}",
                method.DeclaringType, method.Name, patch.DeclaringType, patch.Name);
        }
    }

    private int CountInstalledPatches(
        MethodBase method,
        MethodInfo patch,
        HarmonyPatchType patchType,
        out string otherOwner,
        out bool hasUntrackedGenericPatch)
    {
        otherOwner = null;
        hasUntrackedGenericPatch = false;
        var key = (harmony.Id, method, patch, patchType);
        var patchInfo = Harmony.GetPatchInfo(method);
        if (patchInfo == null) return 0;

        IEnumerable<Patch> livePatches = patchType switch
        {
            HarmonyPatchType.Transpiler => patchInfo.Transpilers,
            HarmonyPatchType.Prefix => patchInfo.Prefixes,
            HarmonyPatchType.Postfix => patchInfo.Postfixes,
            _ => null
        };

        if (livePatches == null) return 0;

        var ownedCount = 0;
        foreach (var livePatch in livePatches)
        {
            var liveMethod = livePatch.PatchMethod;
            if (liveMethod != patch && !IsOpenMethodDefinitionOf(liveMethod, patch)) continue;

            if (livePatch.owner == harmony.Id)
            {
                if (liveMethod == patch || PatchedMethods.Contains(key))
                    ownedCount++;
                else
                    hasUntrackedGenericPatch = true;
            }
            else
                otherOwner ??= livePatch.owner;
        }

        return ownedCount;
    }

    private static bool IsOpenMethodDefinitionOf(MethodInfo liveMethod, MethodInfo requestedMethod)
    {
        if (!liveMethod.ContainsGenericParameters || requestedMethod.ContainsGenericParameters)
            return false;
        if (liveMethod.Module != requestedMethod.Module || liveMethod.MetadataToken != requestedMethod.MetadataToken)
            return false;

        var liveDeclaringType = liveMethod.DeclaringType;
        var requestedDeclaringType = requestedMethod.DeclaringType;
        if (requestedDeclaringType?.IsConstructedGenericType != true
            || requestedDeclaringType.GetGenericTypeDefinition() != typeof(global::GameInterface.Registry.Auto.LifetimePatches<>))
            return false;

        var sameDeclaringType = liveDeclaringType == requestedDeclaringType
            || (liveDeclaringType?.IsGenericTypeDefinition == true
                && requestedDeclaringType?.IsConstructedGenericType == true
                && requestedDeclaringType.GetGenericTypeDefinition() == liveDeclaringType);
        if (!sameDeclaringType) return false;

        return !liveMethod.IsGenericMethod
            && !requestedMethod.IsGenericMethod;
    }

    public void UnpatchAll()
    {
        lock (PatchGate)
        {
            UnpatchAll(transpilers, HarmonyPatchType.Transpiler);
            UnpatchAll(prefixes, HarmonyPatchType.Prefix);
            UnpatchAll(postfixes, HarmonyPatchType.Postfix);
        }
    }

    private void UnpatchAll(List<(MethodBase, MethodInfo)> patches, HarmonyPatchType patchType)
    {
        foreach (var (method, patch) in patches.ToArray())
        {
            try
            {
                var ownedCount = CountInstalledPatches(method, patch, patchType, out var otherOwner, out var hasUntrackedGenericPatch);
                if (ownedCount == 0)
                {
                    PatchedMethods.Remove((harmony.Id, method, patch, patchType));
                    patches.Remove((method, patch));
                    continue;
                }

                if (hasUntrackedGenericPatch)
                {
                    Logger.Error("Cannot unpatch {PatchDeclaringType}.{PatchName} from {DeclaringType}.{MethodName}: the closed generic identity is not known",
                        patch.DeclaringType, patch.Name, method.DeclaringType, method.Name);
                    continue;
                }

                if (otherOwner != null)
                {
                    // Harmony's MethodInfo-only unpatch removes matching hooks across owners.
                    Logger.Error("Cannot unpatch {PatchDeclaringType}.{PatchName} from {DeclaringType}.{MethodName}: the same patch is also owned by {OtherHarmonyId}",
                        patch.DeclaringType, patch.Name, method.DeclaringType, method.Name, otherOwner);
                    continue;
                }

                harmony.Unpatch(method, patch);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to unpatch {DeclaringType}.{MethodName} from patch {PatchDeclaringType}.{PatchName}",
                    method.DeclaringType, method.Name, patch.DeclaringType, patch.Name);
            }

            PatchedMethods.Remove((harmony.Id, method, patch, patchType));
            patches.Remove((method, patch));
        }
    }

    public void Dispose()
    {
    }
}
