using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using CatPatchspace;
using UnityEngine;

namespace CatPatchFullResSprays;

internal class Patches
{
	[HarmonyPatch(typeof(Net))]
	internal static class NetPatch
	{
		[HarmonyPatch(nameof(Net.ShutdownReset))]
		[HarmonyPostfix]
		private static void ShutdownResetPatch()
		{
			NetworkController.Instance.ResetSession();
		}
	}

	[HarmonyPatch(typeof(SprayVisualController))]
	internal static class SprayVisualControllerPatch
	{
		private static Dictionary<string, int> _cachedImageByteLengths = new();

		[HarmonyPatch(nameof(SprayVisualController.TryGetOrCreateSprite))]
		[HarmonyPostfix]
		private static void TryGetOrCreateSpritePostfix(SprayVisualController __instance, ref bool __result, string cacheKey, byte[] imageBytes,
			ref Sprite sprite, ref string error)
		{
			if (!__result)
				return;
			var myCacheKey = SprayVisualController.NormalizeCacheKey(cacheKey);
			_cachedImageByteLengths.TryGetValue(myCacheKey, out int cachedLength);
			if (cachedLength == 0)
			{
				_cachedImageByteLengths[myCacheKey] = imageBytes.Length;
			}
			else if (cachedLength < imageBytes.Length)
			{
				__instance._spriteCache.Remove(myCacheKey);
				_cachedImageByteLengths.Remove(myCacheKey);
				__result = __instance.TryGetOrCreateSprite(myCacheKey, imageBytes, out sprite, out error);
			}
		}
	}

	[HarmonyPatch(typeof(SprayInspector))]
	internal static class SprayInspectorPatch
	{
		private static string GetDisplayString(int len)
		{
			return $"Spray Preview ({len / 1024}KB)";
		}
		
		[HarmonyPatch(nameof(SprayInspector.OnGUI))]
		[HarmonyTranspiler]
		private static IEnumerable<CodeInstruction> SprayInspectorOnGuiTranspiler(IEnumerable<CodeInstruction> instructions)
		{
			return new CodeMatcher(instructions)
				.MatchForward(false,
					new CodeMatch(OpCodes.Ldstr, "Spray Preview"))
				.ThrowIfInvalid("SprayInspectorOnGuiTranspiler could not find a match!")
				.RemoveInstruction()
				.Insert(
					new CodeInstruction(OpCodes.Ldarg_0),
					new CodeInstruction(OpCodes.Ldfld, 
						AccessTools.Field(typeof(SprayInspector), nameof(SprayInspector._previewSpray))),
					new CodeInstruction(OpCodes.Callvirt,
						AccessTools.PropertyGetter(typeof(SprayInstance), nameof(SprayInstance.ImageBytes))),
					new CodeInstruction(OpCodes.Ldlen),
					new CodeInstruction(OpCodes.Conv_I4),
					new CodeInstruction(OpCodes.Call, 
						AccessTools.Method(typeof(SprayInspectorPatch), nameof(SprayInspectorPatch.GetDisplayString))))
				.InstructionEnumeration();
		}
	}
	
	[HarmonyPatch(typeof(SprayDownloadCache))]
	internal static class SprayDownloadCachePatch
	{
		[HarmonyPatch(nameof(SprayDownloadCache.Tick))]
		[HarmonyTranspiler]
		private static IEnumerable<CodeInstruction> TickPatch(IEnumerable<CodeInstruction> instructions)
		{
			var codeMatcher = new CodeMatcher(instructions)
				.MatchForward(false,
					new CodeMatch(OpCodes.Ldarg_0),
					new CodeMatch(OpCodes.Ldfld,
						AccessTools.Field(typeof(SprayDownloadCache), nameof(SprayDownloadCache._cachedBytes))),
					new CodeMatch(OpCodes.Ldloc_0),
					new CodeMatch(OpCodes.Ldfld,
						AccessTools.Field(typeof(SprayDownloadCache.CompletedDownload), nameof(SprayDownloadCache.CompletedDownload.SprayId))),
					new CodeMatch(OpCodes.Ldloc_0),
					new CodeMatch(OpCodes.Ldfld,
						AccessTools.Field(typeof(SprayDownloadCache.CompletedDownload), nameof(SprayDownloadCache.CompletedDownload.ImageBytes))),
					new CodeMatch(OpCodes.Callvirt))
				.ThrowIfInvalid("SprayDownloadCachePatch.TickPatch could not find a match!");
			codeMatcher.Advance(-1);
			Plugin.Logger.LogInfo($"INFO: {codeMatcher.Opcode}, {codeMatcher.Operand}");
			var label = codeMatcher.Operand;
			codeMatcher.Advance(1);
			return codeMatcher.Insert(
					new CodeInstruction(OpCodes.Ldarg_0),
					new CodeInstruction(OpCodes.Ldfld,
						AccessTools.Field(typeof(SprayDownloadCache), nameof(SprayDownloadCache._cachedBytes))),
					new CodeInstruction(OpCodes.Ldloc_0),
					new CodeInstruction(OpCodes.Ldfld,
						AccessTools.Field(typeof(SprayDownloadCache.CompletedDownload), nameof(SprayDownloadCache.CompletedDownload.SprayId))),
					new CodeInstruction(OpCodes.Callvirt,
						AccessTools.Method(typeof(Dictionary<string, byte[]>), nameof(SprayDownloadCache._cachedBytes.ContainsKey))),
					new CodeInstruction(OpCodes.Brtrue_S, label)
				)
				.InstructionEnumeration();
		}
	}

	[HarmonyPatch(typeof(GarythecatBackendClient))]
	internal static class GarythecatBackendClientPatch
	{
		[HarmonyPatch(nameof(GarythecatBackendClient.UploadSpray))]
		[HarmonyPostfix]
		private static void UploadSprayPatch(bool __result, ref byte[] imageBytes, ref GarythecatSprayUploadResponse response)
		{
			if (!__result)
				return;
			NetworkController.Instance.UploadSprayToHost(response.spray_id, imageBytes);
		}

		[HarmonyPatch(nameof(GarythecatBackendClient.DownloadSpray))]
		[HarmonyPrefix]
		private static bool DownloadSprayPatch(ref bool __result, ref string sprayId, ref byte[] imageBytes, ref string error)
		{
			if (sprayId == null || (sprayId = sprayId.Trim()).Length == 0)
				return true; // Let the original function handle this
			
			var myImageBytes = NetworkController.Instance.TryDownloadSpray(sprayId);
			if (myImageBytes == null || myImageBytes.Length == 0)
				return true;

			__result = true;
			imageBytes = myImageBytes;
			error = null;
			return false;
		}
	}
}
