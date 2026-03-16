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
        var harmony = new Harmony("com.chickentuna.archipelago");
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

        // Create a new Block by duplicating OilBlock fr.
        BlocksLibrary blocksLibrary = Resources.FindObjectsOfTypeAll<BlocksLibrary>()[0];
        var oilData = blocksLibrary.GetBlockDataById(19);
        var oilPrefab = oilData.prefab;
        
        var mudData =  ScriptableObject.Instantiate(oilData);

        var mudPrefab = GameObject.Instantiate(oilPrefab);
        mudPrefab.SetActive(false);
        mudPrefab.name = "MudBlock";

        // Get components by name:

        // var leftEye = mudPrefab.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Left eye");

        // Plugin.Logger.LogInfo(leftEye != null ? "Found Left eye" : "Left eye NOT found");


        var face = mudPrefab.transform.Find("Face");
         foreach (Transform t in face)
         {
             Plugin.Logger.LogInfo($"Child: {t.name}");
         }
        var leftEye = face.Find("Left eye").gameObject;
        var rightEye = face.Find("Right eye").gameObject;
        var mouth = face.Find("Mouth").gameObject;
        var symbol = mudPrefab.transform.Find("Symbol").gameObject;
        var block = mudPrefab.transform.Find("Block").gameObject;

        // Change prefab reference
        var PrefabField = AccessTools.Field(typeof(BlockData), "Prefab");
        PrefabField.SetValue(mudData, mudPrefab);
        
        // Change the mouth to the entropy mouth:
        var entropyData = blocksLibrary.GetBlockDataById(38); //37
        var entropyPrefab = entropyData.prefab;
        var entropyMouth = entropyPrefab.transform.Find("Face").Find("Mouth").gameObject;
        mouth.GetComponent<SpriteRenderer>().sprite = entropyMouth.GetComponent<SpriteRenderer>().sprite;
        // Change left to void left eye:
        var voidData = blocksLibrary.GetBlockDataById(38);
        var voidPrefab = voidData.prefab;
        var voidLeftEye = voidPrefab.transform.Find("Face").Find("Left eye").gameObject;
        leftEye.GetComponent<SpriteRenderer>().sprite = voidLeftEye.GetComponent<SpriteRenderer>().sprite;
        // Change right to Copper right eye:
        var copperData = blocksLibrary.GetBlockDataById(29);
        var copperPrefab = copperData.prefab;
        var copperRightEye = copperPrefab.transform.Find("Face").Find("Right eye").gameObject;
        rightEye.GetComponent<SpriteRenderer>().sprite = copperRightEye.GetComponent<SpriteRenderer>().sprite;

        // Find shader
        var waterData = blocksLibrary.GetBlockDataById(14);
        var waterPrefab = waterData.prefab;
        var waterSR = waterPrefab.transform.Find("Block").GetComponent<SpriteRenderer>();
        var waterMat = waterSR.material;

        var mudMat = block.GetComponent<SpriteRenderer>().material;

        for (int i = 0; i < waterMat.shader.GetPropertyCount(); i++)
        {
            Plugin.Logger.LogInfo(waterMat.shader.GetPropertyName(i));
        }


                // Copy the gradient texture
        var gradientTex = waterMat.GetTexture("_GradientTexture");
        mudMat.SetTexture("_ColorGradient", gradientTex);

        // Change block id and name
        var BlockIdField = AccessTools.Field(typeof(BlockData), "BlockId");
        BlockIdField.SetValue(mudData, 100);

        var NameKeyField = AccessTools.Field(typeof(BlockData), "NameKey");
        NameKeyField.SetValue(mudData, $"custom_Mud Block");

        // Add block and create new Recipe.
        var ElementBlocksField = AccessTools.Field(typeof(BlocksLibrary), "ElementBlocks");
        var ElementBlocks = (BlockData[])ElementBlocksField.GetValue(blocksLibrary);
        
        ElementBlocks = ElementBlocks.Append(mudData).ToArray();
        ElementBlocksField.SetValue(blocksLibrary, ElementBlocks);

        // ?? not sure if this is necessary or useful, at least for simple blocks
        mudData.blockController.SetBlockData(mudData);

        Recipe oilRecipe = blocksLibrary.GetRecipesByProduct(19)[0];
        Recipe mudRecipe = ScriptableObject.Instantiate(oilRecipe);
        
        var ProductField = AccessTools.Field(typeof(Recipe), "Product");
        var RecipeIngredientsField = AccessTools.Field(typeof(Recipe), "Ingredients");
        
        RecipeIngredient[] ingredients = new RecipeIngredient[] {
            new RecipeIngredient(waterData, 2)
        };
        
        ProductField.SetValue(mudRecipe, mudData);
        RecipeIngredientsField.SetValue(mudRecipe, ingredients);

        var RecipesField = AccessTools.Field(typeof(BlocksLibrary), "Recipes");
        var Recipes = (Recipe[])RecipesField.GetValue(blocksLibrary);
        Recipes = Recipes.Append(mudRecipe).ToArray();
        RecipesField.SetValue(blocksLibrary, Recipes);
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


[HarmonyPatch(typeof(BlocksLibrary))]
internal class BlocksLibraryPatch
{

    [HarmonyPatch("GetCombineRecipeFromIngredients")]
    [HarmonyPostfix]
    public static void PostGetCombineRecipeFromIngredients(BlocksLibrary __instance, List<ElementBlock> blocks, ref ValueTuple<int, Recipe> __result)
    {
        if (blocks.Count == 2 && blocks[0].blockData.blockId == 14 && blocks[1].blockData.blockId == 14)
        {
            var blocksLibrary = Resources.FindObjectsOfTypeAll<BlocksLibrary>()[0];
            var mudRecipe = blocksLibrary.GetRecipesByProduct(100)[0];
            __result = (2, mudRecipe);
        }
    }

}