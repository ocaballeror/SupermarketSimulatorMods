/*
 * Improvement #5: high-level restockers fetch two boxes per trip.
 *
 * How restockers work in this game build (native dumps of SupermarketSimulator.Clerk.*):
 *   A restocker is a `Clerk` (RestockerSO.RestockerPrefab). The legacy `Restocker` class is unused.
 *   Clerk.Awake builds a small state machine: Idle, ClerkRestockingState, RackingState and
 *   ClerkDropEmptyBoxState. It only runs where !PhotonNetwork.IsConnectedAndSlave (host or offline);
 *   guests only mirror it through NetworkClerk RPCs.
 *   - ClerkIdleState.OnTick walks the clerk to its spawn point and every 3 s (and on the first tick
 *     after entering Idle) calls SearchHandler.PerformSearch, which fills three results:
 *       BoxTarget (IBoxTarget: RackSlotBoxTarget, StreetBoxTarget, VehicleBoxTarget, or CarryBoxTarget
 *       for the box already in hand), DisplayTargets (List<DisplaySlot>) and RackTarget (RackSlot).
 *     If the clerk carries a box (SearchHandler.IsCarryBox = Clerk.m_CarryingBox / m_CurrentBox), the
 *     search only looks for work for that box: displays of its product, else a rack slot, else
 *     "drop the empty box". Otherwise it looks for display slots that need refilling and a box of that
 *     product on the street (if PickUpBoxGround) or in a rack, then street boxes to rack, then empty
 *     street boxes.
 *   - Transitions (lambdas in Clerk+<>c__DisplayClass79_0): Idle -> Restocking when
 *     DisplayTargets.Count > 0, Idle -> Racking when RackTarget != null, back to Idle when the state's
 *     Sequence completes. Clerk.OnBeforeTransition copies the search results into the new state
 *     (inlined ClerkRestockingState.SetDependencies: MoveTo.Destination = BoxTarget.GetPosition(),
 *     TakeBox.Target = BoxTarget, RestockAllDisplays.TargetDisplaySlots = DisplayTargets).
 *   - ClerkRestockingState runs a Sequence [MoveTo, TakeBox, RestockAllDisplays, GoToTrash]. Sequence
 *     keeps its tasks in a List (m_Tasks) and reads it at every step, so a run can be shortened by
 *     removing tasks before Enter (which calls Sequence.Start) and restoring them after Exit.
 *     A leftover box comes back to Idle in hand; the next search then racks it (RackingState).
 *   - TakeBox calls Clerk.CarryBox(box): RPC ClerkCarryBox_RPC to guests, then CarryBox_Internal,
 *     which sets m_CurrentBox, parents the box to m_BoxHolder and tweens it to local zero. It never
 *     touches a previously carried box, so a box that is no longer "current" simply stays parented to
 *     the hands.
 *
 * Approach (host only, no vanilla method replaced): drive the vanilla states through the search results.
 *   Phase None:        vanilla search finds a restock job for a non-carried box (product P1) and the
 *                      clerk is at least MinimumLevel -> if a second job (other product P2) exists,
 *                      the next Restocking run is "fetch only" (tasks after TakeBox removed) -> B1 in hand.
 *   Phase FetchFirst:  next search: still holding B1 -> fill the search results with job 2 ourselves
 *                      (skip vanilla) and fetch only again. TakeBox(B2) makes B2 current; B1 stays in
 *                      the hands and becomes the "stash".
 *   Phase FetchSecond: next search: holding B2 with B1 stashed -> CarryBox(B1) (B2 becomes the stash),
 *                      vanilla search then restocks P1 with B1 (and trashes it if empty).
 *   Phase DeliverFirst: next search: CarryBox(B2) (B1, if it still has products, becomes the stash),
 *                      vanilla restocks P2.
 *   Phase Unwind:      whenever the hands are empty and a box is stashed, CarryBox(stash) and let
 *                      vanilla rack it. Done when nothing is stashed.
 *   Any failure (box taken by someone else, job gone) falls back to vanilla with whatever is in hand.
 *   The stash is dropped to the floor when the game drops the clerk's box for any reason other than the
 *   idle "nowhere to put it" timer (firing, deactivation), and when the mod is switched off.
 *
 * Display reservations: both SearchBestFitDisplaySlotsForRestock overloads write DisplaySlot.m_OccupiedRestocker
 * (+0x70) on every slot they return, and only ClerkRestockingState.Exit clears them (for the run's own
 * dependency list). The probe that decides whether to start a trip therefore restores the previous owners
 * (a vanilla run would have reserved them anyway, but a trip would hold them for much longer). The real
 * box-2 search keeps its reservation (the fetch-only run's Exit clears it); if the trip dies before that,
 * EndPlan / ReleaseAll release those slots by hand.
 *
 * Visuals: a Clerk.CarryBox_Internal postfix stacks every non-current Box under m_BoxHolder on top of
 * the current one (Box.LocalBounds). It is purely cosmetic and runs wherever the mod is installed.
 *
 * Multiplayer: the AI runs on the host, so the HOST needs the mod. Every pick-up, swap, product
 * placement, racking and trashing goes through the game's own networked calls (Clerk.CarryBox,
 * TakeBox, RestockAllDisplays, RackBox, GoToTrash), so an unmodded guest stays in sync. It only cannot
 * stack the boxes visually: both sit at the hands, one inside the other. A guest with the mod sees
 * the stack.
 */
using System;
using System.Collections.Generic;
using DG.Tweening;
using HarmonyLib;
using Photon.Pun;
using SupermarketSimulator.Clerk;
using SupermarketSimulator.Clerk.States;
using SupermarketSimulator.Sequence;
using UnityEngine;
using ClerkT = SupermarketSimulator.Clerk.Clerk;
using DisplaySlotList = Il2CppSystem.Collections.Generic.List<DisplaySlot>;

namespace RestockerDoubleCarry;

internal static class DoubleCarry
{
    internal enum Phase { None, FetchFirst, FetchSecond, DeliverFirst, Unwind }

    internal sealed class Plan
    {
        public ClerkT Clerk;
        public Phase Phase;
        /// <summary>The next ClerkRestockingState.Enter must stop after TakeBox.</summary>
        public bool FetchOnlyNext;
        public Box First, Second;
        public int FirstProduct = -1, SecondProduct = -1;
        /// <summary>
        /// Display slots reserved for the clerk by the box-2 search. The fetch-only run's Exit clears
        /// them; they are released by hand if the plan dies before that.
        /// </summary>
        public List<DisplaySlot> ReservedSecond;
    }

    private const float StackTweenTime = 0.3f;

    private static readonly Dictionary<IntPtr, Plan> Plans = new();
    // ClerkRestockingState currently running a shortened list -> (state, its full task list).
    private static readonly Dictionary<IntPtr, (ClerkRestockingState State, List<SequenceTask> Original)> Stripped = new();

    internal static bool InIdleTick;
    private static bool _dropping;

    // ---------------------------------------------------------------- helpers

    internal static void Info(string msg)
    {
        if (Plugin.Verbose.Value) Plugin.Logger.LogInfo("[DoubleCarry] " + msg);
    }

    internal static bool HostSide => !PhotonNetwork.IsConnectedAndSlave;

    internal static bool Same(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase a,
        Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase b)
        => a != null && b != null && a.Pointer == b.Pointer;

    internal static Box Current(ClerkT clerk)
    {
        var box = clerk.m_CurrentBox;
        return box != null && clerk.m_CarryingBox ? box : null;
    }

    internal static int ProductOf(Box box)
    {
        var data = box?.Data;
        return data != null ? data.ProductID : -1;
    }

    /// <summary>The box is alive, still in this clerk's hands, and not the one it is working with.</summary>
    internal static bool IsStashed(ClerkT clerk, Box box)
    {
        if (box == null || !box.gameObject.activeInHierarchy) return false;
        var holder = clerk.BoxHolder;
        var parent = box.transform.parent;
        if (holder == null || parent == null || parent.Pointer != holder.Pointer) return false;
        var cur = clerk.m_CurrentBox;
        return cur == null || cur.Pointer != box.Pointer;
    }

    internal static Box Stash(ClerkT clerk, Plan plan)
    {
        if (IsStashed(clerk, plan.First)) return plan.First;
        if (IsStashed(clerk, plan.Second)) return plan.Second;
        return null;
    }

    internal static string Name(ClerkT clerk)
    {
        try { return $"restocker #{clerk.EmployeeId}"; } catch { return "restocker"; }
    }

    internal static int Level(ClerkT clerk)
    {
        if (!NoktaSingleton<EmployeeLevelManager>.HasInstance) return 1;
        var mgr = NoktaSingleton<EmployeeLevelManager>.Instance;
        if (mgr != null && mgr.TryGet(clerk.LevelKind, clerk.LevelEmployeeId, out var progress) && progress != null)
            return progress.Level;
        return 1;
    }

    internal static Plan GetPlan(ClerkT clerk) => Plans.TryGetValue(clerk.Pointer, out var p) ? p : null;

    internal static void EndPlan(ClerkT clerk, string why)
    {
        if (Plans.Remove(clerk.Pointer, out var plan))
        {
            ReleaseReserved(clerk, plan);
            if (why != null) Info($"{Name(clerk)}: {why}");
        }
    }

    /// <summary>Gives back the box-2 display reservations that this clerk still holds.</summary>
    private static void ReleaseReserved(ClerkT clerk, Plan plan)
    {
        var list = plan.ReservedSecond;
        plan.ReservedSecond = null;
        if (list == null) return;
        try
        {
            int n = 0;
            foreach (var slot in list)
            {
                if (slot == null) continue;
                if (!Same(slot.m_OccupiedRestocker, clerk)) continue;
                slot.m_OccupiedRestocker = null;
                n++;
            }
            if (n > 0) Info($"{Name(clerk)}: released {n} reserved display slot(s) of the cancelled second job");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[DoubleCarry] releasing display reservations failed: {e.Message}");
        }
    }

    /// <summary>Puts the box in the clerk's hands. Kills a stacking tween first so two tweens don't fight.</summary>
    private static void HandOver(ClerkT clerk, Box box)
    {
        box.transform.DOKill(false);
        clerk.CarryBox(box);
    }

    /// <summary>
    /// Both game search overloads write DisplaySlot.m_OccupiedRestocker on every slot they return. A probe
    /// must not keep that, so remember the owners of the candidate slots and put them back.
    /// </summary>
    private static List<(DisplaySlot Slot, ClerkT Owner)> SnapshotOwners(DisplaySlotList candidates)
    {
        var snap = new List<(DisplaySlot, ClerkT)>();
        if (candidates == null) return snap;
        for (int i = 0; i < candidates.Count; i++)
        {
            var slot = candidates[i];
            if (slot != null) snap.Add((slot, slot.m_OccupiedRestocker));
        }
        return snap;
    }

    private static void RestoreOwners(ClerkT clerk, List<(DisplaySlot Slot, ClerkT Owner)> snap)
    {
        int n = 0;
        foreach (var (slot, owner) in snap)
        {
            if (slot == null || Same(slot.m_OccupiedRestocker, owner)) continue;
            slot.m_OccupiedRestocker = owner;
            n++;
        }
        if (n > 0) Info($"{Name(clerk)}: probe reservation of {n} display slot(s) undone");
    }

    private static void PruneDeadPlans()
    {
        if (Plans.Count == 0) return;
        List<IntPtr> dead = null;
        foreach (var kv in Plans)
            if (kv.Value.Clerk == null) (dead ??= new List<IntPtr>()).Add(kv.Key);
        if (dead != null) foreach (var k in dead) Plans.Remove(k);
    }

    /// <summary>
    /// Finds a restock job for a product other than <paramref name="excludedProduct"/>, the way the
    /// game's own search does (street boxes first if allowed, then racks). With <paramref name="reserve"/>
    /// false it is a pure probe: the display reservations the game's search makes are undone.
    /// </summary>
    internal static bool TryFindSecondJob(SearchHandler h, ClerkT clerk, int excludedProduct, bool reserve,
        out IBoxTarget target, out DisplaySlotList displays, out int productId, out string source)
    {
        target = null; displays = null; productId = -1; source = null;
        var mgmt = clerk.ManagementData;
        if (mgmt == null || !mgmt.IsActive || !mgmt.RestockShelf) return false;
        var all = h.GetDisplaySlotsRequiredRefill();
        if (all == null || all.Count == 0) return false;
        var self = clerk.transform;
        var tried = new HashSet<int>();

        for (int pass = 0; pass < 2; pass++)
        {
            if (pass == 0 && !mgmt.PickUpBoxGround) continue;
            tried.Clear();
            for (int i = 0; i < all.Count; i++)
            {
                var slot = all[i];
                if (slot == null) continue;
                int pid = slot.ProductID;
                if (pid < 0 || pid == excludedProduct || tried.Contains(pid)) continue;
                if (slot.IsOccupiedByOthers(clerk)) continue;
                tried.Add(pid);

                if (pass == 0)
                {
                    if (!h.CheckStreetForRequiredProduct(pid, out Box streetBox) || streetBox == null) continue;
                    if (streetBox.IsOccupiedByAnother(self) || streetBox.IsTargeted) continue;
                    var cand = h.GetDisplaySlotsRequiredRefill(pid);
                    var snap = reserve ? null : SnapshotOwners(cand);
                    var list = h.SearchBestFitDisplaySlotsForRestock(streetBox, cand);
                    if (snap != null) RestoreOwners(clerk, snap);
                    if (list == null || list.Count == 0) continue;
                    target = new IBoxTarget(new StreetBoxTarget(streetBox).Pointer);
                    displays = list; productId = pid; source = "street";
                    return true;
                }
                else
                {
                    if (!h.HasRackSlotForRequiredProduct(pid, out RackSlot rackSlot) || rackSlot == null) continue;
                    var cand = h.GetDisplaySlotsRequiredRefill(pid);
                    var snap = reserve ? null : SnapshotOwners(cand);
                    var list = h.SearchBestFitDisplaySlotsForRestock(rackSlot, cand);
                    if (snap != null) RestoreOwners(clerk, snap);
                    if (list == null || list.Count == 0) continue;
                    target = new IBoxTarget(new RackSlotBoxTarget(rackSlot).Pointer);
                    displays = list; productId = pid; source = "rack";
                    return true;
                }
            }
        }
        return false;
    }

    // ---------------------------------------------------------------- the trip

    /// <summary>Runs before the game's search. Returns false when it filled the search results itself.</summary>
    internal static bool BeforeSearch(SearchHandler h, out bool vanillaRan)
    {
        vanillaRan = true;
        var clerk = h.m_Clerk;
        if (clerk == null) return true;
        var plan = GetPlan(clerk);
        if (plan == null) return true;
        plan.FetchOnlyNext = false;
        // The fetch-only run that reserved these has ended (its Exit cleared them).
        plan.ReservedSecond = null;

        var mgmt = clerk.ManagementData;
        if (mgmt == null || !mgmt.IsActive)
        {
            AbortPlan(clerk, false, "restocker deactivated, second box put down");
            return true;
        }

        var cur = Current(clerk);
        var stash = Stash(clerk, plan);

        switch (plan.Phase)
        {
            case Phase.FetchFirst:
                if (cur != null && stash == null && ProductOf(cur) == plan.FirstProduct)
                {
                    plan.First = cur;
                    if (TryFindSecondJob(h, clerk, plan.FirstProduct, true, out var target, out var displays,
                            out int p2, out string source))
                    {
                        h.BoxTarget = target;
                        h.DisplayTargets = displays;
                        h.RackTarget = null;
                        plan.SecondProduct = p2;
                        plan.ReservedSecond = new List<DisplaySlot>();
                        for (int i = 0; i < displays.Count; i++)
                            if (displays[i] != null) plan.ReservedSecond.Add(displays[i]);
                        plan.Phase = Phase.FetchSecond;
                        plan.FetchOnlyNext = true;
                        vanillaRan = false;
                        Info($"{Name(clerk)}: has box 1 (product {plan.FirstProduct}), fetching box 2 (product {p2}) from {source}");
                        return false;
                    }
                    EndPlan(clerk, "no second job any more, restocking box 1 normally");
                    return true;
                }
                EndPlan(clerk, "could not take box 1, back to normal");
                return true;

            case Phase.FetchSecond:
                if (cur != null && !Same(cur, plan.First) && IsStashed(clerk, plan.First))
                {
                    plan.Second = cur;
                    plan.Phase = Phase.DeliverFirst;
                    HandOver(clerk, plan.First);
                    Info($"{Name(clerk)}: has both boxes, restocking product {plan.FirstProduct} first");
                    return true;
                }
                plan.Phase = Phase.Unwind;
                Info($"{Name(clerk)}: could not take box 2, continuing with box 1");
                return UnwindStep(clerk, plan, cur, stash);

            case Phase.DeliverFirst:
                plan.Phase = Phase.Unwind;
                if (stash != null)
                {
                    HandOver(clerk, stash);
                    Info($"{Name(clerk)}: product {plan.FirstProduct} done (box 1 {(IsStashed(clerk, plan.First) ? "has leftovers" : "gone")}), restocking product {ProductOf(stash)}");
                    return true;
                }
                return UnwindStep(clerk, plan, Current(clerk), null);

            default:
                return UnwindStep(clerk, plan, cur, stash);
        }
    }

    private static bool UnwindStep(ClerkT clerk, Plan plan, Box cur, Box stash)
    {
        if (stash == null)
        {
            EndPlan(clerk, "trip finished");
            return true;
        }
        if (cur == null)
        {
            HandOver(clerk, stash);
            Info($"{Name(clerk)}: taking the remaining box (product {ProductOf(stash)}) back");
        }
        return true;
    }

    /// <summary>Runs after the game's own search: decides whether to start a two-box trip.</summary>
    internal static void AfterSearch(SearchHandler h)
    {
        var clerk = h.m_Clerk;
        if (clerk == null) return;
        var plan = GetPlan(clerk);
        if (plan != null && plan.Phase != Phase.None) return;
        if (Current(clerk) != null) return;

        var displays = h.DisplayTargets;
        if (displays == null || displays.Count == 0 || h.RackTarget != null) return;
        var bt = h.BoxTarget;
        if (bt == null || bt.TryCast<CarryBoxTarget>() != null) return;

        int level = Level(clerk);
        if (level < Plugin.MinimumLevel.Value) return;

        int p1 = bt.ProductID;
        if (p1 < 0) return;
        // Only checks that a second job exists right now; the actual job is picked once box 1 is in hand.
        if (!TryFindSecondJob(h, clerk, p1, false, out _, out _, out int p2, out string source)) return;

        PruneDeadPlans();
        Plans[clerk.Pointer] = new Plan
        {
            Clerk = clerk, Phase = Phase.FetchFirst, FetchOnlyNext = true, FirstProduct = p1,
        };
        Info($"{Name(clerk)} (level {level}): two-box trip, box 1 = product {p1}, then product {p2} ({source})");
    }

    // ---------------------------------------------------------------- restocking state

    internal static void BeforeRestockEnter(ClerkRestockingState state)
    {
        var tasks = state.m_Sequence?.m_Tasks;
        if (tasks == null) return;
        RestoreTasks(state);

        var clerk = state.m_Clerk;
        var plan = clerk != null ? GetPlan(clerk) : null;
        if (plan == null || !plan.FetchOnlyNext) return;
        plan.FetchOnlyNext = false;

        int takeIndex = -1;
        for (int i = 0; i < tasks.Count; i++)
            if (tasks[i]?.TryCast<TakeBox>() != null) { takeIndex = i; break; }
        if (takeIndex < 0)
        {
            Plugin.Logger.LogWarning("[DoubleCarry] restocking sequence has no TakeBox task, two-box trip cancelled");
            AbortPlan(clerk, true, null);
            return;
        }
        if (takeIndex == tasks.Count - 1) return;   // already ends after TakeBox

        var original = new List<SequenceTask>();
        for (int i = 0; i < tasks.Count; i++) original.Add(tasks[i]);
        tasks.RemoveRange(takeIndex + 1, tasks.Count - takeIndex - 1);
        Stripped[state.Pointer] = (state, original);
    }

    internal static void RestoreTasks(ClerkRestockingState state)
    {
        if (!Stripped.Remove(state.Pointer, out var entry)) return;
        var tasks = state.m_Sequence?.m_Tasks;
        if (tasks == null) return;
        tasks.Clear();
        foreach (var t in entry.Original) tasks.Add(t);
    }

    // ---------------------------------------------------------------- visuals

    /// <summary>Stacks every box in the clerk's hands that isn't the current one on top of it.</summary>
    internal static void ArrangeStack(ClerkT clerk)
    {
        var holder = clerk.BoxHolder;
        if (holder == null || holder.childCount < 1) return;
        // Hands emptied (CarryBox(null)): leave everything where it is. The box being let go may still be
        // parented here for a moment (GoToTrash clears the hands before despawning it), and the host
        // hands the stashed box over on the clerk's next search anyway.
        var cur = clerk.m_CurrentBox;
        if (cur == null || cur.transform.parent == null || cur.transform.parent.Pointer != holder.Pointer) return;
        // CarryBox_Internal tweens the current box to local zero / identity.
        Extent(cur, Vector3.zero, out float top, out float cx, out float cz);

        for (int i = 0; i < holder.childCount; i++)
        {
            var child = holder.GetChild(i);
            if (child == null || !child.gameObject.activeSelf) continue;
            var box = child.GetComponent<Box>();
            if (box == null || Same(box, cur)) continue;

            var b = Bounds(box, out var s);
            var pos = new Vector3(cx - b.center.x * s.x,
                top - (b.center.y - b.extents.y) * s.y + 0.005f,
                cz - b.center.z * s.z);
            child.DOKill(false);
            child.DOLocalMove(pos, StackTweenTime, false);
            child.DOLocalRotate(Vector3.zero, StackTweenTime, RotateMode.Fast);
            Extent(box, pos, out top, out cx, out cz);
        }
    }

    private static UnityEngine.Bounds Bounds(Box box, out Vector3 scale)
    {
        scale = box.transform.localScale;
        var b = box.LocalBounds;
        if (b.extents.y < 0.001f) b = new UnityEngine.Bounds(new Vector3(0f, 0.15f, 0f), new Vector3(0.4f, 0.3f, 0.4f));
        return b;
    }

    /// <summary>Top (y) and centre (x, z) of a box placed at <paramref name="localPos"/> in holder space.</summary>
    private static void Extent(Box box, Vector3 localPos, out float top, out float cx, out float cz)
    {
        var b = Bounds(box, out var s);
        top = localPos.y + (b.center.y + b.extents.y) * s.y;
        cx = localPos.x + b.center.x * s.x;
        cz = localPos.z + b.center.z * s.z;
    }

    // ---------------------------------------------------------------- dropping / shutdown

    /// <summary>After the game dropped the clerk's box (fired, deactivated...): drop the stash too.</summary>
    internal static void AfterDrop(ClerkT clerk)
    {
        if (_dropping || InIdleTick || !HostSide) return;
        var plan = GetPlan(clerk);
        if (plan == null) return;
        var stash = Stash(clerk, plan);
        EndPlan(clerk, null);
        if (stash == null) return;
        DropStash(clerk, stash, null);
        Info($"{Name(clerk)}: dropped its box, so the second box (product {ProductOf(stash)}) was put down too");
    }

    private static void DropStash(ClerkT clerk, Box stash, Box keep)
    {
        _dropping = true;
        try
        {
            HandOver(clerk, stash);
            clerk.DropBoxToGround();
            if (keep != null && keep.gameObject.activeInHierarchy)
            {
                keep.transform.DOKill(false);   // drop the stacking tween; CarryBox starts its own
                clerk.CarryBox(keep);
            }
        }
        finally { _dropping = false; }
    }

    /// <summary>
    /// Ends the clerk's trip without leaving a box stuck in its hands: the stashed box is handed back
    /// when the hands are empty and <paramref name="handBack"/> is set, otherwise put on the floor.
    /// </summary>
    internal static void AbortPlan(ClerkT clerk, bool handBack, string why)
    {
        var plan = GetPlan(clerk);
        if (plan == null) return;
        EndPlan(clerk, why);
        if (!HostSide) return;
        var stash = Stash(clerk, plan);
        if (stash == null) return;
        var cur = Current(clerk);
        if (cur == null && handBack) HandOver(clerk, stash);
        else DropStash(clerk, stash, cur);
    }

    private static bool IsFetching(ClerkT clerk)
    {
        foreach (var e in Stripped.Values)
            if (e.State != null && Same(e.State.m_Clerk, clerk)) return true;
        return false;
    }

    /// <summary>Mod switched off: give every restocker back a normal single box.</summary>
    internal static void ReleaseAll()
    {
        foreach (var plan in Plans.Values)
        {
            try
            {
                var clerk = plan.Clerk;
                if (clerk == null || !HostSide) continue;
                ReleaseReserved(clerk, plan);
                var stash = Stash(clerk, plan);
                var cur = Current(clerk);
                if (stash != null)
                {
                    if (cur == null) HandOver(clerk, stash);
                    else DropStash(clerk, stash, cur);
                }
                else if (plan.Phase == Phase.FetchSecond && Same(cur, plan.First) && IsFetching(clerk))
                {
                    // On its way to box 2 with box 1 in hand: once the full sequence is restored this run
                    // would take box 2 and leave box 1 stuck in the hands, so put box 1 down now.
                    _dropping = true;
                    try { clerk.DropBoxToGround(); } finally { _dropping = false; }
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"[DoubleCarry] releasing a second box failed: {e.Message}");
            }
        }
        var stripped = new List<ClerkRestockingState>();
        foreach (var e in Stripped.Values) stripped.Add(e.State);
        foreach (var state in stripped)
        {
            try { if (state != null) RestoreTasks(state); }
            catch (Exception e) { Plugin.Logger.LogWarning($"[DoubleCarry] restoring a restock sequence failed: {e.Message}"); }
        }
    }

    internal static void ResetState()
    {
        Plans.Clear();
        Stripped.Clear();
        InIdleTick = false;
        _dropping = false;
    }
}

// ==================================================================== Harmony patches

[HarmonyPatch(typeof(SearchHandler), nameof(SearchHandler.PerformSearch))]
internal static class PerformSearchPatch
{
    private static bool Prefix(SearchHandler __instance, out bool __state)
    {
        __state = true;
        try
        {
            if (!DoubleCarry.HostSide) return true;
            return DoubleCarry.BeforeSearch(__instance, out __state);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[DoubleCarry] search prefix failed, trip cancelled: {e}");
            try { if (__instance.m_Clerk != null) DoubleCarry.AbortPlan(__instance.m_Clerk, true, null); }
            catch (Exception e2) { Plugin.Logger.LogError($"[DoubleCarry] cancelling the trip failed: {e2.Message}"); }
            __state = true;
            return true;
        }
    }

    private static void Postfix(SearchHandler __instance, bool __state)
    {
        if (!__state) return;
        try
        {
            if (DoubleCarry.HostSide) DoubleCarry.AfterSearch(__instance);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[DoubleCarry] search postfix failed: {e}");
        }
    }
}

[HarmonyPatch(typeof(ClerkRestockingState), nameof(ClerkRestockingState.Enter))]
internal static class RestockEnterPatch
{
    private static void Prefix(ClerkRestockingState __instance)
    {
        try { DoubleCarry.BeforeRestockEnter(__instance); }
        catch (Exception e) { Plugin.Logger.LogError($"[DoubleCarry] restock enter failed: {e}"); }
    }
}

[HarmonyPatch(typeof(ClerkRestockingState), nameof(ClerkRestockingState.Exit))]
internal static class RestockExitPatch
{
    private static void Postfix(ClerkRestockingState __instance)
    {
        try { DoubleCarry.RestoreTasks(__instance); }
        catch (Exception e) { Plugin.Logger.LogError($"[DoubleCarry] restock exit failed: {e}"); }
    }
}

[HarmonyPatch(typeof(ClerkT), nameof(ClerkT.CarryBox_Internal))]
internal static class CarryBoxPatch
{
    private static void Postfix(ClerkT __instance)
    {
        if (!Plugin.StackVisuals.Value) return;
        try { DoubleCarry.ArrangeStack(__instance); }
        catch (Exception e) { Plugin.Logger.LogError($"[DoubleCarry] stacking boxes failed: {e}"); }
    }
}

[HarmonyPatch(typeof(ClerkT), nameof(ClerkT.DropBoxToGround))]
internal static class DropBoxPatch
{
    private static void Postfix(ClerkT __instance)
    {
        try { DoubleCarry.AfterDrop(__instance); }
        catch (Exception e) { Plugin.Logger.LogError($"[DoubleCarry] dropping the second box failed: {e}"); }
    }
}

[HarmonyPatch(typeof(ClerkIdleState), nameof(ClerkIdleState.OnTick))]
internal static class IdleTickPatch
{
    private static void Prefix() => DoubleCarry.InIdleTick = true;

    private static Exception Finalizer(Exception __exception)
    {
        DoubleCarry.InIdleTick = false;
        return __exception;
    }
}
