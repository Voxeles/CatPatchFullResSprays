using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using CatPatchspace;
using HarmonyLib;
using Together;
using UnityEngine;

namespace CatPatchFullResSprays;

[HarmonyPatch]
internal static class Patches
{
    [HarmonyPatch(typeof(Net), nameof(Net.ShutdownReset))]
    [HarmonyPostfix]
    private static void ResetSessionOnShutdownPatch()
    {
        NetworkController.Instance?.ResetSession();
    }

    [HarmonyPatch(typeof(SprayUploadQueue), nameof(SprayUploadQueue.EnqueueFile))]
    [HarmonyPrefix]
    private static bool UploadFilePatch(string sourceFilePath, Action<string, byte[], string> onUploaded, Action<string> onFailed)
    {
        if (NetworkController.Instance == null)
            return true;

        NetworkController.Instance.Service.Upload(sourceFilePath, onUploaded, onFailed);
        return false;
    }

    [HarmonyPatch(typeof(SprayDownloadCache), nameof(SprayDownloadCache.GetOrRequest))]
    [HarmonyPrefix]
    private static bool DownloadFilePatch(string sprayId, Action<byte[]> onReady)
    {
        if (NetworkController.Instance == null)
            return true;

        if (onReady != null)
            NetworkController.Instance.Service.Download(sprayId, onReady);

        return false;
    }

    [HarmonyPatch(typeof(SprayNetworkController), nameof(SprayNetworkController.IsValidSprayId))]
    [HarmonyPrefix]
    private static bool CustomValidSprayIdPatch(string sprayId, ref bool __result)
    {
        __result = SprayImage.IsValidId(sprayId);
        return false;
    }

    [HarmonyPatch(typeof(SprayNetworkController), nameof(SprayNetworkController.SendOrSpawnSpray))]
    [HarmonyPrefix]
    private static bool OfflineSpraysPatch(SprayNetworkController __instance, string sprayId, byte[] localImageBytes, Vector2 position, string sourceFilePath)
    {
        if (KrokoshaScavMultiplayer.network_system_is_running)
            return true;

        __instance.SpawnLocalSpray(sprayId, localImageBytes, position, 0, sourceFilePath);
        return false;
    }

    [HarmonyPatch(typeof(SprayVisualController), nameof(SprayVisualController.TryGetOrCreateSprite))]
    [HarmonyPrefix]
    private static void PrepareSpritePatch(SprayVisualController __instance, string cacheKey, byte[] imageBytes)
    {
        NetworkController.Instance?.Service.PrepareSprite(__instance, cacheKey, imageBytes);
    }

    [HarmonyPatch(typeof(SprayInspector), nameof(SprayInspector.OnGUI))]
    internal static class InspectorPatch
    {
        private static string DisplayTitle(SprayInspector inspector) => $"Spray Preview ({(inspector._previewSpray?.ImageBytes?.Length ?? 0) / 1024} KiB)";

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return new CodeMatcher(instructions)
                .MatchForward(false, new CodeMatch(OpCodes.Ldstr, "Spray Preview"))
                .ThrowIfInvalid("Could not find CatPatch's spray preview title.")
                .SetInstructionAndAdvance(new CodeInstruction(OpCodes.Ldarg_0))
                .Insert(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(InspectorPatch), nameof(DisplayTitle))))
                .InstructionEnumeration();
        }
    }
}
