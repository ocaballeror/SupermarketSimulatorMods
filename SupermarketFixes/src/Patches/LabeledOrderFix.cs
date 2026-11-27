using System;
using BepInEx.Configuration;
using HarmonyLib;

namespace SupermarketFixes.Patches;

/// <summary>
/// Bug #11: after taking an order, labelling the bag (name tag) and dropping it on the floor, a new bag
/// can be opened for the very same order, and the "new order" counter clears only on the screen of the
/// player who handled it; the other players keep the order as new and unclaimed.
///
/// Root cause (native analysis of GameAssembly.dll):
///  - The game never marks an order as "taken by a player". The only things that exist are
///    (1) the "worked on by" text: OrderNotebook.OnViewed registers the viewing player with
///    NetworkOrderManager.ToggleWorking_RPC and OnCanceledView / SwapNotebook / DisableOrderInteraction
///    unregister it, so it disappears as soon as the bag leaves the hands;
///    (2) the label icon: OnlineOrderInteraction.OnAttachTag sends RequestLabelIconEvent_RPC to everybody
///    (a small icon on the order's notebook page) and sets a local-only flag in
///    CourierManager.m_PlayerClaimedOrders that nothing reads (it only fires ClaimChanged);
///    (3) the courier claim (CourierManager.m_ClaimedOrders), which only starts at the hand-over.
///    So a labelled bag lying on the floor protects nothing: OnAttachTag only refuses when a courier
///    already holds the order.
///  - The notebook of the order stays CurrentInstance (list index 0) after the bag is dropped, so the next
///    bag that is opened (EnableAfterSpawn -> TakePaperBag) is bound to that same order again.
///  - The counter is OrderManager.GetUnreadOrderCount: orders whose OrderListData.IsRead is false.
///    IsRead is not networked. DisableOrderInteraction (end of the bag interaction) calls
///    OrderNotebook.Read() on the current notebook of that player only, so the counter drops for
///    the player who handled the order and nowhere else. (Same mechanism as Bug #9, which covers the
///    courier hand-over only.) OnlineOrderInteraction.Drop also calls RemoveInstanceForOrder with the
///    order of the bag PREFAB (m_OrderPaperBagPrefab+0x78, always null) instead of the dropped bag's order,
///    which looks like an intended "remove this order's page" that never happens.
///
/// Fix (local; every player who wants it needs the mod, because the unread flag and notebook are local and
/// the game has no message that would mark an order as handled):
///  - A labelled bag counts as "this order is being handled": OrderManager.GetUnreadOrderCount leaves out
///    orders for which OnlineOrderPaperBagContainer.HasAttachedOrder finds a bag with a tag in the world
///    (the same bag list every client keeps, tag state arrives with the drop RPC). The counter is
///    refreshed (OrderManager.CanShowUI) when a tag is attached or detached locally, when a label request
///    arrives, and when a dropped or placed bag arrives from another player. The order stays in the
///    notebook; only the new-order badge changes.
///  - When the local player takes a bag that carries no tag (a new bag, or an unlabelled one from the floor)
///    and the current notebook belongs to an order that already has a labelled bag, TakePaperBag is
///    preceded by the game's own OnlineOrderInteraction.SelectNotebookOf for the next order without a
///    labelled bag (and no courier). If every order is labelled the notebook is left alone. Scrolling back
///    to a labelled order stays possible.
/// </summary>
internal static class LabeledOrderFix
{
    internal static ConfigEntry<bool> LabeledOrdersTaken;

    internal static void Bind(ConfigFile cfg)
    {
        LabeledOrdersTaken = cfg.Bind("LabeledOrders", "LabeledOrdersTaken", true,
            "An order whose paper bag has a name tag counts as taken: it no longer counts as new in the " +
            "order counter, and a new bag opens on the next order without a labelled bag instead of the same one. " +
            "Only affects your own screen: every player who wants this needs the mod.");
    }

    internal static void ResetState()
    {
        s_loggedError = false;
        if (!Plugin.Loaded) return; // BasePlugin.Load: the game is not running yet
        try
        {
            if (NoktaSingleton<OrderManager>.HasInstance) NoktaSingleton<OrderManager>.Instance.CanShowUI();
        }
        catch (Exception e) { LogError("ResetState", e); }
    }

    internal static bool On => Plugin.Active && LabeledOrdersTaken != null && LabeledOrdersTaken.Value;

    private static bool s_loggedError;

    internal static void LogError(string where, Exception e)
    {
        if (s_loggedError) return;
        s_loggedError = true;
        Plugin.Logger.LogError($"[LabeledOrders] {where} failed (further errors suppressed): {e}");
    }

    private static void Log(string msg)
    {
        if (Plugin.VerboseLogging != null && Plugin.VerboseLogging.Value) Plugin.Logger.LogInfo($"[LabeledOrders] {msg}");
    }

    private static bool Labelled(OrderListData o) => o != null && OnlineOrderPaperBagContainer.HasAttachedOrder(o);

    internal static void AdjustUnread(OrderManager mgr, ref int result)
    {
        if (!On) return;
        var list = mgr.GeneratedOrderLists;
        if (list == null) return;
        int n = 0;
        for (int i = 0; i < list.Count; i++)
        {
            var o = list[i];
            if (o == null || o.IsRead) continue;
            if (CourierOrdersFix.On && CourierOrdersFix.IsClaimed(o.ID)) continue; // already left out by Bug #9
            if (Labelled(o)) n++;
        }
        if (n > 0) result = Math.Max(0, result - n);
    }

    internal static void Refresh()
    {
        if (!On) return;
        try
        {
            if (NoktaSingleton<OrderManager>.HasInstance) NoktaSingleton<OrderManager>.Instance.CanShowUI();
        }
        catch (Exception e) { LogError("CanShowUI", e); }
    }

    internal static void BeforeTakePaperBag(OnlineOrderInteraction oi, OrderPaperBag bag)
    {
        if (!On || oi == null || bag == null || bag.IsTagAttached) return;
        var player = NoktaSingleton<PlayerManager>.HasInstance ? NoktaSingleton<PlayerManager>.Instance.LocalPlayer : null;
        if (player == null || player.OnlineOrderInteraction == null || player.OnlineOrderInteraction.Pointer != oi.Pointer) return;
        var container = oi.OrderNotebookContainer;
        if (container == null) return;
        var list = container.m_NotebookInstances;
        if (list == null || list.Count < 2) return;
        var cur = container.CurrentInstance;
        var curOrder = cur != null ? cur.OrderData : null;
        if (!Labelled(curOrder)) return;
        var courier = NoktaSingleton<SupermarketSimulator.Courier.CourierManager>.HasInstance
            ? NoktaSingleton<SupermarketSimulator.Courier.CourierManager>.Instance : null;
        for (int i = 1; i < list.Count; i++)
        {
            var o = list[i] != null ? list[i].OrderData : null;
            if (o == null || Labelled(o) || (courier != null && courier.IsClaimedByCourier(o.ID))) continue;
            Log($"order #{curOrder.ID} already has a labelled bag; the new bag opens on order #{o.ID}");
            oi.SelectNotebookOf(o);
            return;
        }
    }
}

[HarmonyPatch(typeof(OrderManager), nameof(OrderManager.GetUnreadOrderCount))]
internal static class LabeledOrders_GetUnreadOrderCount_Patch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(OrderManager __instance, ref int __result)
    {
        try { LabeledOrderFix.AdjustUnread(__instance, ref __result); }
        catch (Exception e) { LabeledOrderFix.LogError("GetUnreadOrderCount postfix", e); }
    }
}

[HarmonyPatch(typeof(OnlineOrderInteraction), nameof(OnlineOrderInteraction.TakePaperBag))]
internal static class LabeledOrders_TakePaperBag_Patch
{
    private static void Prefix(OnlineOrderInteraction __instance, OrderPaperBag paperBag)
    {
        try { LabeledOrderFix.BeforeTakePaperBag(__instance, paperBag); }
        catch (Exception e) { LabeledOrderFix.LogError("TakePaperBag prefix", e); }
    }
}

[HarmonyPatch(typeof(OnlineOrderInteraction), nameof(OnlineOrderInteraction.OnAttachTag))]
internal static class LabeledOrders_OnAttachTag_Patch
{
    private static void Postfix() { try { LabeledOrderFix.Refresh(); } catch (Exception e) { LabeledOrderFix.LogError("OnAttachTag postfix", e); } }
}

[HarmonyPatch(typeof(OnlineOrderInteraction), nameof(OnlineOrderInteraction.OnDetachTag))]
internal static class LabeledOrders_OnDetachTag_Patch
{
    private static void Postfix() { try { LabeledOrderFix.Refresh(); } catch (Exception e) { LabeledOrderFix.LogError("OnDetachTag postfix", e); } }
}

[HarmonyPatch(typeof(NetworkOrderManager), nameof(NetworkOrderManager.RaiseLabelIconRequest))]
internal static class LabeledOrders_LabelIcon_Patch
{
    private static void Postfix() { try { LabeledOrderFix.Refresh(); } catch (Exception e) { LabeledOrderFix.LogError("RaiseLabelIconRequest postfix", e); } }
}

[HarmonyPatch(typeof(NetworkOnlineOrderInteraction), nameof(NetworkOnlineOrderInteraction.Drop_RPC))]
internal static class LabeledOrders_DropRpc_Patch
{
    private static void Postfix() { try { LabeledOrderFix.Refresh(); } catch (Exception e) { LabeledOrderFix.LogError("Drop_RPC postfix", e); } }
}

[HarmonyPatch(typeof(NetworkOnlineOrderInteraction), nameof(NetworkOnlineOrderInteraction.PlacePaperBag_RPC))]
internal static class LabeledOrders_PlaceRpc_Patch
{
    private static void Postfix() { try { LabeledOrderFix.Refresh(); } catch (Exception e) { LabeledOrderFix.LogError("PlacePaperBag_RPC postfix", e); } }
}
