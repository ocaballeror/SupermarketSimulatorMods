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
 *   Phase None:        vanilla search finds a job for a non-carried box B1 (product P1): restock
 *                      (DisplayTargets) or take it to storage (RackTarget, RackingState), and the clerk is
 *                      at least MinimumLevel -> if a second job exists (restock another product, or another
 *                      delivery-area box to storage), the next Restocking/Racking run is "fetch only"
 *                      (tasks after TakeBox removed) -> B1 in hand.
 *   Phase FetchFirst:  next search: still holding B1 -> fill the search results with job 2 ourselves
 *                      (skip vanilla) and fetch only again. TakeBox(B2) makes B2 current; B1 stays in
 *                      the hands and becomes the "stash".
 *   Phase FetchSecond: next search: holding B2 with B1 stashed -> work out where each box would go next
 *                      (the vanilla carried-box rule: displays of its product, else a rack slot) and make
 *                      the box with the shorter NavMesh path to its destination the current one. Vanilla
 *                      then handles that box; the other is the stash, on top.
 *   Racking:           putting a box into a rack (RackBox, merge included) isn't unpacking, so it happens
 *                      with the stash still in hand; when the hands empty the stash is handed over.
 *   Storage boxes:     a box picked up for storage (Plan.Storage) is racked by vanilla in one uninterrupted
 *                      RackingState run. Here the run is cut after TakeBox, and the vanilla carried-box search
 *                      would send it to any display of its product that isn't completely full (no refill
 *                      threshold). So every search with such a box in hand is filled by the mod with the
 *                      vanilla carried-box rack job (CarryBoxTarget + merge/space slot), and Destination /
 *                      FetchFromFloor never send it to displays.
 *   Set down:          a clerk never unpacks while holding two boxes. When the first RestockDisplay task
 *                      begins (the clerk has walked to the first display and is about to open the box),
 *                      a RestockDisplay.OnBegin prefix puts the stashed box on the floor behind the clerk
 *                      (Clerk.DropBoxToGround on it, then CarryBox(B1) again). The box is now `Plan.Floor`.
 *                      Clerk-dropped boxes are subscribed to StorageStreet. Every street search also requires
 *                      StorageStreet.IsWithinRestockableArea (the outside delivery area), which should already
 *                      keep other restockers away from a box lying inside the store, but in-game another restocker
 *                      did take one (2026-11-19 report), e.g. when the area covers the spot. So the box is
 *                      also reserved: while it is `Plan.Floor` (owner alive and active, at most ReserveMaxSeconds),
 *                      a StorageStreet.GetBoxesFromStreet postfix removes it from the list (a fresh list; the
 *                      three restocker street searches all read it). Reserving through Box.IsTargeted instead
 *                      would also stop online players picking it up (NetworkBox.IsClaimedByNpc), so it isn't used.
 *                      Safety net: a MoveTo.ShouldAbort postfix stops the walk back (AbortWithFailure) when the
 *                      box is gone (`Plan.FetchingFloor`, hands empty).
 *   Phase DeliverFirst: vanilla handles the current box to the end: more displays, then the trash if
 *                      empty (GoToTrash), or a rack if not (RackingState). The mod stays out of every
 *                      search made with a box in hand, with one exception (regroup):
 *   Regroup:           the unpacked box still has leftovers (vanilla wants to rack it) and the floor box also
 *                      goes to storage (no display wants it, a rack slot has room) -> a search postfix
 *                      replaces the result with a fetch-only Racking run for the floor box, so both are
 *                      racked in one walk (phase FetchSecond again). Once per trip.
 *   Back for B2:       first search with empty hands and B2 still on the floor: fill the search results
 *                      ourselves with B2 as a StreetBoxTarget, plus its display slots (vanilla
 *                      SearchBestFitDisplaySlotsForRestock) or, if no display needs it any more, a rack
 *                      slot (IsMergeAvailableWithBox / HasSpaceInRackSlotForProduct, exactly as the vanilla
 *                      street-to-rack branch). A normal full Restocking/Racking run then picks B2 up,
 *                      restocks P2 and trashes or racks the leftovers. If there is nowhere to take it, the
 *                      clerk does other (vanilla) work and retries on its next empty-handed search, for up
 *                      to FloorRetrySeconds; a player picking B2 up ends the plan.
 *   Phase Unwind:      whenever the hands are empty and a box is still stashed (no display was ever
 *                      reached, e.g. another restocker filled them first), CarryBox(stash) and let
 *                      vanilla handle it. Done when nothing is stashed or on the floor.
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
 * the current one (Box.LocalBounds). When one of two boxes leaves the hands (CarryBox(null); a prefix
 * records which box was current), the remaining one is lowered into the hands; when a box the mod had
 * lifted becomes the current one, its lift tween is replaced by a move to the hands (on guests nothing
 * else killed it, so it could stay floating, in-game 2026-11-15). Purely cosmetic; runs wherever the mod
 * is installed.
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
        /// <summary>The box this clerk put on the floor to unpack the other one; it comes back for it.</summary>
        public Box Floor;
        /// <summary>Time of the first empty-handed search that tried to fetch the floor box (-1: not yet).</summary>
        public float FloorRetryStart = -1f;
        /// <summary>When the box was put on the floor (Time.time); a reservation never outlives <c>ReserveMaxSeconds</c>.</summary>
        public float FloorSince;
        /// <summary>The run started by the last search walks back for <c>Floor</c>; cleared at the next search.</summary>
        public bool FetchingFloor;
        /// <summary>The trip started with a storage job for box 1 (vanilla chose a rack, not displays).</summary>
        public bool FirstToStorage;
        /// <summary>The second job fetched (or being fetched) is a storage job.</summary>
        public bool SecondToStorage;
        /// <summary>
        /// Boxes picked up for storage. Vanilla racks those in one uninterrupted RackingState run; the mod stops
        /// that run after TakeBox, so it must keep the box on its way to storage itself: the vanilla carried-box
        /// search would otherwise top up any display of that product that isn't completely full.
        /// </summary>
        public readonly HashSet<IntPtr> Storage = new();
        /// <summary>The clerk already went back for the floor box with leftovers in hand once this trip.</summary>
        public bool Regrouped;
    }

    /// <summary>A job for a box that isn't in hand yet: restock (Displays) or take it to storage (Rack).</summary>
    internal struct Job
    {
        public IBoxTarget Target;
        public DisplaySlotList Displays;
        public RackSlot Rack;
        public int Product;
        public string Source;
        public bool IsRestock => Displays != null && Displays.Count > 0;
    }

    /// <summary>How long a clerk keeps trying to come back for a box it put down before giving up.</summary>
    private const float FloorRetrySeconds = 180f;
    /// <summary>Where a box is put down: this far from the clerk, on the side away from the display.</summary>
    private const float SetDownDistance = 0.6f;
    /// <summary>After this long the set-down box should have landed; its pose is sent to the guests again.</summary>
    private const float SettleSeconds = 1.5f;

    /// <summary>
    /// A set-down box stays hidden from other restockers' searches at most this long, even if its owner's plan
    /// never ends (the plan normally ends far sooner: the owner picks the box up, finds it gone, or gives up
    /// after <c>FloorRetrySeconds</c> of trying).
    /// </summary>
    private const float ReserveMaxSeconds = 600f;

    private const float StackTweenTime = 0.3f;

    private static readonly Dictionary<IntPtr, Plan> Plans = new();
    // Boxes this mod lifted onto a stack (not the game's own carry position). Only these get re-tweened
    // when they become the box in hand. Stale pointers are harmless: at worst a box gets one extra
    // re-tween to the hands.
    private static readonly HashSet<IntPtr> Raised = new();
    // Restocking/Racking sequence currently running a shortened list -> (sequence, its clerk, full task list).
    private static readonly Dictionary<IntPtr, (SupermarketSimulator.Sequence.Sequence Seq, ClerkT Clerk, List<SequenceTask> Original)> Stripped = new();

    // Reserved boxes already reported in the log (verbose only), so a search every few seconds doesn't spam.
    private static readonly HashSet<IntPtr> LoggedReserved = new();

    internal static bool InIdleTick;
    private static bool _dropping;

    // ---------------------------------------------------------------- helpers

    internal static void Info(string msg)
    {
        if (Plugin.VerboseLogging.Value) Plugin.Logger.LogInfo("[DoubleCarry] " + msg);
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
            if (slot == null) continue;
            var now = slot.m_OccupiedRestocker;
            if (now == null ? owner == null : Same(now, owner)) continue;   // unchanged (Same() is false for nulls)
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

    /// <summary>The box a not-yet-taken box target points at (street or vehicle box; null for a rack slot).</summary>
    internal static Box BoxOf(IBoxTarget target)
    {
        if (target == null) return null;
        var street = target.TryCast<StreetBoxTarget>();
        if (street != null) return street.Box;
        var vehicle = target.TryCast<VehicleBoxTarget>();
        return vehicle?.Box;
    }

    /// <summary>How many boxes of this box's size a rack slot holds (the game's BoxSO grid layout), 0 if unknown.</summary>
    private static int Capacity(Box box)
    {
        var so = box.BoxSO;
        if (so == null && NoktaSingleton<IDManager>.HasInstance) so = NoktaSingleton<IDManager>.Instance.BoxSO(box.BoxID);
        var layout = so?.GridLayout;
        return layout != null ? layout.boxCount : 0;
    }

    /// <summary>Capacity of a slot that already holds boxes: the same rule as RackSlot.Full (BoxSO of Data.BoxID).</summary>
    private static int Capacity(RackSlotData data)
    {
        if (!NoktaSingleton<IDManager>.HasInstance) return 0;
        var layout = NoktaSingleton<IDManager>.Instance.BoxSO(data.BoxID)?.GridLayout;
        return layout != null ? layout.boxCount : 0;
    }

    /// <summary>
    /// Can all these boxes be put in storage, one after the other, under the game's own rules? Replays
    /// RackManager.GetRackSlotThatHasSpaceFor(productID, useUnlabeledRacks) for each box (as the clerk's
    /// search does when it racks a box in hand), counting the boxes placed so far:
    ///   - a slot labelled for the product that isn't full (RackSlot.Full: BoxCount >= the slot's BoxSO
    ///     grid boxCount; a labelled slot with no boxes counts as not full), else
    ///   - only if the restocker may use unlabelled racks: an unlabelled slot with no box, which then takes
    ///     that product and as many boxes as its box size allows (1, 2, 4...).
    /// A merge into a box already on the rack isn't counted on: each box needs a slot place.
    /// </summary>
    internal static bool StorageFits(ClerkT clerk, List<Box> boxes)
    {
        try
        {
            var mgmt = clerk.ManagementData;
            bool unlabeled = mgmt != null && mgmt.UseUnlabeledRacks;
            if (!NoktaSingleton<RackManager>.HasInstance) return false;
            var racks = NoktaSingleton<RackManager>.Instance.m_Racks;
            if (racks == null) return false;

            var added = new Dictionary<IntPtr, int>();                // boxes placed so far, per slot
            var claimed = new Dictionary<IntPtr, (int Pid, int Cap)>(); // unlabelled slots taken by a box so far
            foreach (var box in boxes)
            {
                int pid = ProductOf(box);
                int cap = box != null ? Capacity(box) : 0;
                if (pid < 0 || cap <= 0) return false;
                RackSlot free = null;
                bool placed = false;
                for (int r = 0; r < racks.Count && !placed; r++)
                {
                    var slots = racks[r]?.RackSlots;
                    if (slots == null) continue;
                    for (int i = 0; i < slots.Count; i++)
                    {
                        var slot = slots[i];
                        var data = slot != null ? slot.Data : null;
                        if (data == null) continue;
                        added.TryGetValue(slot.Pointer, out int extra);
                        if (claimed.TryGetValue(slot.Pointer, out var c))
                        {
                            if (c.Pid != pid || extra >= c.Cap) continue;
                        }
                        else if (data.ProductID == pid)
                        {
                            int count = data.BoxCount;
                            int slotCap = count > 0 ? Capacity(data) : cap;
                            if (count + extra >= slotCap) continue;
                        }
                        else
                        {
                            if (unlabeled && free == null && data.ProductID == -1 && !slot.HasBox) free = slot;
                            continue;
                        }
                        added[slot.Pointer] = extra + 1;
                        placed = true;
                        break;
                    }
                }
                if (placed) continue;
                if (free == null) return false;
                claimed[free.Pointer] = (pid, cap);
                added[free.Pointer] = 1;
            }
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[DoubleCarry] checking rack space failed, not pairing: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Finds a second job the way the game's own search does: restock a product other than
    /// <paramref name="firstProduct"/> (street boxes first if allowed, then racks), else take another box
    /// from the delivery area to storage (the one nearest to the clerk). Never returns
    /// <paramref name="firstBox"/> itself. A storage job, and a street-box restock while box 1 goes to storage
    /// (<paramref name="firstToStorage"/>), is only returned if the racks have room for every box of the pair
    /// that will need a rack place (see <see cref="StorageFits"/>). With
    /// <paramref name="reserve"/> false it is a pure probe: the display reservations the game's search makes
    /// are undone.
    /// </summary>
    internal static bool TryFindSecondJob(SearchHandler h, ClerkT clerk, Box firstBox, int firstProduct,
        bool reserve, bool firstToStorage, out Job job)
    {
        job = default;
        var mgmt = clerk.ManagementData;
        if (mgmt == null || !mgmt.IsActive) return false;
        var self = clerk.transform;

        var all = mgmt.RestockShelf ? h.GetDisplaySlotsRequiredRefill() : null;
        if (all != null && all.Count > 0)
        {
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
                    if (pid < 0 || pid == firstProduct || tried.Contains(pid)) continue;
                    if (slot.IsOccupiedByOthers(clerk)) continue;
                    tried.Add(pid);

                    if (pass == 0)
                    {
                        if (!h.CheckStreetForRequiredProduct(pid, out Box streetBox) || streetBox == null) continue;
                        if (Same(streetBox, firstBox)) continue;
                        if (streetBox.IsOccupiedByAnother(self) || streetBox.IsTargeted) continue;
                        var cand = h.GetDisplaySlotsRequiredRefill(pid);
                        var snap = reserve ? null : SnapshotOwners(cand);
                        var list = h.SearchBestFitDisplaySlotsForRestock(streetBox, cand);
                        if (snap != null) RestoreOwners(clerk, snap);
                        if (list == null || list.Count == 0) continue;
                        // Box 1 goes to storage and this box's leftovers will need a rack place too: if they took
                        // the last one, box 1 (set down in the store at a shelf) would be stranded. (Box 2 from a
                        // rack slot needs no check: its leftovers return to the place it frees.)
                        if (firstToStorage && firstBox != null
                            && !StorageFits(clerk, new List<Box> { firstBox, streetBox })) continue;
                        job = new Job { Target = new IBoxTarget(new StreetBoxTarget(streetBox).Pointer), Displays = list,
                            Product = pid, Source = "the delivery area, to restock" };
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
                        job = new Job { Target = new IBoxTarget(new RackSlotBoxTarget(rackSlot).Pointer), Displays = list,
                            Product = pid, Source = "storage, to restock" };
                        return true;
                    }
                }
            }
        }

        // Delivery-area box to storage: the vanilla street-to-rack branch (gated by PickUpBoxGround).
        if (!mgmt.PickUpBoxGround || !NoktaSingleton<StorageStreet>.HasInstance) return false;
        var street = NoktaSingleton<StorageStreet>.Instance;
        var boxes = street?.GetBoxesFromStreet();
        if (boxes == null) return false;
        int excluded = h.m_ExcludedProductId;
        // Cheap per-box filters first; the survivors are tried nearest to the clerk first.
        var near = new List<(float Dist, Box Box, int Pid)>();
        var origin = self.position;
        for (int i = 0; i < boxes.Count; i++)
        {
            var box = boxes[i];
            if (box == null || Same(box, firstBox) || !box.gameObject.activeInHierarchy) continue;
            int pid = ProductOf(box);
            if (pid < 0 || pid == excluded) continue;
            if (box.m_IsBoxOccupied || box.IsTargeted || box.IsOccupiedByAnother(self)) continue;
            var pos = box.transform.position;
            if (!street.IsWithinRestockableArea(pos)) continue;
            near.Add(((pos - origin).sqrMagnitude, box, pid));
        }
        near.Sort((a, b) => a.Dist.CompareTo(b.Dist));
        // Per-call caches: space in a rack slot and rack capacity depend on the product (and box size), not on
        // the individual box. The merge check stays per box.
        var spaceCache = new Dictionary<int, RackSlot>();   // null = no rack slot with space for the product
        var noFit = new HashSet<int>();                      // products for which StorageFits said no
        foreach (var (_, box, pid) in near)
        {
            if (noFit.Contains(pid)) continue;
            // The vanilla rule for this box alone.
            if (!h.IsMergeAvailableWithBox(box, out RackSlot rack) || rack == null)
            {
                if (!spaceCache.TryGetValue(pid, out rack))
                {
                    if (!h.HasSpaceInRackSlotForProduct(pid, out rack)) rack = null;
                    spaceCache[pid] = rack;
                }
                if (rack == null) continue;
            }
            // Both boxes may need a rack place: box 1 when it goes to storage, and also when it is restocked,
            // since its leftovers go to storage too (if they took the last place, this box would be left in
            // the store). Box 1 is unknown only in the trip-start probe when it is still in a rack slot: its
            // own slot place then stays free for its leftovers, except for a box of this same product.
            var demand = new List<Box> { box };
            var b1 = firstBox ?? (pid == firstProduct ? box : null);
            if (b1 != null) demand.Insert(0, b1);
            if (!StorageFits(clerk, demand)) { noFit.Add(pid); continue; }
            job = new Job { Target = new IBoxTarget(new StreetBoxTarget(box).Pointer), Rack = rack,
                Product = pid, Source = "the delivery area, for storage" };
            return true;
        }
        return false;
    }

    /// <summary>Puts a job into the search results so the next state change runs it.</summary>
    private static void ApplyJob(SearchHandler h, Job job)
    {
        h.BoxTarget = job.Target;
        if (job.IsRestock)
        {
            h.DisplayTargets = job.Displays;
            h.RackTarget = null;
        }
        else
        {
            if (h.DisplayTargets == null) h.DisplayTargets = new DisplaySlotList();
            h.DisplayTargets.Clear();   // Idle -> Restocking needs Count > 0; this must go to Racking
            h.RackTarget = job.Rack;
        }
    }

    // ---------------------------------------------------------------- choosing the order

    /// <summary>
    /// Where a box in hand would go next under the vanilla carried-box rule: the first display slot the
    /// game would restock (no refill threshold), else a rack slot. Display reservations made by the probe are
    /// undone. Null when it has nowhere to go (or is empty).
    /// </summary>
    private static Vector3? Destination(SearchHandler h, ClerkT clerk, Plan plan, Box box, out string what)
    {
        what = "nowhere";
        int pid = ProductOf(box);
        if (box == null || pid < 0 || !box.HasProducts) return null;
        var mgmt = clerk.ManagementData;
        if (!IsStorageBox(plan, box) && mgmt != null && mgmt.RestockShelf)
        {
            var cand = h.GetDisplaySlotsRequiredRefillWithoutCondition(pid);
            if (cand != null && cand.Count > 0)
            {
                var snap = SnapshotOwners(cand);
                var list = h.SearchBestFitDisplaySlotsForRestock(box, cand);
                RestoreOwners(clerk, snap);
                if (list != null && list.Count > 0 && list[0] != null)
                {
                    what = "shelf";
                    return StandPoint(list[0]);
                }
            }
        }
        if ((h.IsMergeAvailableWithBox(box, out RackSlot rack) && rack != null)
            || (h.HasSpaceInRackSlotForProduct(pid, out rack) && rack != null))
        {
            what = "storage";
            var ip = rack.m_InteractionPosition;
            return ip != null ? ip.position : rack.transform.position;
        }
        return null;
    }

    /// <summary>Where a restocker stands to fill the slot (falls back to the slot itself).</summary>
    private static Vector3 StandPoint(DisplaySlot slot)
    {
        try { return slot.InteractionPosition; }
        catch (Exception) { return slot.transform.position; }
    }

    internal static bool IsStorageBox(Plan plan, Box box) => plan != null && box != null && plan.Storage.Contains(box.Pointer);

    /// <summary>
    /// The box in hand was picked up for storage: fill the search results with the vanilla carried-box rack
    /// job (merge into a rack box, else a slot with space; CarryBoxTarget, as the game's own search builds it)
    /// instead of letting the carried-box search send it to displays. False (vanilla decides) when there is
    /// no rack place.
    /// </summary>
    private static bool RackCarried(SearchHandler h, ClerkT clerk, Plan plan, Box cur)
    {
        if (cur == null || !IsStorageBox(plan, cur) || !cur.HasProducts) return false;
        int pid = ProductOf(cur);
        if (pid < 0) return false;
        if (!h.IsMergeAvailableWithBox(cur, out RackSlot rack) || rack == null)
            if (!h.HasSpaceInRackSlotForProduct(pid, out rack) || rack == null)
            {
                Info($"{Name(clerk)}: no rack place for the storage box (product {pid}) right now, normal search decides");
                return false;
            }
        int excluded = h.m_ExcludedProductId;   // set by the idle drop timer; Reset() would clear it
        h.Reset();
        h.m_ExcludedProductId = excluded;
        h.BoxTarget = new IBoxTarget(new CarryBoxTarget(pid, cur, clerk).Pointer);
        if (h.DisplayTargets == null) h.DisplayTargets = new DisplaySlotList();
        h.DisplayTargets.Clear();
        h.RackTarget = rack;
        Info($"{Name(clerk)}: taking the box in hand (product {pid}) to storage");
        return true;
    }

    /// <summary>Walking distance along the NavMesh; straight-line distance if no path is found.</summary>
    private static float PathLength(Vector3 from, Vector3 to)
    {
        try
        {
            if (UnityEngine.AI.NavMesh.SamplePosition(from, out var a, 2f, UnityEngine.AI.NavMesh.AllAreas)
                && UnityEngine.AI.NavMesh.SamplePosition(to, out var b, 3f, UnityEngine.AI.NavMesh.AllAreas))
            {
                var path = new UnityEngine.AI.NavMeshPath();
                if (UnityEngine.AI.NavMesh.CalculatePath(a.position, b.position, UnityEngine.AI.NavMesh.AllAreas, path)
                    && path.status != UnityEngine.AI.NavMeshPathStatus.PathInvalid)
                {
                    var corners = path.corners;
                    if (corners != null && corners.Length > 0)
                    {
                        float len = Vector3.Distance(from, corners[0]);
                        for (int i = 1; i < corners.Length; i++) len += Vector3.Distance(corners[i - 1], corners[i]);
                        return len + Vector3.Distance(corners[corners.Length - 1], to);
                    }
                }
            }
        }
        catch (Exception e)
        {
            Info($"path length failed, using straight-line distance: {e.Message}");
        }
        return Vector3.Distance(from, to);
    }

    /// <summary>
    /// Both boxes are in hand (<paramref name="cur"/> current, <paramref name="other"/> stashed): make the one
    /// with the shorter walk to its destination the current one.
    /// </summary>
    private static void ChooseOrder(SearchHandler h, ClerkT clerk, Plan plan, Box cur, Box other)
    {
        var here = clerk.transform.position;
        var dCur = Destination(h, clerk, plan, cur, out string wCur);
        var dOther = Destination(h, clerk, plan, other, out string wOther);
        float lCur = dCur.HasValue ? PathLength(here, dCur.Value) : float.PositiveInfinity;
        float lOther = dOther.HasValue ? PathLength(here, dOther.Value) : float.PositiveInfinity;
        bool swap = lOther < lCur;
        Info($"{Name(clerk)}: has both boxes: product {ProductOf(cur)} -> {wCur} ({Fmt(lCur)}), " +
             $"product {ProductOf(other)} -> {wOther} ({Fmt(lOther)}); product {ProductOf(swap ? other : cur)} first");
        if (swap) HandOver(clerk, other);
    }

    private static string Fmt(float metres) => float.IsInfinity(metres) ? "no destination" : $"{metres:0.0} m";

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
        plan.FetchingFloor = false;   // the run that went back for the floor box (if any) has ended
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
                    if (plan.FirstToStorage) plan.Storage.Add(cur.Pointer);
                    if (TryFindSecondJob(h, clerk, cur, plan.FirstProduct, true, plan.FirstToStorage, out var job))
                    {
                        ApplyJob(h, job);
                        plan.SecondProduct = job.Product;
                        plan.SecondToStorage = !job.IsRestock;
                        if (job.IsRestock)
                        {
                            plan.ReservedSecond = new List<DisplaySlot>();
                            for (int i = 0; i < job.Displays.Count; i++)
                                if (job.Displays[i] != null) plan.ReservedSecond.Add(job.Displays[i]);
                        }
                        plan.Phase = Phase.FetchSecond;
                        plan.FetchOnlyNext = true;
                        vanillaRan = false;
                        Info($"{Name(clerk)}: has box 1 (product {plan.FirstProduct}), fetching box 2 (product {job.Product}) from {job.Source}");
                        return false;
                    }
                    // Stay in the plan while box 1 is in hand so a storage box still goes straight to storage.
                    plan.Phase = Phase.Unwind;
                    Info($"{Name(clerk)}: no second job any more, carrying on with box 1 alone");
                    return UnwindStep(h, clerk, plan, cur, stash, ref vanillaRan);
                }
                EndPlan(clerk, "could not take box 1, back to normal");
                return true;

            case Phase.FetchSecond:
                // Just took a box (box 2, or the floor box again when regrouping) with another plan box
                // still in hand.
                if (cur != null && stash != null && !Same(cur, stash))
                {
                    if (!Same(cur, plan.First) && !Same(cur, plan.Second))
                    {
                        plan.Second = cur;
                        if (plan.SecondToStorage) plan.Storage.Add(cur.Pointer);
                    }
                    if (Same(cur, plan.Floor)) plan.Floor = null;
                    plan.Phase = Phase.DeliverFirst;
                    ChooseOrder(h, clerk, plan, cur, stash);
                    if (RackCarried(h, clerk, plan, Current(clerk))) vanillaRan = false;
                    return vanillaRan;
                }
                plan.Phase = Phase.Unwind;
                Info($"{Name(clerk)}: could not take the other box, carrying on with the one in hand");
                return UnwindStep(h, clerk, plan, cur, stash, ref vanillaRan);

            default:
                return UnwindStep(h, clerk, plan, cur, stash, ref vanillaRan);
        }
    }

    private static bool UnwindStep(SearchHandler h, ClerkT clerk, Plan plan, Box cur, Box stash, ref bool vanillaRan)
    {
        // The clerk picked the floor box up again: it is a normal carried box from now on.
        if (plan.Floor != null && Same(cur, plan.Floor)) plan.Floor = null;
        // A box in hand (box 1 with leftovers, or anything else): vanilla decides what to do with it, except
        // that a box picked up for storage goes to storage. If a box is still stashed and vanilla restocks
        // again, RestockDisplay.OnBegin sets the stash down first.
        if (cur != null)
        {
            if (!RackCarried(h, clerk, plan, cur)) return true;
            vanillaRan = false;
            return false;
        }
        if (stash != null)
        {
            plan.Phase = Phase.Unwind;
            HandOver(clerk, stash);
            Info($"{Name(clerk)}: hands free, carrying on with the other box (product {ProductOf(stash)})");
            if (!RackCarried(h, clerk, plan, stash)) return true;
            vanillaRan = false;
            return false;
        }
        if (plan.Floor != null)
        {
            if (FetchFromFloor(h, clerk, plan))
            {
                vanillaRan = false;
                return false;
            }
            if (plan.Floor != null) return true;   // nowhere to take it right now: vanilla work, retry later
        }
        EndPlan(clerk, "trip finished");
        return true;
    }

    /// <summary>The box still lies where this clerk put it (nobody picked it up, racked it or destroyed it).</summary>
    private static bool IsOnFloor(ClerkT clerk, Box box)
    {
        if (box == null || !box.gameObject.activeInHierarchy) return false;
        if (box.transform.parent != null) return false;
        return !box.IsOccupiedByAnother(clerk.transform);
    }

    /// <summary>
    /// Hands empty and the set-down box is still on the floor: point the search results at it, the way the
    /// vanilla search does for a box lying in the delivery area. Returns true when the results were filled.
    /// Clears <c>plan.Floor</c> only when the box is gone or the clerk gives up: if the fetch run fails
    /// (path aborted, box targeted by someone else), the next empty-handed search tries again.
    /// </summary>
    private static bool FetchFromFloor(SearchHandler h, ClerkT clerk, Plan plan)
    {
        var box = plan.Floor;
        if (!IsOnFloor(clerk, box))
        {
            plan.Floor = null;
            Info($"{Name(clerk)}: the box it put down is gone (picked up by someone else?)");
            return false;
        }
        if (plan.FloorRetryStart < 0f) plan.FloorRetryStart = Time.time;
        else if (Time.time - plan.FloorRetryStart > FloorRetrySeconds)
        {
            plan.Floor = null;
            Info($"{Name(clerk)}: could not get back to the box it put down (product {ProductOf(box)}) for {FloorRetrySeconds:0} s, leaving it on the floor");
            return false;
        }
        int pid = ProductOf(box);
        var mgmt = clerk.ManagementData;

        // Same display rule as vanilla uses for a box already in hand (no refill threshold).
        DisplaySlotList displays = null;
        if (pid >= 0 && box.HasProducts && mgmt != null && mgmt.RestockShelf && !IsStorageBox(plan, box))
        {
            var cand = h.GetDisplaySlotsRequiredRefillWithoutCondition(pid);
            if (cand != null && cand.Count > 0) displays = h.SearchBestFitDisplaySlotsForRestock(box, cand);
        }
        RackSlot rack = null;
        if ((displays == null || displays.Count == 0) && pid >= 0 && box.HasProducts)
        {
            if (!h.IsMergeAvailableWithBox(box, out rack) || rack == null)
                if (!h.HasSpaceInRackSlotForProduct(pid, out rack)) rack = null;
        }

        if ((displays == null || displays.Count == 0) && rack == null)
            return false;   // nowhere to take it right now; retried on the next empty-handed search

        int excluded = h.m_ExcludedProductId;   // set by the idle drop timer; Reset() would clear it
        h.Reset();
        h.m_ExcludedProductId = excluded;
        h.BoxTarget = new IBoxTarget(new StreetBoxTarget(box).Pointer);
        if (displays != null && displays.Count > 0)
        {
            h.DisplayTargets = displays;
            h.RackTarget = null;
            Info($"{Name(clerk)}: going back for the box it put down, restocking product {pid}");
        }
        else
        {
            if (h.DisplayTargets == null) h.DisplayTargets = new DisplaySlotList();
            h.DisplayTargets.Clear();   // Idle -> Restocking needs Count > 0; this must go to Racking
            h.RackTarget = rack;
            Info($"{Name(clerk)}: going back for the box it put down, taking product {pid} to storage");
        }
        plan.Phase = Phase.Unwind;
        plan.FetchingFloor = true;
        return true;
    }

    // ---------------------------------------------------------------- reserving the set-down box

    /// <summary>
    /// The box is the one a clerk with a two-box trip put on the floor and is coming back for: no other
    /// restocker's search may take it. Derived from <c>Plan.Floor</c>, so it ends with the plan (owner picked
    /// the box up, found it gone, gave up, was fired or deactivated, the mod was turned off) and never needs
    /// to be released by hand. Two more limits so it can't stay reserved forever: the owner has to be alive and
    /// active, and <c>ReserveMaxSeconds</c> after the set-down it lapses regardless.
    /// </summary>
    internal static bool IsReservedFloorBox(Box box, out ClerkT owner)
    {
        owner = null;
        if (box == null || Plans.Count == 0) return false;
        foreach (var plan in Plans.Values)
        {
            var floor = plan.Floor;
            if (floor == null || floor.Pointer != box.Pointer) continue;
            var clerk = plan.Clerk;
            if (clerk == null) continue;
            var mgmt = clerk.ManagementData;
            if (mgmt == null || !mgmt.IsActive) continue;
            if (Time.time - plan.FloorSince > ReserveMaxSeconds) continue;
            owner = clerk;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Postfix of <c>StorageStreet.GetBoxesFromStreet</c>, the list every restocker street search walks (restock
    /// from the street, street box to rack, empty street box), including this mod's second-box search. Returns
    /// a fresh list, so removing from it is safe. The owner never needs this list for its own box: it points
    /// its search results at the floor box directly (<c>FetchFromFloor</c>).
    /// </summary>
    internal static void HideReservedBoxes(Il2CppSystem.Collections.Generic.List<Box> boxes)
    {
        if (boxes == null || Plans.Count == 0 || !HostSide) return;
        for (int i = boxes.Count - 1; i >= 0; i--)
        {
            var box = boxes[i];
            if (!IsReservedFloorBox(box, out var owner)) continue;
            boxes.RemoveAt(i);
            if (LoggedReserved.Add(box.Pointer))
                Info($"box (product {ProductOf(box)}) put down by {Name(owner)} is reserved for it, hidden from other restockers' searches");
        }
    }

    /// <summary>
    /// Safety net, called every frame for the clerk's current MoveTo: the clerk is walking back for the box it
    /// put down (hands still empty) and the box has been taken, racked or destroyed since the search -> stop
    /// walking (the run fails and the clerk goes back to idle and picks up its normal work).
    /// </summary>
    internal static bool ShouldStopWalkingBack(MoveTo move)
    {
        if (Plans.Count == 0 || !HostSide) return false;
        var clerk = move.m_Clerk;
        if (clerk == null) return false;
        var plan = GetPlan(clerk);
        if (plan == null || !plan.FetchingFloor) return false;
        // Box in hand: it was picked up (or this is a later leg of the run), nothing left to check.
        if (clerk.m_CurrentBox != null && clerk.m_CarryingBox) { plan.FetchingFloor = false; return false; }
        var floor = plan.Floor;
        if (floor == null) { plan.FetchingFloor = false; return false; }
        if (IsOnFloor(clerk, floor)) return false;
        plan.FetchingFloor = false;
        Info($"{Name(clerk)}: the box it was walking back for (product {ProductOf(floor)}) is gone, stopping");
        return true;
    }

    // ---------------------------------------------------------------- setting a box down

    /// <summary>
    /// The clerk is about to unpack at a display. If it still holds a second box, put that one on the floor
    /// first (behind the clerk, away from the display) and remember it.
    /// </summary>
    internal static void BeforeRestockDisplay(RestockDisplay task)
    {
        if (!HostSide) return;
        var clerk = task.Clerk;
        if (clerk == null) return;
        var plan = GetPlan(clerk);
        if (plan == null) return;
        var stash = Stash(clerk, plan);
        var cur = Current(clerk);
        if (stash == null || cur == null) return;

        DropStash(clerk, stash, cur);
        if (!IsOnFloor(clerk, stash))
        {
            Plugin.Logger.LogWarning($"[DoubleCarry] {Name(clerk)}: putting the second box down failed, trip cancelled");
            AbortPlan(clerk, false, null);
            return;
        }
        MoveAside(clerk, stash, task.TargetDisplaySlot);
        plan.Floor = stash;
        plan.FloorRetryStart = -1f;
        plan.FloorSince = Time.time;
        plan.FetchingFloor = false;
        Info($"{Name(clerk)}: put box (product {ProductOf(stash)}) on the floor to unpack product {ProductOf(cur)}");
    }

    /// <summary>
    /// Moves a just-dropped box next to the clerk, on the side away from the display, so it doesn't fall
    /// against the shelf. Tries away from the display, then left and right of the clerk, and keeps the first
    /// spot that is free (Physics.CheckBox); if none is, the box stays where it fell. Only when this client
    /// owns the box, so the exact pose can be sent to the others. Box transform sync is off after a clerk
    /// pickup and nothing turns it back on, so the pose is sent again once the box has landed.
    /// </summary>
    private static void MoveAside(ClerkT clerk, Box box, DisplaySlot slot)
    {
        try
        {
            var view = box.NetworkBox != null ? box.NetworkBox.m_PhotonTransformView : null;
            bool online = PhotonNetwork.IsConnected && PhotonNetwork.InRoom;
            if (online && (view == null || !view.photonView.IsMine)) return;

            var t = clerk.transform;
            var self = t.position;
            var away = slot != null ? self - slot.transform.position : -t.forward;
            away.y = 0f;
            if (away.sqrMagnitude < 0.0001f) { away = -t.forward; away.y = 0f; }
            if (away.sqrMagnitude < 0.0001f) away = Vector3.back;
            away.Normalize();
            var side = Vector3.Cross(Vector3.up, away);

            var b = Bounds(box, out var scale);
            var half = Vector3.Scale(b.extents, scale);
            var centerOffset = Vector3.Scale(b.center, scale);
            float lift = half.y + 0.05f;   // box bottom 5 cm above the clerk's feet
            // Far enough that the box clears the clerk's own body (about 0.35 m radius).
            float dist = Mathf.Max(SetDownDistance, 0.4f + Mathf.Max(half.x, half.z));

            Vector3? found = null;
            Quaternion rot = Quaternion.identity;
            foreach (var dir in new[] { away, side, -side })
            {
                var r = Quaternion.LookRotation(dir, Vector3.up);
                var center = self + dir * dist + Vector3.up * lift;
                if (Physics.CheckBox(center, half * 0.95f, r, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                    continue;
                found = center - r * centerOffset;
                rot = r;
                break;
            }
            if (found == null)
            {
                Info($"{Name(clerk)}: no free spot next to the shelf, the box stays where it fell");
            }
            else
            {
                var rb = box.GetComponent<Rigidbody>();
                if (rb != null && !rb.isKinematic)
                {
                    rb.linearVelocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    rb.position = found.Value;
                    rb.rotation = rot;
                }
                box.transform.SetPositionAndRotation(found.Value, rot);
            }
            if (!online) return;
            view.BroadcastCurrentTransform();
            DOVirtual.DelayedCall(SettleSeconds, (TweenCallback)(Action)(() => ResendPose(box)), true);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[DoubleCarry] moving the box aside failed (it stays where it fell): {e.Message}");
        }
    }

    private static void ResendPose(Box box)
    {
        try
        {
            if (box == null || !box.gameObject.activeInHierarchy || box.transform.parent != null) return;
            var view = box.NetworkBox != null ? box.NetworkBox.m_PhotonTransformView : null;
            if (view == null || !view.photonView.IsMine) return;
            view.BroadcastCurrentTransform();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[DoubleCarry] resending the set-down box pose failed: {e.Message}");
        }
    }

    /// <summary>
    /// Runs after the game's own search: decides whether to start a two-box trip, or (during a trip) whether
    /// to pick the floor box up again before taking leftovers to storage.
    /// </summary>
    internal static void AfterSearch(SearchHandler h)
    {
        var clerk = h.m_Clerk;
        if (clerk == null) return;
        var plan = GetPlan(clerk);
        if (plan != null)
        {
            TryRegroup(h, clerk, plan);
            return;
        }
        if (Current(clerk) != null) return;

        var bt = h.BoxTarget;
        if (bt == null || bt.TryCast<CarryBoxTarget>() != null) return;
        var displays = h.DisplayTargets;
        bool restock = displays != null && displays.Count > 0;
        var rack = h.RackTarget;
        if (!restock && rack == null) return;   // e.g. an empty box to throw away

        int level = Level(clerk);
        if (level < Plugin.MinimumLevel) return;

        int p1 = bt.ProductID;
        if (p1 < 0) return;
        // Only checks that a second job exists right now; the actual job is picked once box 1 is in hand.
        if (!TryFindSecondJob(h, clerk, BoxOf(bt), p1, false, !restock, out var job)) return;

        PruneDeadPlans();
        Plans[clerk.Pointer] = new Plan
        {
            Clerk = clerk, Phase = Phase.FetchFirst, FetchOnlyNext = true, FirstProduct = p1, FirstToStorage = !restock,
        };
        Info($"{Name(clerk)} (level {level}): two-box trip, box 1 = product {p1} ({(restock ? "to restock" : "for storage")}), " +
             $"then product {job.Product} from {job.Source}");
    }

    /// <summary>
    /// The clerk holds the box it unpacked, with leftovers that vanilla now wants to rack, and the box it put
    /// on the floor also goes to storage: pick the floor box up first and rack both in one walk.
    /// </summary>
    private static void TryRegroup(SearchHandler h, ClerkT clerk, Plan plan)
    {
        if (plan.Regrouped || (plan.Phase != Phase.DeliverFirst && plan.Phase != Phase.Unwind)) return;
        var floor = plan.Floor;
        if (floor == null || !IsOnFloor(clerk, floor)) return;
        var cur = Current(clerk);
        if (cur == null || Stash(clerk, plan) != null) return;
        if (!Same(cur, plan.First) && !Same(cur, plan.Second)) return;
        // Vanilla decided to take the box in hand to storage.
        var rack = h.RackTarget;
        if (rack == null || (h.DisplayTargets != null && h.DisplayTargets.Count > 0)) return;
        var bt = h.BoxTarget;
        if (bt == null || bt.TryCast<CarryBoxTarget>() == null) return;

        if (Destination(h, clerk, plan, floor, out string where) == null || where != "storage") return;
        int pid = ProductOf(floor);
        // Both go to storage now: the racks must have room for both under the game's rules.
        if (!StorageFits(clerk, new List<Box> { cur, floor })) return;

        plan.Regrouped = true;
        // Both are on their way to storage now; keep them off the displays.
        plan.Storage.Add(cur.Pointer);
        plan.Storage.Add(floor.Pointer);
        ApplyJob(h, new Job { Target = new IBoxTarget(new StreetBoxTarget(floor).Pointer), Rack = rack, Product = pid });
        plan.Phase = Phase.FetchSecond;
        plan.FetchOnlyNext = true;
        Info($"{Name(clerk)}: box (product {ProductOf(cur)}) has leftovers and the box on the floor (product {pid}) " +
             "goes to storage too, picking it up again first");
    }

    // ---------------------------------------------------------------- restocking state

    internal static void BeforeRestockEnter(ClerkRestockingState state)
        => BeforeStateEnter(state.m_Sequence, state.m_Clerk);

    internal static void BeforeRackingEnter(RackingState state)
    {
        var owner = state.m_OwnerTransform;
        BeforeStateEnter(state.m_Sequence, owner != null ? owner.GetComponent<ClerkT>() : null);
    }

    /// <summary>
    /// A Restocking or Racking run is about to start. Always restores a list shortened earlier; when the plan
    /// asks for it, removes every task after TakeBox ("fetch only").
    /// </summary>
    private static void BeforeStateEnter(SupermarketSimulator.Sequence.Sequence seq, ClerkT clerk)
    {
        var tasks = seq?.m_Tasks;
        if (tasks == null) return;
        RestoreTasks(seq);

        var plan = clerk != null ? GetPlan(clerk) : null;
        if (plan == null || !plan.FetchOnlyNext) return;
        plan.FetchOnlyNext = false;

        int takeIndex = -1;
        for (int i = 0; i < tasks.Count; i++)
            if (tasks[i]?.TryCast<TakeBox>() != null) { takeIndex = i; break; }
        if (takeIndex < 0)
        {
            Plugin.Logger.LogWarning("[DoubleCarry] restocker sequence has no TakeBox task, two-box trip cancelled");
            AbortPlan(clerk, true, null);
            return;
        }
        if (takeIndex == tasks.Count - 1) return;   // already ends after TakeBox

        var original = new List<SequenceTask>();
        for (int i = 0; i < tasks.Count; i++) original.Add(tasks[i]);
        tasks.RemoveRange(takeIndex + 1, tasks.Count - takeIndex - 1);
        Stripped[seq.Pointer] = (seq, clerk, original);
    }

    internal static void RestoreTasks(SupermarketSimulator.Sequence.Sequence seq)
    {
        if (seq == null || !Stripped.Remove(seq.Pointer, out var entry)) return;
        var tasks = seq.m_Tasks;
        if (tasks == null) return;
        tasks.Clear();
        foreach (var t in entry.Original) tasks.Add(t);
    }

    // ---------------------------------------------------------------- visuals

    /// <summary>
    /// Stacks every box in the clerk's hands that isn't the current one on top of it.
    /// <paramref name="previous"/> is the box that was current before this CarryBox call (or Zero).
    /// </summary>
    internal static void ArrangeStack(ClerkT clerk, IntPtr previous)
    {
        var holder = clerk.BoxHolder;
        if (holder == null || holder.childCount < 1) return;
        var cur = clerk.m_CurrentBox;
        bool holding = cur != null && cur.transform.parent != null && cur.transform.parent.Pointer == holder.Pointer;

        float top = 0f, cx = 0f, cz = 0f;
        bool haveBase = false;
        if (holding)
        {
            // CarryBox_Internal tweens the current box to local zero / identity. But when the top box is
            // put down, the box that stays was lifted onto the stack a moment earlier (CarryBox(top) made
            // the other one a stacked box), and that lift tween may still be running: on guests nothing
            // kills it, so it fights the game's tween and the box can end up floating where the stack was
            // (in-game 2026-11-15). Kill whatever runs on the current box and move it into the hands.
            // Only for a box this mod lifted onto the stack: a normal pickup keeps the game's own tween
            // (its duration follows the employee's speed boost, and DOKill would also stop unrelated tweens
            // such as a rack's DOScale).
            if (Raised.Remove(cur.Pointer)) MoveTo(cur.transform, Vector3.zero, Duration(clerk));
            Extent(cur, Vector3.zero, out top, out cx, out cz);
            haveBase = true;
        }
        else
        {
            // Hands emptied (CarryBox(null)). Racking re-parents the box into the rack before this
            // (RackBox.PlaceBox: RackSlot.AddBox, then CarryBox(null)); GoToTrash clears the hands while
            // the trashed box is still parented here, about to be despawned. Either way the bottom box is
            // leaving: lower whatever else is still held (the stacked box) into the hands instead of
            // leaving it floating until the host hands it over at the next search. The leaving box is
            // `previous`; if it isn't known, don't touch anything.
            if (previous == IntPtr.Zero) return;
        }

        for (int i = 0; i < holder.childCount; i++)
        {
            var child = holder.GetChild(i);
            if (child == null || !child.gameObject.activeSelf) continue;
            var box = child.GetComponent<Box>();
            if (box == null) continue;
            if (holding && Same(box, cur)) continue;
            if (!holding && box.Pointer == previous) continue;

            var b = Bounds(box, out var s);
            bool lift = haveBase;
            Vector3 pos = lift
                ? new Vector3(cx - b.center.x * s.x, top - (b.center.y - b.extents.y) * s.y + 0.005f, cz - b.center.z * s.z)
                : Vector3.zero; // lowest remaining box goes where the hands are
            if (lift) Raised.Add(box.Pointer); else Raised.Remove(box.Pointer);
            MoveTo(child, pos, Duration(clerk));
            Extent(box, pos, out top, out cx, out cz);
            haveBase = true;
        }
    }

    private static void MoveTo(Transform t, Vector3 localPos, float seconds)
    {
        t.DOKill(false);
        t.DOLocalMove(localPos, seconds, false);
        t.DOLocalRotate(Vector3.zero, seconds, RotateMode.Fast);
    }

    /// <summary>The game's carry tween time for this clerk (0.3 s, shortened by the employee's speed boost).</summary>
    private static float Duration(ClerkT clerk)
    {
        try { return LevelableEmployeeExtensions.BoostScaledDuration(new ILevelableEmployee(clerk.Pointer), StackTweenTime); }
        catch (Exception) { return StackTweenTime; }
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
            if (Same(e.Clerk, clerk)) return true;
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
                // A fetch-only run that is still walking keeps its reservation; its own Exit clears it.
                if (!IsFetching(clerk)) ReleaseReserved(clerk, plan);
                var stash = Stash(clerk, plan);
                var cur = Current(clerk);
                if (stash != null)
                {
                    if (cur == null) HandOver(clerk, stash);
                    else DropStash(clerk, stash, cur);
                }
                else if (plan.Phase == Phase.FetchSecond && cur != null && IsFetching(clerk))
                {
                    // On its way to the other box with one in hand: once the full sequence is restored this
                    // run would take the other box and leave this one stuck in the hands, so put it down now.
                    _dropping = true;
                    try { clerk.DropBoxToGround(); } finally { _dropping = false; }
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"[DoubleCarry] releasing a second box failed: {e.Message}");
            }
        }
        var stripped = new List<SupermarketSimulator.Sequence.Sequence>();
        foreach (var e in Stripped.Values) stripped.Add(e.Seq);
        foreach (var seq in stripped)
        {
            try { if (seq != null) RestoreTasks(seq); }
            catch (Exception e) { Plugin.Logger.LogWarning($"[DoubleCarry] restoring a restock sequence failed: {e.Message}"); }
        }
    }

    internal static void ResetState()
    {
        Plans.Clear();
        Stripped.Clear();
        Raised.Clear();
        LoggedReserved.Clear();
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
        try { DoubleCarry.RestoreTasks(__instance.m_Sequence); }
        catch (Exception e) { Plugin.Logger.LogError($"[DoubleCarry] restock exit failed: {e}"); }
    }
}

[HarmonyPatch(typeof(RackingState), nameof(RackingState.Enter))]
internal static class RackingEnterPatch
{
    private static void Prefix(RackingState __instance)
    {
        try { DoubleCarry.BeforeRackingEnter(__instance); }
        catch (Exception e) { Plugin.Logger.LogError($"[DoubleCarry] racking enter failed: {e}"); }
    }
}

[HarmonyPatch(typeof(RackingState), nameof(RackingState.Exit))]
internal static class RackingExitPatch
{
    private static void Postfix(RackingState __instance)
    {
        try { DoubleCarry.RestoreTasks(__instance.m_Sequence); }
        catch (Exception e) { Plugin.Logger.LogError($"[DoubleCarry] racking exit failed: {e}"); }
    }
}

[HarmonyPatch(typeof(RestockDisplay), nameof(RestockDisplay.OnBegin))]
internal static class RestockDisplayBeginPatch
{
    private static void Prefix(RestockDisplay __instance)
    {
        try { DoubleCarry.BeforeRestockDisplay(__instance); }
        catch (Exception e) { Plugin.Logger.LogError($"[DoubleCarry] putting the second box down failed: {e}"); }
    }
}

[HarmonyPatch(typeof(ClerkT), nameof(ClerkT.CarryBox_Internal))]
internal static class CarryBoxPatch
{
    private static void Prefix(ClerkT __instance, out IntPtr __state)
    {
        __state = IntPtr.Zero;
        try { var cur = __instance.m_CurrentBox; if (cur != null) __state = cur.Pointer; }
        catch (Exception) { }
    }

    private static void Postfix(ClerkT __instance, IntPtr __state)
    {
        try { DoubleCarry.ArrangeStack(__instance, __state); }
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

/// <summary>
/// Every restocker street search goes through StorageStreet.GetBoxesFromStreet (CheckStreetForRequiredProduct and
/// both street branches of PerformSearch call it); a clerk's set-down box is removed from the result while its
/// owner is coming back for it.
/// </summary>
[HarmonyPatch(typeof(StorageStreet), nameof(StorageStreet.GetBoxesFromStreet))]
internal static class StreetBoxesPatch
{
    private static void Postfix(ref Il2CppSystem.Collections.Generic.List<Box> __result)
    {
        try { DoubleCarry.HideReservedBoxes(__result); }
        catch (Exception e) { Plugin.Logger.LogError($"[DoubleCarry] hiding the reserved box failed: {e}"); }
    }
}

/// <summary>Sequence.Tick asks the running task every frame; MoveTo only ever aborts on an invalid path.</summary>
[HarmonyPatch(typeof(MoveTo), nameof(MoveTo.ShouldAbort))]
internal static class MoveToAbortPatch
{
    private static void Postfix(MoveTo __instance, ref SequenceTask.AbortStatus __result)
    {
        if (__result != SequenceTask.AbortStatus.Continue) return;
        try
        {
            if (DoubleCarry.ShouldStopWalkingBack(__instance)) __result = SequenceTask.AbortStatus.AbortWithFailure;
        }
        catch (Exception e) { Plugin.Logger.LogError($"[DoubleCarry] walk-back check failed: {e}"); }
    }
}
