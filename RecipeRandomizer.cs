using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FakutoriCustom;

/// <summary>
/// Shuffles both the INPUTS and OUTPUTS of the game's transformations into a scrambled but
/// deterministic, per-save layout that is always completable (reachability-validated, never
/// soft-locks).
///
///   sigma  : a permutation of product blocks. Applied to every recipe's Product/Byproduct, the
///            hardcoded element-controller products, and FallProduct/RiseProduct. (outputs)
///   rho    : a permutation of block identities. Applied to the ingredients of the tree-based
///            recipes (combine/combust/starstruck) by relabelling the live lookup-tree paths.
///            Only recipes whose ingredients are ALL concrete blocks are remapped — their tree
///            paths are pure block-slots, so relabelling is unambiguous and reuses the editor's
///            exact ordering/expansion. Recipes with a property/color ingredient keep vanilla
///            inputs (their abstract slots can't be cleanly relabelled) but still get shuffled
///            outputs. (inputs)
///
/// Everything is done in-process against the live BlocksLibrary recipes and lookup trees; the
/// reachability search uses the real runtime block properties. See RANDOMIZER.md.
/// </summary>
public static class RecipeRandomizer
{
    // ----- Reflection handles -----
    static readonly FieldInfo F_RecipeProduct = AccessTools.Field(typeof(Recipe), "Product");
    static readonly FieldInfo F_RecipeByproduct = AccessTools.Field(typeof(Recipe), "Byproduct");
    static readonly FieldInfo F_IngredientBlock = AccessTools.Field(typeof(RecipeIngredient), "Block");
    static readonly FieldInfo F_LibFallProduct = AccessTools.Field(typeof(BlocksLibrary), "FallProduct");
    static readonly FieldInfo F_LibRiseProduct = AccessTools.Field(typeof(BlocksLibrary), "RiseProduct");

    static readonly (string field, Recipe.RecipeType type)[] TreeFields =
    {
        ("combineRecipes", Recipe.RecipeType.Combine),
        ("combustRecipes", Recipe.RecipeType.Combust),
        ("starstruckRecipes", Recipe.RecipeType.Starstruck),
    };

    static readonly (Type type, string field)[] ControllerProductFields =
    {
        (typeof(Fire), "EvolutionBlock"), (typeof(Antimatter), "OnContactProduct"),
        (typeof(Acid), "ElectricityBlock"), (typeof(QuickeningBlock), "Product"),
        (typeof(StillElement), "StillProduct"), (typeof(UniqueBlock), "LegendaryProduct"),
    };

    static readonly Dictionary<Recipe.RecipeType, BlockProperty> TypeProp = new()
    {
        { Recipe.RecipeType.Combust, BlockProperty.Burning },
        { Recipe.RecipeType.EvolvingFire, BlockProperty.Flammable },
        { Recipe.RecipeType.Starstruck, BlockProperty.ShootingStar },
        { Recipe.RecipeType.Void, BlockProperty.Antimatter },
        { Recipe.RecipeType.DissolveMetals, BlockProperty.Metal },
    };

    // ----- Captured pristine state -----
    static bool captured;
    static BlocksLibrary lib;
    static HashSet<BlockData> sources;
    static BlockData[] productPool;                 // sigma domain
    static Dictionary<BlockData, int> productIndex;
    static BlockData[] ingredientPool;              // rho domain
    static Dictionary<int, int> idToRhoSlot;        // blockId -> index in ingredientPool
    static Edge[] edges;
    static readonly List<ProductSlot> productSlots = new();   // recipe product/byproduct + hardcoded fields + fall/rise
    static readonly List<TreeSnap> treeSnaps = new();         // pristine tree paths per tree field

    public static int? CurrentSeed { get; private set; }

    // Properties that make a block unusable as a combiner ingredient (Immovable can't be fed in,
    // Antimatter annihilates, and the legendary/unique/black-hole specials). Catalysts for in-world
    // mechanics (a burning block, antimatter for Void, a metal for DissolveMetals) are NOT subject
    // to this — only Combine/Rainbow ingredient slots are.
    static bool IsCombinable(BlockData b) =>
        b != null && !b.isAnyBlock && (b.category == null || !b.category.isMachine)
        && !b.HasProperty(BlockProperty.Legendary) && !b.HasProperty(BlockProperty.Unique)
        && !b.HasProperty(BlockProperty.BlackHole) && !b.HasProperty(BlockProperty.Immovable)
        && !b.HasProperty(BlockProperty.Antimatter);

    sealed class Edge
    {
        public Recipe recipe;
        public BlockData product;
        public bool remapInputs;                    // pure-block tree recipe -> rho applies to block reqs
        public bool combinerSlots;                  // Combine/Rainbow -> property/color/any slots need combinable blocks
        public readonly List<BlockData> blockReqs = new();
        public readonly List<(BlockProperty prop, int qty)> propReqs = new();
        public readonly List<(RecipeIngredient ing, int qty)> colorReqs = new();
        public readonly List<int> anyReqs = new();
        public readonly List<BlockProperty> typeProps = new();
        public bool rainbow;
    }

    sealed class ProductSlot
    {
        public Action<BlockData> set;               // writes the mapped product
        public BlockData original;
    }

    sealed class TreeSnap
    {
        public FieldInfo field;
        public List<(int[] path, Recipe recipe)> paths = new();
    }

    // ===================================================================================
    //  Capture
    // ===================================================================================

    static void EnsureCaptured()
    {
        if (captured) return;
        lib = Resources.FindObjectsOfTypeAll<BlocksLibrary>().FirstOrDefault();
        if (lib == null) { Plugin.Logger.LogError("RecipeRandomizer: no BlocksLibrary."); return; }

        var recipes = lib.recipes ?? Array.Empty<Recipe>();
        var anything = lib.anythingBlock;

        bool IsMachine(BlockData b) => b != null && b.category != null && b.category.isMachine;
        bool ShufflableProduct(BlockData b) =>
            b != null && b != anything && !b.isAnyBlock && !IsMachine(b) && !sources.Contains(b);

        sources = new HashSet<BlockData>();
        foreach (var r in recipes)
            if (r != null && r.type == Recipe.RecipeType.Generator && r.product != null)
                sources.Add(r.product);
        foreach (var b in lib.elementBlocks)
            if (b != null && b.unlockedByDefault) sources.Add(b);

        // sigma pool: distinct non-source element-block products
        var prod = new List<BlockData>(); var seen = new HashSet<BlockData>();
        foreach (var r in recipes)
        {
            if (r == null || r.type == Recipe.RecipeType.Generator) continue;
            if (ShufflableProduct(r.product) && seen.Add(r.product)) prod.Add(r.product);
        }
        productPool = prod.ToArray();
        productIndex = new Dictionary<BlockData, int>();
        for (int i = 0; i < productPool.Length; i++) productIndex[productPool[i]] = i;

        // rho pool: blocks that can actually be fed as ingredients (combinable)
        ingredientPool = lib.elementBlocks.Where(IsCombinable).ToArray();
        idToRhoSlot = new Dictionary<int, int>();
        for (int i = 0; i < ingredientPool.Length; i++) idToRhoSlot[ingredientPool[i].blockId] = i;

        var treeTypes = new HashSet<Recipe.RecipeType> { Recipe.RecipeType.Combine, Recipe.RecipeType.Combust, Recipe.RecipeType.Starstruck };
        var edgeList = new List<Edge>();
        foreach (var r in recipes)
        {
            if (r == null) continue;
            CaptureProductSlots(r);
            if (r.type == Recipe.RecipeType.Generator) continue;
            edgeList.Add(BuildEdge(r, treeTypes));
        }
        edges = edgeList.ToArray();

        CaptureControllerAndLibFields();
        CaptureTrees();

        captured = true;
        int remap = edges.Count(e => e.remapInputs);
        Plugin.Logger.LogInfo($"RecipeRandomizer: captured {productPool.Length} products, " +
                              $"{ingredientPool.Length} ingredient blocks, {edges.Length} recipes " +
                              $"({remap} input-remappable), {treeSnaps.Sum(t => t.paths.Count)} tree paths.");
    }

    static Edge BuildEdge(Recipe r, HashSet<Recipe.RecipeType> treeTypes)
    {
        var e = new Edge { recipe = r, product = r.product };
        var ings = r.ingredients ?? Array.Empty<RecipeIngredient>();
        bool IsBlk(RecipeIngredient i) => i.ingredientType == RecipeIngredient.RecipeIngredientType.Block && i.block != null;
        bool hasBlock = ings.Any(IsBlk);
        bool blocksInPool = ings.Where(IsBlk).All(i => idToRhoSlot.ContainsKey(i.block.blockId));
        // Remap the concrete-block slots of any tree recipe; abstract property/color slots stay vanilla.
        e.remapInputs = treeTypes.Contains(r.type) && hasBlock && blocksInPool;
        e.combinerSlots = r.type == Recipe.RecipeType.Combine || r.type == Recipe.RecipeType.Rainbow;

        foreach (var ing in ings)
        {
            switch (ing.ingredientType)
            {
                case RecipeIngredient.RecipeIngredientType.Block:
                    if (ing.block != null) e.blockReqs.Add(ing.block);
                    break;
                case RecipeIngredient.RecipeIngredientType.Property:
                    e.propReqs.Add((ing.property, Math.Max(1, ing.quantity)));
                    break;
                case RecipeIngredient.RecipeIngredientType.Color:
                    e.colorReqs.Add((ing, Math.Max(1, ing.quantity)));
                    break;
                default:
                    e.anyReqs.Add(Math.Max(1, ing.quantity));
                    break;
            }
        }
        if (TypeProp.TryGetValue(r.type, out var tp)) e.typeProps.Add(tp);
        if (r.type == Recipe.RecipeType.Rainbow) e.rainbow = true;
        return e;
    }

    static void CaptureProductSlots(Recipe r)
    {
        var rr = r;
        productSlots.Add(new ProductSlot { original = r.product, set = v => F_RecipeProduct.SetValue(rr, v) });
        productSlots.Add(new ProductSlot { original = r.byproduct, set = v => F_RecipeByproduct.SetValue(rr, v) });
    }

    static void CaptureControllerAndLibFields()
    {
        foreach (var bd in lib.elementBlocks)
        {
            var ctrl = bd?.blockController;
            if (ctrl == null) continue;
            foreach (var (type, fieldName) in ControllerProductFields)
            {
                if (!type.IsInstanceOfType(ctrl)) continue;
                var fi = AccessTools.Field(type, fieldName);
                if (fi?.GetValue(ctrl) is BlockData val && val != null)
                {
                    var c = ctrl; var f = fi;
                    productSlots.Add(new ProductSlot { original = val, set = v => f.SetValue(c, v) });
                }
            }
        }
        AddLibProductSlot(F_LibFallProduct);
        AddLibProductSlot(F_LibRiseProduct);
    }

    static void AddLibProductSlot(FieldInfo fi)
    {
        if (fi?.GetValue(lib) is BlockData val && val != null)
            productSlots.Add(new ProductSlot { original = val, set = v => fi.SetValue(lib, v) });
    }

    static void CaptureTrees()
    {
        foreach (var (field, _) in TreeFields)
        {
            var fi = AccessTools.Field(typeof(BlocksLibrary), field);
            var snap = new TreeSnap { field = fi };
            if (fi?.GetValue(lib) is SortedDictionary<int, RecipeTree> root)
                WalkTree(root, new List<int>(), snap.paths);
            treeSnaps.Add(snap);
        }
    }

    static void WalkTree(SortedDictionary<int, RecipeTree> dict, List<int> prefix, List<(int[], Recipe)> outList)
    {
        foreach (var kv in dict)
        {
            var path = new List<int>(prefix) { kv.Key };
            if (kv.Value.recipe != null) outList.Add((path.ToArray(), kv.Value.recipe));
            if (kv.Value.children != null && kv.Value.children.Count > 0)
                WalkTree(kv.Value.children, path, outList);
        }
    }

    // ===================================================================================
    //  Reachability (rho over blockId, sigma over products) + hazard
    // ===================================================================================

    static HashSet<BlockData> reach;

    static bool EdgeActive(Edge e, int[] rho)
    {
        foreach (var b in e.blockReqs)
        {
            var need = e.remapInputs ? ingredientPool[rho[idToRhoSlot[b.blockId]]] : b;
            if (!reach.Contains(need)) return false;
        }
        // Combiner ingredient slots (Combine/Rainbow) only accept combinable blocks; catalysts for
        // other in-world mechanics may be anything reachable.
        int CountSlot(Func<BlockData, bool> pred) => reach.Count(b => (!e.combinerSlots || IsCombinable(b)) && pred(b));
        foreach (var (prop, qty) in e.propReqs) if (CountSlot(b => b.HasProperty(prop)) < qty) return false;
        foreach (var tp in e.typeProps) if (reach.Count(b => b.HasProperty(tp)) < 1) return false;   // catalyst: any reachable
        foreach (var q in e.anyReqs) if (CountSlot(_ => true) < q) return false;
        foreach (var (ing, qty) in e.colorReqs) if (CountSlot(ing.IsBlockMatching) < qty) return false;
        if (e.rainbow && reach.Where(IsCombinable).Select(b => b.color).Where(c => c != null).Distinct().Count() < 7) return false;
        return true;
    }

    static bool FullyReachable(int[] rho, int[] sigma)
    {
        reach = new HashSet<BlockData>(sources);
        var done = new bool[edges.Length];
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 0; i < edges.Length; i++)
            {
                if (done[i] || !EdgeActive(edges[i], rho)) continue;
                done[i] = true; changed = true;
                var p = edges[i].product;
                reach.Add(p != null && productIndex.TryGetValue(p, out var pi) ? productPool[sigma[pi]] : p);
            }
        }
        return productPool.All(p => reach.Contains(p));
    }

    static bool Hazardous(int[] rho)
    {
        foreach (var e in edges)
        {
            if (!e.remapInputs || e.recipe.type != Recipe.RecipeType.Combine) continue;
            var mapped = e.blockReqs.Select(b => ingredientPool[rho[idToRhoSlot[b.blockId]]]).ToList();
            bool burning = mapped.Any(b => b.HasProperty(BlockProperty.Burning));
            bool flammable = mapped.Any(b => b.HasProperty(BlockProperty.Flammable));
            if (burning && flammable) return true;
            if (mapped.Any(b => b.HasProperty(BlockProperty.Antimatter))) return true;
        }
        return false;
    }

    // ===================================================================================
    //  Apply
    // ===================================================================================

    public static void Apply(int seed)
    {
        EnsureCaptured();
        if (!captured) { CurrentSeed = seed; return; }

        var rng = new System.Random(seed);
        var rho = Enumerable.Range(0, ingredientPool.Length).ToArray();
        var sigma = Enumerable.Range(0, productPool.Length).ToArray();

        if (!FullyReachable(rho, sigma))
            Plugin.Logger.LogWarning("RecipeRandomizer: vanilla graph not fully reachable in our model.");

        int accepted = 0, iters = 12 * (rho.Length + sigma.Length);
        for (int k = 0; k < iters; k++)
        {
            bool doRho = rng.Next(2) == 0 && rho.Length > 1;
            var arr = doRho ? rho : sigma;
            int a = rng.Next(arr.Length), b = rng.Next(arr.Length);
            if (a == b) continue;
            (arr[a], arr[b]) = (arr[b], arr[a]);
            if (FullyReachable(rho, sigma) && !Hazardous(rho)) accepted++;
            else (arr[a], arr[b]) = (arr[b], arr[a]);
        }

        BlockData MapProduct(BlockData x) =>
            x != null && productIndex.TryGetValue(x, out var i) ? productPool[sigma[i]] : x;
        int MapId(int id) => idToRhoSlot.TryGetValue(id, out var s) ? ingredientPool[rho[s]].blockId : id;

        // sigma: outputs
        foreach (var slot in productSlots) slot.set(MapProduct(slot.original));

        // rho: rebuild trees. For a remappable recipe, relabel only the path positions that are its
        // concrete block ingredients (multiset-consume); positions filling an abstract property/color
        // slot are left vanilla, so e.g. "Sand + Time + [black]" -> "rho(Sand) + rho(Time) + [black]".
        var blockCounts = new Dictionary<Recipe, Dictionary<int, int>>();
        foreach (var e in edges.Where(e => e.remapInputs))
        {
            var cnt = new Dictionary<int, int>();
            foreach (var ing in e.recipe.ingredients)
                if (ing.ingredientType == RecipeIngredient.RecipeIngredientType.Block && ing.block != null)
                {
                    cnt.TryGetValue(ing.block.blockId, out var c);
                    cnt[ing.block.blockId] = c + Math.Max(1, ing.quantity);
                }
            blockCounts[e.recipe] = cnt;
        }

        int[] Relabel(int[] path, Recipe recipe)
        {
            if (!blockCounts.TryGetValue(recipe, out var counts)) return path;
            var remaining = new Dictionary<int, int>(counts);
            var outp = new int[path.Length];
            for (int i = 0; i < path.Length; i++)
            {
                if (remaining.TryGetValue(path[i], out var c) && c > 0) { remaining[path[i]] = c - 1; outp[i] = MapId(path[i]); }
                else outp[i] = path[i];
            }
            return outp;
        }

        foreach (var snap in treeSnaps)
        {
            var root = new SortedDictionary<int, RecipeTree>();
            foreach (var (path, recipe) in snap.paths)
                InsertPath(root, Relabel(path, recipe), recipe);
            snap.field.SetValue(lib, root);
        }

        // keep RecipeIngredient.Block consistent with the relabelled tree (for the compendium)
        foreach (var e in edges.Where(e => e.remapInputs))
            foreach (var ing in e.recipe.ingredients)
                if (ing.ingredientType == RecipeIngredient.RecipeIngredientType.Block && ing.block != null)
                    F_IngredientBlock.SetValue(ing, lib.GetBlockDataById(MapId(ing.block.blockId)));

        CurrentSeed = seed;
        int im = ingredientPool.Where((b, i) => rho[i] != i).Count();
        int om = productPool.Where((p, i) => sigma[i] != i).Count();
        Plugin.Logger.LogInfo($"RecipeRandomizer: seed {seed} applied — inputs {im}/{ingredientPool.Length}, " +
                              $"outputs {om}/{productPool.Length} remapped ({accepted} swaps). All reachable.");
    }

    static void InsertPath(SortedDictionary<int, RecipeTree> root, int[] keys, Recipe recipe)
    {
        var dict = root;
        RecipeTree node = null;
        foreach (var k in keys)
        {
            if (!dict.TryGetValue(k, out node)) { node = new RecipeTree(); dict[k] = node; }
            dict = node.children;
        }
        if (node != null) node.recipe = recipe;
    }

    // ===================================================================================
    //  Per-save seed persistence + hooks
    // ===================================================================================

    static string SidecarPath(string fileName) => Path.Combine(Application.persistentDataPath, "Saves", fileName + ".rando");

    static int LoadOrCreateSeed(string fileName)
    {
        try
        {
            var path = SidecarPath(fileName);
            if (File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var s)) return s;
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"RecipeRandomizer: read seed failed: {e.Message}"); }
        var fresh = new System.Random().Next(int.MaxValue);
        WriteSeed(fileName, fresh);
        return fresh;
    }

    static void WriteSeed(string fileName, int seed)
    {
        try
        {
            var path = SidecarPath(fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, seed.ToString());
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"RecipeRandomizer: write seed failed: {e.Message}"); }
    }

    [HarmonyPatch(typeof(GameplayManager), "LoadGame")]
    static class LoadGamePatch
    {
        static void Prefix(string fileName, bool fromVirtualFile)
        {
            if (fromVirtualFile || string.IsNullOrEmpty(fileName)) return;
            Apply(LoadOrCreateSeed(fileName));
        }
    }

    [HarmonyPatch(typeof(GameplayManager), "NewGame", new[] { typeof(bool), typeof(bool) })]
    static class NewGamePatch
    {
        static void Prefix() => Apply(new System.Random().Next(int.MaxValue));
    }

    [HarmonyPatch(typeof(GameplayManager), "SaveGame")]
    static class SaveGamePatch
    {
        static void Postfix(string fileName, bool toVirtualFile)
        {
            if (toVirtualFile || string.IsNullOrEmpty(fileName) || CurrentSeed == null) return;
            WriteSeed(fileName, CurrentSeed.Value);
        }
    }
}
