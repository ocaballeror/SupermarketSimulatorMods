using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using DG.Tweening;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Lean.Pool;
using Photon.Pun;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SupermarketFixes.Patches;

/// <summary>
/// Bug #1: moving a rack leaves some of its boxes behind as floating ghosts / duplicates,
/// mostly on multiplayer guests.
///
/// How racked boxes render (from native analysis of GameAssembly.dll):
///  - A racked Box hides its mesh and registers a frozen snapshot of its world matrix
///    (Box.m_StaticBoxRenderable) with InstancedRenderer sub-renderer #2: Box.ToggleInstanced(true).
///  - Rack.StartPlacingMode unregisters every box in slot.m_Boxes; Rack.Place re-registers them
///    at the new spot. A box that is registered but NOT in its slot's m_Boxes is never unregistered,
///    so its snapshot stays at the old spot forever: a ghost with no collider.
///
/// Causes found:
///  A) Guest-side rack rebuilds. The host's clerk AI (RackBox.MergeBox) broadcasts
///     NetworkRack.RackDataUpdate, and joining guests receive ReceiveRackData_RPC; both make the
///     guest run Rack.set_Data -> Rack.Initialize -> RackSlot.Initialize -> ClearLocalBoxes.
///     ClearLocalBoxes skips networked boxes (Box.NetworkBox != null) but then clears m_Boxes anyway,
///     and Initialize spawns a local duplicate for every box record. The networked boxes are left
///     alive, still registered, owned by no slot -> ghosts/duplicates once the rack is moved.
///     (RackSlot.ENABLE_DEMATERIALIZE_ON_PLACE is a const false, so racked boxes stay networked.)
///  B) Delayed callbacks that call ToggleInstanced(true) without checking that the box is still in
///     a slot or that its rack isn't being carried: RackSlot.AddBox's tween OnComplete, and the
///     NetworkRestocker / NetworkClerk / NetworkBaker PlaceBox delay coroutines (run on guests).
///
///  C) Guests joining mid-game (confirmed by the user's guest log 2026-11-14: F8 "DUPLICATE uid=5/97",
///     a visible networked box with no parent at exactly the position of a local in-slot copy).
///     Boxes put on a rack during the session stay networked on the host (dematerialising is off).
///     A guest that joins later receives them through NetworkBox.OnPhotonInstantiate, which never
///     re-racks them (it only makes them kinematic, so they hang in the air), and ReceiveRackData spawns
///     a local copy for every record as well. Moving the rack carries the copy and leaves the networked
///     box floating, still pickable. Fix: a guest-only sweep (Tick, for 60 s after the store loads,
///     extended after each adoption) adopts each such box into the slot in place of its copy, matched
///     by UID + ProductID + position. A copy that exists but is further than 0.3 m is logged.
///  A') A networked box the rebuild released (host data didn't list it yet) stays a child of the slot,
///     outside m_Boxes. The RackSlot.Initialize prefix also captures such children, so a later rebuild
///     that lists it adopts it instead of spawning a copy next to it.
///  D) Picking a racked networked box up directly (clicking the box, not the slot) goes through
///     Box.InstantInteract / NetworkBox.AskAvailability -> PickUP_RPC (to all) -> Box.PickUp, which never
///     calls RackSlot.RemoveBox (only TakeBoxFromRack / RemoveLocalRackedBoxByUID / Dematerialize / clerk
///     pickups do). The slot keeps the box in m_Boxes and its record in RackedBoxDatas: joining guests
///     then spawn a ghost copy there, and a reload of the save spawns a real duplicate. Seen in the host's
///     Player.log: a guest grabbed racked uid=5 ("racked=True parent=Rack Slot") at t=1025, and the next
///     joining guest had a copy of uid=5 on that rack and on the rack it was moved to. Usually triggered
///     by C (the guest grabs the floating networked box, which is still racked on the host).
///     Fix, host: a NetworkBox.PickUp_Broadcast prefix sends the vanilla NetworkRack.RemoveLocalRackedBox
///     RPC first (the order vanilla TakeAndMaterializeBox uses), so every client, modded or not, runs
///     RemoveLocalRackedBoxByUID: the host and up-to-date guests unrack the networked box, and late-joined
///     guests despawn their local copy. Only sent when the UID is unique in that slot.
///     Fallback, every client: a Box.PickUp prefix calls RackSlot.RemoveBox if the box is still in its
///     slot (host without the mod, offline). On a guest, a loose networked box picked up next to its rack
///     copy has that copy removed.
///     RemoveBox drops every record with the box's UID (DisplayClass76_0.b__1 compares BoxData.m_UID), and
///     clears the slot (RackSlotData.Clear: records + BoxID = -1, RackManager.RemoveRackSlot) once no record
///     is left. When another box in the slot shares the UID, the fallback (RemoveFromSlot) re-adds the records
///     of the boxes still in m_Boxes and undoes a wrongful clear (BoxID, AddRackSlot, label count). The host
///     still skips its broadcast then (clients remove the first UID match); unmodded guests keep vanilla.
///     Vanilla never re-enables network transform sync on this path (only TakeBoxFromRack_RPC does), so the
///     fallback does it, as TakeBoxFromRack_RPC would, on every client running the mod.
///
///  E) Opt-in, host: take networked boxes off racks altogether. The game has a complete host-driven path
///     that unmodded guests handle: NetworkRack.DematerializeBox_Broadcast(slot, uid) (RpcTarget.All) ->
///     RackSlot.DematerializeNetworkedBox(uid) on every client: first box in m_Boxes with that UID; if it
///     is networked and not m_IsBoxOccupied -> RemoveBox, InventoryManager.RemoveBox, DetachDataBeforeDespawn,
///     master PhotonNetwork.Destroy, SpawnLocalRackedBox(data). On a client whose slot holds a local copy
///     instead (late joiner, or a guest after cause A) it does nothing locally, and the master's Destroy
///     removes the loose/orphaned networked copy (Box.OnDisable unregisters its instanced snapshot,
///     Box.OnDestroy removes the instance). The game's own caller (AddBox b__2, ENABLE_DEMATERIALIZE_ON_PLACE)
///     is compiled out. Verified hazard: for the only box of a slot, RemoveBox clears the slot (BoxID = -1)
///     and SpawnLocalRackedBox then finds no BoxSO(-1) and returns null: the box is lost on every client.
///     So the host sweep only dematerializes settled, unclaimed, master-owned boxes whose UID is unique in a
///     slot that keeps other boxes and records. Off by default (DematerializeRackedBoxesOnHost).
///
/// Box UIDs are NOT unique: IDManager.BoxUniqueIdCount restarts every session and guests keep the
/// host's UIDs (one guest scan had uid=5 for products 139 and 157). Always match on UID + ProductID.
/// </summary>
internal static class RackMoveFix
{
    internal static ConfigEntry<bool> AdoptNetworkedBoxes;
    internal static ConfigEntry<bool> GuardInstancing;
    internal static ConfigEntry<bool> FinishTweensBeforePickup;
    internal static ConfigEntry<bool> AdoptOrphansAfterJoin;
    internal static ConfigEntry<bool> UnrackOnDirectPickup;
    internal static ConfigEntry<bool> DematerializeOnHost;

    /// <summary>How far (m) a loose networked box may be from the rack's copy of it to be adopted.
    /// A joining guest receives it at exactly the copy's position.</summary>
    private const float AdoptMaxDistance = 0.3f;
    private const float SweepInterval = 2f;

    internal static void Bind(ConfigFile cfg)
    {
        AdoptOrphansAfterJoin = cfg.Bind("RackMove", "AdoptRackedBoxesAfterJoining", true,
            "Multiplayer guest: after joining a game in progress, put boxes that the host has on racks back " +
            "into their rack, instead of leaving a loose copy floating where it was when the rack moves.");
        UnrackOnDirectPickup = cfg.Bind("RackMove", "RemoveFromRackOnDirectPickup", true,
            "When a box is picked up straight off a rack (by clicking the box itself), remove it from the rack " +
            "properly. Otherwise the rack keeps a record of it, which shows up as a ghost box for players who " +
            "join later and as a duplicate box after the save is loaded. Most useful on the host.");
        DematerializeOnHost = cfg.Bind("RackMove", "DematerializeRackedBoxesOnHost", false,
            "Host only, experimental. Some seconds after a box is put on a rack, turn it into an ordinary racked box " +
            "(like the ones loaded from a save) for every player, using the game's own network message. Guests " +
            "without this mod then no longer get floating copies of it when they join or when employees restock " +
            "racks. A box that is alone in its rack slot is left as it is. Off by default: the game ships with " +
            "this path switched off.");
        AdoptNetworkedBoxes = cfg.Bind("RackMove", "AdoptNetworkedBoxesOnRebuild", true,
            "When a rack slot is rebuilt from network data (multiplayer guest), keep its networked boxes " +
            "in the slot instead of orphaning them and spawning local duplicates. Main fix for ghost boxes.");
        GuardInstancing = cfg.Bind("RackMove", "GuardDelayedInstancing", true,
            "Ignore delayed 'render this box as racked' calls for boxes that are no longer in a rack slot, " +
            "or whose rack is currently being carried.");
        FinishTweensBeforePickup = cfg.Bind("RackMove", "FinishBoxTweensBeforePickup", true,
            "Before a rack is picked up, complete any in-flight box animations on it.");
        RackDiagnostics.Bind(cfg);
    }

    /// <summary>Clear transient state; called whenever the patches are applied or removed.</summary>
    internal static void ResetState()
    {
        s_inPlace = false;
        s_rackLeftovers = null;
        s_nextSweep = 0f;
        s_sweepUntil = -1f;
        s_reported.Clear();
        s_nextHostSweep = 0f;
        s_settledSince.Clear();
        s_rackPickupAt.Clear();
        s_hostReported.Clear();
    }

    // ------------------------------------------------------------------ helpers

    internal static IEnumerable<RackSlot> Slots(Rack rack)
    {
        var arr = rack?.m_RackSlots;
        if (arr == null) yield break;
        for (int i = 0; i < arr.Length; i++)
            if (arr[i] != null) yield return arr[i];
    }

    internal static bool ListContains(Il2CppSystem.Collections.Generic.List<Box> list, Box box)
    {
        if (list == null) return false;
        for (int i = 0; i < list.Count; i++)
        {
            var b = list[i];
            if (b != null && b.Pointer == box.Pointer) return true;
        }
        return false;
    }

    internal static bool IsRegistered(Box box)
    {
        var r = box.m_StaticBoxRenderable;
        return r != null && r.RegisteredIndex >= 0;
    }

    /// <summary>Put a box into the state Rack.StartPlacingMode leaves carried boxes in.</summary>
    private static void SetCarriedRenderState(Box box)
    {
        box.ToggleInstanced(false);
        if (box.m_StaticRenderer != null) box.m_StaticRenderer.forceRenderingOff = true;
    }

    // ------------------------------------------------------------------ A) guest rebuild

    /// <summary>Networked boxes not matched by their own slot; resolved rack-wide after Rack.Initialize.
    /// Non-null only while Rack.Initialize is running.</summary>
    private static List<(Box box, RackSlot origin)> s_rackLeftovers;

    [HarmonyPatch(typeof(Rack), nameof(Rack.Initialize))]
    private static class RackInitializePatch
    {
        private static void Prefix()
        {
            s_rackLeftovers = AdoptNetworkedBoxes.Value ? new List<(Box, RackSlot)>() : null;
        }

        private static void Finalizer(Rack __instance)
        {
            var leftovers = s_rackLeftovers;
            s_rackLeftovers = null;
            try
            {
                if (leftovers != null && leftovers.Count > 0) ResolveRackWide(__instance, leftovers);
                // A rebuild while the rack is carried spawns boxes with colliders on; StartPlacingMode
                // had turned them off. Place turns them back on.
                if (AdoptNetworkedBoxes.Value && __instance != null && __instance.IsPlacingMode)
                    foreach (var slot in Slots(__instance)) slot.EnableBoxColliders = false;
            }
            catch (Exception e) { Plugin.Logger.LogError($"[RackMove] Rack-wide adoption failed: {e}"); }
        }
    }

    [HarmonyPatch(typeof(RackSlot), nameof(RackSlot.Initialize), new Type[0])]
    private static class RackSlotInitializePatch
    {
        /// <summary>__state: networked boxes to keep, with the slot whose highlight lists them (null: none).</summary>
        private static void Prefix(RackSlot __instance, out List<(Box box, RackSlot origin)> __state)
        {
            __state = null;
            try
            {
                if (!AdoptNetworkedBoxes.Value) return;
                var boxes = __instance.m_Boxes;
                for (int i = 0; boxes != null && i < boxes.Count; i++)
                {
                    var b = boxes[i];
                    if (b != null && b.NetworkBox != null)
                        (__state ??= new()).Add((b, __instance));
                }
                // Networked boxes an earlier rebuild released (Release): still children of the slot but not in
                // m_Boxes. If this rebuild lists them again, adopt them instead of spawning a copy beside them.
                var t = __instance.transform;
                for (int i = 0; i < t.childCount; i++)
                {
                    var b = t.GetChild(i).GetComponent<Box>();
                    if (b == null || b.NetworkBox == null || ListContains(boxes, b)) continue;
                    if (IsClaimed(b) || DOTween.IsTweening(b.transform)) continue; // being handled by someone
                    (__state ??= new()).Add((b, null));
                }
            }
            catch (Exception e)
            {
                __state = null;
                Plugin.Logger.LogError($"[RackMove] RackSlot.Initialize prefix failed: {e}");
            }
        }

        private static void Postfix(RackSlot __instance, List<(Box box, RackSlot origin)> __state)
        {
            if (__state == null) return;
            try { AdoptIntoSlot(__instance, __state); }
            catch (Exception e) { Plugin.Logger.LogError($"[RackMove] Adopting networked boxes failed: {e}"); }
        }
    }

    /// <summary>Same box record: UIDs repeat across boxes, so the product must match too.</summary>
    internal static bool SameRecord(Box a, Box b)
    {
        var da = a.Data;
        var db = b.Data;
        return da != null && db != null && da.UID == db.UID && da.ProductID == db.ProductID;
    }

    private static int FindLocalDuplicate(RackSlot slot, Box nb)
    {
        var boxes = slot.m_Boxes;
        for (int i = 0; boxes != null && i < boxes.Count; i++)
        {
            var b = boxes[i];
            if (b != null && b.NetworkBox == null && SameRecord(b, nb)) return i;
        }
        return -1;
    }

    private static void AdoptIntoSlot(RackSlot slot, List<(Box box, RackSlot origin)> networked)
    {
        int adopted = 0, released = 0, deferred = 0, strays = 0;
        foreach (var (nb, origin) in networked)
        {
            if (nb == null || nb.NetworkBox == null || ListContains(slot.m_Boxes, nb)) continue;
            // Someone holds it or is picking it up: never pull it back into the rack.
            if (IsClaimed(nb)) { Release(origin, nb); released++; continue; }
            int idx = FindLocalDuplicate(slot, nb);
            if (idx >= 0)
            {
                Swap(slot, idx, nb, origin);
                adopted++;
                if (origin == null) strays++;
            }
            else if (s_rackLeftovers != null) { s_rackLeftovers.Add((nb, origin)); deferred++; }
            else { Release(origin, nb); released++; }
        }
        if (adopted + released + deferred > 0)
            Plugin.Logger.LogInfo($"[RackMove] Slot rebuild on rack {slot.OwnRack?.ID}: kept {adopted} networked box(es) " +
                                  $"in place of local duplicates ({strays} released by an earlier rebuild), released {released}, " +
                                  $"checking {deferred} rack-wide");
    }

    /// <summary>A box the host moved to another slot of the same rack: match against every slot.</summary>
    private static void ResolveRackWide(Rack rack, List<(Box box, RackSlot origin)> leftovers)
    {
        int adopted = 0, released = 0;
        foreach (var (nb, origin) in leftovers)
        {
            if (nb == null || nb.NetworkBox == null) continue;
            if (IsClaimed(nb)) { Release(origin, nb); released++; continue; }
            bool done = false;
            foreach (var slot in Slots(rack))
            {
                if (ListContains(slot.m_Boxes, nb)) { done = true; break; }
                int idx = FindLocalDuplicate(slot, nb);
                if (idx < 0) continue;
                Swap(slot, idx, nb, origin);
                adopted++;
                done = true;
                break;
            }
            if (!done) { Release(origin, nb); released++; }
        }
        Plugin.Logger.LogInfo($"[RackMove] Rack {rack?.ID} rebuild: kept {adopted} networked box(es) moved between slots, released {released}");
    }

    /// <summary>Replace the local duplicate at <paramref name="dupIdx"/> with the networked box.
    /// <paramref name="origin"/> is the slot whose highlight still lists nb, or null if none does.</summary>
    private static void Swap(RackSlot slot, int dupIdx, Box nb, RackSlot origin)
    {
        var boxes = slot.m_Boxes;
        var dup = boxes[dupIdx];
        var nt = nb.transform;
        var dt = dup.transform;
        if (nt.parent == null || nt.parent.Pointer != slot.transform.Pointer)
            nt.SetParent(slot.transform, false);
        nt.localPosition = dt.localPosition;
        nt.localRotation = dt.localRotation;
        nt.localScale = dt.localScale;

        boxes[dupIdx] = nb;

        // Dispose of the duplicate exactly like RackSlot.ClearLocalBoxes does.
        slot.m_Highlightable?.AddOrRemoveRenderer(dup.RenderersToHighlight, false);
        dup.KillAllInstancingAnimations();
        dup.ToggleInstanced(false);
        dup.DetachDataBeforeDespawn();
        LeanPool.Despawn(dup);

        // nb's highlight renderers are still registered with its original slot (ClearLocalBoxes skipped it),
        // or with no slot at all (a box received when joining).
        if (origin == null || origin.Pointer != slot.Pointer)
        {
            origin?.m_Highlightable?.AddOrRemoveRenderer(nb.RenderersToHighlight, false);
            slot.m_Highlightable?.AddOrRemoveRenderer(nb.RenderersToHighlight, true);
        }

        // Re-snapshot the networked box at its (possibly new) slot position.
        bool carrying = slot.OwnRack != null && slot.OwnRack.IsPlacingMode;
        nb.KillAllInstancingAnimations();
        if (!nb.Racked) nb.Racked = true;
        // Racked boxes don't follow network transform updates (vanilla PlaceBoxToRack_RPC does the same).
        nb.NetworkBox?.DisableNetworkTransformSync(false);
        nb.FrezeeBox();
        if (nb.m_MainCollider != null) nb.m_MainCollider.enabled = !carrying;
        if (carrying) SetCarriedRenderState(nb);
        else nb.ToggleInstanced(true);
    }

    /// <summary>Host data no longer has this box on the rack: stop drawing its stale snapshot.
    /// The box is a networked object; its owner keeps controlling where it goes. If it stays a child of the
    /// slot, the next rebuild of the slot looks at it again (RackSlotInitializePatch).</summary>
    private static void Release(RackSlot origin, Box nb)
    {
        origin?.m_Highlightable?.AddOrRemoveRenderer(nb.RenderersToHighlight, false);
        nb.KillAllInstancingAnimations();
        nb.ToggleInstanced(false);
    }

    // ------------------------------------------------------------------ C) guest joined mid-game

    /// <summary>Sweep for this long once the guest's store has loaded...</summary>
    private const float SweepWindow = 60f;
    /// <summary>...and keep going this long after the last adoption.</summary>
    private const float SweepExtension = 20f;
    /// <summary>How often a guest checks whether racks exist yet, while none do.</summary>
    private const float NoRackProbeInterval = 10f;

    private static float s_nextSweep;
    /// <summary>End of the sweep window; -1 = not started for the current room.</summary>
    private static float s_sweepUntil = -1f;
    /// <summary>Boxes whose rejection was already logged in this window, so each is reported once.</summary>
    private static readonly HashSet<IntPtr> s_reported = new();

    /// <summary>A local (non-networked) copy of a box record, in a slot of a rack that isn't being carried.</summary>
    private sealed class RackCopy
    {
        public RackSlot Slot;
        public Box Box;
    }

    /// <summary>Called every frame from DiagnosticsBehaviour.</summary>
    internal static void Tick(float now)
    {
        TickHost(now);
        if (now < s_nextSweep) return;
        s_nextSweep = now + SweepInterval;
        if (!PhotonNetwork.InRoom || PhotonNetwork.IsMasterClient) { s_sweepUntil = -1f; return; }
        if (!AdoptOrphansAfterJoin.Value) return;

        if (s_sweepUntil < 0f)
        {
            // Start when the store has loaded (racks exist), not when the room was joined. A store without
            // racks needs no sweep, so probe for racks less often.
            if (Object.FindObjectsOfType(Il2CppType.Of<Rack>()).Length == 0) { s_nextSweep = now + NoRackProbeInterval; return; }
            s_sweepUntil = now + SweepWindow;
            s_reported.Clear();
            if (RackDiagnostics.Enabled.Value)
                Plugin.Logger.LogInfo($"[RackMove] Joined as a guest: checking for loose racked boxes for {SweepWindow:F0} s");
        }
        if (now > s_sweepUntil) return;
        if (AdoptOrphans() > 0) s_sweepUntil = Math.Max(s_sweepUntil, now + SweepExtension);
    }

    /// <summary>(UID, ProductID) -> local rack copies. Also collects every box listed by any slot into
    /// <paramref name="owned"/> when given.</summary>
    private static Dictionary<(int, int), List<RackCopy>> IndexLocalCopies(HashSet<IntPtr> owned)
    {
        var index = new Dictionary<(int, int), List<RackCopy>>();
        foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<Rack>()))
        {
            var rack = o.TryCast<Rack>();
            if (rack == null) continue;
            bool carried = rack.IsPlacingMode; // a carried rack's copies aren't where the box was received
            foreach (var slot in Slots(rack))
            {
                var boxes = slot.m_Boxes;
                for (int i = 0; boxes != null && i < boxes.Count; i++)
                {
                    var b = boxes[i];
                    if (b == null) continue;
                    owned?.Add(b.Pointer);
                    var d = b.Data;
                    if (carried || d == null || b.NetworkBox != null) continue;
                    var key = (d.UID, d.ProductID);
                    if (!index.TryGetValue(key, out var list)) index[key] = list = new List<RackCopy>();
                    list.Add(new RackCopy { Slot = slot, Box = b });
                }
            }
        }
        return index;
    }

    /// <summary>The single rack copy of <paramref name="nb"/> within AdoptMaxDistance; null if there is none
    /// (<paramref name="found"/> = 0) or more than one. <paramref name="nearest"/> is the distance to the
    /// closest copy with the same UID + ProductID at any distance (infinity if there is none).</summary>
    private static RackCopy FindCopy(Dictionary<(int, int), List<RackCopy>> index, Box nb, out int found, out float nearest)
    {
        found = 0;
        nearest = float.PositiveInfinity;
        var d = nb.Data;
        if (d == null || !index.TryGetValue((d.UID, d.ProductID), out var list)) return null;
        var pos = nb.transform.position;
        RackCopy match = null;
        foreach (var c in list)
        {
            float dist = Vector3.Distance(c.Box.transform.position, pos);
            if (dist < nearest) nearest = dist;
            if (dist > AdoptMaxDistance) continue;
            found++;
            match = c;
        }
        return found == 1 ? match : null;
    }

    private static int IndexOf(Il2CppSystem.Collections.Generic.List<Box> list, Box box)
    {
        for (int i = 0; list != null && i < list.Count; i++)
        {
            var b = list[i];
            if (b != null && b.Pointer == box.Pointer) return i;
        }
        return -1;
    }

    /// <summary>Returns how many boxes were adopted.</summary>
    private static int AdoptOrphans()
    {
        // Loose = networked, no parent, unclaimed. Usually a handful (street deliveries, boxes on the floor).
        var perRecord = new Dictionary<(int, int), int>();
        List<Box> loose = null;
        foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<NetworkBox>()))
        {
            var b = o.TryCast<NetworkBox>()?.Box;
            var d = b?.Data;
            if (d == null || d.UID <= 0) continue;
            var key = (d.UID, d.ProductID);
            perRecord[key] = perRecord.TryGetValue(key, out var n) ? n + 1 : 1;
            if (b.transform.parent == null && b.gameObject.activeInHierarchy && !IsClaimed(b))
                (loose ??= new List<Box>()).Add(b);
        }
        if (loose == null) return 0;

        var owned = new HashSet<IntPtr>();
        var index = IndexLocalCopies(owned);
        int adopted = 0;
        foreach (var nb in loose)
        {
            if (nb == null || owned.Contains(nb.Pointer)) continue;
            var d = nb.Data;
            var key = (d.UID, d.ProductID);
            if (perRecord[key] > 1) { Reject(nb, "another live networked box has the same UID and product"); continue; }

            var copy = FindCopy(index, nb, out int found, out float nearest);
            if (found == 0)
            {
                // An ordinary loose box, unless a rack has a copy of it further away (received late, or that
                // rack moved on the host): log that, so a failed match can be told apart in a test.
                if (!float.IsPositiveInfinity(nearest))
                    Reject(nb, $"the rack copy with the same UID and product is {nearest:F2} m away (limit {AdoptMaxDistance} m)");
                continue;
            }
            if (copy == null) { Reject(nb, $"{found} rack copies match it"); continue; }
            int idx = IndexOf(copy.Slot.m_Boxes, copy.Box);
            if (idx < 0) continue;

            try
            {
                Swap(copy.Slot, idx, nb, null);
                index[key].Remove(copy); // despawned now
                adopted++;
                s_reported.Remove(nb.Pointer);
                Plugin.Logger.LogInfo($"[RackMove] Put networked box uid={d.UID} product={d.ProductID} back into rack " +
                                      $"{copy.Slot.OwnRack?.ID} (it was received loose when joining; replaced the rack's copy)");
            }
            catch (Exception e) { Plugin.Logger.LogError($"[RackMove] Adopting box uid={d.UID} after joining failed: {e}"); }
        }
        return adopted;
    }

    /// <summary>Held, being picked up, or targeted by an employee.</summary>
    private static bool IsClaimed(Box b)
    {
        if (b.m_IsBoxOccupied || b.m_OccupyOwner != null) return true;
        var net = b.NetworkBox;
        return net != null && (net.IsNetworkOccupied || net.IsClaimedByNpc());
    }

    private static void Reject(Box nb, string why)
    {
        if (!RackDiagnostics.Enabled.Value || !s_reported.Add(nb.Pointer)) return;
        Plugin.Logger.LogInfo($"[RackMove] Not adopting loose networked box ({why}): {RackDiagnostics.Describe(nb)}");
    }

    // ------------------------------------------------------------------ D) box picked straight off a rack

    /// <summary>Host: before the pickup goes out, tell every client (with or without the mod) to drop the rack's
    /// entry for the box. This is the order the vanilla take-from-rack uses (RemoveLocalRackedBox_Broadcast, then
    /// the pickup). Guests remove their local copy of it too, which matters for guests who joined late.</summary>
    [HarmonyPatch(typeof(NetworkBox), nameof(NetworkBox.PickUp_Broadcast))]
    private static class PickUpBroadcastPatch
    {
        private static void Prefix(NetworkBox __instance)
        {
            if (!UnrackOnDirectPickup.Value || !PhotonNetwork.IsMasterClient) return;
            try
            {
                var box = __instance?.Box;
                var d = box?.Data;
                if (d == null) return;
                var slot = box.GetComponentInParent<RackSlot>(true);
                if (slot == null || !ListContains(slot.m_Boxes, box)) return;
                // Clients remove the first box in the slot with this UID, so only send when that is this box.
                if (CountUid(slot.m_Boxes, d.UID) != 1)
                {
                    Plugin.Logger.LogWarning($"[RackMove] Box uid={d.UID} picked up off rack {slot.OwnRack?.ID} shares its UID " +
                                             "with another box in the slot; removing it on this machine only");
                    return;
                }
                var net = slot.OwnRack?.OwnNetworkRack;
                if (net == null) return;
                if (RackDiagnostics.Enabled.Value)
                    Plugin.Logger.LogInfo($"[RackMove] Box uid={d.UID} product={d.ProductID} picked up straight off rack " +
                                          $"{slot.OwnRack.ID}: removing it from the rack for everyone");
                net.RemoveLocalRackedBox_Broadcast(slot.GetIndexFromRack(), d.UID); // runs here at once (RpcTarget.All)
            }
            catch (Exception e) { Plugin.Logger.LogError($"[RackMove] Broadcasting removal of a picked-up box failed: {e}"); }
        }
    }

    private static int CountUid(Il2CppSystem.Collections.Generic.List<Box> list, int uid)
    {
        int n = 0;
        for (int i = 0; list != null && i < list.Count; i++)
            if (list[i]?.Data != null && list[i].Data.UID == uid) n++;
        return n;
    }

    [HarmonyPatch(typeof(Box), nameof(Box.PickUp), new Type[0])]
    private static class PickUpPatch
    {
        private static void Prefix(Box __instance) => UnrackBeforePickup(__instance);
    }

    [HarmonyPatch(typeof(Box), nameof(Box.PickUp), new[] { typeof(PlayerInstance) })]
    private static class PickUpPlayerPatch
    {
        private static void Prefix(Box __instance) => UnrackBeforePickup(__instance);
    }

    /// <summary>Every client: fallback when the host didn't broadcast the removal (host without the mod,
    /// offline, or a UID shared within the slot).</summary>
    private static void UnrackBeforePickup(Box box)
    {
        if (!UnrackOnDirectPickup.Value || box == null) return;
        try
        {
            var d = box.Data;
            var slot = box.GetComponentInParent<RackSlot>(true);
            // Vanilla re-enables transform sync only when a box is taken through the slot (TakeBoxFromRack_RPC).
            // A box grabbed straight off a rack (removed here, or already by the host's RemoveLocalRackedBox)
            // would otherwise keep sync off, so other players could see it hang where it was after a drop.
            if (slot != null && box.NetworkBox != null && PhotonNetwork.InRoom)
                box.NetworkBox.DisableNetworkTransformSync(true); // true = sync on (the name is inverted)
            if (slot != null && ListContains(slot.m_Boxes, box))
            {
                if (RackDiagnostics.Enabled.Value)
                    Plugin.Logger.LogInfo($"[RackMove] Box uid={d?.UID} product={d?.ProductID} picked up straight off rack " +
                                          $"{slot.OwnRack?.ID}: removing it from the rack slot first");
                RemoveFromSlot(slot, box);
                return;
            }

            // A guest grabbing a networked box it received loose when joining, before the sweep adopted it:
            // the rack still has a local copy of it. Remove that copy the way RemoveLocalRackedBoxByUID does.
            if (box.NetworkBox == null || box.transform.parent != null || !PhotonNetwork.InRoom || PhotonNetwork.IsMasterClient)
                return;
            var copy = FindCopy(IndexLocalCopies(null), box, out _, out _);
            if (copy == null) return;
            if (RackDiagnostics.Enabled.Value)
                Plugin.Logger.LogInfo($"[RackMove] Loose box uid={d?.UID} product={d?.ProductID} picked up next to its copy on rack " +
                                      $"{copy.Slot.OwnRack?.ID}: removing the copy");
            RemoveFromSlot(copy.Slot, copy.Box);
            copy.Box.DetachDataBeforeDespawn();
            LeanPool.Despawn(copy.Box);
        }
        catch (Exception e) { Plugin.Logger.LogError($"[RackMove] Removing a picked-up box from its rack failed: {e}"); }
    }

    /// <summary>RackSlot.RemoveBox, minus the damage it does when another box in the slot shares the UID:
    /// it drops that box's record too, and if no record is left it clears the slot (BoxID = -1,
    /// RackManager.RemoveRackSlot) although a box is still in it.</summary>
    private static void RemoveFromSlot(RackSlot slot, Box box)
    {
        var d = box.Data;
        bool shared = d != null && CountUid(slot.m_Boxes, d.UID) > 1;
        var data = slot.m_Data;
        int boxId = data?.BoxID ?? -1;
        slot.RemoveBox(box);
        if (!shared || data == null) return;

        // Put back the records of the boxes still in the slot (same BoxData instances, as UpdateRackedBoxDatas
        // would; done by hand so a null entry or an unrelated record doesn't matter).
        var records = data.RackedBoxDatas;
        int readded = 0;
        for (int i = 0; records != null && slot.m_Boxes != null && i < slot.m_Boxes.Count; i++)
        {
            var bd = slot.m_Boxes[i]?.Data;
            if (bd == null) continue;
            bool listed = false;
            for (int j = 0; j < records.Count && !listed; j++)
                listed = records[j] != null && records[j].Pointer == bd.Pointer;
            if (!listed) { records.Add(bd); readded++; }
        }
        bool restored = false;
        if (slot.m_Boxes != null && slot.m_Boxes.Count > 0 && data.BoxID == -1 && boxId != -1)
        {
            data.BoxID = boxId;
            if (NoktaSingleton<RackManager>.HasInstance)
                NoktaSingleton<RackManager>.Instance.AddRackSlot(data.ProductID, slot);
            restored = true;
        }
        if (slot.m_Label != null) slot.m_Label.ProductCount = data.TotalProductCount;
        Plugin.Logger.LogInfo($"[RackMove] Box uid={d.UID} shared its UID with another box in rack {slot.OwnRack?.ID}: " +
                              $"put back {readded} record(s) of the other box(es) ({records?.Count ?? 0} left)" +
                              (restored ? ", and undid the slot being cleared" : ""));
    }

    // ------------------------------------------------------------------ E) host: dematerialize racked boxes (opt-in)

    /// <summary>A networked box must sit untouched in its slot this long before it is dematerialized.</summary>
    private const float SettleSeconds = 10f;
    /// <summary>After the host accepts a guest's request to carry a rack, leave that rack alone this long:
    /// the guest is already carrying it before the host's copy enters placing mode.</summary>
    private const float RackPickupGrace = 10f;
    /// <summary>Limits the RPC burst in a store with many networked racked boxes.</summary>
    private const int MaxDematerializePerSweep = 8;

    private static float s_nextHostSweep;
    /// <summary>Networked box in a slot of a resting rack -> when it was first seen there.</summary>
    private static readonly Dictionary<IntPtr, float> s_settledSince = new();
    /// <summary>NetworkRack -> when the host last answered a guest's request to carry it.</summary>
    private static readonly Dictionary<IntPtr, float> s_rackPickupAt = new();
    /// <summary>Boxes whose "left networked" reason was already logged.</summary>
    private static readonly HashSet<IntPtr> s_hostReported = new();

    [HarmonyPatch(typeof(NetworkRack), nameof(NetworkRack.StartRackPlacingMode_Response))]
    private static class RackPickupResponsePatch
    {
        private static void Prefix(NetworkRack __instance)
        {
            try { if (__instance != null) s_rackPickupAt[__instance.Pointer] = Time.unscaledTime; }
            catch (Exception e) { Plugin.Logger.LogError($"[RackMove] Recording a rack pickup request failed: {e}"); }
        }
    }

    private static void TickHost(float now)
    {
        if (now < s_nextHostSweep) return;
        s_nextHostSweep = now + SweepInterval;
        if (!DematerializeOnHost.Value || !PhotonNetwork.InRoom || !PhotonNetwork.IsMasterClient)
        {
            s_settledSince.Clear();
            return;
        }

        var seen = new HashSet<IntPtr>();
        int done = 0;
        foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<Rack>()))
        {
            var rack = o.TryCast<Rack>();
            if (rack == null || rack.IsPlacingMode) continue; // carried: settle again once it is placed
            var net = rack.OwnNetworkRack;
            if (net == null) continue;
            if (s_rackPickupAt.TryGetValue(net.Pointer, out var askedAt) && now - askedAt < RackPickupGrace) continue;

            foreach (var slot in Slots(rack))
            {
                if (slot.m_ColliderEnabler != null && slot.m_ColliderEnabler.IsActive()) continue; // a box just went in
                var boxes = slot.m_Boxes;
                for (int i = 0; boxes != null && i < boxes.Count; i++)
                {
                    var b = boxes[i];
                    if (b == null || b.NetworkBox == null) continue;
                    seen.Add(b.Pointer);
                    if (!s_settledSince.TryGetValue(b.Pointer, out var since)) { s_settledSince[b.Pointer] = now; continue; }
                    if (now - since < SettleSeconds || done >= MaxDematerializePerSweep) continue;

                    var why = WhyNotDematerialize(slot, b, out bool lasting);
                    if (why != null)
                    {
                        if (!lasting) s_settledSince[b.Pointer] = now; // busy: wait for it to settle again
                        else if (RackDiagnostics.Enabled.Value && s_hostReported.Add(b.Pointer))
                            Plugin.Logger.LogInfo($"[RackMove] Host: leaving racked box networked ({why}): {RackDiagnostics.Describe(b)}");
                        continue;
                    }

                    var d = b.Data;
                    if (RackDiagnostics.Enabled.Value)
                        Plugin.Logger.LogInfo($"[RackMove] Host: turning networked box uid={d.UID} product={d.ProductID} on rack " +
                                              $"{rack.ID} into an ordinary racked box for everyone");
                    s_settledSince.Remove(b.Pointer);
                    s_hostReported.Remove(b.Pointer);
                    seen.Remove(b.Pointer);
                    done++;
                    // RpcTarget.All: runs here at once (the slot's lists change), then on every guest.
                    net.DematerializeBox_Broadcast(slot.GetIndexFromRack(), d.UID);
                    break; // m_Boxes changed; the slot's other boxes are looked at again next sweep
                }
            }
        }

        if (s_settledSince.Count > seen.Count)
            foreach (var k in new List<IntPtr>(s_settledSince.Keys))
                if (!seen.Contains(k)) s_settledSince.Remove(k);
        if (s_rackPickupAt.Count > 0)
            foreach (var k in new List<IntPtr>(s_rackPickupAt.Keys))
                if (now - s_rackPickupAt[k] >= RackPickupGrace) s_rackPickupAt.Remove(k);
    }

    /// <summary>Null if the game's dematerialize RPC is safe for this box. <paramref name="lasting"/> is false
    /// for a passing state (someone is handling the box), true for one that won't change by itself.</summary>
    private static string WhyNotDematerialize(RackSlot slot, Box b, out bool lasting)
    {
        lasting = false;
        if (!b.gameObject.activeInHierarchy || IsClaimed(b) || DOTween.IsTweening(b.transform) || !b.Racked)
            return "busy";
        lasting = true;
        var d = b.Data;
        if (d == null || d.UID <= 0) return "no box data";
        var view = b.NetworkBox.PhotonView;
        if (view == null || !view.IsMine) return "owned by another player, so the host can't destroy it";
        // Every client takes the first box in the slot with this UID.
        if (CountUid(slot.m_Boxes, d.UID) != 1) return "another box in the slot has the same UID";
        // RemoveBox clears the slot when no record is left, and the box can then no longer be respawned:
        // it would be lost for everyone. Only go ahead if other boxes and records stay in the slot.
        int otherRecords = 0;
        var records = slot.m_Data?.RackedBoxDatas;
        for (int i = 0; records != null && i < records.Count; i++)
            if (records[i] != null && records[i].UID != d.UID) otherRecords++;
        if (slot.m_Boxes.Count < 2 || otherRecords < 1) return "it is the only box in its slot";
        return null;
    }

    // ------------------------------------------------------------------ B) delayed instancing guard

    private static bool s_inPlace;

    [HarmonyPatch(typeof(Box), nameof(Box.ToggleInstanced))]
    private static class ToggleInstancedGuard
    {
        private static bool Prefix(Box __instance, bool value)
        {
            if (!value || !GuardInstancing.Value || s_inPlace) return true;
            try
            {
                var slot = __instance.GetComponentInParent<RackSlot>(true);
                var rack = slot?.OwnRack;
                bool inSlot = slot != null && ListContains(slot.m_Boxes, __instance);
                bool carried = inSlot && rack != null && rack.IsPlacingMode;
                if (inSlot && !carried) return true;

                // Don't leave an old snapshot behind either (ToggleInstanced(false) passes this guard).
                if (carried) SetCarriedRenderState(__instance);
                else if (IsRegistered(__instance)) __instance.ToggleInstanced(false);

                if (RackDiagnostics.Enabled.Value)
                    Plugin.Logger.LogInfo($"[RackMove] Blocked late instancing of box uid={__instance.Data?.UID}: " +
                                          (carried ? "its rack is being carried" : "box is not in a rack slot"));
                return false;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[RackMove] ToggleInstanced guard failed: {e}");
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(Rack), nameof(Rack.Place))]
    private static class PlacePatch
    {
        // Place re-instances boxes while IsPlacingMode is still true; let those calls through.
        private static void Prefix() => s_inPlace = true;
        private static void Finalizer() => s_inPlace = false;
        private static void Postfix(Rack __instance) => RackDiagnostics.AfterPlace(__instance);
    }

    // ------------------------------------------------------------------ pickup

    [HarmonyPatch(typeof(Rack), nameof(Rack.StartPlacingMode), new Type[0])]
    private static class StartPlacingModePatch
    {
        private static void Prefix(Rack __instance) => BeforePickup(__instance);
        private static void Postfix(Rack __instance) => RackDiagnostics.Audit(__instance, "after pickup");
    }

    [HarmonyPatch(typeof(Rack), nameof(Rack.StartPlacingMode), new[] { typeof(string) })]
    private static class StartPlacingModeRemotePatch
    {
        private static void Prefix(Rack __instance) => BeforePickup(__instance);
        private static void Postfix(Rack __instance) => RackDiagnostics.Audit(__instance, "after remote pickup");
    }

    private static void BeforePickup(Rack rack)
    {
        RackDiagnostics.Audit(rack, "before pickup");
        if (!FinishTweensBeforePickup.Value || rack == null) return;
        try
        {
            int completed = 0;
            foreach (var slot in Slots(rack))
            {
                if (slot.m_ColliderEnabler != null && slot.m_ColliderEnabler.IsActive())
                    slot.m_ColliderEnabler.Complete(true);
                var boxes = slot.m_Boxes;
                for (int i = 0; boxes != null && i < boxes.Count; i++)
                    if (boxes[i] != null)
                        completed += DOTween.Complete(boxes[i].transform, true);
            }
            if (completed > 0 && RackDiagnostics.Enabled.Value)
                Plugin.Logger.LogInfo($"[RackMove] Rack {rack.ID}: finished {completed} in-flight box tween(s) before pickup");
        }
        catch (Exception e) { Plugin.Logger.LogError($"[RackMove] Finishing box tweens before pickup failed: {e}"); }
    }
}
