#!/usr/bin/env python3
"""
Aggressive INPUT + OUTPUT remap simulation.

  rho  = a permutation of block identities, applied to the ingredient blocks of the tree-based
         recipes (combine/combust/starstruck).  In game this is a relabel of the combine/combust/
         starstruck tree keys, which preserves the trees' ordering convention.
  sigma = a permutation of product blocks, applied to every recipe's product (and the hardcoded
         element-controller products + fall/rise) — the output shuffle.

A recipe "Water + Fire 1 -> Steam" becomes "rho(Water) + rho(Fire 1) -> sigma(Steam)".

(rho, sigma) are found by seeded hill-climbing from identity, keeping only pairs that (a) keep every
product craftable from the four generators (reachability) and (b) avoid hazardous combos
(a Burning + a Flammable in one combine, or antimatter in a combine) when the hazard filter is on.

Scope: rho touches combine/combust/starstruck (tree-based). The property-triggered transforms (Fall,
Quickening, Time, Void, EvolvingFire, DissolveMetals, BlackHole, Quasar) act on a single
property-bearing block, so their inputs stay vanilla; their outputs are still shuffled by sigma.

Usage:  .venv/bin/python tools/simulate_input_shuffle.py [SEED ...] [--no-hazard-filter]
"""
import importlib.util
import os
import random
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
spec = importlib.util.spec_from_file_location("sim", os.path.join(ROOT, "tools", "simulate_shuffle.py"))
sim = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sim)

TREE_TYPES = {"Combine", "Combust", "Starstruck"}

# Properties that make a block unusable as a recipe/combiner ingredient:
#   Immovable -> can't be fed into a combiner (Black hole, Quasar)
#   Antimatter -> annihilates neighbours (Antimatter)
#   Legendary/Unique/BlackHole -> special blocks
NON_COMBINABLE = {"Legendary", "Unique", "BlackHole", "Immovable", "Antimatter"}


def is_combinable(b, props, color):
    return color(b) not in ("Machine", "Colorless") and not (props(b) & NON_COMBINABLE)


def rainbow_colour_blocks(have, props, color):
    """Pick one combinable block per distinct colour from `have` (up to 7), for the Rainbow recipe."""
    chosen = {}
    for b in sorted(have):
        c = color(b)
        if is_combinable(b, props, color) and c and c not in chosen:
            chosen[c] = b
    return list(chosen.items())[:7]


def remappable(r):
    """A tree-based recipe is input-remappable if it has at least one concrete block ingredient.
    We relabel its block slots through rho; property/color/any slots stay abstract (a "[black] block"
    is still any black block). So 'Sand + Time + [color]' becomes 'rho(Sand) + rho(Time) + [color]'.
    """
    return r["type"] in TREE_TYPES and any("block" in i for i in r["ingredients"])


def color_family_products(edges):
    """Quartz-style families: Combine recipes sharing a concrete-block ingredient multiset where a base
    (blocks only, or blocks + [Any color]) coexists with variants adding one specific colour. Their
    products are pinned out of sigma so the family stays coherent (base + [colour] -> the variant)."""
    by_blocks = {}
    for r in edges:
        if r["type"] != "Combine" or not r["product"]:
            continue
        ids, specific, anyc, other = [], False, False, False
        for ing in r["ingredients"]:
            if "block" in ing:
                ids.append(ing["block"])
            elif "color" in ing:
                anyc = anyc or ing["color"] == "Any color"
                specific = specific or ing["color"] != "Any color"
            else:
                other = True
        if other or not ids:
            continue
        key = ",".join(sorted(ids))
        by_blocks.setdefault(key, []).append((r, not specific, specific and not anyc))
    out = set()
    for members in by_blocks.values():
        if any(b for _, b, _ in members) and any(v for _, _, v in members):
            out.update(r["product"] for r, _, _ in members)
    return out


def build_block_pool(blocks):
    """Blocks eligible as ingredient targets for rho: must be combinable."""
    return sorted(n for n, b in blocks.items()
                  if n != "Anything"
                  and b.get("color") not in ("Machine", "Colorless")
                  and not (set(b.get("properties", [])) & NON_COMBINABLE))


def edge_active(r, R, props, color, rho):
    remap = remappable(r)
    # Combiner ingredients (Combine + Rainbow) must be combinable blocks; other recipe types act on
    # blocks in-world, where catalysts (a burning block, antimatter, a metal...) need only exist.
    slot_pool = [b for b in R if is_combinable(b, props, color)] if r["type"] in ("Combine", "Rainbow") else R
    for ing in r["ingredients"]:
        q = ing["quantity"]
        if "block" in ing:
            need = rho.get(ing["block"], ing["block"]) if remap else ing["block"]
            if need not in R:
                return False
        elif "property" in ing:
            prop = sim.PROP_ALIAS.get(ing["property"], ing["property"])
            if sum(1 for b in slot_pool if prop in props(b)) < q:
                return False
        elif "color" in ing:
            if ing["color"] == "Any color":
                if len({color(b) for b in slot_pool}) < q:
                    return False
            elif sum(1 for b in slot_pool if (color(b) == ing["color"] or "Multicolored" in props(b)
                                              or "Quartz" in props(b))) < q:
                return False
        else:
            if len(slot_pool) < q:
                return False
    tp = sim.TYPE_PROP.get(r["type"])
    if tp and sum(1 for b in R if tp in props(b)) < 1:   # catalyst: any reachable block, combinable or not
        return False
    if r["type"] == "Rainbow" and len({color(b) for b in slot_pool}) < 7:
        return False
    return True


def reachable(rho, sigma, sources, pool, edges, props, color):
    R = set(sources)
    poolset = set(pool)
    done = [False] * len(edges)
    changed = True
    while changed:
        changed = False
        for i, r in enumerate(edges):
            if done[i] or not edge_active(r, R, props, color, rho):
                continue
            done[i] = True
            changed = True
            p = r["product"]
            R.add(sigma.get(p, p) if p in poolset else p)
    return R


def fully_reachable(rho, sigma, sources, pool, edges, props, color):
    R = reachable(rho, sigma, sources, pool, edges, props, color)
    return all(p in R for p in pool)


def hazardous(rho, edges, props):
    for r in edges:
        if r["type"] != "Combine":
            continue
        mapped = [rho.get(i["block"], i["block"]) for i in r["ingredients"] if "block" in i]
        ps = [props(b) for b in mapped]
        if any("Burning" in x for x in ps) and any("Flammable" in x for x in ps):
            return True
        if any("Antimatter" in x for x in ps):
            return True
    return False


def shuffle(seed, sources, brpool, pool, edges, props, color, hazard_filter):
    rng = random.Random(seed)
    rperm = list(range(len(brpool)))   # rho over ingredient blocks
    sperm = list(range(len(pool)))     # sigma over products

    def rho():
        return {brpool[i]: brpool[rperm[i]] for i in range(len(brpool))}

    def sigma():
        return {pool[i]: pool[sperm[i]] for i in range(len(pool))}

    def ok(rh, sg):
        return (fully_reachable(rh, sg, sources, pool, edges, props, color)
                and not (hazard_filter and hazardous(rh, edges, props)))

    assert ok(rho(), sigma()), "vanilla graph invalid?!"
    accepted = 0
    iters = 12 * (len(brpool) + len(pool))
    for _ in range(iters):
        if rng.random() < 0.5 and len(brpool) > 1:
            a, b = rng.randrange(len(brpool)), rng.randrange(len(brpool))
            if a == b:
                continue
            rperm[a], rperm[b] = rperm[b], rperm[a]
            if ok(rho(), sigma()):
                accepted += 1
            else:
                rperm[a], rperm[b] = rperm[b], rperm[a]
        else:
            a, b = rng.randrange(len(pool)), rng.randrange(len(pool))
            if a == b:
                continue
            sperm[a], sperm[b] = sperm[b], sperm[a]
            if ok(rho(), sigma()):
                accepted += 1
            else:
                sperm[a], sperm[b] = sperm[b], sperm[a]
    return rho(), sigma(), accepted


def ing_str(r, rho):
    remap = remappable(r)
    out = []
    for i in r["ingredients"]:
        pre = f'{i["quantity"]}x ' if i["quantity"] > 1 else ""
        if "block" in i:
            out.append(pre + (rho.get(i["block"], i["block"]) if remap else i["block"]))
        elif "property" in i:
            out.append(pre + f'<{i["property"]}>')
        elif "color" in i:
            out.append(pre + f'[{i["color"]}]')
        else:
            out.append(pre + "<any>")
    return " + ".join(out) or "(none)"


def main():
    argv = sys.argv[1:]
    hazard_filter = "--no-hazard-filter" not in argv
    seeds = [int(a) for a in argv if not a.startswith("--")] or [12345]

    recipes, blocks = sim.load()
    sources, pool, edges, props, color = sim.build(recipes, blocks)
    brpool = build_block_pool(blocks)
    family = color_family_products(edges)        # pinned out of sigma (Quartz family stays coherent)
    pool = [p for p in pool if p not in family]

    out = [f"INPUT + OUTPUT remap   (hazard filter: {'on' if hazard_filter else 'off'})",
           f"Ingredient blocks permutable (rho): {len(brpool)}   products permutable (sigma): {len(pool)}",
           f"rho applies to: pure-block combine/combust/starstruck   sigma applies to: all products\n"]

    for seed in seeds:
        rho, sigma, accepted = shuffle(seed, sources, brpool, pool, edges, props, color, hazard_filter)
        ok = fully_reachable(rho, sigma, sources, pool, edges, props, color)
        rm = sum(1 for b in brpool if rho[b] != b)
        sm = sum(1 for p in pool if sigma[p] != p)
        out.append("=" * 82)
        out.append(f"SEED {seed}   reachable={'ALL OK' if ok else 'FAILED'}   "
                   f"inputs remapped {rm}/{len(brpool)}   outputs remapped {sm}/{len(pool)}   "
                   f"({accepted} swaps kept)")
        out.append("=" * 82)

        out.append("\n-- Scrambled recipe book (inputs via rho, outputs via sigma) --")
        for r in sorted([r for r in edges if r["product"]], key=lambda r: (r["type"], r["product"])):
            p = r["product"]
            newp = sigma.get(p, p) if p in set(pool) else p
            tag = "" if remappable(r) else "   (inputs vanilla)"
            out.append(f'  [{r["type"]:<13}] {ing_str(r, rho):<44} ->  {newp}{tag}')

        out.append("\n-- Build order (craft one of every block from the generators) --")
        out.append("  Start: " + ", ".join(sorted(sources)))
        have, used, step, progressed = set(sources), set(), 0, True
        while not set(pool).issubset(have) and progressed:
            progressed = False
            for idx, r in enumerate(edges):
                if idx in used or not edge_active(r, have, props, color, rho):
                    continue
                used.add(idx)
                p = r["product"]
                newp = sigma.get(p, p) if p in set(pool) else p
                if newp and newp not in have:
                    rainbow_picks = rainbow_colour_blocks(have, props, color) if r["type"] == "Rainbow" else None
                    have.add(newp)
                    step += 1
                    progressed = True
                    out.append(f'  {step:2}. [{r["type"]:<13}] {ing_str(r, rho):<42} ->  {newp}')
                    if rainbow_picks:
                        out.append("           colours: " + ", ".join(f"{c}={b}" for c, b in rainbow_picks))
        left = sorted(set(pool) - have)
        out.append("  " + (f"INCOMPLETE, missing: {left}" if left else f"All {len(pool)} blocks crafted in {step} steps."))
        out.append("")

    text = "\n".join(out)
    print(text)
    dst = os.path.join(ROOT, "ref", "scrambled_inputs_example.txt")
    open(dst, "w").write(text + "\n")
    print(f"\n(written to {os.path.relpath(dst, ROOT)})")


if __name__ == "__main__":
    main()
