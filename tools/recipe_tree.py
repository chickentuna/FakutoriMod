#!/usr/bin/env python3
"""
Render the combine recipes as a trie (the structure the game uses for combine lookup): each level is
an ingredient, shared prefixes are merged, and a node that completes a recipe shows its product.

Ingredients are unordered in a recipe, so to draw a single tree we pick a canonical order (sorted by
label, quantities expanded into repeated entries). The real game bakes every ordering as a separate
path; this is one readable canonical view.

Usage:  .venv/bin/python tools/recipe_tree.py
Writes ref/recipe_tree.txt
"""
import json
import os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RECIPES = os.path.join(ROOT, "ref", "recipes.json")


def label(ing):
    if "block" in ing:
        return ing["block"]
    if "property" in ing:
        return f'<{ing["property"]}>'
    if "color" in ing:
        return f'[{ing["color"]}]'
    return "<any>"


def expanded_labels(r):
    out = []
    for ing in r["ingredients"]:
        out += [label(ing)] * ing["quantity"]
    return sorted(out)


def insert(tree, labels, product):
    cur = tree
    for i, lbl in enumerate(labels):
        node = cur.setdefault(lbl, {"children": {}, "products": []})
        if i == len(labels) - 1:
            node["products"].append(product)
        cur = node["children"]


def render(tree, prefix, lines):
    items = sorted(tree.items())
    for idx, (lbl, node) in enumerate(items):
        last = idx == len(items) - 1
        prod = ("  → " + ", ".join(node["products"])) if node["products"] else ""
        lines.append(f"{prefix}{'└─ ' if last else '├─ '}{lbl}{prod}")
        render(node["children"], prefix + ("   " if last else "│  "), lines)


def main():
    data = json.load(open(RECIPES))["recipes"]
    lines = []

    # displayOnly recipes are compendium aggregates / special-mechanic entries, not combiner-tree
    # lookups, so they don't belong in the trie.
    combine = [r for r in data if r["type"] == "Combine" and not r["displayOnly"]]
    tree = {}
    for r in combine:
        insert(tree, expanded_labels(r), r["product"])
    lines.append(f"COMBINE — {len(combine)} recipes as a trie "
                 "(ingredients sorted; <prop>, [color]; leaf → product)")
    lines.append("combiner")
    render(tree, "", lines)

    # Single-ingredient mechanics: just block -> product
    for t in ("Combust", "Starstruck"):
        rs = [r for r in data if r["type"] == t and not r["displayOnly"] and expanded_labels(r)]
        if not rs:
            continue
        lines.append("")
        lines.append(f"{t.upper()} — {len(rs)} single-block recipes")
        for r in sorted(rs, key=lambda r: r["product"]):
            lines.append(f"  {expanded_labels(r)[0]:<16} → {r['product']}")

    text = "\n".join(lines)
    print(text)
    out = os.path.join(ROOT, "ref", "recipe_tree.txt")
    open(out, "w").write(text + "\n")
    print(f"\n(written to {os.path.relpath(out, ROOT)})")


if __name__ == "__main__":
    main()
