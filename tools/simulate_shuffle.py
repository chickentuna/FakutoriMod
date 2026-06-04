#!/usr/bin/env python3
"""
Offline simulation of the in-game RecipeRandomizer (RecipeRandomizer.cs), so the scrambled recipe
book can be inspected without launching the game.

Mirrors the C# logic exactly:
  - sources  = generator outputs + unlockedByDefault blocks   (generators stay vanilla)
  - pool P   = distinct non-source, non-machine recipe products  (the shuffle's bijection domain)
  - a permutation sigma:P->P is VALID iff a reachability BFS from the sources, adding sigma(product)
    for every recipe whose ingredients are satisfied, ends up covering all of P
  - sigma is found by seeded hill-climbing from identity, keeping only reachability-preserving swaps

Recipe data comes from ref/recipes.json (extracted from the Playtest assets); block properties/colors
come from the blocks_dump.txt catalog (system is unchanged between builds).

Usage:  .venv/bin/python tools/simulate_shuffle.py [SEED ...]
"""
import json
import os
import random
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RECIPES = os.path.join(ROOT, "ref", "recipes.json")
BLOCKS = "/home/jpn/.steam/debian-installation/steamapps/common/Fakutori Demo/BepInEx/plugins/blocks_dump.txt"

# recipe property-ingredient asset name -> blocks_dump enum name
PROP_ALIAS = {"Raw element": "RawElement"}


def load():
    recipes = json.load(open(RECIPES))["recipes"]
    blocks = {b["name"]: b for b in json.load(open(BLOCKS))["blocks"]}
    return recipes, blocks


def build(recipes, blocks):
    def props(name):
        return set(blocks.get(name, {}).get("properties", []))

    def color(name):
        return blocks.get(name, {}).get("color")

    def is_machine(name):
        b = blocks.get(name, {})
        return b.get("category") == "Machine" or b.get("color") == "Machine"

    # sources: generator products + default-unlocked blocks
    sources = set()
    for r in recipes:
        if r["type"] == "Generator" and r["product"]:
            sources.add(r["product"])
    for name, b in blocks.items():
        if b.get("unlockedByDefault"):
            sources.add(name)

    # pool: distinct non-source, non-machine, non-"Anything" products of real recipes
    pool, seen = [], set()
    for r in recipes:
        if r["type"] == "Generator":
            continue
        p = r["product"]
        if p and p not in sources and p != "Anything" and not is_machine(p) and p not in seen:
            seen.add(p)
            pool.append(p)

    edges = [r for r in recipes if r["type"] != "Generator"]
    return sources, pool, edges, props, color


TYPE_PROP = {"Combust": "Burning", "EvolvingFire": "Flammable", "Starstruck": "ShootingStar",
             "Void": "Antimatter", "DissolveMetals": "Metal"}

# Blocks with any of these properties can't be fed into a combiner (Immovable / Antimatter / the
# legendary specials), so they can't satisfy a Combine/Rainbow ingredient slot or count as a colour.
NON_COMBINABLE = {"Legendary", "Unique", "BlackHole", "Immovable", "Antimatter"}


def edge_active(r, R, props, color):
    """True if recipe r's ingredients are all satisfied by the reachable set R."""
    def matches_color(b, c):
        if color(b) == c:
            return True
        bp = props(b)
        return "Multicolored" in bp or "Quartz" in bp

    def combinable(b):
        return color(b) not in ("Machine", "Colorless") and not (props(b) & NON_COMBINABLE)

    # Combiner ingredients (Combine/Rainbow) must be combinable; other recipes act on blocks in-world.
    slot = [b for b in R if combinable(b)] if r["type"] in ("Combine", "Rainbow") else R
    for ing in r["ingredients"]:
        q = ing["quantity"]
        if "block" in ing:
            if ing["block"] not in R:
                return False
        elif "property" in ing:
            prop = PROP_ALIAS.get(ing["property"], ing["property"])
            if sum(1 for b in slot if prop in props(b)) < q:
                return False
        elif "color" in ing:
            if ing["color"] == "Any color":
                if len({color(b) for b in slot}) < q:        # q different colors
                    return False
            elif sum(1 for b in slot if matches_color(b, ing["color"])) < q:
                return False
        else:  # Any
            if len(slot) < q:
                return False
    tp = TYPE_PROP.get(r["type"])
    if tp and sum(1 for b in R if tp in props(b)) < 1:
        return False
    if r["type"] == "Rainbow" and len({color(b) for b in slot}) < 7:
        return False
    return True


def reachable(sigma, sources, pool, edges, props, color):
    """Return the reachable block set under permutation sigma (dict block->block)."""
    R = set(sources)
    poolset = set(pool)
    done = [False] * len(edges)
    changed = True
    while changed:
        changed = False
        for i, r in enumerate(edges):
            if done[i] or not edge_active(r, R, props, color):
                continue
            done[i] = True
            changed = True
            p = r["product"]
            R.add(sigma.get(p, p) if p in poolset else p)
    return R


def fully_reachable(sigma, sources, pool, edges, props, color):
    R = reachable(sigma, sources, pool, edges, props, color)
    return all(p in R for p in pool)


def shuffle(seed, sources, pool, edges, props, color):
    rng = random.Random(seed)
    perm = list(range(len(pool)))

    def sig():
        return {pool[i]: pool[perm[i]] for i in range(len(pool))}

    assert fully_reachable(sig(), sources, pool, edges, props, color), "vanilla graph unreachable!"

    accepted = 0
    for _ in range(10 * len(pool)):
        a, b = rng.randrange(len(pool)), rng.randrange(len(pool))
        if a == b:
            continue
        perm[a], perm[b] = perm[b], perm[a]
        if fully_reachable(sig(), sources, pool, edges, props, color):
            accepted += 1
        else:
            perm[a], perm[b] = perm[b], perm[a]
    return sig(), accepted


def ing_str(r):
    out = []
    for i in r["ingredients"]:
        pre = f'{i["quantity"]}x ' if i["quantity"] > 1 else ""
        out.append(pre + (i.get("block") or (i.get("property") and f'<{i["property"]}>')
                          or (i.get("color") and f'[{i["color"]}]') or "<any>"))
    return " + ".join(out) or "(none)"


def main():
    seeds = [int(s) for s in sys.argv[1:]] or [12345]
    recipes, blocks = load()
    sources, pool, edges, props, color = build(recipes, blocks)

    report = []
    report.append(f"Sources (vanilla, never shuffled): {len(sources)}")
    report.append(f"Shuffle pool (blocks remapped): {len(pool)}")
    report.append(f"Recipe edges: {len(edges)}\n")

    for seed in seeds:
        sigma, accepted = shuffle(seed, sources, pool, edges, props, color)
        ok = fully_reachable(sigma, sources, pool, edges, props, color)
        moved = sum(1 for p in pool if sigma[p] != p)
        report.append("=" * 78)
        report.append(f"SEED {seed}   reachable={'ALL OK' if ok else 'FAILED'}   "
                      f"remapped {moved}/{len(pool)}   ({accepted} swaps kept)")
        report.append("=" * 78)

        report.append("\n-- Scrambled recipe book (ingredients are unchanged; products are shuffled) --")
        shown = [r for r in edges if r["product"]]
        shown.sort(key=lambda r: (r["type"], sigma.get(r["product"], r["product"])))
        for r in shown:
            p = r["product"]
            newp = sigma.get(p, p)
            by = r["byproduct"]
            newby = sigma.get(by, by) if by else None
            extra = f'  ~{newby}' if newby else ""
            report.append(f'  [{r["type"]:<13}] {ing_str(r):<40} ->  {newp}{extra}')

        report.append("\n-- Product remapping (what each block now comes out as) --")
        for p in sorted(pool):
            if sigma[p] != p:
                report.append(f"  {p:<18} ->  {sigma[p]}")

        report.append("")

    text = "\n".join(report)
    print(text)
    out = os.path.join(ROOT, "ref", "scrambled_example.txt")
    open(out, "w").write(text + "\n")
    print(f"\n(written to {os.path.relpath(out, ROOT)})")


if __name__ == "__main__":
    main()
