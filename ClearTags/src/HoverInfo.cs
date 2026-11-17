using System;
using System.Globalization;
using UnityEngine;

namespace ClearTags;

/// <summary>One row of the hover panel ("label: value").</summary>
internal sealed class HoverRow
{
    public string Label, Value;
    public bool Positive;   // value drawn in the game's green (like the positive numbers on the Daily Statistics screen), else white
}

/// <summary>What the hover panel shows: a title (with brand), an icon, and label/value rows.</summary>
internal sealed class HoverData
{
    public string Title;
    public int ProductId;          // > 0: icon from ProductAtlasManager.GetProductIcon
    public Sprite IconSprite;      // furniture: FurnitureSO.FurnitureIcon
    public string IconKey = "";    // changes only when the icon must be refreshed
    public readonly System.Collections.Generic.List<HoverRow> Rows = new();
    public string Signature => _sig ??= Build();
    private string _sig;

    public void Add(string label, string value, bool positive = false) { Rows.Add(new HoverRow { Label = label, Value = value, Positive = positive }); _sig = null; }

    private string Build()
    {
        var sb = new System.Text.StringBuilder(Title ?? "");
        sb.Append('|').Append(IconKey);
        foreach (var r in Rows) sb.Append('|').Append(r.Label).Append(':').Append(r.Value);
        return sb.ToString();
    }
}

/// <summary>
/// Works out what the crosshair is on and what the hover panel shows (see <see cref="HoverPanel"/> for how it is drawn).
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
///  - A price tag: <c>PriceTag</c> (shelf slot, <c>m_DisplaySlot</c>) or <c>VendingPriceTag</c> (vending slot, <c>VendingSlot</c>);
///    racks have none. <c>VendingPriceTag</c> is a SUBCLASS of <c>PriceTag</c> (its <c>EnableTag</c> writes only <c>m_VendingSlot</c>,
///    so the inherited <c>DisplaySlot</c> stays null): <c>Resolve</c> must test for <c>VendingPriceTag</c> before <c>PriceTag</c>,
///    because <c>GetComponent&lt;PriceTag&gt;()</c> returns vending tags too. Same content as the slot's Label. The price tag is its own object with its own BoxCollider
///    (layer Interactable, like the Label), on the same GameObject as the component. That GameObject is inactive in the
///    prefab and only activated by <c>PriceTag.EnableTag</c> (<c>DisplaySlot.SetPriceTag</c>, when the slot has a product),
///    and the collider is switched off while the furniture is carried or being placed
///    (<c>Display.set_EnablePriceTagColliders(false)</c> in StartPlacingMode, true again in Place), so it can be hit only while the slot
///    has a product and the furniture is standing (not carried or being placed).
///
/// What is shown (decision, no vanilla tooltip exists for this):
///  - title: brand + localized name (<see cref="DisplayName"/>); the icon is the product's atlas icon;
///  - ONLY on a product box (loose, racked or held): quantity = items inside / capacity (<c>Box.ProductCount</c> /
///    <c>MaxProductCount</c>) and the box price = <c>ProductSO.BoxPrice</c>, the price of one full box of that product at
///    the current market cost (<c>PriceManager.CurrentCost(id) * GridLayoutInBox.productCount</c>);
///  - shelf, rack and vending tags: icon and name only, nothing else;
///  - furniture box: furniture name, its shop price, the furniture's icon sprite; a paint-can box: name only.
/// </summary>
internal static class HoverInfo
{
    private static IntPtr s_lastTarget;
    internal static string LastKind = "";

    /// <summary>The text to show now, or null.</summary>
    internal static HoverData Resolve()
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

        HoverData text = null;
        IntPtr target = IntPtr.Zero;

        // The box in the hands: it is on "Ignore Raycast", so the physics ray never sees it.
        var held = HeldBox(p);
        if (held != null && TagHit(TagScaler.UiOf(held), ray, range + 1f))
        {
            target = held.Pointer;
            s_pBox = held;
            text = Cached(target, KBox);
            LastKind = "held box";
        }
        else if (HeldFurnitureBox(p) is FurnitureBox heldFb && TagHit(TagScaler.UiOf(heldFb), ray, range + 1f))
        {
            target = heldFb.Pointer;
            s_pFb = heldFb;
            text = Cached(target, KFurn);
            LastKind = "held furniture box";
        }
        else if (Physics.Raycast(origin, dir, out var hit, range, pi.m_InteractableLayer.value))
        {
            var col = hit.collider;
            if (col != null)
            {
                var go = col.gameObject;
                var label = go.GetComponent<Label>();
                // VendingPriceTag derives from PriceTag, so GetComponent<PriceTag>() would also return a vending tag
                // (whose DisplaySlot is null): the vending tag has to be looked up first.
                var vendingTag = label == null ? go.GetComponent<VendingPriceTag>() : null;
                var priceTag = label == null && vendingTag == null ? go.GetComponent<PriceTag>() : null;
                if (label != null)
                {
                    target = label.Pointer;
                    s_pLabel = label;
                    text = Cached(target, KLabel);
                }
                else if (vendingTag != null)
                {
                    target = vendingTag.Pointer;
                    s_pVendingTag = vendingTag;
                    text = Cached(target, KVendingTag);
                }
                else if (priceTag != null)
                {
                    target = priceTag.Pointer;
                    s_pPriceTag = priceTag;
                    text = Cached(target, KPriceTag);
                }
                else
                {
                    var box = go.GetComponent<Box>();
                    if (box != null)
                    {
                        if (TagHit(TagScaler.UiOf(box), ray, range))
                        {
                            target = box.Pointer;
                            s_pBox = box;
                            text = Cached(target, KBox);
                            LastKind = box.m_Racked ? "racked box" : "box";
                        }
                    }
                    else
                    {
                        var fb = go.GetComponent<FurnitureBox>();
                        if (fb != null && TagHit(TagScaler.UiOf(fb), ray, range))
                        {
                            target = fb.Pointer;
                            s_pFb = fb;
                            text = Cached(target, KFurn);
                            LastKind = "furniture box";
                        }
                    }
                }
            }
        }

        if (target != s_lastTarget)
        {
            s_lastTarget = target;
            if (text != null) Plugin.Info($"hover ({LastKind}): {text.Signature}");
        }
        return text;
    }

    private static IntPtr s_cTarget;
    private static HoverData s_cText;
    private static float s_cAt;

    // What to build on a cache miss. Static fields and an int kind instead of a lambda: no closure is allocated per frame.
    private const int KBox = 0, KFurn = 1, KLabel = 2, KPriceTag = 3, KVendingTag = 4;
    private static Box s_pBox;
    private static FurnitureBox s_pFb;
    private static Label s_pLabel;
    private static PriceTag s_pPriceTag;
    private static VendingPriceTag s_pVendingTag;

    /// <summary>
    /// The text is built (game lookups, string formatting) only when the target changes, and then at most twice a
    /// second so quantities stay current; not 60 times a second.
    /// </summary>
    private static HoverData Cached(IntPtr target, int kind)
    {
        float now = Time.unscaledTime;
        if (target == s_cTarget && now - s_cAt < 0.5f) return s_cText;
        s_cTarget = target;
        s_cAt = now;
        s_cText = null;   // if the build throws, don't keep showing the previous target's text
        try
        {
            switch (kind)
            {
                case KBox: s_cText = BoxData(s_pBox); break;
                case KFurn: s_cText = FurnitureBoxData(s_pFb); break;
                case KPriceTag: s_cText = PriceTagData(s_pPriceTag, out var pk); LastKind = pk; break;
                case KVendingTag: s_cText = VendingPriceTagData(s_pVendingTag, out var vk); LastKind = vk; break;
                default: s_cText = LabelData(s_pLabel, out var k); LastKind = k; break;
            }
        }
        catch (Exception) { s_cText = null; }   // cached as "nothing to show" for 0.5 s, then tried again
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
    /// mesh lies in. The "Color Point" of a furniture box is a small built-in Quad (extents (0.5, 0.5, 0), scale 0.025) and a tiny extra target.
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

    private static HoverData BoxData(Box box)
    {
        var so = box.Product;
        if (so == null) return null;
        var d = ProductHeader(so);
        int count = box.ProductCount;
        int max = 0;
        try { max = box.MaxProductCount; } catch (Exception) { }
        d.Add("Quantity:", max > 0 ? $"{count} / {max}" : count.ToString(), positive: true);
        string price = Money(() => so.BoxPrice);
        if (price != null) d.Add("Box price:", price);   // a failed price lookup leaves the row out
        return d;
    }

    /// <summary>
    /// Furniture box: name of the furniture inside (<c>LocalizationManager.LocalizedFurnitureName</c>), and
    /// <c>FurnitureSO.Cost</c> as the price (what the furniture shop charges for it; the box is just its packaging).
    /// There is no quantity. The icon is <c>FurnitureSO.FurnitureIcon</c> (a sprite of the furniture-shop atlas). A paint-bucket
    /// box shows the bucket name without a price or icon.
    /// </summary>
    private static HoverData FurnitureBoxData(FurnitureBox fb)
    {
        var data = fb.Data;
        if (data == null) return null;
        var d = new HoverData();
        string name = null;
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
                try
                {
                    var icon = so.FurnitureIcon;
                    if (icon != null) { d.IconSprite = icon; d.IconKey = "f" + so.ID; }
                }
                catch (Exception) { }
                string price = Money(() => so.Cost);
                if (price != null) d.Add("Price:", price);
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
        d.Title = name;
        return d;
    }

    /// <summary>Shelf, rack and vending tags: only the product (icon and brand + name); quantity and price are for boxes only.</summary>
    private static HoverData LabelData(Label label, out string kind)
    {
        kind = "label";
        int id = 0;
        var ds = label.DisplaySlot;
        if (ds != null) { kind = "shelf tag"; id = ds.ProductID; }
        else
        {
            var rs = label.RackSlot;
            if (rs != null)
            {
                kind = "rack tag";
                var data = rs.Data;
                if (data == null) return null;
                id = data.ProductID;
            }
            else
            {
                var vs = label.VendingSlot;
                if (vs != null) { kind = "vending tag"; id = vs.ProductID; }
            }
        }
        return ProductData(id);
    }

    /// <summary>The price tag of a shelf slot: the same content as that slot's Label.</summary>
    private static HoverData PriceTagData(PriceTag tag, out string kind)
    {
        kind = "shelf price tag";
        var ds = tag.DisplaySlot;
        return ds != null ? ProductData(ds.ProductID) : null;
    }

    /// <summary>The price tag of a vending machine slot: the same content as that slot's Label.</summary>
    private static HoverData VendingPriceTagData(VendingPriceTag tag, out string kind)
    {
        kind = "vending price tag";
        var vs = tag.VendingSlot;
        return vs != null ? ProductData(vs.ProductID) : null;
    }

    /// <summary>Icon and brand + name of a product id (what every shelf/rack/vending tag shows), or null when the id is unknown.</summary>
    private static HoverData ProductData(int productId)
    {
        var so = Lookup(productId);
        return so != null ? ProductHeader(so) : null;
    }

    private static ProductSO Lookup(int productId)
    {
        if (productId <= 0 || !NoktaSingleton<IDManager>.HasInstance) return null;
        return NoktaSingleton<IDManager>.Instance.ProductSO(productId);
    }

    private static HoverData ProductHeader(ProductSO so)
    {
        return new HoverData { Title = DisplayName(so), ProductId = so.ID, IconKey = "p" + so.ID };
    }

    private static string Money(Func<float> value)
    {
        try { return "$" + value().ToString("0.00", CultureInfo.InvariantCulture); } catch (Exception) { return null; }
    }

    /// <summary>
    /// "Brand Name", for example "Kilemol Cleaner". The game's own cards (market, pricing, scanner) show
    /// <c>ProductSO.ProductBrand</c> (a plain, not localized string, field at 0x30) in a separate text next to the localized name
    /// (<c>LocalizationManager.LocalizedProductName(id)</c>, which <c>ProductSO.TempProductName</c> wraps), so the two are joined here. The brand
    /// is left out when it is empty or the name already contains it.
    /// </summary>
    internal static string DisplayName(ProductSO so)
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

        string brand = null;
        try { brand = so.ProductBrand?.Trim(); } catch (Exception) { }
        if (!string.IsNullOrEmpty(brand) && !ContainsWord(name, brand))
            return brand + " " + name;
        return name;
    }

    /// <summary>Whole-word, case-insensitive containment ("Ice" is not found in "Rice", but is in "Ice Cream" and "Fresh Ice").</summary>
    private static bool ContainsWord(string text, string word)
    {
        int from = 0;
        while (from <= text.Length - word.Length)
        {
            int i = text.IndexOf(word, from, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return false;
            bool startOk = i == 0 || !char.IsLetterOrDigit(text[i - 1]);
            int end = i + word.Length;
            bool endOk = end >= text.Length || !char.IsLetterOrDigit(text[end]);
            if (startOk && endOk) return true;
            from = i + 1;
        }
        return false;
    }
}
