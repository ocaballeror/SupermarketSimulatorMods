/*
 * Improvement #9, online-order paper bags: modifier + Take fills the held paper bag from the shelf
 * slot, modifier + Use puts the bag's products for that slot back onto it.
 *
 * How the game does it (native dumps of OnlineOrderInteraction / OnlineOrderService):
 *   - OnTake(down) / OnUse(down) latch m_holdingTake / m_holdingUse (after handling couriers, products
 *     on the floor, customers, trash bins, placement areas and opening the bag). Update then, once per
 *     m_ProductInteractionInterval, calls
 *       take:  CheckForTakingItem -> OnlineOrderService.TryTakeProductIntoBag(m_CurrentDisplaySlot,
 *              m_CurrentOrderPaperBag, m_MaxBagItemCount, sfxPos, m_TakeProductBroadcast)
 *              -> TakeProductStatus {Success, NoProduct, BagFull, ItemNull}
 *       place: PlaceProductToDisplay()  (checks bag.HasProduct, bag.IsTargetProductExist(slot) and the
 *              slot's display type / Full, shows the game's warnings itself)
 *   - Both are local-authoritative on every client: the acting client changes its own state at once and
 *     broadcasts (TakeProductFromDisplay_Broadcast / PlaceProductToDisplay_Broadcast). No host round trip.
 *   - Animation: OrderPaperBagAnimation.PlayProductMove builds a DOTween Sequence (move/rotate/scale);
 *     placing back onto a shelf goes through DisplaySlot.AddProduct.
 *
 * What the mod does: modifier + click on a slot (bag open, nothing else targeted) starts a burst instead
 * of latching the hold flag; the OnlineOrderInteraction.Update prefix then calls the same game methods
 * once per step until they stop moving products (bag full, slot empty, slot full, no matching product).
 * Each step runs inside TweenBoost, so the game's fly animation is just played faster.
 *
 * Order cap (rework 2026-11-15): when the bag is being packed for an active delivery, a take burst only
 * takes what the order still needs of that product (order quantity minus what the bag already holds);
 * otherwise as much as the bag can carry. "Active delivery" (the user's definition): there is an order,
 * and nobody else is handling it. The order is the bag's own tag (OrderPaperBag.m_OrderData, once a tag is
 * attached) or else the order selected in the player's notebook (OrderNotebookContainer.CurrentOrder).
 * "Handled by someone else" uses the same checks as the notebook's "worked on by" label
 * (OrderNotebook.UpdateWorkedStatus): CourierManager.TryGetClaimStatus (a courier claimed it / is out
 * delivering it) and, online, NetworkOrderManager.IsBeingWorkedOn(id, ownUserId, out by) (another
 * player). Rule: a product the order wants and still needs is capped at what it needs; a product the order wants
 * and the bag already has in full gets no burst (the click is swallowed with the game's "paper bag is full" popup);
 * a product that isn't part of the order is not limited and fills the bag like the crate (v0.2.4; before, every
 * such click was swallowed silently, which looked like "shift-click does nothing" to the player).
 */
using System;
using HarmonyLib;
using UnityEngine;
using __Project__.Scripts.UI;

namespace BulkShelfTransfer;

internal static class BagBulk
{
    private sealed class Burst
    {
        public OnlineOrderInteraction Oi;
        public OrderPaperBag Bag;
        public DisplaySlot Slot;
        public bool Take;
        public int Moved;
        public float NextStep;
        public float Started;
        public float LastTick;
        public int Limit = -1;   // max products to take; -1 = as many as the bag holds
    }

    private static Burst _active;
    private const int MaxSteps = 500;
    private const float StaleSeconds = 1f;

    internal static void Reset() => _active = null;

    internal static bool TryStart(OnlineOrderInteraction oi, bool take)
    {
        if (!Plugin.PaperBags || !(take ? Plugin.BulkTake : Plugin.BulkPlace)) return false;
        if (!Bulk.ModifierHeld()) return false;
        // A burst of this interaction is running: ignore the click (the vanilla hold flag must not start, and a restart
        // could change the limit mid-way). A stale burst is ended by Tick.
        if (_active != null && Bulk.Same(_active.Oi, oi))
        {
            Bulk.Info($"bag {(take ? "take" : "place")}: ignored (a transfer is running)");
            return true;
        }
        // From here on the player asked for a bulk move: say why one doesn't start (the first in-game test
        // showed no bag burst at all and no clue why).
        string why = Refusal(oi, take);
        if (why != null)
        {
            // Shift is also Sprint: stay quiet while no shelf slot is aimed at.
            if (oi.m_CurrentDisplaySlot != null) Bulk.Info($"bag {(take ? "take" : "place")}: not started ({why})");
            return false;
        }

        var bag = oi.m_CurrentOrderPaperBag;
        var slot = oi.m_CurrentDisplaySlot;
        int limit = -1;   // -1 = as many as the bag holds
        string orderNote = "";
        if (take)
        {
            int pid = slot.ProductID;
            var order = ActiveOrder(oi, bag, out string orderWhy);
            if (order != null)
            {
                int wanted = WantedOf(order, pid);
                int needed = Math.Max(0, wanted - CountInBag(bag, pid));
                if (wanted == 0)
                {
                    // Not part of the order: same as the crate, fill the bag.
                    orderNote = $", order #{order.ID} doesn't contain product {pid}: filling the bag";
                }
                else if (needed > 0)
                {
                    // The order still needs this product: take exactly what it needs.
                    limit = needed;
                    orderNote = $", order #{order.ID} ({order.CustomerName}) needs {needed} more of product {pid}";
                }
                else
                {
                    // The order wants this product and the bag already has all of it: take nothing, but say so.
                    Bulk.Info($"bag take: not started (order #{order.ID} already has all {wanted} of product {pid})");
                    Warn();
                    return true;
                }
            }
            else orderNote = $", no active order ({orderWhy}): filling the bag";
        }

        TweenBoost.Boosted = 0;
        _active = new Burst { Oi = oi, Bag = bag, Slot = slot, Take = take, Started = Time.time, LastTick = Time.time, Limit = limit };
        Bulk.Info($"bag {(take ? "take" : "place")}: start, bag {bag.ProductCount}/{oi.m_MaxBagItemCount}, slot {slot.ProductCount}{orderNote}");
        return true;
    }

    /// <summary>Why a burst can't start (null = it can).</summary>
    private static string Refusal(OnlineOrderInteraction oi, bool take)
    {
        if (!Bulk.IsLocal(oi.PlayerInstance)) return "not the local player";
        var bag = oi.m_CurrentOrderPaperBag;
        var slot = oi.m_CurrentDisplaySlot;
        if (bag == null) return "no paper bag in hand";
        if (slot == null) return "not looking at a shelf slot";
        if (!bag.m_IsOpen) return "bag is closed";
        // Not wheel.IsWheelInteraction: its last link is OnlineOrderInteraction.enabled, which is true for as long as a bag is held.
        // Leave every other use of the click to the game.
        if (oi.m_CurrentCourier != null) return "looking at a courier";
        if (oi.m_CurrentCustomer != null) return "looking at a customer";
        if (oi.m_CurrentTrashBin != null) return "looking at a trash bin";
        if (oi.m_TargetPlacementArea != null || oi.m_CurrentPlacementArea != null) return "looking at a placement area";
        if (oi.m_PlacingMode) return "placing mode";
        if (NoktaSingleton<WeaponWheelController>.HasInstance)
        {
            var wheel = NoktaSingleton<WeaponWheelController>.Instance;
            if (wheel != null && wheel.IsOpen) return "tool wheel open";
        }
        if (take)
        {
            if (oi.m_holdingUse) return "other button held";
            if (oi.m_CurrentProduct != null) return "looking at a loose product";
            if (!slot.HasProduct) return "slot is empty";
            if (bag.ProductCount >= oi.m_MaxBagItemCount) return "bag is full"; // the game shows its warning
        }
        else
        {
            if (oi.m_holdingTake) return "other button held";
            if (!bag.HasProduct) return "bag is empty";
        }
        return null;
    }

    /// <summary>
    /// The order the bag is being packed for, if nobody else is handling it: the order selected in the player's notebook (a
    /// still-attached tag wins, but opening a bag detaches it, so that is rare). Null when there is none (reason in <paramref name="why"/>).
    /// </summary>
    private static OrderListData ActiveOrder(OnlineOrderInteraction oi, OrderPaperBag bag, out string why)
    {
        why = null;
        OrderListData order = null;
        try { if (bag.IsTagAttached) order = bag.m_OrderData; } catch (Exception) { }
        if (!IsRealOrder(order))
        {
            order = null;
            try
            {
                var nb = oi.m_OrderNotebookContainer;
                if (nb != null && !nb.IsEmpty) order = nb.CurrentOrder;
            }
            catch (Exception) { order = null; }
        }
        if (!IsRealOrder(order)) { why = "no order selected"; return null; }
        if (order.IsExpired) { why = $"order #{order.ID} expired"; return null; }

        try
        {
            if (NoktaSingleton<SupermarketSimulator.Courier.CourierManager>.HasInstance)
            {
                var cm = NoktaSingleton<SupermarketSimulator.Courier.CourierManager>.Instance;
                if (cm != null && cm.TryGetClaimStatus(order.ID, out string courier, out bool _))
                { why = $"order #{order.ID} is handled by courier {courier}"; return null; }
            }
        }
        catch (Exception e) { Bulk.Error("courier claim check", e); }
        try
        {
            if (Photon.Pun.PhotonNetwork.IsConnected && NoktaSingletonPunCallbacks<NetworkOrderManager>.HasInstance)
            {
                var nom = NoktaSingletonPunCallbacks<NetworkOrderManager>.Instance;
                string me = oi.PlayerInstance != null ? oi.PlayerInstance.UserId : null;
                if (nom != null && nom.IsBeingWorkedOn(order.ID, me, out string by))
                { why = $"order #{order.ID} is being packed by {by}"; return null; }
            }
        }
        catch (Exception e) { Bulk.Error("order-in-progress check", e); }
        return order;
    }

    private static bool IsRealOrder(OrderListData o) =>
        o != null && o.ID > 0 && o.OrderItems != null && o.OrderItems.Count > 0;

    /// <summary>Total quantity of a product over all entries of the order (an entry per product is assumed, but not relied on).</summary>
    private static int WantedOf(OrderListData order, int pid)
    {
        int n = 0;
        var items = order.OrderItems;
        for (int i = 0; items != null && i < items.Count; i++)
            if (items[i] != null && items[i].ProductID == pid) n += items[i].ProductCount;
        return n;
    }

    /// <summary>The game's "paper bag is full" popup: the only fitting warning, shown when the order already has all it wants of the product.</summary>
    private static void Warn()
    {
        try
        {
            if (NoktaSingletonPunCallbacks<WarningSystem>.HasInstance)
                NoktaSingletonPunCallbacks<WarningSystem>.Instance.RaiseInteractionWarning(InteractionWarningType.FULL_PAPERBAG, new string[0]);
        }
        catch (Exception e) { Bulk.Error("warning popup", e); }
    }

    private static int CountInBag(OrderPaperBag bag, int pid)
    {
        int n = 0;
        var list = bag.m_SortedProducts;
        for (int i = 0; list != null && i < list.Count; i++)
            if (list[i] != null && list[i].ProductID == pid) n++;
        return n;
    }

    internal static void Tick(OnlineOrderInteraction oi)
    {
        var b = _active;
        if (b == null || !Bulk.Same(b.Oi, oi)) return;
        float now = Time.time;
        // Update stops running while the bag interaction is off (bag put away); don't resume a stale burst.
        if (now - b.LastTick > StaleSeconds) { Finish("stale (interaction was not updating)"); return; }
        b.LastTick = now;
        if (now < b.NextStep) return;

        var bag = oi.m_CurrentOrderPaperBag;
        var slot = oi.m_CurrentDisplaySlot;
        string stop = null;
        if (!Bulk.Same(bag, b.Bag)) stop = "bag changed";
        else if (!Bulk.Same(slot, b.Slot)) stop = "looked away from the slot";
        else if (!bag.m_IsOpen) stop = "bag closed";
        else if (b.Moved >= MaxSteps || now - b.Started > 30f) stop = "limit";
        else if (b.Limit >= 0 && b.Moved >= b.Limit) stop = "order has what it needs";
        else if (b.Take ? oi.m_holdingUse : oi.m_holdingTake) stop = "other button held";
        // Quiet end checks; the first step goes to the game unchecked so a refusal shows its warning.
        else if (b.Moved > 0 && (b.Take ? !slot.HasProduct || bag.ProductCount >= oi.m_MaxBagItemCount
                                        : !bag.HasProduct || slot.Full || !bag.IsTargetProductExist(slot))) stop = "nothing left to move";
        if (stop != null) { Finish(stop); return; }

        bool begun = TweenBoost.Begin();
        try
        {
            if (b.Take)
            {
                var status = OnlineOrderService.TryTakeProductIntoBag(slot, bag, oi.m_MaxBagItemCount,
                    oi.transform.position, oi.m_TakeProductBroadcast);
                if (status != OnlineOrderService.TakeProductStatus.Success) { Finish(status.ToString()); return; }
            }
            else
            {
                int before = bag.ProductCount;
                oi.PlaceProductToDisplay();
                if (bag.ProductCount == before) { Finish("no progress"); return; }
            }
        }
        finally { TweenBoost.End(begun); }

        b.Moved++;
        b.NextStep = now + Bulk.Interval;
    }

    private static void Finish(string why)
    {
        var b = _active;
        _active = null;
        if (b == null) return;
        Bulk.Info($"bag {(b.Take ? "take" : "place")}: stopped ({why}) after {b.Moved} product(s), {TweenBoost.Boosted} animation(s) sped up");
    }
}

[HarmonyPatch(typeof(OnlineOrderInteraction), nameof(OnlineOrderInteraction.OnTake))]
internal static class BagOnTakePatch
{
    private static bool Prefix(OnlineOrderInteraction __instance, bool down)
    {
        try { if (down && BagBulk.TryStart(__instance, take: true)) return false; }
        catch (Exception e) { Bulk.Error("OnlineOrderInteraction.OnTake", e); }
        return true;
    }
}

[HarmonyPatch(typeof(OnlineOrderInteraction), nameof(OnlineOrderInteraction.OnUse))]
internal static class BagOnUsePatch
{
    private static bool Prefix(OnlineOrderInteraction __instance, bool down)
    {
        try { if (down && BagBulk.TryStart(__instance, take: false)) return false; }
        catch (Exception e) { Bulk.Error("OnlineOrderInteraction.OnUse", e); }
        return true;
    }
}

[HarmonyPatch(typeof(OnlineOrderInteraction), nameof(OnlineOrderInteraction.Update))]
internal static class BagUpdatePatch
{
    private static void Prefix(OnlineOrderInteraction __instance)
    {
        try { BagBulk.Tick(__instance); }
        catch (Exception e) { Bulk.Error("OnlineOrderInteraction.Update", e); BagBulk.Reset(); }
    }
}
