using GameInterface.AutoSync;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace GameInterface.Tests.AutoSync;

public class AutoSyncPatchCollectorReconnectTests
{
    private static int prefixCalls;
    private static int postfixCalls;
    private static int transpilerCalls;

    [Fact]
    public void PatchAll_RepeatedAndRebuiltCollectorWithSameOwner_InvokesEachHookOnce()
    {
        ResetCounts();
        var harmony = CreateHarmony();
        var collector = CreateCollector(harmony);
        AutoSyncPatchCollector? rebuiltCollector = null;

        try
        {
            collector.PatchAll();
            AssertPatchCounts(harmony);
            var transpilerCallsAfterInstall = transpilerCalls;
            Assert.True(transpilerCallsAfterInstall > 0);

            Assert.Equal(5, PatchTarget(5));
            AssertHookCounts(1);

            collector.PatchAll();
            AssertPatchCounts(harmony);
            Assert.Equal(transpilerCallsAfterInstall, transpilerCalls);
            Assert.Equal(6, PatchTarget(6));
            AssertHookCounts(2);

            rebuiltCollector = CreateCollector(new Harmony(harmony.Id));
            rebuiltCollector.PatchAll();
            AssertPatchCounts(harmony);
            Assert.Equal(transpilerCallsAfterInstall, transpilerCalls);
            Assert.Equal(7, PatchTarget(7));
            AssertHookCounts(3);
        }
        finally
        {
            rebuiltCollector?.UnpatchAll();
            collector.UnpatchAll();
        }
    }

    [Fact]
    public void PatchAll_SameHookUnderAnotherOwnerIsRejectedAndCannotUnpatchTheFirstOwner()
    {
        ResetCounts();
        var firstHarmony = CreateHarmony();
        var secondHarmony = CreateHarmony();
        var firstCollector = CreateCollector(firstHarmony);
        var secondCollector = CreateCollector(secondHarmony);

        try
        {
            firstCollector.PatchAll();
            AssertPatchCounts(firstHarmony);
            AssertPatchCounts(secondHarmony, expected: 0);
            var transpilerCallsAfterFirstInstall = transpilerCalls;
            Assert.True(transpilerCallsAfterFirstInstall > 0);

            secondCollector.PatchAll();
            AssertPatchCounts(firstHarmony);
            AssertPatchCounts(secondHarmony, expected: 0);
            Assert.Equal(transpilerCallsAfterFirstInstall, transpilerCalls);

            Assert.Equal(11, PatchTarget(11));
            AssertHookCounts(1);

            secondCollector.UnpatchAll();
            AssertPatchCounts(firstHarmony);
            AssertPatchCounts(secondHarmony, expected: 0);

            Assert.Equal(12, PatchTarget(12));
            AssertHookCounts(2);
        }
        finally
        {
            secondCollector.UnpatchAll();
            firstCollector.UnpatchAll();
        }
    }

    [Fact]
    public void PatchAll_WhenLivePatchWasExternallyRemoved_ReinstallsIt()
    {
        ResetCounts();
        var harmony = CreateHarmony();
        var collector = CreateCollector(harmony);

        try
        {
            collector.PatchAll();
            AssertPatchCounts(harmony);
            var transpilerCallsAfterInstall = transpilerCalls;
            Assert.True(transpilerCallsAfterInstall > 0);

            Assert.Equal(8, PatchTarget(8));
            AssertHookCounts(1);

            // Simulate Harmony removing these patches while the collector's static bookkeeping remains.
            harmony.UnpatchAll(harmony.Id);
            Assert.Equal(9, PatchTarget(9));
            AssertHookCounts(1);

            collector.PatchAll();
            AssertPatchCounts(harmony);
            Assert.True(transpilerCalls > transpilerCallsAfterInstall);
            Assert.Equal(10, PatchTarget(10));
            AssertHookCounts(2);
        }
        finally
        {
            collector.UnpatchAll();
        }
    }

    [Fact]
    public void PatchAll_RebuiltCollectorRecognizesOpenHarmonyMetadataForLifetimePatch()
    {
        var target = typeof(GenericPatchTarget).GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null)!;
        var firstPatchMethod = GetLifetimeCreatePrefix(typeof(GenericPatchTarget));
        var rebuiltPatchMethod = GetLifetimeCreatePrefix(typeof(GenericPatchTarget));
        var harmony = CreateHarmony();
        var firstCollector = new AutoSyncPatchCollector(harmony);
        var rebuiltCollector = new AutoSyncPatchCollector(new Harmony(harmony.Id));
        firstCollector.AddPrefix(target, firstPatchMethod);
        rebuiltCollector.AddPrefix(target, rebuiltPatchMethod);

        try
        {
            firstCollector.PatchAll();
            var installed = Assert.Single(Harmony.GetPatchInfo(target)!.Prefixes.Where(p => p.owner == harmony.Id));
            Assert.True(installed.PatchMethod.ContainsGenericParameters);
            Assert.Equal(firstPatchMethod.Module.ModuleVersionId, installed.PatchMethod.Module.ModuleVersionId);
            Assert.Equal(firstPatchMethod.MetadataToken, installed.PatchMethod.MetadataToken);
            Assert.Equal(firstPatchMethod.DeclaringType!.GetGenericTypeDefinition(), installed.PatchMethod.DeclaringType);
            Assert.False(installed.PatchMethod.Equals(firstPatchMethod));

            rebuiltCollector.PatchAll();

            Assert.Single(Harmony.GetPatchInfo(target)!.Prefixes.Where(p => p.owner == harmony.Id));
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
        }
    }

    [Fact]
    public void PatchAll_UnknownOpenGenericMetadataIsNotTreatedAsVerifiedInstallation()
    {
        var target = typeof(UntrackedGenericPatchTarget).GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null)!;
        var manualPatchMethod = GetLifetimeCreatePrefix(typeof(UntrackedGenericPatchTarget));
        var collectorPatchMethod = GetLifetimeCreatePrefix(typeof(UntrackedGenericPatchTarget));
        var harmony = CreateHarmony();
        var collector = new AutoSyncPatchCollector(harmony);

        try
        {
            harmony.Patch(target, prefix: new HarmonyMethod(manualPatchMethod));
            collector.AddPrefix(target, collectorPatchMethod);
            collector.PatchAll();

            var installed = Assert.Single(Harmony.GetPatchInfo(target)!.Prefixes.Where(p => p.owner == harmony.Id));
            Assert.True(installed.PatchMethod.ContainsGenericParameters);
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
        }
    }

    private static AutoSyncPatchCollector CreateCollector(Harmony harmony)
    {
        var collector = new AutoSyncPatchCollector(harmony);
        collector.AddTranspiler(TargetMethod(), Method(nameof(PatchTranspiler)));
        collector.AddPrefix(TargetMethod(), Method(nameof(PatchPrefix)));
        collector.AddPostfix(TargetMethod(), Method(nameof(PatchPostfix)));
        return collector;
    }

    private static MethodInfo GetLifetimeCreatePrefix(Type managedType)
    {
        var patchDefinition = typeof(IAutoSyncPatchCollector).Assembly
            .GetType("GameInterface.Registry.Auto.LifetimePatches`1", throwOnError: true)!;
        var patchType = patchDefinition.MakeGenericType(managedType);
        return AccessTools.Method(patchType, "CreatePrefix")!;
    }

    private static Harmony CreateHarmony() => new($"AutoSyncPatchCollectorReconnectTests.{System.Guid.NewGuid():N}");

    private static MethodBase TargetMethod() => Method(nameof(PatchTarget));

    private static MethodInfo Method(string name) =>
        typeof(AutoSyncPatchCollectorReconnectTests).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;

    private static void AssertPatchCounts(Harmony harmony, int expected = 1)
    {
        var patchInfo = Harmony.GetPatchInfo(TargetMethod());
        Assert.NotNull(patchInfo);
        Assert.Equal(expected, patchInfo!.Prefixes.Count(p => p.owner == harmony.Id && p.PatchMethod == Method(nameof(PatchPrefix))));
        Assert.Equal(expected, patchInfo.Postfixes.Count(p => p.owner == harmony.Id && p.PatchMethod == Method(nameof(PatchPostfix))));
        Assert.Equal(expected, patchInfo.Transpilers.Count(p => p.owner == harmony.Id && p.PatchMethod == Method(nameof(PatchTranspiler))));
    }

    private static void AssertHookCounts(int expected)
    {
        Assert.Equal(expected, prefixCalls);
        Assert.Equal(expected, postfixCalls);
    }

    private static void ResetCounts()
    {
        prefixCalls = 0;
        postfixCalls = 0;
        transpilerCalls = 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int PatchTarget(int value) => value;

    private static void PatchPrefix() => prefixCalls++;

    private static void PatchPostfix() => postfixCalls++;

    private static IEnumerable<CodeInstruction> PatchTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        transpilerCalls++;
        return instructions;
    }

    private sealed class GenericPatchTarget
    {
        public GenericPatchTarget() { }
    }

    private sealed class UntrackedGenericPatchTarget
    {
        public UntrackedGenericPatchTarget() { }
    }
}
