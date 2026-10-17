using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Common;
using Common.Util;
using GameInterface.Services.CampaignAuthority;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.Core;

namespace GameInterface.Services.Time.Patches
{
    /// <summary>
    /// Enables campaign ticks while in menu states to prevent local pausing and desynchronisation.
    /// </summary>
    [HarmonyPatch(typeof(MapState), "OnMapModeTick")]
    class MapStatePatch
    {
        [HarmonyPrefix]
        private static bool OnMapModeTickPrefix()
        {
            bool authorityContextAvailable =
                ContainerProvider.TryResolve<ICampaignAuthorityContext>(out var authorityContext);
            return ShouldTickMapMode(
                ModInformation.IsListenAuthority,
                authorityContextAvailable,
                authorityContext);
        }

        internal static bool ShouldTickMapMode(
            bool isListenAuthority,
            bool authorityContextAvailable,
            ICampaignAuthorityContext authorityContext)
        {
            if (!isListenAuthority || !authorityContextAvailable)
                return true;

            bool localAuthority = authorityContext.TryGetLocalAuthorityLease(out var lease);
            return !lease.IsValid || localAuthority;
        }

        //  Removes the "base.GameStateManager.ActiveState == this" condition from the guard clause.
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> instrs = instructions.ToList();

            MethodInfo activeStateGetter = typeof(GameStateManager).GetProperty(nameof(GameStateManager.ActiveState)).GetGetMethod();

            for (int i = 0; i < instrs.Count(); i++)
            {
                var instr = instrs[i];

                if (instr.opcode == OpCodes.Callvirt &&
                   instr.operand as MethodInfo == activeStateGetter &&
                   instrs.Count() > i + 2 &&
                   instrs[i - 2].opcode == OpCodes.Ldarg_0 &&
                   instrs[i + 2].opcode == OpCodes.Bne_Un_S)
                {
                    instrs.RemoveRange(i - 2, 5);
                    break;
                }
            }

            return instrs;
        }
    }
}
