using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProductHighlight;

/// <summary>
/// Improvement #13: outline where the product in your hands belongs. Purely local and visual, nothing is patched or networked.
///
/// What the game does today (native dumps of Box / HighlightManager): <c>Box.UpdateMatchingDisplaySlotsHighlight</c> picks a mode for
/// the box the local player holds. Closed box: <c>HighlightMode.Rack</c>, the racked boxes with the same product get a green outline
/// (<c>HighlightManager.SetScrollHighlights</c>). Open box: <c>HighlightMode.Display</c>, the product groups of that product on the
/// shelves (<c>DisplaySlot.ActiveHighlightable</c>, or the label of an empty slot). Both go through the EPOOutline <c>Outlinable</c>
/// component, coloured <c>HighlightManager.m_OutlineColor</c>. The game does nothing for a shelf that is labeled but empty, and
/// nothing at all for a paper bag.
///
/// What this mod adds, from one throttled poll (<see cref="PollInterval"/>) of the local player's hands:
///   - holding a product <b>box</b>: every shelf, fridge and freezer (<c>Display</c>) with a slot labeled with that product is outlined
///     as a whole (its body renderers), full or empty. Slots are found with the game's own lookups: the labeled empty ones through
///     <c>DisplayManager.GetLabeledEmptyDisplaySlots(productId, list)</c>, the labeled ones with products through
///     <c>DisplayManager.DisplayedProducts[productId]</c> (the dictionary the game's open-box highlight uses). Carried or removed
///     displays are not in the manager, so they aren't outlined.
///   - holding an online-order <b>paper bag</b>: the product groups (<c>DisplaySlot.m_Highlightable.m_RenderersToHighlight</c>, all product
///     renderers of the slot) of every slot that holds a product the order still needs (order quantity minus what is in the bag). An
///     empty slot is never outlined. The order is the same one the bag is packed for in BulkShelfTransfer: the bag's own tag if attached,
///     else the order selected in the notebook.
///   - live update: the poll recomputes the wanted set every time, so label changes, stock changes, a display being placed or picked
///     up, and the bag filling up are all picked up within a quarter second; outlines are only touched when the set changes (a product
///     group is rebuilt when its renderer list changes).
///   - clearing: when nothing qualifies (box/bag put down, thrown, racked, trashed, or the setting/mod turned off) every outline is destroyed.
///
/// Held item: <c>PlayerObjectHolder.m_CurrentObject</c> counts only while it hangs directly under <c>m_ObjectHolder</c> (the game never
/// clears <c>m_CurrentObject</c> on a drop). Outline drawing: see <see cref="OutlineHandle"/>.
/// </summary>
public class HighlightBehaviour : MonoBehaviour
{
    public HighlightBehaviour(IntPtr ptr) : base(ptr) { }

    private const float PollInterval = 0.25f;

    private static HighlightBehaviour s_instance;
    private static bool s_refresh;

    private float _next;
    private readonly Dictionary<int, OutlineHandle> _shelves = new();   // Display instance id -> outline of the whole furniture
    private readonly Dictionary<int, OutlineHandle> _groups = new();    // DisplaySlot instance id -> outline of its products
    private readonly Dictionary<int, Display> _wantedShelves = new();
    private readonly Dictionary<int, DisplaySlot> _wantedGroups = new();
    private readonly List<int> _stale = new();
    private Il2CppSystem.Collections.Generic.List<DisplaySlot> _labeled;
    private string _lastDescription = "";
    private string _lastError;
    private int _errors;

    internal static void RequestRefresh() => s_refresh = true;

    internal static void Shutdown()
    {
        if (s_instance != null) s_instance.ClearAll();
    }

    private void Awake() { s_instance = this; }

    private void OnDestroy()
    {
        try { ClearAll(); } catch (Exception) { }
        if (s_instance == this) s_instance = null;
    }

    private void Update()
    {
        if (s_instance == null) s_instance = this;
        float now = Time.unscaledTime;
        if (!s_refresh && now < _next) return;
        s_refresh = false;
        _next = now + PollInterval;
        try
        {
            Tick();
            _errors = 0;
        }
        catch (Exception e)
        {
            // Fail safe: drop everything so a broken state can't leave stray outlines, and don't spam the log.
            _errors++;
            string msg = e.GetType().Name + ": " + e.Message;
            if (msg != _lastError) { _lastError = msg; Plugin.Logger.LogError($"[Highlight] tick failed ({_errors}): {e}"); }
            try { ClearAll(); } catch (Exception) { }
            _next = now + Math.Min(5f, 0.5f * _errors);
        }
    }

    // ---- the poll ----

    private void Tick()
    {
        _wantedShelves.Clear();
        _wantedGroups.Clear();
        string description = "nothing held";

        if (Plugin.Enabled.Value)
        {
            var player = LocalPlayer();
            if (player != null)
            {
                var held = HeldObject(player, out var box, out var bag);
                if (box != null)
                {
                    int pid = ProductOf(box);
                    description = $"box of product {pid}";
                    if (Plugin.ShelvesForBox.Value && pid > 0) CollectShelves(pid);
                    else description += " (shelf highlight off)";
                }
                else if (bag != null)
                {
                    description = "paper bag";
                    if (Plugin.ProductsForBag.Value) description += CollectOrderGroups(player, bag);
                    else description += " (product highlight off)";
                }
                else if (held != null) description = "something else";
            }
        }

        SyncShelves();
        SyncGroups();

        string now = $"{description}: {_shelves.Count} shelves, {_groups.Count} product groups outlined";
        if (now != _lastDescription)
        {
            _lastDescription = now;
            Plugin.Info(now);
        }
    }

    private static PlayerInstance LocalPlayer()
    {
        if (!NoktaSingleton<PlayerManager>.HasInstance) return null;
        var p = NoktaSingleton<PlayerManager>.Instance.LocalPlayer;
        if (p == null || !p.IsLocalPlayerInstance) return null;
        return p;
    }

    /// <summary>The object in the local player's hands (null if none), and whether it is a product box or an order paper bag.</summary>
    private static GameObject HeldObject(PlayerInstance p, out Box box, out OrderPaperBag bag)
    {
        box = null; bag = null;
        var holder = p.PlayerObjectHolder;
        if (holder == null) return null;
        var hold = holder.m_ObjectHolder;
        var obj = holder.m_CurrentObject;
        if (hold == null || obj == null) return null;
        if (obj.transform.parent != hold) return null;   // m_CurrentObject is stale after a drop
        box = obj.GetComponent<Box>();
        if (box == null) bag = obj.GetComponent<OrderPaperBag>();
        return obj;
    }

    private static int ProductOf(Box box)
    {
        var data = box.Data;
        return data != null ? data.ProductID : 0;
    }

    // ---- what to outline ----

    private static bool IsShelfLike(Display d)
    {
        switch (d.m_Type)
        {
            case DisplayType.TRASH:
            case DisplayType.CRATE:
            case DisplayType.ICE_CREAM_STAND:
                return false;   // not "a shelf, fridge or freezer"; ice cream stands have their own flavour slots
            default:
                return true;
        }
    }

    private void CollectShelves(int pid)
    {
        if (!NoktaSingleton<DisplayManager>.HasInstance) return;
        var dm = NoktaSingleton<DisplayManager>.Instance;
        if (dm == null) return;

        // Labeled but empty (the game's own search used by customers and restockers).
        _labeled ??= new Il2CppSystem.Collections.Generic.List<DisplaySlot>();
        if (dm.GetLabeledEmptyDisplaySlots(pid, _labeled) > 0)
            for (int i = 0; i < _labeled.Count; i++) AddShelf(_labeled[i], pid);

        // Labeled with products on them.
        var dict = dm.DisplayedProducts;
        if (dict != null && dict.TryGetValue(pid, out var slots) && slots != null)
            for (int i = 0; i < slots.Count; i++) AddShelf(slots[i], pid);
    }

    private void AddShelf(DisplaySlot slot, int pid)
    {
        if (slot == null || slot.ProductID != pid) return;   // the dictionary can hold stale entries
        var d = slot.m_Display;
        if (d == null || !IsShelfLike(d) || !d.gameObject.activeInHierarchy) return;
        _wantedShelves[d.GetInstanceID()] = d;
    }

    /// <summary>Fills <see cref="_wantedGroups"/> for the bag's order; returns a short text for the log.</summary>
    private string CollectOrderGroups(PlayerInstance p, OrderPaperBag bag)
    {
        var order = ActiveOrder(p, bag);
        if (order == null) return " (no order selected)";
        if (!NoktaSingleton<DisplayManager>.HasInstance) return "";
        var dm = NoktaSingleton<DisplayManager>.Instance;
        var dict = dm != null ? dm.DisplayedProducts : null;
        if (dict == null) return "";

        var items = order.OrderItems;
        int needed = 0;
        for (int k = 0; items != null && k < items.Count; k++)
        {
            var it = items[k];
            if (it == null) continue;
            int pid = it.ProductID;
            if (pid <= 0 || it.ProductCount - CountInBag(bag, pid) <= 0) continue;
            needed++;
            if (!dict.TryGetValue(pid, out var slots) || slots == null) continue;
            for (int i = 0; i < slots.Count; i++)
            {
                var s = slots[i];
                if (s == null || s.ProductID != pid || s.ProductCount <= 0) continue;
                var d = s.m_Display;
                if (d == null || !d.gameObject.activeInHierarchy) continue;
                _wantedGroups[s.GetInstanceID()] = s;
            }
        }
        return $" for order #{order.ID} ({needed} product(s) still needed)";
    }

    private static int CountInBag(OrderPaperBag bag, int pid)
    {
        int n = 0;
        var list = bag.m_SortedProducts;
        for (int i = 0; list != null && i < list.Count; i++)
            if (list[i] != null && list[i].ProductID == pid) n++;
        return n;
    }

    /// <summary>The order the bag is meant for: its own tag if attached, else the order selected in the player's notebook.</summary>
    private static OrderListData ActiveOrder(PlayerInstance p, OrderPaperBag bag)
    {
        OrderListData order = null;
        try { if (bag.IsTagAttached) order = bag.m_OrderData; } catch (Exception) { }
        if (!IsRealOrder(order))
        {
            order = null;
            try
            {
                var oi = p.OnlineOrderInteraction;
                var nb = oi != null ? oi.m_OrderNotebookContainer : null;
                if (nb != null && !nb.IsEmpty) order = nb.CurrentOrder;
            }
            catch (Exception) { order = null; }
        }
        if (!IsRealOrder(order) || order.IsExpired) return null;
        return order;
    }

    private static bool IsRealOrder(OrderListData o) =>
        o != null && o.ID > 0 && o.OrderItems != null && o.OrderItems.Count > 0;

    // ---- applying the wanted sets ----

    /// <summary>Forgets outlines whose object died under us (a display or product destroyed while outlined).</summary>
    private void PruneDead(Dictionary<int, OutlineHandle> current)
    {
        _stale.Clear();
        foreach (var kv in current)
            if (!kv.Value.Alive) _stale.Add(kv.Key);
        for (int i = 0; i < _stale.Count; i++) { current[_stale[i]].Destroy(); current.Remove(_stale[i]); }
    }

    private void SyncShelves()
    {
        var current = _shelves;
        PruneDead(current);
        _stale.Clear();
        foreach (var kv in current)
            if (!_wantedShelves.ContainsKey(kv.Key)) _stale.Add(kv.Key);
        for (int i = 0; i < _stale.Count; i++) { current[_stale[i]].Destroy(); current.Remove(_stale[i]); }

        foreach (var kv in _wantedShelves)
        {
            if (current.ContainsKey(kv.Key)) continue;
            try
            {
                var renderers = BodyRenderers(kv.Value);
                if (renderers.Count == 0) { Plugin.Warn($"no body renderers found on display {kv.Value.name}"); continue; }
                var made = OutlineHandle.Create(kv.Value.transform, "ProductHighlight Outline", renderers, renderers.Count, OutlineColor());
                current[kv.Key] = made;
                LogFirstOutlines($"shelf {kv.Value.name}", renderers.Count, made);
            }
            catch (Exception e) { Plugin.Warn($"outline for display {kv.Value.name} failed: {e.Message}"); }
        }
    }

    private void SyncGroups()
    {
        var current = _groups;
        PruneDead(current);
        _stale.Clear();
        foreach (var kv in current)
            if (!_wantedGroups.ContainsKey(kv.Key)) _stale.Add(kv.Key);
        for (int i = 0; i < _stale.Count; i++) { current[_stale[i]].Destroy(); current.Remove(_stale[i]); }

        foreach (var kv in _wantedGroups)
        {
            try
            {
                var renderers = ProductRenderers(kv.Value, out int sig);
                if (current.TryGetValue(kv.Key, out var h))
                {
                    if (h.Signature == sig) continue;           // products unchanged
                    h.Destroy();                                 // products came or went: rebuild
                    current.Remove(kv.Key);
                }
                if (renderers.Count == 0) continue;
                var made = OutlineHandle.Create(kv.Value.transform, "ProductHighlight Outline", renderers, sig, OutlineColor());
                current[kv.Key] = made;
                LogFirstOutlines($"product group {kv.Value.name} (display {(kv.Value.m_Display != null ? kv.Value.m_Display.name : "?")})", renderers.Count, made);
            }
            catch (Exception e) { Plugin.Warn($"outline for slot {kv.Value.name} failed: {e.Message}"); }
        }
    }

    private static int s_createLogs;

    /// <summary>Logs the first few outlines created, to confirm in-game that renderers were found and targets accepted.</summary>
    private static void LogFirstOutlines(string what, int renderers, OutlineHandle h)
    {
        if (++s_createLogs > 8) return;
        var c = h.Outlinable.outlineParameters.color;
        Plugin.Info($"outline created on {what}: {h.TargetCount} target(s) from {renderers} renderer(s), colour ({c.r:0.##}, {c.g:0.##}, {c.b:0.##}, {c.a:0.##})");
    }

    private void ClearAll()
    {
        foreach (var h in _shelves.Values) h.Destroy();
        foreach (var h in _groups.Values) h.Destroy();
        _shelves.Clear();
        _groups.Clear();
        _wantedShelves.Clear();
        _wantedGroups.Clear();
    }

    // ---- renderers ----

    /// <summary>The game's outline colour (green); the same one the racked-box and shelf highlights use.</summary>
    private static Color OutlineColor()
    {
        try
        {
            if (NoktaSingleton<HighlightManager>.HasInstance)
            {
                var c = NoktaSingleton<HighlightManager>.Instance.m_OutlineColor;
                if (c.a > 0.01f) return c;
            }
        }
        catch (Exception) { }
        return new Color(0.2f, 1f, 0.3f, 1f);
    }

    /// <summary>
    /// The furniture model of a display: every enabled mesh renderer under it, except the ones under a slot (products, price tag,
    /// label), the placing-mode hologram and the sale signs. Built once per outline.
    /// </summary>
    private static List<Renderer> BodyRenderers(Display d)
    {
        var result = new List<Renderer>();
        var all = d.GetComponentsInChildren<Renderer>(false);
        var root = d.transform;
        GameObject placing = null;
        try { var pm = d.m_PlacingMode; if (pm != null) placing = pm.gameObject; } catch (Exception) { }
        for (int i = 0; i < all.Length; i++)
        {
            var r = all[i];
            if (r == null || !r.enabled) continue;
            if (r.TryCast<MeshRenderer>() == null && r.TryCast<SkinnedMeshRenderer>() == null) continue;
            if (placing != null && r.gameObject == placing) continue;
            if (r.GetComponentInParent<DisplaySlot>() != null) continue;
            if (UnderNamed(r.transform, root, "Sale Signs")) continue;
            result.Add(r);
        }
        return result;
    }

    private static bool UnderNamed(Transform t, Transform stop, string name)
    {
        for (var p = t; p != null && p != stop; p = p.parent)
            if (p.name == name) return true;
        return false;
    }

    /// <summary>The renderers of every product on the slot (the game keeps them in the slot's Highlightable), plus a hash of the list.</summary>
    private static List<Renderer> ProductRenderers(DisplaySlot slot, out int signature)
    {
        var result = new List<Renderer>();
        int sig = 17;
        var h = slot.m_Highlightable;
        var list = h != null ? h.m_RenderersToHighlight : null;
        if (list != null)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var r = list[i];
                if (r == null) continue;
                result.Add(r);
                long ptr = r.Pointer.ToInt64();
                sig = sig * 31 + (int)(ptr ^ (ptr >> 32));
            }
        }
        signature = sig * 31 + result.Count;
        return result;
    }
}
