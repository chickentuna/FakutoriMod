using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using Fakutori;
using UnityEngine;
using System.Linq;
using System.Collections.Generic;
using Fakutori.Grid;
using System.IO;


namespace FakutoriCustom;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    internal static new ManualLogSource Logger;
    internal static Texture2D GeneratorAnyFxTexture;

    private void Awake()
    {
        // Plugin startup logic
        Logger = base.Logger;
        Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");
        var harmony = new Harmony("com.chickentuna.skyblock");
        harmony.PatchAll();
    }
}

[HarmonyPatch(typeof(GameplayManager))]
class GameplayManagerPatch
{
    [HarmonyPatch("NewGame")]
    [HarmonyPostfix]
    public static void PostNewGame()
    {
        Plugin.Logger.LogInfo("GameplayManager.NewGame has been called!");
        // Place 1 generator any at 0,0
        var blocksManager = AbstractSingleton<BlocksManager>.Instance;
        GridCell cell = AbstractSingleton<GridManager>.Instance.GetOrCreateGridCell(new Vector2Int(0, 0));
        blocksManager.SpawnBlock(
            cell,
            blocksManager.blocksLibrary.GetBlockDataById(100),
             duration: 0,
             ignoreBlockOnPosition: true,
             status: null,
             originalBlock: null,
             fromSave: false
        );

        
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

        var bytes = File.ReadAllBytes("BepInEx/plugins/FakutoriCustom/generator fx any.png");
        Plugin.GeneratorAnyFxTexture = new Texture2D(128, 128);
        Plugin.GeneratorAnyFxTexture.LoadImage(bytes);
        Plugin.GeneratorAnyFxTexture.name = "generator fx any";

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


        // Change block id, cost and name
        var BlockIdField = AccessTools.Field(typeof(BlockData), "BlockId");
        BlockIdField.SetValue(anyGenData, 100);

        var NameKeyField = AccessTools.Field(typeof(BlockData), "NameKey");
        NameKeyField.SetValue(anyGenData, $"custom_Any Generator");

        var MoneyValueField = AccessTools.Field(typeof(BlockData), "MoneyValue");
        MoneyValueField.SetValue(anyGenData, 0);


        // Add block to library
        var MachineBlocksField = AccessTools.Field(typeof(BlocksLibrary), "MachineBlocks");
        var MachineBlocks = (BlockData[])MachineBlocksField.GetValue(blocksLibrary);
        MachineBlocks = MachineBlocks.Append(anyGenData).ToArray();
        MachineBlocksField.SetValue(blocksLibrary, MachineBlocks);

        // new block controller (dunno how this bit works yet)
        var BlockControllerField = AccessTools.Field(typeof(BlockData), "BlockController");
        BlockControllerField.SetValue(anyGenData, new GeneratorAny());
    }
}


[HarmonyPatch(typeof(EditModeMenu))]
class EditModeMenuPatch
{

    [HarmonyPatch("FillItems")]
    [HarmonyPostfix]
    static void PostFillItems(EditModeMenu __instance)
    {
        Plugin.Logger.LogInfo("EditModeMenu.FillItems has been called!");
        var currentLayerField = AccessTools.Field(typeof(EditModeMenu), "currentLayer");
        var currentLayer = (EditModeLayer)currentLayerField.GetValue(__instance);
        if (currentLayer != EditModeLayer.Machines)
        {
            return;
        }


        // Game has just filled the menu with the standard icons.
        var menuItemsField = AccessTools.Field(typeof(EditModeMenu), "menuItems");
        var menuItems = (List<SelectMenuItem>)menuItemsField.GetValue(__instance);

        // remove from items the blocks with index 3 to 6, the default generators.
        UnityEngine.Object.Destroy(menuItems[3].gameObject);
        UnityEngine.Object.Destroy(menuItems[4].gameObject);
        UnityEngine.Object.Destroy(menuItems[5].gameObject);
        UnityEngine.Object.Destroy(menuItems[6].gameObject);
        menuItems.RemoveAt(3);
        menuItems.RemoveAt(3);
        menuItems.RemoveAt(3);
        menuItems.RemoveAt(3);
        // Remove last item (the generator)
        UnityEngine.Object.Destroy(menuItems[menuItems.Count-1].gameObject);
        menuItems.RemoveAt(menuItems.Count-1);

        menuItemsField.SetValue(__instance, menuItems);

        // bool allowCreateAnyBlock = false;
        // if (allowCreateAnyBlock)
        // {
        //     var AddItemMethod = AccessTools.Method(typeof(EditModeMenu), "AddItem",
        //         new Type[] {
        //                 typeof(Sprite),      // uiSprite
        //                 typeof(string),      // name
        //                 typeof(string),      // price
        //                 typeof(bool),        // showShadow
        //                 typeof(SelectionTool), // tool (base class of UnlockMachineBlockTool)
        //                 typeof(bool),        // isLocked
        //                 typeof(int)          // atIndex
        //         }
        //     );

        //     // Put new machine block in menu
        //     BlockData blockData = blocksLibrary.GetBlockDataById(100);

        //     AddItemMethod.Invoke(__instance, new object[] {
        //     blockData.uiSprite,
        //     blockData.blockName,
        //     blockData.moneyValue.ToString(),
        //     true,
        //     new PlaceMachineBlockTool(blockData) as SelectionTool,
        //     false,
        //     -1
        // });
        // }
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

[HarmonyPatch(typeof(BlocksManager))]
class BlocksManagerPatch
{

    [HarmonyPatch(
        "SpawnBlockVisuals",
        new Type[] { typeof(Block), typeof(GridCell) }
    )]
    [HarmonyPrefix]
    static void PreSpawnBlockVisuals(BlocksManager __instance, Block onBlock, GridCell onCell)
    {
        BlockVisuals blockVisuals = __instance.SpawnBlockVisuals(onBlock.blockData);
        ((Component)blockVisuals).transform.parent = ((Component)__instance).transform;
        ((Component)blockVisuals).transform.position = onCell.position.ToVector3();
        blockVisuals.AttachToBlock(onBlock);

        if (onBlock.blockData.blockId == 100)
        {
            ((GeneratorAny)onBlock).blockVisuals = blockVisuals;
            var go = blockVisuals.transform.Find("Visuals").Find("Block");
            var ren = go.GetComponent<SpriteRenderer>();
            var mpb = new MaterialPropertyBlock();
            ren.GetPropertyBlock(mpb);
            mpb.SetTexture("_FXTexture", Plugin.GeneratorAnyFxTexture);
            ren.SetPropertyBlock(mpb);

            // Why no working?
        }
    }
}

