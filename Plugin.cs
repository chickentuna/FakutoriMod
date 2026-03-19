using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using Fakutori;
using UnityEngine;
using System.Linq;
using System.Collections.Generic;


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
        var harmony = new Harmony("com.chickentuna.skyblock");
        harmony.PatchAll();
    }
}

[HarmonyPatch(typeof(ProgressManager))]
class ProgressManagerPatch
{
    [HarmonyPatch("Start")]
    [HarmonyPostfix]
    public static void PostStart()
    {
        Plugin.Logger.LogInfo("ProgressManager.Start has been called!");

        // Create a new Block by duplicating Generator Water.
        BlocksLibrary blocksLibrary = Resources.FindObjectsOfTypeAll<BlocksLibrary>()[0];
        var waterGenData = blocksLibrary.GetBlockDataById(1);
        var waterGenPrefab = waterGenData.prefab;

        var anyGenData = ScriptableObject.Instantiate(waterGenData);

        var anyGenPrefab = GameObject.Instantiate(waterGenPrefab);
        anyGenPrefab.SetActive(false);
        anyGenPrefab.name = "Generator any";

        // Change prefab reference
        var PrefabField = AccessTools.Field(typeof(BlockData), "Prefab");
        PrefabField.SetValue(anyGenData, anyGenPrefab);

        // Change stuff in the prefab

        // var symbol = mudPrefab.transform.Find("Symbol").gameObject;
        var block = anyGenPrefab.transform.Find("Visuals").Find("Block").gameObject;



        // Change block id and name
        var BlockIdField = AccessTools.Field(typeof(BlockData), "BlockId");
        BlockIdField.SetValue(anyGenData, 100);

        var NameKeyField = AccessTools.Field(typeof(BlockData), "NameKey");
        NameKeyField.SetValue(anyGenData, $"custom_Any Generator");

        // Icon
        
        var mat = block.GetComponent<SpriteRenderer>().material;
        var renderers = anyGenPrefab.GetComponentsInChildren<Renderer>(true);

        foreach (var r in renderers)
        {
            Plugin.Logger.LogInfo($"Renderer: {r.name} ({r.GetType().Name})");

            foreach (var m in r.materials)
            {
                if (m != null)
                {
                    Plugin.Logger.LogInfo($"  Mat: {m.name} | Shader: {m.shader?.name}");
                }
            }
        }

        // Add block to library
        var MachineBlocksField = AccessTools.Field(typeof(BlocksLibrary), "MachineBlocks");
        var MachineBlocks = (BlockData[])MachineBlocksField.GetValue(blocksLibrary);
        MachineBlocks = MachineBlocks.Append(anyGenData).ToArray();
        MachineBlocksField.SetValue(blocksLibrary, MachineBlocks);

        // new block controller (dunno how this bit works yet)
        var BlockControllerField = AccessTools.Field(typeof(BlockData), "BlockController");
        BlockControllerField.SetValue(anyGenData, new GeneratorAny());
        //TODO: make a custom class
    }
}

class EditModeMenuPatch
{

    [HarmonyPatch("FillItems")]
    [HarmonyPostfix]
    static void PostFillItems(EditModeMenu __instance)
    {
        var currentLayerField = AccessTools.Field(typeof(EditModeMenu), "currentLayer");
        var currentLayer = (EditModeLayer)currentLayerField.GetValue(__instance);
        if (currentLayer != EditModeLayer.Machines)
        {
            return;
        }


        // Game has just filled the menu with the standard icons.
        var menuItemsField = AccessTools.Field(typeof(EditModeMenu), "menuItems");
        var menuItems = (List<SelectMenuItem>)menuItemsField.GetValue(__instance);
        var blocksLibrary = AbstractSingleton<BlocksManager>.Instance.blocksLibrary;

        var AddItemMethod = AccessTools.Method(typeof(EditModeMenu), "AddItem",
            new Type[] {
                    typeof(Sprite),      // uiSprite
                    typeof(string),      // name
                    typeof(string),      // price
                    typeof(bool),        // showShadow
                    typeof(SelectionTool), // tool (base class of UnlockMachineBlockTool)
                    typeof(bool),        // isLocked
                    typeof(int)          // atIndex
            }
        );

        // Put new machine block in menu
        BlockData blockData = blocksLibrary.GetBlockDataById(100);

        AddItemMethod.Invoke(__instance, new object[] {
            blockData.uiSprite,
            blockData.blockName,
            blockData.moneyValue.ToString(),
            true,
            new PlaceMachineBlockTool(blockData) as SelectionTool,
            false,
            -1
        });
    }
}

[HarmonyPatch(typeof(BlockData))]
class BlockNamePropertyPatch
{
    [HarmonyPatch("blockName", MethodType.Getter)]
    [HarmonyPrefix]
    static bool Prefix(BlockData __instance, ref string __result)
    {
        var NameKeyField = AccessTools.Field(typeof(BlockData), "NameKey");
        var NameKey = (string)NameKeyField.GetValue(__instance);
        if (NameKey.StartsWith("custom_"))
        {
            __result = NameKey.Substring("custom_".Length);
            return false;
        }
        return true;
    }
}

