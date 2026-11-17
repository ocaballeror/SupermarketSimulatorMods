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
///    box, which the ray can't hit: it is on "Ignore Raycast"), then the same ray is tested against the world
///    bounds of the tag renderers under <c>Box.m_IconUI</c> (padded a little; this follows the scaled size).
///    Racked boxes work too: vanilla swaps their interactable for the RackSlot, but the hit collider is still
///    the Box's.
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
        if (held != null && TagHit(held, ray, range + 1f))
        {
            text = BoxText(held);
            target = held.Pointer;
            LastKind = "held box";
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
                    text = LabelText(label, out LastKind);
                    target = label.Pointer;
                }
                else
                {
                    var box = go.GetComponent<Box>();
                    if (box != null && TagHit(box, ray, range))
                    {
                        text = BoxText(box);
                        target = box.Pointer;
                        LastKind = box.m_Racked ? "racked box" : "box";
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

    /// <summary>Does the ray pass through the (scaled) tag of this box?</summary>
    private static bool TagHit(Box box, Ray ray, float maxDistance)
    {
        var ui = box.m_IconUI;
        if (ui == null || !ui.activeInHierarchy) return false;
        bool any = false;
        var bounds = new Bounds();
        foreach (var r in ui.GetComponentsInChildren<MeshRenderer>())
        {
            if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
            if (!any) { bounds = r.bounds; any = true; }
            else bounds.Encapsulate(r.bounds);
        }
        if (!any) return false;
        bounds.Expand(0.03f);   // the quads are flat; a little slack so grazing angles still count
        return bounds.IntersectRay(ray, out float d) && d <= maxDistance;
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
