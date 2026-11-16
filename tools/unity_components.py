#!/usr/bin/env python3
"""List the components (with game script names) of GameObjects by name, from the shipped assets.

Usage: <venv with UnityPy>/bin/python tools/unity_components.py "<game>/Supermarket Simulator_Data" [--raw] <GameObject name> [...]

Searches every *.assets file and level3 (the store scene). Prints "<file> <name> [components]" per match, so you can see
which scripts a scene object or prefab root carries (e.g. whether it has a PhotonView). Script names are resolved through
the MonoScript PPtr in the raw MonoBehaviour data (IL2CPP builds have no type trees, so fields can't be read normally).
--raw also prints the serialized bytes of PhotonView, PhotonTransformViewOptimized and NetworkFurniture components
(after the empty-name header; bools are padded to 4 bytes).
Needs UnityPy: python3 -m venv <scratch>/upy && <scratch>/upy/bin/pip install UnityPy. Takes about a minute.
"""
import glob
import os
import struct
import sys

import UnityPy

args = sys.argv[1:]
raw = "--raw" in args
args = [a for a in args if a != "--raw"]
game, names = args[0], set(args[1:])
files = sorted(glob.glob(game + "/*.assets")) + [game + "/level3"]
env = UnityPy.load(*files)

scripts = {}
for o in env.objects:
    if o.type.name == "MonoScript":
        try:
            scripts[(os.path.basename(o.assets_file.name), o.path_id)] = o.read().m_ClassName
        except Exception:
            pass


def script_name(o):
    data = o.get_raw_data()
    fid, pid = struct.unpack_from("<iq", data, 16)  # m_Script PPtr after GameObject PPtr and m_Enabled
    af = o.assets_file
    if fid == 0:
        fn = os.path.basename(af.name)
    else:
        ex = af.externals[fid - 1]
        fn = os.path.basename(getattr(ex, "path", None) or getattr(ex, "name", ""))
    return scripts.get((fn, pid), f"MonoBehaviour({fn},{pid})")


for o in env.objects:
    if o.type.name != "GameObject":
        continue
    go = o.read()
    if go.m_Name not in names:
        continue
    comps = []
    for cp in go.m_Component:
        c = (cp.component if hasattr(cp, "component") else cp).deref()
        n = script_name(c) if c.type.name == "MonoBehaviour" else c.type.name
        comps.append(n)
        if raw and n in ("PhotonView", "PhotonTransformViewOptimized", "NetworkFurniture"):
            print("   raw", n, c.get_raw_data()[28:].hex())
    print(os.path.basename(o.assets_file.name), go.m_Name, comps)
