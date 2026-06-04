#!/usr/bin/env python3
"""
Extract Fakutori's recipe definitions straight from the game's Unity assets — no need to run
the game. Reads the BlocksLibrary's Recipe ScriptableObjects out of resources.assets.

Recipe is a plain Unity-serialized ScriptableObject, so we parse its bytes directly (UnityPy's
typetree reader mis-sizes these objects). BlockData/BlockColor/BlockPropertyData are Odin-serialized
(SerializedScriptableObject), but we only need each one's asset name (m_Name), which is a plain base
field, so those resolve fine too.

Usage:  .venv/bin/python tools/extract_recipes.py [GAME_DATA_DIR]
Writes ref/recipes.json and ref/recipes.txt.

Field order (from the decompiled Recipe / RecipeIngredient / RecipeCost classes):
  Recipe: Type(int), Product(PPtr), Byproduct(PPtr), DisplayOnly(bool), Ingredients[], Cost
  RecipeIngredient: IngredientType(int), Block(PPtr), Property(PPtr), Color(PPtr), Quantity(int), AllowWildcard(bool)
  RecipeCost: moneyCost(int), manaCost(int), timeCost(int), probability(float)
"""
import json
import os
import struct
import sys

import UnityPy

DEFAULT_DATA = "/home/jpn/.steam/debian-installation/steamapps/common/Fakutori Playtest/Fakutori_Data"
DATA = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_DATA
OUT_DIR = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "ref")

RECIPE_TYPES = ["Combine", "Combust", "Generator", "Fall", "Starstruck", "Void", "EvolvingFire",
                "Quickening", "Time", "Rainbow", "BlackHole", "Quasar", "DissolveMetals"]
INGREDIENT_TYPES = {0: "Block", 1: "Property", 3: "Color", 4: "Any"}


class Reader:
    def __init__(self, data):
        self.d = data
        self.p = 0

    def align(self):
        self.p = (self.p + 3) & ~3

    def i32(self):
        v = struct.unpack_from("<i", self.d, self.p)[0]; self.p += 4; return v

    def i64(self):
        v = struct.unpack_from("<q", self.d, self.p)[0]; self.p += 8; return v

    def f32(self):
        v = struct.unpack_from("<f", self.d, self.p)[0]; self.p += 4; return v

    def boolean(self):
        v = self.d[self.p]; self.p += 1; self.align(); return bool(v)

    def string(self):
        n = self.i32(); s = self.d[self.p:self.p + n].decode("utf-8", "replace"); self.p += n; self.align(); return s

    def pptr(self):  # (m_FileID, m_PathID)
        return self.i32(), self.i64()


def main():
    env = UnityPy.load(DATA)

    # MonoScript path_id -> class name
    scriptmap = {}
    for o in env.objects:
        if o.type.name == "MonoScript":
            try:
                scriptmap[o.path_id] = o.read_typetree().get("m_ClassName")
            except Exception:
                pass

    def class_of(o):
        return scriptmap.get(struct.unpack_from("<q", o.get_raw_data(), 20)[0])

    # path_id -> asset name (m_Name lives at the fixed base offset 28 for every MonoBehaviour)
    name_by_pathid = {}
    recipe_objs = []
    for o in env.objects:
        if o.type.name != "MonoBehaviour":
            continue
        d = o.get_raw_data()
        try:
            n = struct.unpack_from("<i", d, 28)[0]
            name_by_pathid[o.path_id] = d[32:32 + n].decode("utf-8", "replace")
        except Exception:
            pass
        if class_of(o) == "Recipe":
            recipe_objs.append(o)

    def name(pathid):
        if pathid == 0:
            return None
        return name_by_pathid.get(pathid, f"#{pathid}")

    recipes = []
    for o in recipe_objs:
        r = Reader(o.get_raw_data())
        r.pptr()            # m_GameObject
        r.boolean()         # m_Enabled
        r.pptr()            # m_Script
        asset_name = r.string()

        rtype = r.i32()
        product = r.pptr()
        byproduct = r.pptr()
        display_only = r.boolean()
        ing_count = r.i32()
        ingredients = []
        for _ in range(ing_count):
            itype = r.i32()
            block = r.pptr()
            prop = r.pptr()
            color = r.pptr()
            qty = r.i32()
            r.boolean()     # AllowWildcard
            ing = {"type": INGREDIENT_TYPES.get(itype, itype), "quantity": qty}
            if itype == 0:
                ing["block"] = name(block[1])
            elif itype == 1:
                ing["property"] = name(prop[1])
            elif itype == 3:
                ing["color"] = name(color[1])
            ingredients.append(ing)
        cost = {"money": r.i32(), "mana": r.i32(), "time": r.i32(), "probability": round(r.f32(), 4)}

        recipes.append({
            "asset": asset_name,
            "type": RECIPE_TYPES[rtype] if 0 <= rtype < len(RECIPE_TYPES) else rtype,
            "product": name(product[1]),
            "byproduct": name(byproduct[1]),
            "displayOnly": display_only,
            "ingredients": ingredients,
            "cost": cost,
        })

    recipes.sort(key=lambda x: (str(x["type"]), str(x["product"])))
    os.makedirs(OUT_DIR, exist_ok=True)
    with open(os.path.join(OUT_DIR, "recipes.json"), "w") as f:
        json.dump({"source": DATA, "count": len(recipes), "recipes": recipes}, f, indent=2)

    # Human-readable view
    lines = [f"{len(recipes)} recipes extracted from {DATA}", ""]
    for r in recipes:
        ing = " + ".join(
            (f'{i["quantity"]}x ' if i["quantity"] > 1 else "") +
            (i.get("block") or (i.get("property") and f'<{i["property"]}>') or
             (i.get("color") and f'[{i["color"]}]') or "<any>")
            for i in r["ingredients"]
        ) or "(none)"
        extra = f'  ~{r["byproduct"]}' if r["byproduct"] else ""
        c = r["cost"]
        cost = ""
        if c["money"] or c["mana"] or c["time"] or c["probability"] != 1.0:
            cost = f'   [${c["money"]} m{c["mana"]} t{c["time"]} p{c["probability"]}]'
        lines.append(f'[{r["type"]:<13}] {ing}  ->  {r["product"]}{extra}{cost}')
    with open(os.path.join(OUT_DIR, "recipes.txt"), "w") as f:
        f.write("\n".join(lines) + "\n")

    print(f"Wrote {len(recipes)} recipes to ref/recipes.json and ref/recipes.txt")


if __name__ == "__main__":
    main()
