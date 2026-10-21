using GameInterface.Services.Heroes.Patches;
using HarmonyLib;
using GameInterface.Services.CampaignAuthority;
using SandBox.View.Map;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameMenus;

namespace GameInterface.Services.Time.Patches;

[HarmonyPatch]
internal static class DisableGameMenuPausePatches
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        return new MethodBase[]
        {
            AccessTools.Method(typeof(GameMenu), nameof(GameMenu.ActivateGameMenu)),
            AccessTools.Method(typeof(GameMenu), nameof(GameMenu.StartWait)),
            AccessTools.Method(typeof(GameMenu), nameof(GameMenu.EndWait)),
            AccessTools.Method(typeof(GameMenu), nameof(GameMenu.ExitToLast)),
            AccessTools.Method(typeof(GameMenu), nameof(GameMenu.SwitchToMenu)),
            AccessTools.Method(typeof(MapScreen), "HandleLeftMouseButtonClick"),
            AccessTools.Method(typeof(MapScreen), "HandleMouse"),
            AccessTools.Method(typeof(Campaign), nameof(Campaign.Tick)),
        };
    }

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        List<CodeInstruction> instrs = instructions.ToList();

        MethodInfo timeControlSetter = typeof(Campaign).GetProperty(nameof(Campaign.TimeControlMode)).GetSetMethod();
        MethodInfo deferFunction = typeof(DisableMapClickTimeChange).GetMethod("SetTimeControlModeDeference", BindingFlags.Static | BindingFlags.NonPublic);

        if (__originalMethod.DeclaringType == typeof(Campaign) && __originalMethod.Name == nameof(Campaign.Tick))
            AdaptWorldMenuGuard(instrs);

        foreach (var instr in instrs)
        {
            if (instr.opcode == OpCodes.Callvirt &&
                instr.operand as MethodInfo == timeControlSetter)
            {
                instr.opcode = OpCodes.Call;
                instr.operand = deferFunction;
            }
        }

        return instrs;
    }

    private static void AdaptWorldMenuGuard(List<CodeInstruction> instructions)
    {
        var getter = AccessTools.PropertyGetter(typeof(GameMenu), nameof(GameMenu.IsWaitActive));
        int index = instructions.FindIndex(code => code.Calls(getter));
        if (index < 0 || instructions.Count(code => code.Calls(getter)) != 1 || index + 5 >= instructions.Count ||
            instructions[index].opcode != OpCodes.Callvirt ||
            (instructions[index + 1].opcode != OpCodes.Brtrue && instructions[index + 1].opcode != OpCodes.Brtrue_S) ||
            instructions[index + 2].opcode != OpCodes.Ldarg_0 ||
            instructions[index + 3].opcode != OpCodes.Ldc_R4 || !Equals(instructions[index + 3].operand, 0f) ||
            instructions[index + 4].opcode != OpCodes.Stfld ||
            !Equals(instructions[index + 4].operand, AccessTools.Field(typeof(Campaign), "_dt")) ||
            instructions[index + 1].operand is not Label target || !instructions[index + 5].labels.Contains(target))
            throw new InvalidOperationException("Native Campaign.Tick local-menu delta guard changed; world-time adapter was not applied.");

        instructions[index].opcode = OpCodes.Call;
        instructions[index].operand = AccessTools.Method(typeof(DisableGameMenuPausePatches), nameof(IsWorldRunningOrMenuWaiting));
    }

    private static bool IsWorldRunningOrMenuWaiting(GameMenu menu)
    {
        bool waiting = menu.IsWaitActive;
        return waiting || CampaignSimulationAuthorityPolicy.CanRunWorldSimulation();
    }
}
