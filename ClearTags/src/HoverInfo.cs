using System;
using System.Globalization;
using UnityEngine;

namespace ClearTags;

/// <summary>
/// Works out what the crosshair is on and builds the hover text.
///
/// Targets (the same ray the game uses in PlayerInteraction.Raycast: camera position + forward, up to
/// <c>m_InteractionDistance</c>, only colliders in <c>m_InteractableLayer</c>; the game takes the IInteractable
/// from the hit collider's own GameObject, so we do too):
///  - A box tag. The tag quads have no collider, so the ray first has to hit the box's collider (or the held
///    box, which the ray can't hit: it is on "Ignore Raycast"), then the same ray is tested, in each tag quad's
///    own local space, against its mesh bounds (a flat box for a quad; padded 10 %). That follows the quad's
///    real rectangle at any box rotation and at the scaled size. The renderers under <c>Box.m_IconUI</c> are
///    cached per instance id (no per-frame allocation while holding a box).
///    Racked boxes work too: vanilla swaps their interactable for the RackSlot, but the hit collider is still
///    the Box's.
///  - A furniture box (<c>FurnitureBox</c>, same kind of tag; also the one in the hands): name and price of the furniture inside.
///  - A Label (<c>Label</c> component, collider on its own GameObject): the tag of a shelf slot
///    (<c>Label.m_DisplaySlot</c>), a rack slot (<c>m_RackSlot</c>) or a vending slot (<c>m_VendingSlot</c>).
///
/// What is shown (decision, no vanilla tooltip exists for this):
///  - name: <c>LocalizationManager.LocalizedProductName(id)</c> (falls back to <c>ProductSO.ProductName</c>);
///  - quantity: box = items inside / capacity (<c>Box.ProductCount</c> / <c>MaxProductCount</c>); shelf slot =
///    items on the slot / capacity (<c>GridLayoutInStorage.productCount</c>); rack slot = items in all its
///    boxes and the number of boxes; vending slot = items on the slot;
///  - price: <c>ProductSO.BoxPrice</c>, the price of one full box of that product at the current market cost
///    (<c>PriceManager.CurrentCost(id) * GridLayoutInBox.productCount</c>, the price the wholesale market
///    charges for a box). Shown for every kind of tag, also on a half-empty box, so it always means "what a
///    box of this costs to buy".
/// </summary>
internal static class HoverInfo
{
    private static IntPtr s_lastTarget;
    internal static string LastKind = "";

    /// <summary>The text to show now, or null.</summary>
    internal static string Resolve()
    {
        if (!Plugin.Active || !Plugin.HoverText.Value) return null;
        if (!NoktaSingleton<PlayerManager>.HasInstance) return null;
        var p = NoktaSingleton<PlayerManager>.Instance.LocalPlayer;
        if (p == null || !p.IsLocalPlayerInstance) return null;
        if (Cursor.lockState != CursorLockMode.Locked) return null;      // a menu or a computer screen is open

        var pi = p.PlayerInteraction;
        if (pi == null || pi.m_Paused || pi.m_InGameMenu) return null;
        var cam = pi.m_MainCamera;
        if (cam == null) return null;
        var t = cam.transform;
        var origin = t.position;
        var dir = t.forward;
        var ray = new Ray(origin, dir);
        float range = pi.m_InteractionDistance;

        string text = null;
        IntPtr target = IntPtr.Zero;

        // The box in the hands: it is on "Ignore Raycast", so the physics ray never sees it.
        var held = HeldBox(p);
        if (held != null && TagHit(TagScaler.UiOf(held), ray, range + 1f))
        {
            target = held.Pointer;
            text = Cached(target, () => BoxText(held));
            LastKind = "held box";
        }
        else if (HeldFurnitureBox(p) is FurnitureBox heldFb && TagHit(TagScaler.UiOf(heldFb), ray, range + 1f))
        {
            target = heldFb.Pointer;
            text = Cached(target, () => FurnitureBoxText(heldFb));
            LastKind = "held furniture box";
        }
        else if (Physics.Raycast(origin, dir, out var hit, range, pi.m_InteractableLayer.value))
        {
            var col = hit.collider;
            if (col != null)
            {
                var go = col.gameObject;
                var label = go.GetComponent<Label>();
                if (label != null)
                {
                    target = label.Pointer;
                    text = Cached(target, () => { var t = LabelText(label, out var k); LastKind = k; return t; });
                }
                else
                {
                    var box = go.GetComponent<Box>();
                    if (box != null)
                    {
                        if (TagHit(TagScaler.UiOf(box), ray, range))
                        {
                            target = box.Pointer;
                            text = Cached(target, () => BoxText(box));
                            LastKind = box.m_Racked ? "racked box" : "box";
                        }
                    }
                    else
                    {
                        var fb = go.GetComponent<FurnitureBox>();
                        if (fb != null && TagHit(TagScaler.UiOf(fb), ray, range))
                        {
                            target = fb.Pointer;
                            text = Cached(target, () => FurnitureBoxText(fb));
                            LastKind = "furniture box";
                        }
                    }
                }
            }
        }

        if (target != s_lastTarget)
        {
            s_lastTarget = target;
            if (text != null) Plugin.Info($"hover ({LastKind}): {text.Replace("\n", " | ")}");
        }
        return text;
    }

    private static IntPtr s_cTarget;
    private static string s_cText;
    private static float s_cAt;

    /// <summary>
    /// The text is built (game lookups, string formatting) only when the target changes, and then at most twice a
    /// second so quantities stay current; not 60 times a second.
    /// </summary>
    private static string Cached(IntPtr target, Func<string> build)
    {
        float now = Time.unscaledTime;
        if (target == s_cTarget && now - s_cAt < 0.5f) return s_cText;
        s_cTarget = target;
        s_cAt = now;
        s_cText = build();
        return s_cText;
    }

    private static Box HeldBox(PlayerInstance p)
    {
        try
        {
            var bi = p.BoxInteraction;
            var holder = p.PlayerObjectHolder;
            if (bi == null || holder == null) return null;
            var box = bi.m_Box;
            if (box == null || holder.m_ObjectHolder == null) return null;
            // m_Box can be stale after a drop; the box is only in the hands while it hangs under the holder.
            return box.transform.parent == holder.m_ObjectHolder ? box : null;
        }
        catch (Exception) { return null; }
    }

    private static FurnitureBox HeldFurnitureBox(PlayerInstance p)
    {
        try
        {
            var fi = p.FurnitureBoxInteraction;
            var holder = p.PlayerObjectHolder;
            if (fi == null || holder == null) return null;
            var box = fi.m_Box;
            if (box == null || holder.m_ObjectHolder == null) return null;
            return box.transform.parent == holder.m_ObjectHolder ? box : null;
        }
        catch (Exception) { return null; }
    }

    private sealed class TagParts
    {
        public MeshRenderer[] Renderers;
        public Bounds[] Local;   // each quad's mesh bounds in its own local space
    }

    // The renderers under a box's "Icon UI", cached per instance id so holding a box doesn't allocate every frame.
    private static readonly System.Collections.Generic.Dictionary<int, TagParts> Parts = new();

    private static TagParts GetParts(GameObject ui)
    {
        int id = ui.GetInstanceID();
        if (Parts.TryGetValue(id, out var tp))
        {
            bool ok = true;
            foreach (var r in tp.Renderers) if (r == null) { ok = false; break; }
            if (ok) return tp;
        }
        if (Parts.Count > 512) Parts.Clear();   // boxes come and go; ids of destroyed ones just get dropped here
        var list = new System.Collections.Generic.List<MeshRenderer>();
        var bl = new System.Collections.Generic.List<Bounds>();
        foreach (var r in ui.GetComponentsInChildren<MeshRenderer>(true))
        {
            var mf = r.GetComponent<MeshFilter>();
            var mesh = mf != null ? mf.sharedMesh : null;
            if (mesh == null) continue;
            list.Add(r);
            bl.Add(mesh.bounds);
        }
        tp = new TagParts { Renderers = list.ToArray(), Local = bl.ToArray() };
        Parts[id] = tp;
        return tp;
    }

    /// <summary>
    /// Does the ray pass through one of the (scaled) tag quads under this "Icon UI"? Each quad is tested in its own
    /// local space: the ray is intersected with the quad's plane (the thinnest axis of its mesh bounds), and the hit
    /// point must lie within the quad's extents in the other two axes (plus 10 %). So the hit area is the quad's real
    /// rectangle at any box rotation and any scale, with no depth slack, and it doesn't matter which local plane the
    /// mesh lies in. The "Color Point" of a furniture box (a small solid mesh, not flat) is a tiny extra target.
    /// </summary>
    private static bool TagHit(GameObject ui, Ray ray, float maxDistance)
    {
        if (ui == null || !ui.activeInHierarchy) return false;
        var tp = GetParts(ui);
        for (int i = 0; i < tp.Renderers.Length; i++)
        {
            var r = tp.Renderers[i];
            if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
            var tr = r.transform;
            var lo = tr.InverseTransformPoint(ray.origin);
            var ld = tr.InverseTransformPoint(ray.origin + ray.direction) - lo;
            var lb = tp.Local[i];
            var c = lb.center;
            var e = lb.extents;
            int k = 0;                                   // the thin axis = the quad's normal
            if (e.y < e[k]) k = 1;
            if (e.z < e[k]) k = 2;
            float dk = ld[k];
            if (Mathf.Abs(dk) < 1e-8f) continue;         // ray parallel to the quad
            float t = (c[k] - lo[k]) / dk;
            if (t < 0f) continue;                        // quad is behind the camera
            var hit = lo + ld * t;
            bool inside = true;
            for (int a2 = 0; a2 < 3; a2++)
            {
                if (a2 == k) continue;
                if (Mathf.Abs(hit[a2] - c[a2]) > e[a2] * 1.1f + 0.01f) { inside = false; break; }
            }
            if (!inside) continue;
            var world = tr.TransformPoint(hit);
            if (Vector3.Distance(ray.origin, world) <= maxDistance) return true;
        }
        return false;
    }

    private static string BoxText(Box box)
    {
        var so = box.Product;
        if (so == null) return null;
        int count = box.ProductCount;
        int max = 0;
        try { max = box.MaxProductCount; } catch (Exception) { }
        return Format(so, max > 0 ? $"{count} / {max}" : count.ToString());
    }

    /// <summary>
    /// Furniture box: name of the furniture inside (<c>LocalizationManager.LocalizedFurnitureName</c>), and
    /// <c>FurnitureSO.Cost</c> as the price (what the furniture shop charges for it; the box is just its packaging).
    /// There is no quantity, so that line is left out. A paint-bucket box shows the bucket name without a price.
    /// </summary>
    private static string FurnitureBoxText(FurnitureBox fb)
    {
        var data = fb.Data;
        if (data == null) return null;
        string name = null;
        string price = null;
        // Same test as NetworkFurnitureBox.OnPhotonInstantiate: bucketData set (and its first field not -1) = paint can box.
        // FurnitureBoxData.Furniture and .Bucket both read the same id field, and Furniture logs an error and scans every
        // FurnitureSO when the id isn't a furniture id, so only touch the one that applies.
        var bd = data.bucketData;
        bool isBucket = bd != null && bd.ProductID != -1;
        if (!isBucket)
        {
            var so = data.Furniture;
            if (so != null)
            {
                try
                {
                    if (NoktaSingleton<LocalizationManager>.HasInstance)
                        name = NoktaSingleton<LocalizationManager>.Instance.LocalizedFurnitureName(so.ID);
                }
                catch (Exception) { }
                if (string.IsNullOrEmpty(name)) name = so.FurnitureName;
                try { price = "$" + so.Cost.ToString("0.00", CultureInfo.InvariantCulture); } catch (Exception) { }
            }
        }
        else
        {
            var bucket = data.Bucket;
            if (bucket != null)
            {
                try
                {
                    if (NoktaSingleton<LocalizationManager>.HasInstance)
                        name = NoktaSingleton<LocalizationManager>.Instance.LocalizedBucketName(bucket.ID);
                }
                catch (Exception) { }
            }
        }
        if (string.IsNullOrEmpty(name)) return null;
        return price != null ? $"<b>{name}</b>\nPrice: {price}" : $"<b>{name}</b>";
    }

    private static string LabelText(Label label, out string kind)
    {
        kind = "label";
        var ds = label.DisplaySlot;
        if (ds != null)
        {
            kind = "shelf tag";
            int id = ds.ProductID;
            var so = Lookup(id);
            if (so == null) return null;
            int cap = 0;
            try { cap = so.GridLayoutInStorage.productCount; } catch (Exception) { }
            int count = ds.ProductCount;
            return Format(so, cap > 0 ? $"{count} / {cap}" : count.ToString());
        }
        var rs = label.RackSlot;
        if (rs != null)
        {
            kind = "rack tag";
            var data = rs.Data;
            if (data == null) return null;
            var so = Lookup(data.ProductID);
            if (so == null) return null;
            int boxes = data.BoxCount;
            return Format(so, $"{rs.ProductCount} ({boxes} {(boxes == 1 ? "box" : "boxes")})");
        }
        var vs = label.VendingSlot;
        if (vs != null)
        {
            kind = "vending tag";
            var so = Lookup(vs.ProductID);
            if (so == null) return null;
            return Format(so, vs.ProductCount.ToString());
        }
        return null;
    }

    private static ProductSO Lookup(int productId)
    {
        if (productId <= 0 || !NoktaSingleton<IDManager>.HasInstance) return null;
        return NoktaSingleton<IDManager>.Instance.ProductSO(productId);
    }

    private static string Format(ProductSO so, string quantity)
    {
        string name = null;
        try
        {
            if (NoktaSingleton<LocalizationManager>.HasInstance)
                name = NoktaSingleton<LocalizationManager>.Instance.LocalizedProductName(so.ID);
        }
        catch (Exception) { }
        if (string.IsNullOrEmpty(name)) name = so.ProductName;
        if (string.IsNullOrEmpty(name)) name = "Product " + so.ID;

        string price = "?";
        try { price = "$" + so.BoxPrice.ToString("0.00", CultureInfo.InvariantCulture); } catch (Exception) { }

        return $"<b>{name}</b>\nQuantity: {quantity}\nBox price: {price}";
    }
}
