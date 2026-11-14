#!/usr/bin/env python3
"""List the game's default input bindings (action map / action / control path).

Usage: tools/bindings.py [filter]        e.g. tools/bindings.py escape    tools/bindings.py Player/Use

The bindings come from the InputActionAsset JSON embedded in
decompiled/native-all/UnityEngine.InputSystem.CustomInputActions.txt (regenerate the dumps after a
game update). Players can rebind keys in-game, so mods should look actions up by name through the
player's PlayerInput instead of hard-coding keys.
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
DUMP = os.path.join(HERE, "..", "decompiled", "native-all", "UnityEngine.InputSystem.CustomInputActions.txt")


def main(argv):
    flt = argv[0].lower() if argv else ""
    text = open(DUMP, encoding="utf-8", errors="replace").read()
    start = text.find('"{')
    if start < 0:
        print("asset JSON not found in dump")
        return 1
    js = text[start + 1:start + 400000]
    maps = re.split(r'\n        \{\n            "name": ', js)[1:]
    for m in maps:
        map_name = m.split('"')[1]
        actions = re.findall(r'"name": "([^"]+)",\s*"type": "([^"]*)",\s*"id": "[^"]+"', m)
        bindings = re.findall(
            r'"name": "([^"]*)",\s*"id": "[^"]+",\s*"path": "([^"]+)",\s*"interactions": "([^"]*)",'
            r'\s*"processors": "[^"]*",\s*"groups": "[^"]*",\s*"action": "([^"]+)"', m)
        bound = set()
        for part, path, inter, action in bindings:
            bound.add(action)
            line = f"{map_name}/{action}\t{path}" + (f"\t({part})" if part else "") + (f"\t[{inter}]" if inter else "")
            if flt in line.lower():
                print(line)
        for name, typ in actions:
            if name not in bound and flt in f"{map_name}/{name}".lower():
                print(f"{map_name}/{name}\t<no default binding>\t{typ}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
