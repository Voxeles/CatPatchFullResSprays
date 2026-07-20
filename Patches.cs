using HarmonyLib;
using KrokoshaCasualtiesMP;
using CatPatchspace;

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
