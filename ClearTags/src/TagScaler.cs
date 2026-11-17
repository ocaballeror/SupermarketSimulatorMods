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
/// air). So after scaling, all children are shifted together by <see cref="ClampShift"/> until the tag's bounding box lies
/// inside the box collider's bounds (minus 8 mm); the size is never changed by this. The shift is derived from the base pose
/// every time, so a factor change at runtime doesn't compound, and factor 1 writes the exact original positions.
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
        public Vector3 FaceMin, FaceMax;
    }

    /// <summary>Gap kept between the enlarged tag and the box edge (metres, in the tag's parent space): the cardboard is bevelled.</summary>
    private const float EdgeMargin = 0.008f;
    private static int s_clampLogs;

    private static readonly Dictionary<int, State> States = new();

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
    /// </summary>
    private static bool TryGetFace(GameObject ui, out Vector3 mn, out Vector3 mx)
    {
        mn = mx = Vector3.zero;
        try
        {
            var parent = ui.transform.parent;
            var col = parent != null ? parent.GetComponent<BoxCollider>() : null;
            if (col == null) col = ui.GetComponentInParent<BoxCollider>(true);
            if (col == null) return false;
            var c = col.center;
            var h = col.size * 0.5f;
            var ct = col.transform;
            var ut = ui.transform;
            bool first = true;
            for (int i = 0; i < 8; i++)
            {
                var corner = c + new Vector3((i & 1) != 0 ? h.x : -h.x, (i & 2) != 0 ? h.y : -h.y, (i & 4) != 0 ? h.z : -h.z);
                var p = ut.InverseTransformPoint(ct.TransformPoint(corner));
                if (first) { mn = mx = p; first = false; }
                else { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
            }
            return (mx - mn).sqrMagnitude > 1e-6f;
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Extra offset (in "Icon UI" local space) that keeps the scaled tag inside the face of the box it lies on.
    /// The tag is a flat set of quads (BG frame, Icon, and on furniture boxes a Color Point) lying on one face of the box:
    /// the face is the plane perpendicular to the thinnest axis of the tags' bounding box (in the prefabs that is local y,
    /// the tag is rotated 90 degrees about x, at y of 0.25 to 0.84 on the top face). On the other two axes the tag's AABB (from the
    /// children's base pose scaled by <paramref name="f"/> about the frame centre, not from the current transforms, so nothing
    /// compounds) is moved inside the box collider's AABB minus <see cref="EdgeMargin"/>; a tag bigger than the face is
    /// centred on it instead. A tag that already fits gets no shift.
    /// </summary>
    private static Vector3 ClampShift(State st, float f, string uiName)
    {
        if (!st.HasFace || st.BasePos.Length == 0) return Vector3.zero;
        var tmin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var tmax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        for (int i = 0; i < st.BasePos.Length; i++)
        {
            var pos = st.Center + (st.BasePos[i] - st.Center) * f;
            var sc = st.BaseScale[i] * f;
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
        // Plain float arrays: no Vector3 indexer calls (the interop struct's indexer is a native call; setting through it is unproven).
        float[] mn = { tmin.x, tmin.y, tmin.z }, mx = { tmax.x, tmax.y, tmax.z };
        float[] fmin = { st.FaceMin.x, st.FaceMin.y, st.FaceMin.z }, fmax = { st.FaceMax.x, st.FaceMax.y, st.FaceMax.z };
        float[] ext = { mx[0] - mn[0], mx[1] - mn[1], mx[2] - mn[2] };
        int n = 0;                                    // the tag's normal axis = its thinnest extent
        if (ext[1] < ext[n]) n = 1;
        if (ext[2] < ext[n]) n = 2;
        float[] sh = new float[3];
        for (int a = 0; a < 3; a++)
        {
            if (a == n) continue;
            float lo = fmin[a] + EdgeMargin, hi = fmax[a] - EdgeMargin;
            if (hi <= lo) continue;
            if (ext[a] >= hi - lo) sh[a] = (lo + hi) * 0.5f - (mn[a] + mx[a]) * 0.5f;   // can't fit: centre it
            else if (mn[a] < lo) sh[a] = lo - mn[a];
            else if (mx[a] > hi) sh[a] = hi - mx[a];
        }
        var shift = new Vector3(sh[0], sh[1], sh[2]);
        if ((sh[0] != 0f || sh[1] != 0f || sh[2] != 0f) && s_clampLogs < 4)
        {
            s_clampLogs++;
            Plugin.Info($"tag kept on its box face (x{f}, normal axis {"xyz"[n]}): shift ({sh[0]:0.000}, {sh[1]:0.000}, {sh[2]:0.000}), " +
                        $"tag extent ({ext[0]:0.000}, {ext[1]:0.000}, {ext[2]:0.000}), face ({fmin[0]:0.000}, {fmin[1]:0.000}, {fmin[2]:0.000}) .. ({fmax[0]:0.000}, {fmax[1]:0.000}, {fmax[2]:0.000})");
        }
        return shift;
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
            st.HasFace = TryGetFace(ui, out st.FaceMin, out st.FaceMax);
            States[id] = st;
        }
        if (st.BaseScale.Length != n) return false;         // not the layout we know: leave it alone
        if (Mathf.Approximately(st.Applied, f)) return false;

        // Keep the bigger tag on the face it lies on: shift all children by one common vector (never changes the size).
        var shift = Mathf.Approximately(f, 1f) ? Vector3.zero : ClampShift(st, f, ui.name);
        for (int i = 0; i < n; i++)
        {
            var c = tr.GetChild(i);
            // All three axes: a flat quad's thickness is invisible, and this is right whichever plane the mesh lies in.
            c.localScale = st.BaseScale[i] * f;
            // Scale the layout about the frame's centre too (the furniture box's little "Color Point" sits inside the
            // frame; BG and Icon are within a few millimetres of the centre, so they hardly move).
            c.localPosition = st.Center + (st.BasePos[i] - st.Center) * f + shift;
        }
        st.Applied = f;
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
            if (changed > 0) Plugin.Logger.LogInfo($"[ClearTags] sweep: {changed} of {total} boxes had not been resized by the hooks (x{Factor})");
        }
        else
        {
            Plugin.Logger.LogInfo($"[ClearTags] {reason}: {changed} of {total} boxes resized (x{Factor})");
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
