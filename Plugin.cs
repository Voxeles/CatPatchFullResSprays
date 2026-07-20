using System.Collections;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CatPatchFullResSprays;

[BepInPlugin(ModGUID, ModName, ModVersion)]
[BepInDependency("meow.catpatch")]
public class Plugin : BaseUnityPlugin
{
	public const string ModGUID = "meowharder.catpatch.fullressprays";
	public const string ModName = "CatPatchFullResSprays";
	public const string ModVersion = "0.0.1";
	
	public static Plugin Instance { get; private set; } = null!;

	internal new static ManualLogSource Logger { get; private set; }

	private readonly Harmony _harmony = new(ModGUID);

	public ConfigEntry<int> ConfigMaxSpraySizeInBytes;

	void Awake()
	{
		Instance = this;
		Logger = base.Logger;

		ConfigMaxSpraySizeInBytes = Config.Bind(
			"General",
			"maxSpraySizeInBytes",
			10 * 1024 * 1024,
			"Maximum size, in bytes, that a spray image can have.\nThe server will not store sprays above this limit.\nDefaults to 10MB.");
		
		_harmony.PatchAll();
		gameObject.AddComponent<NetworkController>();
		
		Logger.LogInfo($"{ModName} is loaded.");
	}

	void OnDestroy()
	{
		_harmony?.UnpatchSelf();
		Instance = null!;
	}
}
