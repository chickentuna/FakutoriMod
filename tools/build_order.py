#!/usr/bin/env python3
"""
Trace a craftable build order through a scrambled recipe graph: starting from the generators, keep
applying recipes whose ingredients are available until one of every block has been crafted.

Uses the same shuffle + reachability logic as simulate_shuffle.py.

Usage:  .venv/bin/python tools/build_order.py [SEED]
Writes ref/build_order_<seed>.txt
"""
import importlib.util
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
spec = importlib.util.spec_from_file_location("sim", os.path.join(ROOT, "tools", "simulate_shuffle.py"))
sim = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sim)


def trace(seed):
    recipes, blocks = sim.load()
    sources, pool, edges, props, color = sim.build(recipes, blocks)
    sigma, _ = sim.shuffle(seed, sources, pool, edges, props, color)
    poolset = set(pool)
    target = set(pool)                       # we want one of each pool block

    have = set(sources)
    crafted_order = []                       # (recipe, produced_block)
    used_recipe = set()

    # Greedy layered build: each round, fire every newly-available recipe that yields something new.
    progressed = True
    while not target.issubset(have) and progressed:
        progressed = False
        for idx, r in enumerate(edges):
            if idx in used_recipe:
                continue
            if not sim.edge_active(r, have, props, color):
                continue
            p = r["product"]
            produced = sigma.get(p, p) if p in poolset else p
            used_recipe.add(idx)
            if produced not in have:
                have.add(produced)
                crafted_order.append((r, produced))
                progressed = True

    lines = []
    lines.append(f"BUILD ORDER  —  seed {seed}")
    lines.append(f"Goal: craft one of every block ({len(target)} shuffled products)")
    lines.append("")
    lines.append("Start with (generators + default-unlocked, never shuffled):")
    lines.append("  " + ", ".join(sorted(sources)))
    lines.append("")

    for i, (r, produced) in enumerate(crafted_order, 1):
        action = {
            "Combine": "combine", "Combust": "burn", "Starstruck": "drop a star on",
            "Fall": "let fall/rise", "Void": "touch antimatter to", "EvolvingFire": "evolve fire from",
            "Quickening": "quicken", "Time": "let sit", "DissolveMetals": "dissolve", "Rainbow": "combine",
            "BlackHole": "arrange", "Quasar": "feed", "Generator": "generate",
        }.get(r["type"], r["type"].lower())
        lines.append(f"  {i:2}. {action:<16} {sim.ing_str(r):<38} ->  {produced}")

    leftover = sorted(target - have)
    lines.append("")
    if leftover:
        lines.append(f"UNREACHABLE ({len(leftover)}): {', '.join(leftover)}")
    else:
        lines.append(f"All {len(target)} blocks crafted in {len(crafted_order)} steps. Nothing left out.")

    text = "\n".join(lines)
    print(text)
    out = os.path.join(ROOT, "ref", f"build_order_{seed}.txt")
    open(out, "w").write(text + "\n")
    print(f"\n(written to {os.path.relpath(out, ROOT)})")


if __name__ == "__main__":
    seed = int(sys.argv[1]) if len(sys.argv) > 1 else 12345
    trace(seed)
