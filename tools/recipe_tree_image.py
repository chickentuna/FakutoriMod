#!/usr/bin/env python3
"""
Render the combine-recipe trie as a colourful image with Graphviz. Each ingredient node is filled
with that block's real in-game colour; [colour] slots use the colour itself; <property> slots use an
amber accent; products are shown as rounded leaf nodes in the product block's colour.

Needs the `dot` binary (graphviz) and the `graphviz` python lib.

Usage:  .venv/bin/python tools/recipe_tree_image.py
Writes ref/recipe_tree.png and ref/recipe_tree.svg
"""
import json
import os
import sys

from graphviz import Digraph

# "TB" = top-to-bottom (landscape: breadth spreads horizontally); "LR" = left-to-right (portrait).
RANKDIR = sys.argv[1] if len(sys.argv) > 1 else "TB"

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RECIPES = os.path.join(ROOT, "ref", "recipes.json")
BLOCKS = "/home/jpn/.steam/debian-installation/steamapps/common/Fakutori Demo/BepInEx/plugins/blocks_dump.txt"

COLOR_HEX = {
    "Blue": "#3b82f6", "Brown": "#a16207", "Orange": "#f97316", "White": "#f1f5f9",
    "Black": "#334155", "Green": "#22c55e", "Grey": "#94a3b8", "Pink": "#ec4899",
    "Purple": "#a855f7", "Red": "#ef4444", "Yellow": "#eab308", "Colorless": "#cbd5e1",
    "Machine": "#64748b", "Multicolor": "#f472b6:#38bdf8",   # gradient
}
PROP_FILL = "#fbbf24"   # amber for <property> slots
EDGE = "#64748b"


def luminance(hexcol):
    h = hexcol.split(":")[0].lstrip("#")
    r, g, b = int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16)
    return 0.299 * r + 0.587 * g + 0.114 * b


def font_for(fill):
    return "#0b1220" if luminance(fill) > 150 else "#f8fafc"


def main():
    data = json.load(open(RECIPES))["recipes"]
    blocks = {b["name"]: b for b in json.load(open(BLOCKS))["blocks"]}

    def block_fill(name):
        return COLOR_HEX.get(blocks.get(name, {}).get("color", ""), "#475569")

    def label(ing):
        if "block" in ing:
            return ing["block"], block_fill(ing["block"])
        if "property" in ing:
            return f'<{ing["property"]}>', PROP_FILL
        if "color" in ing:
            return f'[{ing["color"]}]', COLOR_HEX.get(ing["color"], "#cbd5e1")
        return "<any>", "#cbd5e1"

    def expanded(r):
        out = []
        for ing in r["ingredients"]:
            out += [label(ing)] * ing["quantity"]
        return sorted(out, key=lambda x: x[0])

    g = Digraph("recipes", format="png")
    g.attr(rankdir=RANKDIR, bgcolor="#0f172a", splines="spline", ranksep="0.8", nodesep="0.25",
           fontname="Helvetica", pad="0.4")
    g.attr("node", style="filled", fontname="Helvetica", penwidth="0", fontsize="13")
    g.attr("edge", color=EDGE, penwidth="1.4", arrowsize="0.7")
    g.node("ROOT", "combiner", shape="doublecircle", fillcolor="#1e293b", fontcolor="#e2e8f0")

    counter = [0]

    def add(parent_id, children_seq):
        """children_seq: ordered list of (label,fill) along a path; build/merge nodes under parent."""
        # not used; we walk a nested structure instead
        pass

    # build nested trie carrying (fill) per label
    tree = {}
    for r in data:
        if r["type"] != "Combine" or r["displayOnly"]:
            continue
        cur = tree
        seq = expanded(r)
        for i, (lbl, fill) in enumerate(seq):
            node = cur.setdefault((lbl, fill), {"children": {}, "products": []})
            if i == len(seq) - 1:
                node["products"].append(r["product"])
            cur = node["children"]

    def walk(subtree, parent_id):
        for (lbl, fill), node in sorted(subtree.items(), key=lambda kv: kv[0][0]):
            counter[0] += 1
            nid = f"n{counter[0]}"
            g.node(nid, lbl, shape="box", style="filled,rounded", fillcolor=fill, fontcolor=font_for(fill))
            g.edge(parent_id, nid)
            for prod in node["products"]:
                counter[0] += 1
                pid = f"p{counter[0]}"
                pf = block_fill(prod)
                g.node(pid, "▶ " + prod, shape="note", fillcolor=pf, fontcolor=font_for(pf), fontsize="14")
                g.edge(nid, pid, color="#cbd5e1", style="bold", arrowsize="0.9")
            walk(node["children"], nid)

    walk(tree, "ROOT")

    out = os.path.join(ROOT, "ref", "recipe_tree")
    g.render(out, format="png", cleanup=True)
    g.render(out, format="svg", cleanup=True)
    print(f"Wrote {os.path.relpath(out, ROOT)}.png and .svg")


if __name__ == "__main__":
    main()
