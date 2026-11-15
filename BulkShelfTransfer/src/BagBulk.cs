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
 */
using System;
using HarmonyLib;
using UnityEngine;

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
    }

    private static Burst _active;
    private const int MaxSteps = 500;

    internal static void Reset() => _active = null;

    internal static bool TryStart(OnlineOrderInteraction oi, bool take)
    {
        if (!Plugin.PaperBags.Value || !(take ? Plugin.BulkTake.Value : Plugin.BulkPlace.Value)) return false;
        if (!Bulk.ModifierHeld()) return false;
        if (!Bulk.IsLocal(oi.PlayerInstance)) return false;

        var bag = oi.m_CurrentOrderPaperBag;
        var slot = oi.m_CurrentDisplaySlot;
        if (bag == null || slot == null || !bag.m_IsOpen) return false;
        // Leave every other use of the click to the game.
        if (oi.m_CurrentCourier != null || oi.m_CurrentCustomer != null || oi.m_CurrentTrashBin != null) return false;
        if (oi.m_TargetPlacementArea != null || oi.m_CurrentPlacementArea != null || oi.m_PlacingMode) return false;

        if (take)
        {
            if (oi.m_holdingUse || oi.m_CurrentProduct != null || !slot.HasProduct) return false;
        }
        else
        {
            if (oi.m_holdingTake || !bag.HasProduct) return false;
        }

        TweenBoost.Boosted = 0;
        _active = new Burst { Oi = oi, Bag = bag, Slot = slot, Take = take, Started = Time.time };
        Bulk.Info($"bag {(take ? "take" : "place")}: start, bag {bag.ProductCount}/{oi.m_MaxBagItemCount}, slot {slot.ProductCount}");
        return true;
    }

    internal static void Tick(OnlineOrderInteraction oi)
    {
        var b = _active;
        if (b == null || !Bulk.Same(b.Oi, oi)) return;
        float now = Time.time;
        if (now < b.NextStep) return;

        var bag = oi.m_CurrentOrderPaperBag;
        var slot = oi.m_CurrentDisplaySlot;
        string stop = null;
        if (!Bulk.Same(bag, b.Bag)) stop = "bag changed";
        else if (!Bulk.Same(slot, b.Slot)) stop = "looked away from the slot";
        else if (!bag.m_IsOpen) stop = "bag closed";
        else if (b.Moved >= MaxSteps || now - b.Started > 30f) stop = "limit";
        else if (b.Take ? oi.m_holdingUse : oi.m_holdingTake) stop = "other button held";
        // Quiet end checks; the first step goes to the game unchecked so a refusal shows its warning.
        else if (b.Moved > 0 && (b.Take ? !slot.HasProduct || bag.ProductCount >= oi.m_MaxBagItemCount
                                        : !bag.HasProduct || slot.Full)) stop = "nothing left to move";
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
