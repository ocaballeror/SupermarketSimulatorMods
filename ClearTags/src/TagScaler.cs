using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ClearTags;

/// <summary>
/// Makes the product icon tag on boxes and furniture boxes bigger (config <c>ScaleMultiplier</c>).
///
/// What the tag is: every box prefab (CardboardBox_*, with and without the PhotonView) has a child
/// "Icon UI" (<c>Box.m_IconUI</c>, with a LODGroup) whose children "BG" (the frame) and "Icon" (the product
/// icon, <c>Box.m_ProductIconImage</c>) are flat MeshFilter+MeshRenderer quads. "Icon UI" itself sits at the
/// box origin with scale 1; BG and Icon carry the tag position (e.g. (-0.092, 0.484, -0.113)) in their own
/// local position and their size in localScale (about 0.16). So the scale must go on the two children, NOT
/// on "Icon UI": scaling the parent would move the tag away from its spot (scaling about the box origin).
/// Multiplying the children's whole localScale keeps each quad centred on the same spot (a flat quad's
/// thickness is invisible, and all three axes is right whichever local plane the mesh lies in).
/// <c>Box.Initialize</c> and <c>Box.Setup</c> only call SetActive on "Icon UI" and ProductAtlasManager.SetLabelData
/// (the icon texture) and never touch the scale, so a scale set once stays.
///
/// When it runs: postfixes on Box.OnEnable, Initialize and Setup (every spawn path: store boxes, boxes
/// loaded from the save, boxes the host spawned for a guest, pooled boxes coming back), plus a diagnostic sweep (every 30 s, only while VerboseLogging is on) over
/// all boxes (<see cref="ClearTagsBehaviour"/>) that also logs when a box was missed by the hooks.
///
/// No compounding: the scale a tag had when the mod first saw it is remembered per "Icon UI" GameObject
/// (instance id), and the applied value is always base * factor, set only when the factor changed. Disabling
/// the mod or setting the multiplier to 1 restores the remembered scale.
///
/// Position: the tag sits on the top face of the box, in a corner area (for example (-0.092, 0.484, -0.113) on a
/// 15x15x15 box whose half width is 0.225), and BG/Icon are rotated 90 degrees about x (the quad faces up). Growing a
/// tag about its own centre therefore pushes its edge past the box edge (seen in-game at 2x: the frame floated in the
/// air). So <see cref="Plan"/> caps the factor per box so the tag's bounding box fits inside the box collider's bounds (minus
/// 8 mm; small boxes and large factors therefore get a smaller tag than requested, never smaller than vanilla) and shifts all
/// children together until it lies inside. It is derived from the base pose every time, so a factor change at runtime doesn't
/// compound, and factor 1 writes the exact original positions.
///
/// Shelf, rack and vending label graphics are intentionally never changed (the user's decision; the shelf
/// shader paints them, see ClearTags/CLAUDE.md). Only boxes and furniture boxes are touched here.
/// </summary>
internal static class TagScaler
{
    private sealed class State
    {
        public float Applied = 1f;
        public Vector3[] BaseScale;
        public Vector3[] BasePos;
        public Quaternion[] BaseRot;
        public Vector3[] MeshCenter;   // each child's mesh bounds (local space of the child)
        public Vector3[] MeshExt;
        public Vector3 Center;   // the "BG" quad's position: children scale about it
        public bool HasFace;     // the box's collider, as an AABB in the "Icon UI" local space (the face the tag must stay on)
        public bool AppliedFace; // HasFace at the time the current scale was written (a late face means re-apply)
        public Vector3 FaceMin, FaceMax;
    }

    /// <summary>Gap kept between the enlarged tag and the box edge (metres, in the tag's parent space): the cardboard is bevelled.</summary>
    private const float EdgeMargin = 0.008f;
    private static readonly HashSet<string> s_capLogged = new();
    private static bool s_noFaceLogged;

    private static readonly Dictionary<int, State> States = new();

    /// <summary>Number of remembered tags; the behaviour prunes (via <see cref="ResizeAll"/>) when this grows large.</summary>
    internal static int StateCount => States.Count;

    /// <summary>The factor tags should have right now (1 while the mod is off).</summary>
    internal static float Factor => Plugin.Active ? Plugin.TagScale.Value : 1f;

    /// <summary>The "Icon UI" object of a product box, or null.</summary>
    internal static GameObject UiOf(Box box) => box != null ? box.m_IconUI : null;

    /// <summary>
    /// The "Icon UI" object of a furniture box (it has no m_IconUI field; its icon quad
    /// <c>FurnitureBox.m_ProductIconImage</c> is a child of "Icon UI", next to "BG" and a small "Color Point").
    /// </summary>
    internal static GameObject UiOf(FurnitureBox fb)
    {
        if (fb == null) return null;
        var r = fb.m_ProductIconImage;
        if (r == null) return null;
        var parent = r.transform.parent;
        return parent != null ? parent.gameObject : null;
    }

    /// <summary>
    /// The box's BoxCollider as an AABB in the "Icon UI" local space (= the space the tag children's localPosition is in).
    /// Every box prefab has one on the root (checked: all 18 CardboardBox_* prefabs), and it matches the cardboard model
    /// within 5 mm; the product-box models are not always a plain mesh child (the furniture boxes only have a skinned model),
    /// so the collider is the common reference.
    /// Computed from the collider's centre/size and "Icon UI"'s own local TRS (its parent is the collider's transform), with
    /// no world transforms: pooled furniture boxes are tweened to scale 0 (<c>FurnitureBox.OpenBox</c> then Despawn), and a
    /// world mapping through a zero-scale root would be degenerate.
    /// </summary>
    private static bool TryGetFace(GameObject ui, out Vector3 mn, out Vector3 mx)
    {
        mn = mx = Vector3.zero;
        try
        {
            var ut = ui.transform;
            var parent = ut.parent;
            var col = parent != null ? parent.GetComponent<BoxCollider>() : null;
            if (col == null) return false;
            var sc = ut.localScale;
            if (Mathf.Abs(sc.x) < 1e-4f || Mathf.Abs(sc.y) < 1e-4f || Mathf.Abs(sc.z) < 1e-4f) return false;
            var pos = ut.localPosition;
            var inv = Quaternion.Inverse(ut.localRotation);
            var c = col.center;
            var h = col.size * 0.5f;
            bool first = true;
            for (int i = 0; i < 8; i++)
            {
                var corner = c + new Vector3((i & 1) != 0 ? h.x : -h.x, (i & 2) != 0 ? h.y : -h.y, (i & 4) != 0 ? h.z : -h.z);
                var q = inv * (corner - pos);
                var p = new Vector3(q.x / sc.x, q.y / sc.y, q.z / sc.z);
                if (first) { mn = mx = p; first = false; }
                else { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
            }
            return (mx - mn).sqrMagnitude > 1e-6f;
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Works out how the tag is laid out at the requested factor <paramref name="f"/> so that it stays on the face of the box it lies on:
    /// returns the effective scale factor (never below 1 for f &gt; 1, never above f) and the common shift to add to every
    /// child's position (in "Icon UI" local space).
    /// The tag is a flat set of quads (BG frame, Icon, and on furniture boxes a Color Point) lying on one face of the box:
    /// the face is the plane perpendicular to the thinnest axis of the tag's bounding box (local y in the prefabs: the quads are
    /// rotated 90 degrees about x, so they face up on the top face). The bounding box is the union of every quad's real AABB (its
    /// mesh bounds with its own rotation and scale, so the Icon's few degrees of in-plane rotation count) from the base pose at
    /// factor 1. Scaling about the frame centre C scales that box about C, so its extent at factor f is f * ext1. The factor is capped
    /// to min over the two in-plane axes of (face - 2 * <see cref="EdgeMargin"/>) / ext1, so the tag fits the lid (the smallest boxes
    /// and anything at 3x or more get a smaller tag than requested); then the tag is shifted inside the face. Everything comes from the base pose,
    /// so nothing compounds. If the face is smaller than the base tag the factor stays 1 and the tag is left where vanilla has it.
    /// </summary>
    private static float Plan(State st, float f, out Vector3 shift)
    {
        shift = Vector3.zero;
        if (!st.HasFace || st.BasePos.Length == 0 || f <= 1.0001f) return f;
        var tmin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var tmax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        for (int i = 0; i < st.BasePos.Length; i++)
        {
            var pos = st.BasePos[i];
            var sc = st.BaseScale[i];
            var rot = st.BaseRot[i];
            var ctr = pos + rot * Vector3.Scale(st.MeshCenter[i], sc);
            var e = st.MeshExt[i];
            var hx = rot * new Vector3(sc.x * e.x, 0f, 0f);
            var hy = rot * new Vector3(0f, sc.y * e.y, 0f);
            var hz = rot * new Vector3(0f, 0f, sc.z * e.z);
            var half = new Vector3(
                Mathf.Abs(hx.x) + Mathf.Abs(hy.x) + Mathf.Abs(hz.x),
                Mathf.Abs(hx.y) + Mathf.Abs(hy.y) + Mathf.Abs(hz.y),
                Mathf.Abs(hx.z) + Mathf.Abs(hy.z) + Mathf.Abs(hz.z));
            tmin = Vector3.Min(tmin, ctr - half);
            tmax = Vector3.Max(tmax, ctr + half);
        }
        // Plain float arrays: no Vector3 indexer calls (the interop struct's indexer is a native call).
        float[] mn = { tmin.x, tmin.y, tmin.z }, mx = { tmax.x, tmax.y, tmax.z };
        float[] fmin = { st.FaceMin.x, st.FaceMin.y, st.FaceMin.z }, fmax = { st.FaceMax.x, st.FaceMax.y, st.FaceMax.z };
        float[] cen = { st.Center.x, st.Center.y, st.Center.z };
        float[] ext = { mx[0] - mn[0], mx[1] - mn[1], mx[2] - mn[2] };
        int n = 0;                                    // the tag's normal axis = its thinnest extent
        if (ext[1] < ext[n]) n = 1;
        if (ext[2] < ext[n]) n = 2;

        float cap = f;
        for (int a = 0; a < 3; a++)
        {
            if (a == n || ext[a] < 1e-5f) continue;
            float avail = fmax[a] - fmin[a] - 2f * EdgeMargin;
            if (avail <= 0f) { cap = 1f; continue; }
            cap = Mathf.Min(cap, avail / ext[a]);
        }
        float fEff = Mathf.Max(1f, Mathf.Min(f, cap));
        if (fEff <= 1.0001f) return 1f;

        float[] sh = new float[3];
        for (int a = 0; a < 3; a++)
        {
            if (a == n) continue;
            float lo = fmin[a] + EdgeMargin, hi = fmax[a] - EdgeMargin;
            float smin = cen[a] + (mn[a] - cen[a]) * fEff, smax = cen[a] + (mx[a] - cen[a]) * fEff;
            if (smin < lo) sh[a] = lo - smin;
            else if (smax > hi) sh[a] = hi - smax;
        }
        shift = new Vector3(sh[0], sh[1], sh[2]);
        if (fEff < f - 1e-4f)
        {
            string key = $"{fmax[0] - fmin[0]:0.00}x{fmax[2] - fmin[2]:0.00}@{f:0.0}";
            if (s_capLogged.Add(key))
                Plugin.Info($"tag capped to x{fEff:0.00} (requested x{f:0.0}) so it fits a lid {fmax[0] - fmin[0]:0.000} x {fmax[2] - fmin[2]:0.000} (normal axis {"xyz"[n]}, base tag extent {ext[0]:0.000}, {ext[1]:0.000}, {ext[2]:0.000}), shift ({sh[0]:0.000}, {sh[1]:0.000}, {sh[2]:0.000})");
        }
        return fEff;
    }

    /// <summary>Resize one tag to the current factor. Returns true when it actually changed.</summary>
    internal static bool Apply(GameObject ui)
    {
        if (ui == null) return false;
        float f = Factor;
        var tr = ui.transform;
        int n = tr.childCount;
        int id = ui.GetInstanceID();

        if (!States.TryGetValue(id, out var st))
        {
            if (Mathf.Approximately(f, 1f)) return false;   // nothing to apply, nothing to restore
            st = new State
            {
                BaseScale = new Vector3[n], BasePos = new Vector3[n], BaseRot = new Quaternion[n],
                MeshCenter = new Vector3[n], MeshExt = new Vector3[n],
            };
            int centre = 0;
            for (int i = 0; i < n; i++)
            {
                var c = tr.GetChild(i);
                st.BaseScale[i] = c.localScale;
                st.BasePos[i] = c.localPosition;
                st.BaseRot[i] = c.localRotation;
                if (c.name == "BG") centre = i;
                try
                {
                    var mf = c.GetComponent<MeshFilter>();
                    var mesh = mf != null ? mf.sharedMesh : null;
                    if (mesh != null) { var b = mesh.bounds; st.MeshCenter[i] = b.center; st.MeshExt[i] = b.extents; }
                }
                catch (Exception) { }
            }
            if (n > 0) st.Center = st.BasePos[centre];
            States[id] = st;
        }
        if (st.BaseScale.Length != n) return false;         // not the layout we know: leave it alone
        if (!st.HasFace)
        {
            st.HasFace = TryGetFace(ui, out st.FaceMin, out st.FaceMax);   // retried until it works (e.g. a collider-less moment)
            if (!st.HasFace && !s_noFaceLogged)
            {
                s_noFaceLogged = true;
                Plugin.Info($"no box collider face found for a tag ('{ui.name}', parent '{(tr.parent != null ? tr.parent.name : "none")}'); the tag is scaled without the keep-on-lid clamp until one is found");
            }
        }
        if (Mathf.Approximately(st.Applied, f) && st.AppliedFace == st.HasFace) return false;

        // Effective factor and a common shift keep the bigger tag on the face it lies on (see Plan).
        float fEff = Plan(st, f, out var shift);
        for (int i = 0; i < n; i++)
        {
            var c = tr.GetChild(i);
            // All three axes: a flat quad's thickness is invisible, and this is right whichever plane the mesh lies in.
            if (Mathf.Approximately(fEff, 1f))
            {
                c.localScale = st.BaseScale[i];        // vanilla: the exact captured values, no float round trip
                c.localPosition = st.BasePos[i];
                continue;
            }
            c.localScale = st.BaseScale[i] * fEff;
            // Scale the layout about the frame's centre too (the furniture box's little "Color Point" sits inside the
            // frame; BG and Icon are within a few millimetres of the centre, so they hardly move).
            c.localPosition = st.Center + (st.BasePos[i] - st.Center) * fEff + shift;
        }
        st.Applied = f;
        st.AppliedFace = st.HasFace;
        try
        {
            // The LODGroup caches the object size it culls by; keep it in step with the bigger renderers.
            var lod = ui.GetComponent<LODGroup>();
            if (lod != null) lod.RecalculateBounds();
        }
        catch (Exception) { /* cosmetic */ }
        return true;
    }

    /// <summary>Resize every box and furniture box that exists right now (settings change, diagnostic sweep).</summary>
    /// <returns>how many tags were changed</returns>
    internal static int ResizeAll(string reason, bool logMissed = false)
    {
        int total = 0, changed = 0;
        try
        {
            var live = new HashSet<int>();
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<Box>(), true))
            {
                var box = o.TryCast<Box>();
                if (box == null) continue;
                total++;
                try
                {
                    var ui = UiOf(box);
                    if (ui != null) live.Add(ui.GetInstanceID());
                    if (Apply(ui)) changed++;
                }
                catch (Exception e) { Plugin.Logger.LogWarning($"[ClearTags] resize failed on a box: {e.Message}"); }
            }
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<FurnitureBox>(), true))
            {
                var fb = o.TryCast<FurnitureBox>();
                if (fb == null) continue;
                total++;
                try
                {
                    var ui = UiOf(fb);
                    if (ui != null) live.Add(ui.GetInstanceID());
                    if (Apply(ui)) changed++;
                }
                catch (Exception e) { Plugin.Logger.LogWarning($"[ClearTags] resize failed on a furniture box: {e.Message}"); }
            }
            // Forget tags of destroyed boxes.
            if (States.Count > live.Count)
            {
                var stale = new List<int>();
                foreach (var k in States.Keys) if (!live.Contains(k)) stale.Add(k);
                foreach (var k in stale) States.Remove(k);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[ClearTags] resize sweep ({reason}) failed: {e.Message}");
        }
        if (logMissed)
        {
            if (changed > 0) Plugin.Info($"sweep: {changed} of {total} boxes had not been resized by the hooks (x{Factor})");
        }
        else
        {
            Plugin.Info($"{reason}: {changed} of {total} boxes resized (x{Factor})");
        }
        return changed;
    }

    internal static void OnBox(Box box)
    {
        if (!Plugin.Active) return;
        try { Apply(UiOf(box)); }
        catch (Exception e) { Plugin.Logger.LogWarning($"[ClearTags] resize failed: {e.Message}"); }   // fail open
    }

    internal static void OnFurnitureBox(FurnitureBox fb)
    {
        if (!Plugin.Active) return;
        try { Apply(UiOf(fb)); }
        catch (Exception e) { Plugin.Logger.LogWarning($"[ClearTags] resize failed: {e.Message}"); }   // fail open
    }
}

[HarmonyPatch(typeof(Box), nameof(Box.OnEnable))]
internal static class BoxOnEnablePatch
{
    private static void Postfix(Box __instance) => TagScaler.OnBox(__instance);
}

[HarmonyPatch(typeof(Box), nameof(Box.Initialize))]
internal static class BoxInitializePatch
{
    private static void Postfix(Box __instance) => TagScaler.OnBox(__instance);
}

[HarmonyPatch(typeof(Box), nameof(Box.Setup))]
internal static class BoxSetupPatch
{
    private static void Postfix(Box __instance) => TagScaler.OnBox(__instance);
}

[HarmonyPatch(typeof(FurnitureBox), nameof(FurnitureBox.OnEnable))]
internal static class FurnitureBoxOnEnablePatch
{
    private static void Postfix(FurnitureBox __instance) => TagScaler.OnFurnitureBox(__instance);
}

[HarmonyPatch(typeof(FurnitureBox), nameof(FurnitureBox.Initialize))]
internal static class FurnitureBoxInitializePatch
{
    private static void Postfix(FurnitureBox __instance) => TagScaler.OnFurnitureBox(__instance);
}
