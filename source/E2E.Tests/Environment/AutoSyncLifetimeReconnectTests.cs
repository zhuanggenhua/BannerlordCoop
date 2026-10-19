using E2E.Tests.Environment;
using GameInterface.AutoSync;
using HarmonyLib;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using Xunit;
using Xunit.Abstractions;

namespace E2E.Tests.Environment;

public sealed class AutoSyncLifetimeReconnectTests
{
    private readonly ITestOutputHelper output;

    public AutoSyncLifetimeReconnectTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact]
    public void RebuildingEnvironmentKeepsLifetimeConstructorHookInstalledOnce()
    {
        MethodBase target;
        var first = new E2ETestEnvironment(output);
        try
        {
            var harmonyId = first.Server.Resolve<Harmony>().Id;
            var (firstTarget, livePatch, expectedPatch) = FindAlleyCreatePrefix(harmonyId);
            target = firstTarget;

            output.WriteLine($"Harmony owner: {harmonyId}");
            output.WriteLine($"Target: {target}");
            output.WriteLine($"Live patch: {Describe(livePatch.PatchMethod)}");
            output.WriteLine($"Expected patch: {Describe(expectedPatch)}");
            output.WriteLine($"Live patch equals fresh closed-generic method: {livePatch.PatchMethod.Equals(expectedPatch)}");

            Assert.Single(Harmony.GetPatchInfo(target)!.Prefixes.Where(p => p.owner == harmonyId));
        }
        finally
        {
            first.Dispose();
        }

        using var reconnected = new E2ETestEnvironment(output);
        var repeatedOwner = reconnected.Server.Resolve<Harmony>().Id;
        var (repeatedTarget, repeatedLivePatch, repeatedExpectedPatch) = FindAlleyCreatePrefix(repeatedOwner);

        output.WriteLine($"Reconnect live patch: {Describe(repeatedLivePatch.PatchMethod)}");
        output.WriteLine($"Reconnect expected patch: {Describe(repeatedExpectedPatch)}");
        output.WriteLine($"Reconnect equality: {repeatedLivePatch.PatchMethod.Equals(repeatedExpectedPatch)}");

        Assert.Equal(target, repeatedTarget);
        Assert.Single(Harmony.GetPatchInfo(repeatedTarget)!.Prefixes.Where(p => p.owner == repeatedOwner));
        Assert.NotNull(reconnected.CreateRegisteredObject<CharacterObject>());
    }

    private static (MethodBase Target, Patch LivePatch, MethodInfo ExpectedPatch) FindAlleyCreatePrefix(string harmonyId)
    {
        var lifetimePatchDefinition = typeof(IAutoSyncPatchCollector).Assembly
            .GetType("GameInterface.Registry.Auto.LifetimePatches`1", throwOnError: true)!;
        var lifetimePatchType = lifetimePatchDefinition.MakeGenericType(typeof(Alley));
        var expectedPatch = AccessTools.Method(lifetimePatchType, "CreatePrefix")!;

        foreach (var constructor in typeof(Alley).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            var livePatch = Harmony.GetPatchInfo(constructor)?.Prefixes
                .SingleOrDefault(p => p.owner == harmonyId && p.PatchMethod.Name == "CreatePrefix");
            if (livePatch != null)
                return (constructor, livePatch, expectedPatch);
        }

        throw new InvalidOperationException("No Alley constructor has the expected lifetime creation prefix.");
    }

    private static string Describe(MethodInfo method) =>
        $"{method.DeclaringType?.AssemblyQualifiedName}.{method.Name}; module={method.Module.ModuleVersionId}; token=0x{method.MetadataToken:X8}; genericArgs=[{string.Join(",", method.DeclaringType?.GetGenericArguments().Select(t => t.AssemblyQualifiedName) ?? Array.Empty<string>())}]";
}
