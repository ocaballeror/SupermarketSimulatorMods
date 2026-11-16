using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using DG.Tweening;
using HarmonyLib;
using SupermarketSimulator.Courier;

namespace SupermarketFixes.Patches;

/// <summary>
/// Bug #9: in multiplayer, when a player gives an online-order paper bag to the courier, the order drops out
/// of the order counter on that player's screen but not on the other player's. More generally, orders the
/// courier already has (in the trunk or out for delivery) stay in every player's pending-order list until the
/// customer has the bag.
///
/// What the game does (native analysis of GameAssembly.dll):
///  - The pending-order list on a screen is the local player's OrderNotebookContainer: one OrderNotebook
///    instance per order in OrderManager.GeneratedOrderLists (created by OnOrderIssued from the OrderIssued
///    event, the CreateOrder RPC, and LoadOrderData after a load; removed by OnOrderRemoved from the
///    OrderRemoved event). The number badge (OnlineOrderOfferIndicator.Count, set by OrderManager.CanShowUI)
///    is OrderManager.GetUnreadOrderCount: the orders in GeneratedOrderLists whose OrderListData.IsRead is
///    false. IsRead is not networked; every client has its own flag, set by OrderNotebook.Read.
///  - Handing a bag to the courier (OnlineOrderInteraction.GiveBagToCourier) ends the player's bag
///    interaction (DisableOrderInteraction calls CurrentInstance.Read), so the order becomes "read" for that
///    player only, and the badge drops there. For everyone else the order stays unread and in the list.
///  - The order is removed from OrderManager.GeneratedOrderLists (and so from every notebook list) only by
///    RemoveOrderOfId, which OrderCompleted / OrderExpired / CancelOrder reach: delivery to the customer,
///    expiry, cancelling. The courier handover only changes CourierManager's claim table
///    (m_ClaimedOrders: orderId -> Courier; Courier.ReceiveBag on the host, then NetworkCourier.Claim_Broadcast
///    -> CourierClaim_RPC -> CourierManager.ApplyNetworkClaim on everybody, and CourierSyncState_RPC for late
///    joiners), and fires CourierManager.ClaimChanged. The notebook only reacts to it by repainting the
///    "in the courier's trunk" / "out for delivery" status text.
///
/// Why the order can't simply be removed through the game's own removal: RemoveOrderOfId also drops the
/// customer's minimap marker and raises OrderRemoved, and CourierManager.OnOrderRemoved reacts to that by
/// releasing the courier's claim and its collection progress; the courier still needs the order (GetOrder) for
/// the hand-over, a take-back and the delivery payment. So the order must stay in OrderManager and in the
/// courier's claim table; only what the player looks at has to change.
///
/// Fix (local UI only; nothing is sent over the network and no game state is changed):
///  - Hide: when CourierManager.ClaimChanged fires for an order the courier holds (CourierManager.
///    IsClaimedByCourier), the local OrderNotebookContainer's notebook for that order is removed
///    (RemoveInstance, as OnOrderRemoved does). An OrderNotebookContainer.OnOrderIssued postfix does the same
///    for an order that is already claimed when its notebook is created (a save load, a late join).
///  - Count: an OrderManager.GetUnreadOrderCount postfix leaves courier-held orders out of the count, and the
///    claim handler re-runs OrderManager.CanShowUI so the badge refreshes at once.
///  - Show again: if the courier releases an order that still exists (a player takes the bag back from the
///    courier, the courier is dismissed, or the parcel is dropped), the notebook is re-created through the
///    game's own OnOrderIssued, but only for notebooks this patch removed, and after a short delay so a
///    release that is followed at once by the order's removal (delivery) doesn't flash it back. A
///    take-back by this player (OnlineOrderInteraction.ReceiveParcelFromCourier prefix) re-creates it
///    immediately, because that method selects the order's notebook for the bag in hand.
///  - Turning the fix (or the whole mod) off restores the removed notebooks.
///
/// Where it runs: every machine that should see the effect, since the lists are local UI. On the host it
/// fixes the host's screen; a guest needs the mod too. An unmodded guest keeps the vanilla list (the game
/// has no RPC that only hides an order: RemoveOrder_RPC removes it from the order list for good, which
/// would also release the courier's claim on that client).
/// </summary>
internal static class CourierOrdersFix
{
    internal static ConfigEntry<bool> HideCourierOrders;
    internal static ConfigEntry<bool> Diagnostics;

    internal static void Bind(ConfigFile cfg)
    {
        HideCourierOrders = cfg.Bind("CourierOrders", "HideCourierOrders", true,
            "Online orders that the courier has already taken (in the courier's trunk or out for delivery) " +
            "are removed from your pending-orders notebook and from the new-orders counter, instead of " +
            "staying there until the customer receives the bag. They come back if the order is taken back " +
            "from the courier. Only affects your own screen: every player who wants this needs the mod.");
        Diagnostics = cfg.Bind("CourierOrders", "Diagnostics", true,
            "Log a [CourierOrders] line whenever an order is hidden from or shown again in the notebook. " +
            "Turn off once the fix is confirmed.");
    }

    internal static bool On => Plugin.Active && HideCourierOrders != null && HideCourierOrders.Value;

    /// <summary>Order ids whose notebook this patch removed (so only those are ever re-created).</summary>
    private static readonly HashSet<int> s_hidden = new();
    private static bool s_force;
    private static bool s_loggedError;
    private static Il2CppSystem.Action<int> s_handler;
    private static CourierManager s_subscribed;
    private static IntPtr s_subscribedPtr;

    internal static void ResetState()
    {
        s_loggedError = false;
        try
        {
            if (Plugin.Active) SubscribeToExisting();
            else
            {
                Unsubscribe();
                RestoreAll();
            }
        }
        catch (Exception e) { LogError("ResetState", e); }
        if (!Plugin.Active) s_hidden.Clear();
    }

    internal static void LogError(string where, Exception e)
    {
        if (s_loggedError) return;
        s_loggedError = true;
        Plugin.Logger.LogError($"[CourierOrders] {where} failed (further errors suppressed): {e}");
    }

    private static void Log(string msg)
    {
        if (Diagnostics != null && Diagnostics.Value) Plugin.Logger.LogInfo($"[CourierOrders] {msg}");
    }

    internal static bool IsClaimed(int orderId)
    {
        if (!NoktaSingleton<CourierManager>.HasInstance) return false;
        var mgr = NoktaSingleton<CourierManager>.Instance;
        return mgr != null && mgr.IsClaimedByCourier(orderId);
    }

    // ---- subscription to CourierManager.ClaimChanged ------------------------------------------------

    internal static void SubscribeToExisting()
    {
        if (NoktaSingleton<CourierManager>.HasInstance) Subscribe(NoktaSingleton<CourierManager>.Instance);
    }

    internal static void Subscribe(CourierManager mgr)
    {
        if (mgr == null) return;
        if (s_subscribed != null && s_subscribedPtr == mgr.Pointer) return;
        Unsubscribe();
        s_handler ??= (Action<int>)OnClaimChanged;
        mgr.add_ClaimChanged(s_handler);
        s_subscribed = mgr;
        s_subscribedPtr = mgr.Pointer;
        Log("subscribed to CourierManager.ClaimChanged");
        // Claims that exist already (patch applied mid-session): hide what is held now.
        SweepClaimed();
    }

    private static void Unsubscribe()
    {
        var mgr = s_subscribed;
        s_subscribed = null;
        s_subscribedPtr = IntPtr.Zero;
        if (mgr == null || s_handler == null) return; // == null: destroyed Unity object or never set
        try { mgr.remove_ClaimChanged(s_handler); }
        catch (Exception e) { LogError("Unsubscribe", e); }
    }

    // ---- notebook list ------------------------------------------------------------------------------

    private static OrderNotebookContainer LocalContainer()
    {
        try { return OnlineOrderService.GetLocalNotebookContainer(); }
        catch (Exception) { return null; }
    }

    private static void RefreshBadge()
    {
        try
        {
            if (NoktaSingleton<OrderManager>.HasInstance) NoktaSingleton<OrderManager>.Instance.CanShowUI();
        }
        catch (Exception e) { LogError("CanShowUI", e); }
    }

    /// <summary>Removes the notebook of this order from <paramref name="container"/> (and remembers it).</summary>
    internal static bool Hide(OrderNotebookContainer container, int orderId, string why)
    {
        if (container == null) return false;
        var nb = container.GetNotebookOfOrderId(orderId);
        if (nb == null) return false;
        container.RemoveInstance(nb);
        s_hidden.Add(orderId);
        Log($"order #{orderId} hidden from the notebook ({why})");
        return true;
    }

    private static void Show(int orderId, string why)
    {
        if (!s_hidden.Contains(orderId)) return;
        var container = LocalContainer();
        var order = NoktaSingleton<OrderManager>.HasInstance ? NoktaSingleton<OrderManager>.Instance.GetOrder(orderId) : null;
        if (container == null || order == null)
        {
            // Delivered, expired or cancelled meanwhile (the order is gone), or no local notebook yet.
            if (order == null) s_hidden.Remove(orderId);
            return;
        }
        s_hidden.Remove(orderId);
        if (container.GetNotebookOfOrderId(orderId) != null) return;
        s_force = true;
        try { container.OnOrderIssued(order); }
        finally { s_force = false; }
        Log($"order #{orderId} shown in the notebook again ({why})");
        RefreshBadge();
    }

    private static void RestoreAll()
    {
        if (s_hidden.Count == 0) return;
        foreach (var id in new List<int>(s_hidden)) Show(id, "fix turned off");
    }

    private static void SweepClaimed()
    {
        if (!On || !NoktaSingleton<OrderManager>.HasInstance) return;
        var container = LocalContainer();
        var list = NoktaSingleton<OrderManager>.Instance.GeneratedOrderLists;
        if (container == null || list == null) return;
        bool any = false;
        for (int i = 0; i < list.Count; i++)
        {
            var o = list[i];
            if (o != null && IsClaimed(o.ID) && Hide(container, o.ID, "already with the courier")) any = true;
        }
        if (any) RefreshBadge();
    }

    internal static void OnClaimChanged(int orderId)
    {
        try
        {
            if (!On) return;
            if (IsClaimed(orderId))
            {
                if (Hide(LocalContainer(), orderId, "taken by the courier")) RefreshBadge();
                else RefreshBadge(); // the badge may still count it (never had a notebook yet)
            }
            else if (s_hidden.Contains(orderId))
            {
                // Wait a moment: a release that precedes the order's removal (delivery) shouldn't flash it back.
                DOVirtual.DelayedCall(0.4f, (TweenCallback)(Action)(() =>
                {
                    try
                    {
                        if (!On || IsClaimed(orderId)) return;
                        Show(orderId, "released by the courier");
                    }
                    catch (Exception e) { LogError("delayed show", e); }
                }));
            }
        }
        catch (Exception e) { LogError("ClaimChanged", e); }
    }

    /// <summary>OrderNotebookContainer.OnOrderIssued postfix: an order that is already with the courier gets no notebook.</summary>
    internal static void AfterOrderIssued(OrderNotebookContainer container, OrderListData data)
    {
        if (!On || s_force || data == null) return;
        if (!IsClaimed(data.ID)) return;
        if (Hide(container, data.ID, "issued while with the courier")) RefreshBadge();
    }

    /// <summary>The player takes a bag back from the courier: make sure its notebook exists before the game selects it.</summary>
    internal static void BeforeReceiveParcel(OrderPaperBag bag)
    {
        if (!On || bag == null) return;
        var order = bag.m_OrderData;
        if (order == null || !s_hidden.Contains(order.ID)) return;
        Show(order.ID, "taken back from the courier");
    }

    /// <summary>GetUnreadOrderCount postfix: the vanilla count, minus the orders the courier holds.</summary>
    internal static void AdjustUnread(OrderManager mgr, ref int result)
    {
        if (!On || !NoktaSingleton<CourierManager>.HasInstance) return;
        var list = mgr.GeneratedOrderLists;
        if (list == null) return;
        int n = 0;
        for (int i = 0; i < list.Count; i++)
        {
            var o = list[i];
            if (o == null || o.IsRead || IsClaimed(o.ID)) continue;
            n++;
        }
        result = n;
    }
}

[HarmonyPatch(typeof(CourierManager), nameof(CourierManager.Start))]
internal static class CourierOrders_CourierManagerStart_Patch
{
    private static void Postfix(CourierManager __instance)
    {
        try { if (CourierOrdersFix.On) CourierOrdersFix.Subscribe(__instance); }
        catch (Exception e) { CourierOrdersFix.LogError("CourierManager.Start postfix", e); }
    }
}

[HarmonyPatch(typeof(OrderNotebookContainer), nameof(OrderNotebookContainer.OnOrderIssued))]
internal static class CourierOrders_OnOrderIssued_Patch
{
    private static void Postfix(OrderNotebookContainer __instance, OrderListData data)
    {
        try { CourierOrdersFix.AfterOrderIssued(__instance, data); }
        catch (Exception e) { CourierOrdersFix.LogError("OnOrderIssued postfix", e); }
    }
}

[HarmonyPatch(typeof(OrderManager), "GetUnreadOrderCount")]
internal static class CourierOrders_GetUnreadOrderCount_Patch
{
    private static void Postfix(OrderManager __instance, ref int __result)
    {
        try { CourierOrdersFix.AdjustUnread(__instance, ref __result); }
        catch (Exception e) { CourierOrdersFix.LogError("GetUnreadOrderCount postfix", e); }
    }
}

[HarmonyPatch(typeof(OnlineOrderInteraction), nameof(OnlineOrderInteraction.ReceiveParcelFromCourier))]
internal static class CourierOrders_ReceiveParcel_Patch
{
    private static void Prefix(OrderPaperBag bag)
    {
        try { CourierOrdersFix.BeforeReceiveParcel(bag); }
        catch (Exception e) { CourierOrdersFix.LogError("ReceiveParcelFromCourier prefix", e); }
    }
}
