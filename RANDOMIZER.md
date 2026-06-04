# Fakutori Recipe Randomizer

A BepInEx/Harmony mod that shuffles **every** transformation in *Fakutori* — what you get from
combining, burning, dissolving, star-striking, etc. — into a scrambled but **deterministic,
per-save** layout. Loading a save always reproduces the same scramble; a different save gets a
different one.

## Chosen direction: input + output remap

Both the **inputs** and the **outputs** of recipes are shuffled (decided with the user):

- **σ (outputs)** — permute each recipe's product/byproduct (+ the hardcoded element-controller
  products and fall/rise). *Implemented and safe.*
- **ρ (inputs)** — permute the block identities used as ingredients, applied to the
  combine/combust/starstruck recipes. The mod runs in-process, so it reads the *live* lookup trees
  at runtime and relabels each remappable recipe's existing tree paths through ρ. Reusing the live
  paths means we inherit the editor's exact ordering/expansion — no need to rebuild or reverse-
  engineer the bake. Only recipes whose ingredients are **all concrete blocks** are remapped (their
  paths are pure block-slots, so relabelling is unambiguous); recipes with a property/color
  ingredient keep vanilla inputs but still get shuffled outputs. `RecipeIngredient.Block` is updated
  to match, so the compendium stays consistent.

A reachability-validated, hazard-filtered search over (ρ, σ) — using the real runtime block
properties — guarantees every block stays craftable from the four (vanilla) generators.
`tools/simulate_input_shuffle.py` demonstrates the identical logic offline (scrambled recipe book +
a step-by-step build order proving completability). `RecipeDumper.cs` additionally dumps the live
recipes and lookup trees to JSON for inspection/verification, but the randomizer does not depend on
it.

## How the game produces blocks (reverse-engineered)

Production happens through **three** distinct mechanisms — shuffling `Recipe.Product` alone is not
enough:

1. **Recipe-driven** — reads `recipe.product` / `recipe.GetProduct()` at runtime: `Combine`,
   `Combust`, `Starstruck`, `Rainbow`. The `Recipe` objects live in `BlocksLibrary.Recipes[]` and
   are *referenced by the same object* from the baked `combineRecipes`/`combustRecipes`/
   `starstruckRecipes` lookup trees **and** from `GetRecipesByProduct` (compendium). Mutating a
   `Recipe`'s `Product`/`Byproduct` propagates to gameplay lookup *and* the compendium.
2. **Hardcoded `[SerializeField] BlockData` on element/generator controller classes** (captured at
   construction, re-passed through `GetNewInstance()`):
   `Fire.EvolutionBlock`, `Antimatter.OnContactProduct`, `Acid.ElectricityBlock`,
   `QuickeningBlock.Product`, `StillElement.StillProduct`, `UniqueBlock.LegendaryProduct`.
3. **`BlocksLibrary.FallProduct` / `RiseProduct`** — used directly by falling/rising blocks.

The lookup trees are Odin/Sirenix-baked (no runtime builder), so we never touch them. Because the
trees share the same `Recipe` instances as the flat list, **permuting outputs is the exact dual of
permuting ingredient combinations** — it rewires the entire crafting graph with zero tree work.

## Design

`RecipeRandomizer.cs` is a static class plus Harmony patches.

### Capture (once)
Snapshot every "edge": each `Recipe`'s original `Product`/`Byproduct` and its ingredient
requirements; each hardcoded controller field's value; `FallProduct`/`RiseProduct`. All later
shuffles are re-derived from this pristine snapshot.

- **Sources `S`** (always available): products of `RecipeType.Generator` recipes + blocks flagged
  `unlockedByDefault`. **Generators stay vanilla** — water/fire/earth/air always generate normally.
- **Pool `P`**: distinct non-source element-block products (machines and the "anything" block
  excluded). The shuffle is a bijection over `P`.

### Reachability-aware shuffle (`Apply(seed)`) — can never soft-lock
A bijection `σ: P → P` is **valid** iff, starting from `R = S` and repeatedly adding `σ(product)`
for every recipe whose ingredients are satisfied by `R` (a BFS fixpoint), `R` ends up covering all
of `P`. The identity is valid (vanilla). We generate a scrambled-yet-valid `σ` by seeded
hill-climbing that never leaves the valid set:

- `rng = System.Random(seed)`, `σ = identity`.
- Repeatedly pick two pool entries, tentatively swap their images, run the reachability BFS, and
  **keep** the swap only if everything is still reachable; otherwise revert.

The result is deterministic for the seed and isolated from the game's `UnityEngine.Random`. `σ` is
then written back via reflection to every `Recipe.Product`/`Byproduct`, every hardcoded controller
field, and `FallProduct`/`RiseProduct`.

### Per-save seed (sidecar)
Hooks on `GameplayManager`:
- `LoadGame(fileName)` *prefix*: read seed from `Saves/<fileName>.rando` (under
  `Application.persistentDataPath`); generate one if absent; `Apply` it.
- `NewGame(...)` *prefix*: generate a fresh seed and `Apply`.
- `SaveGame(fileName)` *postfix*: persist the current seed to `Saves/<fileName>.rando` (covers
  autosave, manual save, and save-as).

The seed is re-applied from the pristine snapshot on every load, so switching between saves in one
session reshuffles correctly to each save's seed.

## Build & test
- `dotnet build` (Debug `OutputPath` already targets the BepInEx plugins folder).
- In-game: check the BepInEx console for the logged seed; combine known blocks and confirm the
  output is scrambled vs. vanilla; save → reload → confirm identical scramble; new game → confirm a
  different seed. Generators should still produce their vanilla element. The log asserts the chosen
  `σ` is fully reachable, so no shipped seed can soft-lock.
