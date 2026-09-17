using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace CatPatchFullResSprays;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInDependency("meow.catpatch")]
public class Plugin : BaseUnityPlugin
{
	public const string ModGuid = MyPluginInfo.PLUGIN_GUID;
	public const string ModName = MyPluginInfo.PLUGIN_NAME;
	public const string ModVersion = MyPluginInfo.PLUGIN_VERSION;
	
	public static Plugin Instance { get; private set; } = null!;

	internal new static ManualLogSource Logger { get; private set; }

	private readonly Harmony _harmony = new(ModGuid);

	public ConfigEntry<bool> ConfigShowSpraysOnMainMenu;
	public ConfigEntry<int> ConfigMaxSpraySizeInBytes;

	void Awake()
	{
		Instance = this;
		Logger = base.Logger;

		ConfigShowSpraysOnMainMenu = Config.Bind(
			"Client",
			"showSpraysOnMainMenu",
			false,
			"Whenever to show sprays on the main menu");
		ConfigMaxSpraySizeInBytes = Config.Bind(
			"Server",
			"maxSpraySizeInBytes",
			10 * 1024 * 1024,
			"Maximum size, in bytes, that a spray image can have.\nThe server will not store sprays above this limit.\nDefaults to 10MB.");
		
		MainMenuSpraysData.ShowSpraysOnMainMenu = ConfigShowSpraysOnMainMenu.Value;
		
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
