#!/usr/bin/env python3
"""Print a GameObject hierarchy from the shipped assets: components (with game script names), RectTransform anchors and
sizes, and the materials of renderers (name, shader, base texture, base colour).

Usage: <venv with UnityPy>/bin/python -I tools/unity_tree.py "<game>/Supermarket Simulator_Data" <file[,file...]> \
           (--component <ScriptOrComponentName> | --root <GameObject name>) [--depth N] [--no-materials] [--keep-bones]

  <file>        assets file(s) to search, e.g. level3 (the store scene), sharedassets2.assets (prefabs), resources.assets
  --component   every object that has this component (e.g. CustomizationTab), plus its ancestors; prints its sub-tree
  --root        every parentless object with this name (prefab roots such as Restocker); prints its sub-tree
  --depth N     sub-tree depth (default 6)
  --no-materials / --keep-bones   skip the renderer materials / list "mixamorig:*" bone objects too

Needs UnityPy: python3 -m venv <scratch>/upy && <scratch>/upy/bin/pip install UnityPy. Takes 15-30 s.
IL2CPP builds have no type trees, so script names are resolved through the MonoScript PPtr (as in unity_components.py);
engine types (Transform, RectTransform, Renderer, Material) read normally.
"""
import glob
import os
import struct
import sys

import UnityPy

args = sys.argv[1:]
if len(args) < 4:
    print(__doc__)
    sys.exit(1)
game, files = args[0], args[1].split(",")
opt = {"--component": None, "--root": None, "--depth": "6"}
flags = set()
i = 2
while i < len(args):
    if args[i] in opt:
        opt[args[i]] = args[i + 1]
        i += 2
    else:
        flags.add(args[i])
        i += 1
maxd = int(opt["--depth"])
want_mats = "--no-materials" not in flags
keep_bones = "--keep-bones" in flags

env = UnityPy.load(*[os.path.join(game, f) for f in files])
scripts = {}
for o in UnityPy.load(*sorted(glob.glob(game + "/*.assets"))).objects:
    if o.type.name == "MonoScript":
        try:
            scripts[(os.path.basename(o.assets_file.name), o.path_id)] = o.read().m_ClassName
        except Exception:
            pass


def script_name(o):
    data = o.get_raw_data()
    fid, pid = struct.unpack_from("<iq", data, 16)
    af = o.assets_file
    if fid == 0:
        fn = os.path.basename(af.name)
    else:
        ex = af.externals[fid - 1]
        fn = os.path.basename(getattr(ex, "path", None) or getattr(ex, "name", ""))
    return scripts.get((fn, pid), "MonoBehaviour")


def transform(go):
    for cp in go.m_Component:
        c = (cp.component if hasattr(cp, "component") else cp).deref()
        if c.type.name in ("Transform", "RectTransform"):
            return c.read()
    return None


def components(go):
    out = []
    for cp in go.m_Component:
        c = (cp.component if hasattr(cp, "component") else cp).deref()
        out.append((script_name(c) if c.type.name == "MonoBehaviour" else c.type.name, c))
    return out


def material_line(pp):
    try:
        m = pp.deref().read()
    except Exception as e:
        return "<material unreadable: %s>" % e
    try:
        sh = m.m_Shader.deref().read()
        shader = (sh.m_ParsedForm.m_Name if hasattr(sh, "m_ParsedForm") else sh.m_Name).split("/")[-1]
    except Exception:
        shader = "?"
    base = tex = None
    try:
        for k, v in m.m_SavedProperties.m_Colors:
            if k == "_BaseColor":
                base = tuple(round(x, 2) for x in (v.r, v.g, v.b, v.a))
        for k, v in m.m_SavedProperties.m_TexEnvs:
            if k in ("_BaseMap", "_MainTex") and v.m_Texture and v.m_Texture.path_id:
                tex = v.m_Texture.deref().read().m_Name
                if k == "_BaseMap":
                    break
    except Exception:
        pass
    return "%s | %s | base color %s | texture %s" % (m.m_Name, shader, base, tex)


def walk(go, depth):
    comps = components(go)
    names = [n for n, _ in comps if n not in ("Transform", "CanvasRenderer")]
    t = transform(go)
    extra = ""
    if t is not None and type(t).__name__ == "RectTransform":
        extra = " anchors=(%.2f,%.2f)-(%.2f,%.2f) size=(%.0f,%.0f) pos=(%.0f,%.0f)" % (
            t.m_AnchorMin.x, t.m_AnchorMin.y, t.m_AnchorMax.x, t.m_AnchorMax.y,
            t.m_SizeDelta.x, t.m_SizeDelta.y, t.m_AnchoredPosition.x, t.m_AnchoredPosition.y)
    print("  " * depth + go.m_Name + ("" if go.m_IsActive else " (inactive)") + " [" + ",".join(names) + "]"
          + extra + " layer=%d" % go.m_Layer)
    if want_mats:
        for n, c in comps:
            if c.type.name in ("SkinnedMeshRenderer", "MeshRenderer"):
                for m in c.read().m_Materials:
                    print("  " * depth + "    MAT " + material_line(m))
    if depth >= maxd or t is None:
        return
    for ch in t.m_Children:
        g = ch.deref().read().m_GameObject.deref().read()
        if not keep_bones and g.m_Name.startswith("mixamorig"):
            continue
        walk(g, depth + 1)


for o in env.objects:
    if o.type.name != "GameObject":
        continue
    go = o.read()
    t = transform(go)
    if t is None:
        continue
    if opt["--component"]:
        if opt["--component"] not in [n for n, _ in components(go)]:
            continue
        anc, p = [], t
        while p.m_Father.path_id != 0:
            p = p.m_Father.deref().read()
            anc.append(p.m_GameObject.deref().read().m_Name)
        print("####", os.path.basename(o.assets_file.name), "ancestors:", " < ".join(anc))
    elif opt["--root"]:
        if go.m_Name != opt["--root"] or t.m_Father.path_id != 0:
            continue
        print("####", os.path.basename(o.assets_file.name), go.m_Name)
    else:
        continue
    walk(go, 0)
