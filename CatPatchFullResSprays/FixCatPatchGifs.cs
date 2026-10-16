using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using CatPatchspace;

namespace CatPatchFullResSprays;

[HarmonyPatch(typeof(GifAnimationDecoder), nameof(GifAnimationDecoder.CreateTextureFromFrame))]
internal static class FixCatPatchGifs
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var codeMatcher = new CodeMatcher(code);

        codeMatcher.MatchForward(false, new CodeMatch(OpCodes.Ldstr, "FromImage"));
        if (codeMatcher.IsInvalid)
        {
            Plugin.Logger.LogInfo($"FixCatPatchGifs - didn't match FromImage ldstr. Skipping transpiler.");
            return code;
        }

        codeMatcher.MatchForward(false,
            new CodeMatch(OpCodes.Ldarg_0),
            new CodeMatch(OpCodes.Stelem_Ref),
            new CodeMatch(OpCodes.Call, AccessTools.Method(typeof(GifAnimationDecoder), nameof(GifAnimationDecoder.InvokeStatic))),
            new CodeMatch(OpCodes.Castclass),
            new CodeMatch(OpCodes.Stloc_3));
        if (codeMatcher.IsInvalid)
        {
            Plugin.Logger.LogInfo($"FixCatPatchGifs - didn't match erroneous code. Skipping transpiler.");
            return code;
        }

        codeMatcher.SetInstruction(new CodeInstruction(OpCodes.Ldloc_2));
        Plugin.Logger.LogInfo($"Applied transpiler");
        return codeMatcher.InstructionEnumeration();
    }
}
