/*
 * Improvement #9, product boxes: modifier + Take fills the held box from the shelf slot you look at,
 * modifier + Use empties it onto the slot, in one click.
 *
 * How the game moves products (native dumps of BoxInteraction / NetworkBoxInteraction):
 *   - Input: BoxInteraction.OnTake(down) / OnUse(down) only latch m_holdingTake / m_holdingUse.
 *     BoxInteraction.Update then moves one product per m_ProductInteractionInterval * ProductSO.PlacementSpeedMultiplier:
 *       take:  CheckForTakingItem -> TryTakeProductFromSlot
 *       place: TryPlaceProductToDisplay(Vector3)
 *   - TryTakeProductFromSlot = CheckTakeProductFromSlot (same product or empty box, box not Full, the
 *     product's GridLayoutInBox.boxSize == box size; shows the game's warning otherwise), then
 *       offline: TakeProductFromDisplay(box, slot)        (slot.TakeProductFromDisplay + Box.AddProduct)
 *       host:    NetworkBoxInteraction.TakeProduct_Broadcast -> TakeProduct_RPC to All (runs locally at once)
 *       guest:   NetworkBoxInteraction.AskTakeProduct -> host's AskTakeProduct_RPC relays TakeProduct_RPC
 *                to All WITHOUT re-checking anything; every client then runs TakeProductFromDisplay on
 *                its copy, and Box.AddProduct has no capacity check. So a guest must never ask for more
 *                products than the box has room for: its local state only changes when the RPC comes back.
 *   - TryPlaceProductToDisplay(Vector3) = CheckPlaceProductToSlot (display type, same product or empty
 *     slot, slot not Full) then offline PlaceProductToDisplay / host PlaceProduct_Broadcast (All) /
 *     guest AskPlaceProduct -> host re-runs TryPlaceProductToDisplay(slot, pos) on its own copy, so the
 *     host does re-check placements.
 *   - Animations: Box.AddProduct and DisplaySlot.AddProduct start DOLocalJump + DOLocalRotateQuaternion
 *     (0.3 s) and DOScale (0.2 s) on the product. DisplaySlot's ToggleInstancedAnimated coroutine waits
 *     for the move tween with WaitForCompletion, so a faster tween stays consistent.
 *
 * What the mod does: prefixes on OnTake/OnUse(down=true) recognise modifier + click while looking at
 * a DisplaySlot (vending machines are left to the game) and, instead of latching the hold flag, start
 * a burst. The burst is driven from a BoxInteraction.Update prefix (it runs before the game's own
 * per-product code, in the same frame as the click) and calls the game's own CheckXxx + TryXxx
 * methods once per step, so all of the game's rules, networking and warnings apply unchanged. The
 * number of steps is capped up front at min(products available, room left) computed from the local
 * state, which is what keeps a guest from over-asking. On the host and offline, a step that moved
 * nothing ends the burst. Box.AddProduct / DisplaySlot.AddProduct are wrapped in TweenBoost while the
 * box / slot is marked, which speeds up the fly animation on this client (including on a guest, where
 * the products arrive through the host's RPC a little later).
 *
 * Guest safety (review round 1): TakeProduct_RPC / PlaceProduct_RPC are only applied by a client if the
 * message's box is that client's BoxInteraction.m_Box ("Boxes are not same" otherwise), and DropBox clears
 * m_Box at once. So while a guest burst is running, and until the box shows the expected count (2 s
 * timeout), Take/Use presses (incl. rack, trash, placement area branches of OnUse), Drop and Throw are
 * swallowed: the box can't leave the hands and nothing can count from stale numbers.
 */
using System;
using HarmonyLib;
using UnityEngine;

namespace BulkShelfTransfer;

internal static class BoxBulk
{
    private sealed class Burst
    {
        public BoxInteraction Bi;
        public Box Box;
        public DisplaySlot Slot;
        public bool Take;
        public int Target;
        public int Sent;
        public float NextStep;
        public float Started;
        public float LastTick;
        public int StartBoxCount;
    }

    private static Burst _active;
    private const float StaleSeconds = 1f;

    // Guest only: after a burst, the host's answers arrive over the next moments. Until the box shows the
    // expected count (or a short timeout), any new take/place on that box would count from stale numbers and
    // could ask for more than fits, and letting the box leave the hands would make this client reject the late
    // answers ("Boxes are not same"), so those inputs are swallowed (see Guarded).
    private static IntPtr _pendingBox;
    private static int _pendingExpected;
    private static float _pendingUntil;
    private const float PendingSeconds = 2f;

    /// <summary>How long after the last step incoming products still animate fast (guest round trip).</summary>
    private const float MarkSeconds = 3f;

    internal static void Reset()
    {
        _active = null;
        _pendingBox = IntPtr.Zero;
    }

    /// <summary>
    /// Guest only: true while a burst on this interaction is running or its answers from the host are still
    /// arriving for the held box. While guarded, presses that would take/place more products or make the box
    /// leave the hands (drop, throw, rack, trash, placement area) are swallowed.
    /// </summary>
    internal static bool Guarded(BoxInteraction bi)
    {
        if (!Bulk.IsGuest) return false;
        var b = _active;
        if (b != null && Bulk.Same(b.Bi, bi) && Bulk.Same(b.Box, bi.m_Box)) return true;
        return PendingUnresolved(bi.m_Box);
    }

    private static bool PendingUnresolved(Box box)
    {
        if (box == null || _pendingBox == IntPtr.Zero || _pendingBox != box.Pointer) return false;
        if (Time.time < _pendingUntil && box.ProductCount != _pendingExpected) return true;
        _pendingBox = IntPtr.Zero;
        return false;
    }

    /// <summary>Called from OnTake/OnUse(down=true). Returns true if the click was consumed by a burst.</summary>
    internal static bool TryStart(BoxInteraction bi, bool take)
    {
        var running = _active;
        if (running != null && Bulk.Same(running.Bi, bi))
        {
            if (Bulk.Same(running.Box, bi.m_Box))
            {
                Bulk.Info("box: a bulk transfer is still running, click ignored");
                return true;
            }
            Finish("box changed");
        }

        if (!Plugin.Boxes || !(take ? Plugin.BulkTake : Plugin.BulkPlace)) return false;
        if (!Bulk.ModifierHeld()) return false;
        if (!Bulk.IsLocal(bi.PlayerInstance)) return false;
        if (bi.IsWaitingAvailability) return false;

        var box = bi.m_Box;
        var slot = bi.m_CurrentDisplaySlot;
        if (box == null || slot == null) return false;
        if (!box.IsOpen) return false;

        if (bi.m_PlacingMode) return false;

        int target;
        if (take)
        {
            if (bi.m_holdingUse || !slot.HasProduct) return false;
            int cap = box.Product != null ? box.MaxProductCount : Bulk.ProductCapacityInBox(slot.ProductID);
            target = Math.Min(slot.ProductCount, cap - box.ProductCount);
        }
        else
        {
            if (bi.m_holdingTake || !bi.m_IsBoxOpen || !box.HasProducts) return false;
            if (bi.m_CurrentRackSlot != null || bi.m_CurrentPlacementArea != null || bi.m_CurrentTrashBin != null ||
                bi.m_TargetPlacementArea != null) return false;
            bool special = slot.TryCast<IceCreamConeDisplaySlot>() != null || slot.TryCast<IceCreamFlavourDisplaySlot>() != null;
            if (special && Bulk.IsGuest) return false; // custom capacity rules; can't count them safely from a guest
            int count = slot.ProductCount;
            int cap = Bulk.ProductCapacityOnShelf(count > 0 ? slot.ProductID : box.Product.ID);
            target = special ? box.ProductCount : Math.Min(box.ProductCount, cap - count);
        }
        // Nothing fits (box or slot full): let the game handle the click, it shows its own warning.
        if (target <= 0) return false;

        // One check through the game's own rules first; if it refuses, the game has shown its warning and
        // we swallow the click (no hold-to-repeat starts).
        bool ok = take ? bi.CheckTakeProductFromSlot() : bi.CheckPlaceProductToSlot();
        if (!ok)
        {
            Bulk.Info($"box {(take ? "take" : "place")}: refused by the game's check");
            return true;
        }

        _active = new Burst
        {
            Bi = bi, Box = box, Slot = slot, Take = take, Target = target,
            NextStep = 0f, Started = Time.time, LastTick = Time.time, StartBoxCount = box.ProductCount,
        };
        TweenBoost.Boosted = 0;
        TweenBoost.Mark(take ? box : slot, MarkSeconds);
        Bulk.Info($"box {(take ? "take" : "place")}: start, up to {target} product(s), " +
                  $"box {box.ProductCount}/{(box.Product != null ? box.MaxProductCount : -1)}, slot {slot.ProductCount}" +
                  (Bulk.IsGuest ? " (guest: through the host)" : ""));
        return true;
    }

    internal static void Tick(BoxInteraction bi)
    {
        var b = _active;
        if (b == null || !Bulk.Same(b.Bi, bi)) return;
        float now = Time.time;
        // Update stops running while the box is out of the hands (thrown, interaction ended); don't resume a stale burst.
        if (now - b.LastTick > StaleSeconds) { Finish("stale (interaction was not updating)"); return; }
        b.LastTick = now;
        if (now < b.NextStep) return;

        var box = bi.m_Box;
        var slot = bi.m_CurrentDisplaySlot;
        string stop = null;
        if (!Bulk.Same(box, b.Box)) stop = "box changed";
        else if (!Bulk.Same(slot, b.Slot)) stop = "looked away from the slot";
        else if (b.Sent >= b.Target) stop = "done";
        else if (now - b.Started > 30f) stop = "timeout";
        else if (!box.IsOpen) stop = "box closed";
        else if (bi.m_PlacingMode) stop = "placing mode";
        else if (b.Take ? bi.m_holdingUse : bi.m_holdingTake) stop = "other button held";
        else if (b.Take ? (!slot.HasProduct || box.Full) : (!box.HasProducts || slot.Full)) stop = "nothing left to move";
        if (stop != null) { Finish(stop); return; }

        int before = box.ProductCount;
        if (b.Take)
        {
            if (!bi.CheckTakeProductFromSlot()) { Finish("refused by the game's check"); return; }
            bi.TryTakeProductFromSlot();
        }
        else
        {
            if (!bi.CheckPlaceProductToSlot()) { Finish("refused by the game's check"); return; }
            bi.TryPlaceProductToDisplay(bi.transform.position);
        }
        b.Sent++;
        b.NextStep = now + Bulk.Interval;
        TweenBoost.Mark(b.Take ? (Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)box : slot, MarkSeconds);

        // Offline and on the host the move happens inside the call; if nothing moved, stop.
        if (!Bulk.IsGuest && box.ProductCount == before) Finish("no progress");
    }

    private static void Finish(string why)
    {
        var b = _active;
        _active = null;
        if (b == null) return;
        if (Bulk.IsGuest && b.Sent > 0 && b.Box != null)
        {
            _pendingBox = b.Box.Pointer;
            _pendingExpected = b.StartBoxCount + (b.Take ? b.Sent : -b.Sent);
            _pendingUntil = Time.time + PendingSeconds;
        }
        Bulk.Info($"box {(b.Take ? "take" : "place")}: stopped ({why}) after {b.Sent}/{b.Target} product(s), " +
                  $"{TweenBoost.Boosted} animation(s) sped up so far");
    }

    internal static bool IsBurstBox(Box box) => TweenBoost.IsMarked(box);
    internal static bool IsBurstSlot(DisplaySlot slot) => TweenBoost.IsMarked(slot);
}

[HarmonyPatch(typeof(BoxInteraction), nameof(BoxInteraction.OnTake))]
internal static class BoxOnTakePatch
{
    private static bool Prefix(BoxInteraction __instance, bool down)
    {
        try
        {
            if (!down) return true;
            if (BoxBulk.Guarded(__instance)) { Bulk.Info("box: waiting for the host, Take ignored"); return false; }
            if (BoxBulk.TryStart(__instance, take: true)) return false;
        }
        catch (Exception e) { Bulk.Error("BoxInteraction.OnTake", e); }
        return true;
    }
}

[HarmonyPatch(typeof(BoxInteraction), nameof(BoxInteraction.OnUse))]
internal static class BoxOnUsePatch
{
    private static bool Prefix(BoxInteraction __instance, bool down)
    {
        try
        {
            if (!down) return true;
            if (BoxBulk.Guarded(__instance)) { Bulk.Info("box: waiting for the host, Use ignored"); return false; }
            if (BoxBulk.TryStart(__instance, take: false)) return false;
        }
        catch (Exception e) { Bulk.Error("BoxInteraction.OnUse", e); }
        return true;
    }
}

/// <summary>Guest: the box must stay in the hands until the host's answers for a burst have arrived.</summary>
[HarmonyPatch(typeof(BoxInteraction), nameof(BoxInteraction.OnDrop))]
internal static class BoxOnDropPatch
{
    private static bool Prefix(BoxInteraction __instance)
    {
        try { if (BoxBulk.Guarded(__instance)) { Bulk.Info("box: waiting for the host, Drop ignored"); return false; } }
        catch (Exception e) { Bulk.Error("BoxInteraction.OnDrop", e); }
        return true;
    }
}

[HarmonyPatch(typeof(BoxInteraction), nameof(BoxInteraction.OnThrow))]
internal static class BoxOnThrowPatch
{
    private static bool Prefix(BoxInteraction __instance)
    {
        try { if (BoxBulk.Guarded(__instance)) { Bulk.Info("box: waiting for the host, Throw ignored"); return false; } }
        catch (Exception e) { Bulk.Error("BoxInteraction.OnThrow", e); }
        return true;
    }
}

[HarmonyPatch(typeof(BoxInteraction), nameof(BoxInteraction.Update))]
internal static class BoxUpdatePatch
{
    private static void Prefix(BoxInteraction __instance)
    {
        try { BoxBulk.Tick(__instance); }
        catch (Exception e) { Bulk.Error("BoxInteraction.Update", e); BoxBulk.Reset(); }
    }
}

[HarmonyPatch(typeof(Box), nameof(Box.AddProduct))]
internal static class BoxAddProductPatch
{
    private static void Prefix(Box __instance, out bool __state)
    {
        __state = false;
        try { if (BoxBulk.IsBurstBox(__instance)) __state = TweenBoost.Begin(); }
        catch (Exception e) { Bulk.Error("Box.AddProduct prefix", e); }
    }

    private static void Postfix(bool __state) => TweenBoost.End(__state);
}

[HarmonyPatch(typeof(DisplaySlot), nameof(DisplaySlot.AddProduct))]
internal static class SlotAddProductPatch
{
    private static void Prefix(DisplaySlot __instance, out bool __state)
    {
        __state = false;
        try { if (BoxBulk.IsBurstSlot(__instance)) __state = TweenBoost.Begin(); }
        catch (Exception e) { Bulk.Error("DisplaySlot.AddProduct prefix", e); }
    }

    private static void Postfix(bool __state) => TweenBoost.End(__state);
}
