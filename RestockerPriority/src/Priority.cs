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
 * This mod fixes exactly that: a PerformSearch postfix (host only). When the vanilla search picked a delivery-area
 * box for product P (BoxTarget is a StreetBoxTarget, DisplayTargets filled, no box in hand) and a product Q that is
 * STRICTLY emptier (lower GetShelfFillStatus ratio) has a usable box (delivery area, else rack: the same checks and
 * SearchBestFitDisplaySlotsForRestock call as the game's own search), the results are replaced by Q's job. Ties keep
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
        var bt = h.BoxTarget;
        var displays = h.DisplayTargets;
        if (bt == null || displays == null || displays.Count == 0) return;
        var street = bt.TryCast<StreetBoxTarget>();
        if (street == null) return;
        var mgmt = clerk.ManagementData;
        if (mgmt == null || !mgmt.IsActive || !mgmt.RestockShelf) return;

        int p0 = bt.ProductID;
        float fill0 = Fill(p0);
        if (fill0 <= Epsilon) return;   // already the emptiest possible

        var all = h.GetDisplaySlotsRequiredRefill();
        if (all == null) return;
        var tried = new HashSet<int>();
        var self = clerk.transform;
        for (int i = 0; i < all.Count; i++)
        {
            var slot = all[i];
            if (slot == null) continue;
            int pid = slot.ProductID;
            if (pid == p0) break;          // reached the vanilla choice: nothing emptier is usable
            if (pid < 0 || !tried.Add(pid)) continue;
            if (slot.IsOccupiedByOthers(clerk)) continue;
            if (Fill(pid) >= fill0 - Epsilon) continue;   // not strictly emptier

            IBoxTarget target = null;
            DisplaySlotList list = null;
            string source = null;
            if (mgmt.PickUpBoxGround && h.CheckStreetForRequiredProduct(pid, out Box box) && box != null
                && !box.IsOccupiedByAnother(self) && !box.IsTargeted)
            {
                list = h.SearchBestFitDisplaySlotsForRestock(box, h.GetDisplaySlotsRequiredRefill(pid));
                if (list != null && list.Count > 0)
                {
                    target = new IBoxTarget(new StreetBoxTarget(box).Pointer);
                    source = "the delivery area";
                }
            }
            if (target == null && h.HasRackSlotForRequiredProduct(pid, out RackSlot rack) && rack != null)
            {
                list = h.SearchBestFitDisplaySlotsForRestock(rack, h.GetDisplaySlotsRequiredRefill(pid));
                if (list != null && list.Count > 0)
                {
                    target = new IBoxTarget(new RackSlotBoxTarget(rack).Pointer);
                    source = "a storage rack";
                }
            }
            if (target == null) continue;

            Release(clerk, displays);
            h.BoxTarget = target;
            h.DisplayTargets = list;
            h.RackTarget = null;
            Info($"restocker #{Id(clerk)}: product {pid} ({Fill(pid):P0} stocked) from {source} goes before product {p0} ({fill0:P0}, delivery area)");
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
