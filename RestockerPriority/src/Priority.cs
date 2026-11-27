/*
 * Restocker priority: which product does a restocker (Clerk) restock first?
 *
 * Findings (native dumps of SupermarketSimulator.Clerk.SearchHandler, DisplayManager, DisplaySlot):
 *   The game ALREADY ranks products by fill ratio. SearchHandler.GetDisplaySlotsRequiredRefill() builds, from
 *   DisplayManager.m_DisplayedProducts (every labelled shelf slot, empty ones included),
 *   CalculateProductFullnessWeights: per product  sum(DisplaySlot.ProductCount) / (ProductSO.GridLayoutInStorage
 *   .productCount * slot count), sorts the products ascending (0% first) and returns their slots in that order,
 *   keeping only slots with IsSlotRequiredFill (count <= 75% of the slot's capacity). A product whose slots are all
 *   above 75% is not restocked at all, so its boxes only go to storage (the street-to-rack branch, which comes last).
 *   DisplayManager.GetShelfFillStatus(pid) returns the same two numbers (items, capacity).
 *
 *   What PerformSearch then does with that order (box not in hand), as read from the ISIL:
 *     1. a box in a delivery vehicle (not product-ordered),
 *     2. PASS 1, only if "pick up boxes from the ground": walk the slots in fullness order and take the FIRST product
 *        that has a box in the delivery area (CheckStreetForRequiredProduct) whose displays can take it,
 *     3. PASS 2, only if pass 1 found no delivery-area box for ANY product (a flag set as soon as one is seen):
 *        walk the slots in order and take the first product with a box in a storage rack.
 *   So the rank is only respected inside each pass. A product at 60% with a box in the delivery area beats a product
 *   at 0% whose box is in a rack, and the restocker leaves the empty shelves empty until the delivery area is clear.
 *
 * This mod fixes exactly that: a PerformSearch postfix (host only), no box in hand. When the vanilla search picked a
 * delivery-area box for product P (StreetBoxTarget with DisplayTargets), or ended with a storage/throw-away job from
 * a delivery-area box or with no job (the rack pass is also skipped when a street box was seen but rejected, e.g.
 * targeted by another restocker), and a product Q that is STRICTLY emptier than P (any product when there was no
 * restock job) has a usable rack box (same checks and SearchBestFitDisplaySlotsForRestock call as the game's own rack
 * pass; delivery-area boxes of earlier products were already rejected by vanilla), the results become Q's job.
 * A street box outside the delivery area (IsWithinRestockableArea false) is never touched: vanilla never picks one,
 * it is RestockerDoubleCarry's FetchFromFloor result. Ties keep
 * the vanilla choice. If Q's job is not usable nothing changes, so a restocker is never left without work by this mod.
 * Q's displays are reserved by the game's own SearchBestFit call (DisplaySlot.m_OccupiedRestocker); P's reservation
 * (made by the vanilla search) is released by hand.
 * Not touched: vehicle boxes, rack boxes (vanilla already picks the emptiest product there), a box already in hand,
 * boxes for storage, and which individual box/slot is used for the chosen product.
 *
 * Coexistence with RestockerDoubleCarry (no dependency): this postfix is ordered before DoubleCarry's (HarmonyBefore its
 * GUID), so DoubleCarry's trip starts from the corrected box 1. DoubleCarry picks its second box with the same rule on
 * its own (emptiest product first, delivery area before rack only within the same product), so the pair is consistent
 * with or without this mod.
 */
using System;
using System.Collections.Generic;
using HarmonyLib;
using Photon.Pun;
using SupermarketSimulator.Clerk;
using ClerkT = SupermarketSimulator.Clerk.Clerk;
using DisplaySlotList = Il2CppSystem.Collections.Generic.List<DisplaySlot>;

namespace RestockerPriority;

internal static class PriorityLogic
{
    private const float Epsilon = 0.0001f;

    internal static void Info(string msg)
    {
        if (Plugin.VerboseLogging.Value) Plugin.Logger.LogInfo("[RestockerPriority] " + msg);
    }

    /// <summary>Items on the product's labelled shelf slots divided by what they hold; 1 when unknown.</summary>
    internal static float Fill(int pid)
    {
        try
        {
            if (!NoktaSingleton<DisplayManager>.HasInstance) return 1f;
            var t = NoktaSingleton<DisplayManager>.Instance.GetShelfFillStatus(pid);
            int items = t.Item1, cap = t.Item2;
            return cap > 0 ? (float)items / cap : 1f;
        }
        catch (Exception) { return 1f; }
    }

    internal static void AfterSearch(SearchHandler h)
    {
        var clerk = h.m_Clerk;
        if (clerk == null) return;
        if (clerk.m_CarryingBox && clerk.m_CurrentBox != null) return;
        var mgmt = clerk.ManagementData;
        if (mgmt == null || !mgmt.IsActive || !mgmt.RestockShelf) return;

        // What vanilla ended with. Acted on: a delivery-area box to restock from, a delivery-area box for storage or
        // to throw away, or no job at all. Left alone: a carried box, vehicle and rack boxes, and a street box that
        // is outside the delivery area (vanilla never picks one: it is RestockerDoubleCarry's box on the floor).
        int p0 = -1;
        float fill0 = float.PositiveInfinity;
        var bt = h.BoxTarget;
        if (bt != null)
        {
            var street = bt.TryCast<StreetBoxTarget>();
            if (street == null) return;
            var box = street.Box;
            if (box == null || !NoktaSingleton<StorageStreet>.HasInstance
                || !NoktaSingleton<StorageStreet>.Instance.IsWithinRestockableArea(box.transform.position)) return;
            var displays0 = h.DisplayTargets;
            if (displays0 != null && displays0.Count > 0)
            {
                p0 = bt.ProductID;
                fill0 = Fill(p0);
                if (fill0 <= Epsilon) return;   // already the emptiest possible
            }
        }

        var all = h.GetDisplaySlotsRequiredRefill();
        if (all == null) return;
        var tried = new HashSet<int>();
        for (int i = 0; i < all.Count; i++)
        {
            var slot = all[i];
            if (slot == null) continue;
            int pid = slot.ProductID;
            if (pid == p0) break;          // reached the vanilla choice: nothing emptier is usable
            if (pid < 0 || tried.Contains(pid)) continue;
            if (slot.IsOccupiedByOthers(clerk)) continue;   // another slot of the product may be free
            tried.Add(pid);
            if (Fill(pid) >= fill0 - Epsilon) continue;     // not strictly emptier

            // Only a rack box can be new here: vanilla's delivery-area pass already tried every earlier product.
            if (!h.HasRackSlotForRequiredProduct(pid, out RackSlot rack) || rack == null) continue;
            var list = h.SearchBestFitDisplaySlotsForRestock(rack, h.GetDisplaySlotsRequiredRefill(pid));
            if (list == null || list.Count == 0) continue;

            if (p0 >= 0) Release(clerk, h.DisplayTargets);
            h.BoxTarget = new IBoxTarget(new RackSlotBoxTarget(rack).Pointer);
            h.DisplayTargets = list;
            h.RackTarget = null;
            Info($"restocker #{Id(clerk)}: product {pid} ({Fill(pid):P0} stocked) from a storage rack goes before " +
                 (p0 >= 0 ? $"product {p0} ({fill0:P0}, delivery area)" : "the job the game chose (storage or none)"));
            return;
        }
    }

    private static string Id(ClerkT clerk)
    {
        try { return clerk.EmployeeId.ToString(); } catch { return "?"; }
    }

    /// <summary>Gives back the display reservations the vanilla search made for the job that was replaced.</summary>
    private static void Release(ClerkT clerk, DisplaySlotList slots)
    {
        for (int i = 0; i < slots.Count; i++)
        {
            var s = slots[i];
            if (s == null) continue;
            var owner = s.m_OccupiedRestocker;
            if (owner != null && owner.Pointer == clerk.Pointer) s.m_OccupiedRestocker = null;
        }
    }
}

[HarmonyPatch(typeof(SearchHandler), nameof(SearchHandler.PerformSearch))]
[HarmonyBefore("flamboyanthopper.restockerdoublecarry")]
[HarmonyPriority(Priority.High)]
internal static class PerformSearchPatch
{
    private static void Postfix(SearchHandler __instance)
    {
        try
        {
            if (!PhotonNetwork.IsConnectedAndSlave) PriorityLogic.AfterSearch(__instance);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[RestockerPriority] search postfix failed: {e}");
        }
    }
}
