using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Fakutori;

namespace FakutoriCustom;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    internal static new ManualLogSource Logger;
        
    private void Awake()
    {
        // Plugin startup logic
        Logger = base.Logger;
        Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");
        var harmony = new Harmony("com.chickentuna.archipelago");
        harmony.PatchAll();
    }
}

[HarmonyPatch(typeof(BlocksLibrary))]
class BlocksLibraryPatch
{
    [HarmonyPatch("Awake")]
    [HarmonyPostfix]
    public static void PostAwake()
    {
        Plugin.Logger.LogInfo("BlocksLibrary.Awake has been called!");
        // Additional logic to modify the BlocksLibrary after initialization can be added here.
    }
}