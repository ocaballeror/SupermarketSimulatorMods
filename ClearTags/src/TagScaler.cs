using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ClearTags;

/// <summary>
/// Makes the product icon tag on boxes bigger (config <c>ScaleMultiplier</c>).
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
/// loaded from the save, boxes the host spawned for a guest, pooled boxes coming back), plus a slow sweep over
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
        public Vector3[] Base;
    }

    private static readonly Dictionary<int, State> States = new();

    /// <summary>The factor tags should have right now (1 while the mod is off).</summary>
    internal static float Factor => Plugin.Active ? Plugin.TagScale.Value : 1f;

    /// <summary>Resize one box's tag to the current factor. Returns true when it actually changed.</summary>
    internal static bool Apply(Box box)
    {
        if (box == null) return false;
        var ui = box.m_IconUI;
        if (ui == null) return false;
        float f = Factor;
        var tr = ui.transform;
        int n = tr.childCount;
        int id = ui.GetInstanceID();

        if (!States.TryGetValue(id, out var st))
        {
            if (Mathf.Approximately(f, 1f)) return false;   // nothing to apply, nothing to restore
            st = new State { Base = new Vector3[n] };
            for (int i = 0; i < n; i++) st.Base[i] = tr.GetChild(i).localScale;
            States[id] = st;
        }
        if (st.Base.Length != n) return false;               // not the layout we know: leave it alone
        if (Mathf.Approximately(st.Applied, f)) return false;

        for (int i = 0; i < n; i++)
        {
            var b = st.Base[i];
            tr.GetChild(i).localScale = b * f;   // all three axes: a flat quad's thickness is invisible, and this is right whichever plane the mesh lies in
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

    /// <summary>Resize every box that exists right now (used when the setting changes, and for the slow sweep).</summary>
    /// <returns>how many boxes were changed</returns>
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
                    var ui = box.m_IconUI;
                    if (ui != null) live.Add(ui.GetInstanceID());
                    if (Apply(box)) changed++;
                }
                catch (Exception e) { Plugin.Logger.LogWarning($"[ClearTags] resize failed on a box: {e.Message}"); }
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
        try { Apply(box); }
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
