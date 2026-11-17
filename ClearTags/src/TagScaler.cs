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
/// Shelf slot labels and rack slot labels are NOT scaled: their icons aren't objects, the shelf shader paints
/// them from a texture atlas (LabelMaskController / RackLabelMaskController set the material's
/// _AtlasIndexMatrix / _IDMatrix), see ClearTags/CLAUDE.md.
/// </summary>
internal static class TagScaler
{
    private sealed class State
    {
        public float Applied = 1f;
        public Vector3[] BaseScale;
        public Vector3[] BasePos;
        public Vector3 Center;   // the "BG" quad's position: children scale about it
    }

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
            st = new State { BaseScale = new Vector3[n], BasePos = new Vector3[n] };
            int centre = 0;
            for (int i = 0; i < n; i++)
            {
                var c = tr.GetChild(i);
                st.BaseScale[i] = c.localScale;
                st.BasePos[i] = c.localPosition;
                if (c.name == "BG") centre = i;
            }
            if (n > 0) st.Center = st.BasePos[centre];
            States[id] = st;
        }
        if (st.BaseScale.Length != n) return false;         // not the layout we know: leave it alone
        if (Mathf.Approximately(st.Applied, f)) return false;

        for (int i = 0; i < n; i++)
        {
            var c = tr.GetChild(i);
            // All three axes: a flat quad's thickness is invisible, and this is right whichever plane the mesh lies in.
            c.localScale = st.BaseScale[i] * f;
            // Scale the layout about the frame's centre too (the furniture box's little "Color Point" sits inside the
            // frame; BG and Icon are within a few millimetres of the centre, so they hardly move).
            c.localPosition = st.Center + (st.BasePos[i] - st.Center) * f;
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
