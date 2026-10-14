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
	public ConfigEntry<int> ConfigMaxSpraySizeMiB;
	public ConfigEntry<int> ConfigMemoryCacheMiB;
	public ConfigEntry<int> ConfigDiskCacheMiB;
	public ConfigEntry<bool> ConfigClearDiskCacheOnExit;
	public ConfigEntry<bool> ConfigRequireBackend;
	public ConfigEntry<bool> ConfigVerbose;

	void Awake()
	{
		Instance = this;
		Logger = base.Logger;

		ConfigShowSpraysOnMainMenu = Config.Bind(
			"Client",
			"showSpraysOnMainMenu",
			true,
			"Display sprays on the main menu");
		ConfigMemoryCacheMiB = Config.Bind(
			"Cache",
			"memoryCacheMiB",
			64,
			new ConfigDescription("Maximum size for the RAM cache",
				new AcceptableValueRange<int>(11, 4096)));
		ConfigDiskCacheMiB = Config.Bind(
			"Cache",
			"diskCacheMiB",
			1024,
			new ConfigDescription("Maximum size for the on-disk cache",
				new AcceptableValueRange<int>(0, 10240)));
		ConfigClearDiskCacheOnExit = Config.Bind(
			"Cache",
			"clearDiskCacheOnExit",
			true,
			"Delete cached downloads on game exit.\nThis is about the on-disk cache located in 'BepInEx/config/Cat-Patch/FullResSpraysCache' - Your spray library is never deleted");
		ConfigRequireBackend = Config.Bind(
			"Server",
			"requireBackendUpload",
			false,
			"Only accept uploads reported by the client as successfully uploaded to CatPatch's HTTP backend");
		ConfigMaxSpraySizeMiB = Config.Bind(
			"Server",
			"maxSpraySizeMiB",
			10,
			new ConfigDescription("Maximum image file size accepted by the host",
				new AcceptableValueRange<int>(1, SprayImage.HardByteSizeLimit / 1024 / 1024)));
		ConfigVerbose = Config.Bind(
			"Debug",
			"verboseMode",
			false,
			"Print info messages to debug console");
		
		MainMenuSpraysData.ShowSpraysOnMainMenu = ConfigShowSpraysOnMainMenu.Value;
		
		gameObject.AddComponent<NetworkController>();
		_harmony.PatchAll();
		
		Logger.LogInfo($"{ModName} is loaded.");
	}

	void OnDestroy()
	{
		_harmony?.UnpatchSelf();
		Instance = null!;
	}

	internal static void LogVerbose(string message)
	{
		if (Instance.ConfigVerbose.Value)
			Logger.LogInfo(message);
	}
}
