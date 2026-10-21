using Autofac;
using Common;
using Common.Util;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using GameInterface.Services.Time.Patches;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameMenus;
using Xunit;

namespace GameInterface.Tests.Services.Time.Patches;

[Collection(ModInformationRoleCollection.Name)]
public class CampaignMenuWorldDeltaTests
{
    private static int thinkCalls;
    private static float thinkDelta;

    [Theory]
    [InlineData("a")]
    [InlineData("b")]
    public void NativeNonwaitMenuGuardMustNotStarveQualifiedWritersLateAi(string writer)
    {
        WithRole(writer, writer, false, false, () =>
        {
            var campaign = ObjectHelper.SkipConstructor<Campaign>();
            campaign.CurrentTickCount = 100;
            var menu = ObjectHelper.SkipConstructor<GameMenu>();
            var harmony = new Harmony("test.native-menu-world-delta." + writer);
            harmony.Patch(AccessTools.Method(typeof(Campaign), "PartiesThink"),
                prefix: new HarmonyMethod(typeof(CampaignMenuWorldDeltaTests), nameof(RecordThink)));
            try
            {
                thinkCalls = 0;
                campaign._dt = .25f;
                NativeMenuGuard(false)(campaign, menu);
                campaign.LateAITickAux();
                Assert.Equal(0f, campaign._dt);
                Assert.Equal(0, thinkCalls);

                campaign._dt = .25f;
                NativeMenuGuard(true)(campaign, menu);
                campaign.LateAITickAux();
                Assert.Equal(.25f, campaign._dt);
                Assert.Equal(1, thinkCalls);
                Assert.Equal(.25f, thinkDelta);
                Assert.False(menu.IsWaitActive);
            }
            finally { harmony.UnpatchAll(harmony.Id); }
        });
    }

    [Theory]
    [InlineData("a", "b", false, false)]
    [InlineData("b", "a", false, false)]
    [InlineData("a", "a", false, true)]
    [InlineData("a", "", true, false)]
    public void ObserverFrozenOrPausedContextKeepsOriginalLocalMenuGuard(
        string local, string writer, bool paused, bool frozen)
    {
        WithRole(local, writer, paused, frozen, () =>
        {
            var campaign = ObjectHelper.SkipConstructor<Campaign>();
            campaign._dt = .25f;
            NativeMenuGuard(true)(campaign, ObjectHelper.SkipConstructor<GameMenu>());
            Assert.Equal(0, campaign._dt);
        });
    }

    [Theory]
    [InlineData(CampaignExecutionPhase.Replay)]
    [InlineData(CampaignExecutionPhase.Bootstrap)]
    [InlineData(CampaignExecutionPhase.CheckpointInstall)]
    public void RestrictedPhaseCannotPreserveWorldDeltaThroughLocalMenu(CampaignExecutionPhase phase)
    {
        WithRole("a", "a", false, false, () =>
        {
            var campaign = ObjectHelper.SkipConstructor<Campaign>();
            campaign._dt = .25f;
            using (CampaignExecutionScope.Enter(phase))
                NativeMenuGuard(true)(campaign, ObjectHelper.SkipConstructor<GameMenu>());
            Assert.Equal(0, campaign._dt);
        });
    }

    [Fact]
    public void AllowedReceiveThreadCannotPreserveWorldDeltaThroughLocalMenu()
    {
        WithRole("a", "a", false, false, () =>
        {
            var campaign = ObjectHelper.SkipConstructor<Campaign>();
            campaign._dt = .25f;
            using (new AllowedThread())
                NativeMenuGuard(true)(campaign, ObjectHelper.SkipConstructor<GameMenu>());
            Assert.Equal(0, campaign._dt);
        });
    }

    [Fact]
    public void ExistingWaitMenuAndNullFailureSemanticsAreUnchanged()
    {
        WithRole("a", "b", false, false, () =>
        {
            var campaign = ObjectHelper.SkipConstructor<Campaign>();
            var menu = ObjectHelper.SkipConstructor<GameMenu>();
            menu.IsWaitActive = true;
            campaign._dt = .25f;
            NativeMenuGuard(true)(campaign, menu);
            Assert.Equal(.25f, campaign._dt);
            Assert.True(menu.IsWaitActive);
        });
        WithRole("a", "a", false, false, () =>
            Assert.Throws<NullReferenceException>(() =>
                NativeMenuGuard(true)(ObjectHelper.SkipConstructor<Campaign>(), null!)));
    }

    [Fact]
    public void ExistingAdapterMustTargetNativeTickAndReplaceOnlyItsMenuGetter()
    {
        var tick = AccessTools.Method(typeof(Campaign), nameof(Campaign.Tick));
        var targets = (IEnumerable<MethodBase>)AccessTools.Method(typeof(DisableGameMenuPausePatches), "TargetMethods").Invoke(null, null)!;
        Assert.Contains(tick, targets);
        var original = PatchProcessor.GetOriginalInstructions(tick);
        var adapted = Adapt(original, tick);
        Assert.Equal(original.Count, adapted.Count);
        Assert.Single(adapted, code => code.operand is MethodInfo method &&
            method.DeclaringType == typeof(DisableGameMenuPausePatches));
        Assert.DoesNotContain(adapted, code => code.Calls(AccessTools.PropertyGetter(typeof(GameMenu), nameof(GameMenu.IsWaitActive))));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("zero-store")]
    [InlineData("branch-target")]
    public void UnexpectedNativeGuardShapeRefusesPatchInsteadOfSilentlyOmittingWorldTimeAdaptation(string failure)
    {
        var tick = AccessTools.Method(typeof(Campaign), nameof(Campaign.Tick));
        var instructions = PatchProcessor.GetOriginalInstructions(tick);
        int getter = instructions.FindIndex(code => code.Calls(AccessTools.PropertyGetter(typeof(GameMenu), nameof(GameMenu.IsWaitActive))));
        if (failure == "missing") instructions[getter] = new CodeInstruction(OpCodes.Nop);
        if (failure == "duplicate") instructions.Add(new CodeInstruction(instructions[getter]));
        if (failure == "zero-store") instructions[getter + 3].operand = 1f;
        if (failure == "branch-target") instructions[getter + 5].labels.Clear();
        var error = Assert.Throws<TargetInvocationException>(() => Adapt(instructions, tick));
        Assert.IsType<InvalidOperationException>(error.InnerException);
    }

    [Fact]
    public void ExistingOtherMenuTargetsKeepGetterAndTimeControlSetterAdaptation()
    {
        var target = AccessTools.Method(typeof(GameMenu), nameof(GameMenu.ActivateGameMenu));
        var wait = new CodeInstruction(OpCodes.Callvirt, AccessTools.PropertyGetter(typeof(GameMenu), nameof(GameMenu.IsWaitActive)));
        var speed = new CodeInstruction(OpCodes.Callvirt, AccessTools.PropertySetter(typeof(Campaign), nameof(Campaign.TimeControlMode)));
        var result = Adapt(new List<CodeInstruction> { wait, speed }, target);
        Assert.Same(wait, result[0]);
        Assert.True(wait.Calls(AccessTools.PropertyGetter(typeof(GameMenu), nameof(GameMenu.IsWaitActive))));
        Assert.Equal(OpCodes.Call, speed.opcode);
        Assert.Equal("SetTimeControlModeDeference", ((MethodInfo)speed.operand).Name);
    }

    [Fact]
    public void QualifiedWriterDoesNotInventPositiveTimeWhenGlobalDeltaIsZero()
    {
        WithRole("a", "a", false, false, () =>
        {
            var campaign = ObjectHelper.SkipConstructor<Campaign>();
            campaign._dt = 0;
            NativeMenuGuard(true)(campaign, ObjectHelper.SkipConstructor<GameMenu>());
            Assert.Equal(0, campaign._dt);
        });
    }

    private static bool RecordThink(float dt)
    {
        thinkCalls++;
        thinkDelta = dt;
        return false;
    }

    private static List<CodeInstruction> Adapt(List<CodeInstruction> instructions, MethodBase target)
    {
        var transpiler = AccessTools.Method(typeof(DisableGameMenuPausePatches), "Transpiler");
        var args = transpiler.GetParameters().Length == 1
            ? new object[] { instructions }
            : new object[] { instructions, target };
        return ((IEnumerable<CodeInstruction>)transpiler.Invoke(null, args)!).ToList();
    }

    private static Action<Campaign, GameMenu> NativeMenuGuard(bool adapt)
    {
        var tick = AccessTools.Method(typeof(Campaign), nameof(Campaign.Tick));
        var native = PatchProcessor.GetOriginalInstructions(tick);
        int getter = native.FindIndex(code => code.Calls(AccessTools.PropertyGetter(typeof(GameMenu), nameof(GameMenu.IsWaitActive))));
        Assert.True(getter >= 0);
        Assert.True(native[getter + 1].opcode == OpCodes.Brtrue || native[getter + 1].opcode == OpCodes.Brtrue_S);
        Assert.Equal(OpCodes.Ldarg_0, native[getter + 2].opcode);
        Assert.Equal(OpCodes.Ldc_R4, native[getter + 3].opcode);
        Assert.Equal(0f, native[getter + 3].operand);
        Assert.Equal(OpCodes.Stfld, native[getter + 4].opcode);
        Assert.Equal(AccessTools.Field(typeof(Campaign), "_dt"), native[getter + 4].operand);
        var transformed = adapt ? Adapt(native, tick) : native;
        var fragment = new DynamicMethod("NativeMenuDeltaGuard", typeof(void),
            new[] { typeof(Campaign), typeof(GameMenu) }, typeof(CampaignMenuWorldDeltaTests).Module, true);
        var il = fragment.GetILGenerator();
        var end = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(transformed[getter].opcode, (MethodInfo)transformed[getter].operand);
        il.Emit(OpCodes.Brtrue, end);
        il.Emit(native[getter + 2].opcode);
        il.Emit(native[getter + 3].opcode, (float)native[getter + 3].operand);
        il.Emit(native[getter + 4].opcode, (FieldInfo)native[getter + 4].operand);
        il.MarkLabel(end);
        il.Emit(OpCodes.Ret);
        return (Action<Campaign, GameMenu>)fragment.CreateDelegate(typeof(Action<Campaign, GameMenu>));
    }

    private static void WithRole(string local, string writer, bool paused, bool frozen, Action action)
    {
        var identity = new SessionIdentity();
        var lease = new AuthorityLease("room", "authority", writer, 9, paused);
        Assert.True(identity.TryApply(lease, "a"));
        if (frozen) Assert.True(identity.TryFreezeLocalAuthority(lease, 1));
        var controller = new ControllerIdProvider();
        controller.SetControllerId(local);
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new CampaignAuthorityContext(identity, controller)).As<ICampaignAuthorityContext>();
        using var container = builder.Build();
        bool hadPrevious = ContainerProvider.TryGetContainer(out var previous);
        var oldMode = ModInformation.SessionMode;
        try
        {
            ContainerProvider.SetContainer(container);
            ModInformation.SessionMode = local == "a" ? SessionMode.ListenAuthority : SessionMode.RemoteReplica;
            action();
        }
        finally
        {
            ModInformation.SessionMode = oldMode;
            if (hadPrevious) ContainerProvider.SetContainer(previous);
            else ContainerProvider.Clear();
        }
    }
}
