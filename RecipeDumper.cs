using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace FakutoriCustom;

/// <summary>
/// Diagnostic: dumps every recipe in the live BlocksLibrary to
/// <c>BepInEx/plugins/recipes_dump.json</c> once at startup (mirrors the existing blocks_dump.txt).
/// Purely informational — used to inspect/verify the crafting graph offline. Safe to delete.
/// </summary>
[HarmonyPatch(typeof(ProgressManager), "Start")]
static class RecipeDumper
{
    static readonly FieldInfo F_IngredientColor = AccessTools.Field(typeof(RecipeIngredient), "Color");
    static bool done;

    static void Postfix()
    {
        if (done) return;
        done = true;

        var lib = Resources.FindObjectsOfTypeAll<BlocksLibrary>().FirstOrDefault();
        if (lib == null) { Plugin.Logger.LogWarning("RecipeDumper: no BlocksLibrary found."); return; }

        var recipes = lib.recipes ?? new Recipe[0];
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine($"  \"recipeCount\": {recipes.Length},");
        sb.AppendLine("  \"recipes\": [");

        for (int i = 0; i < recipes.Length; i++)
        {
            var r = recipes[i];
            sb.AppendLine("    {");
            sb.AppendLine($"      \"type\": \"{r.type}\",");
            sb.AppendLine($"      \"displayOnly\": {(r.displayOnly ? "true" : "false")},");
            sb.AppendLine($"      \"product\": {Block(r.product)},");
            sb.AppendLine($"      \"byproduct\": {Block(r.byproduct)},");
            var c = r.cost;
            sb.AppendLine($"      \"cost\": {{ \"money\": {c.moneyCost}, \"mana\": {c.manaCost}, " +
                          $"\"time\": {c.timeCost}, \"probability\": {c.probability} }},");
            sb.AppendLine("      \"ingredients\": [");
            var ings = r.ingredients ?? new RecipeIngredient[0];
            for (int j = 0; j < ings.Length; j++)
                sb.AppendLine($"        {Ingredient(ings[j])}{(j < ings.Length - 1 ? "," : "")}");
            sb.AppendLine("      ]");
            sb.AppendLine($"    }}{(i < recipes.Length - 1 ? "," : "")}");
        }

        sb.AppendLine("  ]");
        sb.AppendLine("}");

        var path = Path.Combine(BepInEx.Paths.PluginPath, "recipes_dump.json");
        try
        {
            File.WriteAllText(path, sb.ToString());
            Plugin.Logger.LogInfo($"RecipeDumper: wrote {recipes.Length} recipes to {path}");
        }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"RecipeDumper: write failed: {e.Message}"); }

        DumpTrees(lib);
    }

    // Dumps the baked combine/combust/starstruck lookup tries (each tree path = the blockId
    // sequence the combiner walks; leaves carry a recipe). This reveals the exact key ordering and
    // how property/color ingredients are expanded into concrete blocks — needed to remap inputs
    // (relabel tree keys) without breaking the soft-lock-free reachability guarantee.
    static void DumpTrees(BlocksLibrary lib)
    {
        var trees = new (string name, string field)[]
        {
            ("combine", "combineRecipes"), ("combust", "combustRecipes"), ("starstruck", "starstruckRecipes"),
        };
        var sb = new StringBuilder();
        sb.AppendLine("{");
        for (int t = 0; t < trees.Length; t++)
        {
            var fi = AccessTools.Field(typeof(BlocksLibrary), trees[t].field);
            var root = fi?.GetValue(lib) as System.Collections.IDictionary;
            var paths = new List<string>();
            if (root != null)
                WalkTree(lib, root, new List<int>(), paths);
            sb.AppendLine($"  \"{trees[t].name}\": [");
            for (int i = 0; i < paths.Count; i++)
                sb.AppendLine($"    {paths[i]}{(i < paths.Count - 1 ? "," : "")}");
            sb.AppendLine($"  ]{(t < trees.Length - 1 ? "," : "")}");
        }
        sb.AppendLine("}");

        var path = Path.Combine(BepInEx.Paths.PluginPath, "recipe_trees_dump.json");
        try
        {
            File.WriteAllText(path, sb.ToString());
            Plugin.Logger.LogInfo($"RecipeDumper: wrote lookup trees to {path}");
        }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"RecipeDumper: tree write failed: {e.Message}"); }
    }

    // RecipeTree has: public SortedDictionary<int,RecipeTree> children; public Recipe recipe;
    static readonly FieldInfo F_TreeChildren = AccessTools.Field(typeof(RecipeTree), "children");
    static readonly FieldInfo F_TreeRecipe = AccessTools.Field(typeof(RecipeTree), "recipe");

    static void WalkTree(BlocksLibrary lib, System.Collections.IDictionary dict, List<int> prefix, List<string> outPaths)
    {
        foreach (System.Collections.DictionaryEntry e in dict)
        {
            int key = (int)e.Key;
            var node = e.Value;
            var path = new List<int>(prefix) { key };

            var recipe = F_TreeRecipe?.GetValue(node) as Recipe;
            if (recipe != null)
            {
                var names = path.Select(id => Esc(lib.GetBlockDataById(id)?.blockName ?? "?"));
                outPaths.Add($"{{ \"path\": [{string.Join(", ", path)}], " +
                             $"\"names\": [{string.Join(", ", names.Select(n => $"\"{n}\""))}], " +
                             $"\"product\": {Block(recipe.product)}, \"type\": \"{recipe.type}\" }}");
            }
            if (F_TreeChildren?.GetValue(node) is System.Collections.IDictionary children && children.Count > 0)
                WalkTree(lib, children, path, outPaths);
        }
    }

    static string Block(BlockData b)
        => b == null ? "null" : $"{{ \"id\": {b.blockId}, \"name\": \"{Esc(b.blockName)}\" }}";

    static string Ingredient(RecipeIngredient ing)
    {
        var parts = new List<string> { $"\"type\": \"{ing.ingredientType}\"", $"\"quantity\": {ing.quantity}" };
        switch (ing.ingredientType)
        {
            case RecipeIngredient.RecipeIngredientType.Block:
                parts.Add($"\"block\": {Block(ing.block)}");
                break;
            case RecipeIngredient.RecipeIngredientType.Property:
                parts.Add($"\"property\": \"{ing.property}\"");
                break;
            case RecipeIngredient.RecipeIngredientType.Color:
                var color = F_IngredientColor?.GetValue(ing) as BlockColor;
                parts.Add($"\"color\": \"{Esc(color != null ? color.colorName : "?")}\"");
                break;
        }
        return "{ " + string.Join(", ", parts) + " }";
    }

    static string Esc(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
