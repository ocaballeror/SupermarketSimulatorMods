using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using DG.Tweening;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace SupermarketFixes.Patches;

/// <summary>
/// Bug #2: after putting a held item down on the floor, an image of it stays in the player's hands
/// (blocking the view) although the player can interact with everything else, until a reload.
///
/// How holding works (native analysis of GameAssembly.dll):
///  - PlayerObjectHolder.HoldObject(item) parents the item directly to the hold point (m_ObjectHolder,
///    via MoveToCarryingPosition), saves the item's layer in m_CurrentObjectsLayer and switches it to
///    "Ignore Raycast", makes the colliders of its active objects triggers, makes the root Rigidbody
///    kinematic, takes the item's IPlacingMode as m_CurrentPlacingMode, and stores the item in
///    m_CurrentObject, OVERWRITING any previous object (and its saved layer) without releasing it.
///  - Dropping (DropObject / placing mode / throw) only ever releases m_CurrentObject: unparent, restore
///    m_CurrentObjectsLayer, m_CurrentPlacingMode.PlacingMode(false, null) (hologram off, triggers
///    cleared), colliders solid, physics on (kinematic stays on for a networked object owned by someone
///    else: NetworkRigidbodySetter).
///  So any earlier object still under the hold point when HoldObject takes a new one is never released:
///  it stays in view, can't be raycast (so it can't be clicked), and nothing "holds" it. Every item type
///  goes through HoldObject (Box, FurnitureBox, PaintBucket, FloorBox, OrderPaperBag, IceCreamBucket).
///
/// Suspected trigger (not confirmed yet; guest only): a guest's box pickup request times out after
/// 3 s (NetworkBoxInteraction.AvailabilityTimeoutRoutine). If the host's grant (NetworkBox.PickUP_RPC)
/// arrives later while the player already holds or uses something else, the game logs
/// "[BoxDiag] PickUP_RPC arrived AFTER timeout" / "[Box] Pickup kabulü zaman aşımından sonra geldi..."
/// and STILL calls Box.PickUp, which binds the late box to BoxInteraction and HoldObject()s it on top of
/// the current item. Two ghosts follow from that:
///  a) the item held before is now an orphan under the hold point; the next "put down" drops the late box
///     (the current object) instead, and the original item stays in the hands;
///  b) if the player was busy with another interaction, the late box ends up as m_CurrentObject while
///     no interaction is active, BoxInteraction is disabled, and nothing is waiting or placing, so no
///     input can drop it. No vanilla flow leaves a box in the hands like that: every release ends with
///     InteractionEnd after the box left the hold point.
///
/// Fix (runs for this machine's own player, so it must be installed on the machine that shows the
/// ghost, which for the suspected trigger is always a guest):
///  - A HoldObject prefix (local player only) remembers each item's layer before HoldObject changes it
///    (the game only keeps the latest one), and notes which object a hold left behind in the hands. That
///    is the moment a ghost is created; it's only reported if the object stays visible, because the
///    ice-cream bucket grab does this too (ThrowObjectToBin doesn't unparent the box) and then hides it.
///  - A watchdog looks at the direct children of the hold point every 0.25 s. After DelaySeconds it
///    drops:
///     * orphan boxes (not the current object), restoring their pre-hold layer ("Interactable", which
///       Box.PickUp sets right before HoldObject, if none was recorded);
///     * other orphan items, but only those HoldObject put there (recorded by the prefix, still on
///       "Ignore Raycast"), restoring the recorded layer; anything else is only logged;
///     * the current object if it is a box in state (b), through the game's own drop (DropObject plus,
///       online, NetworkBoxInteraction.DropBox_Broadcast so every client, unmodded ones included,
///       releases it from this player's hands, re-enables its transform sync and frees it on the host).
///    Orphans are released the way DropObject would (unparent, layer, PlacingMode(false), active
///    colliders solid, physics on unless another client owns the object), locally only: DropBox_RPC
///    always drops the receiver's current object, so it can't target an orphan.
///  - Before dropping, it checks that the item doesn't overlap solid geometry (the layers the game's own
///    placement check uses). Vanilla only drops through placing mode, whose hologram refuses such spots;
///    TryDropObject itself can't be used here because its trigger list is only filled while placing. If
///    the spot is blocked, it waits until the player moves.
///  Skipped: inactive items (the ice-cream bucket box is parked inactive in the hands while the bucket is
///  used), racked or tweening boxes, the box BoxInteraction is waiting for (the 3 s pickup handshake),
///  everything while another interaction owns the hands, and everything during a placement.
///
/// Limitation: dropping an orphan only fixes this machine's view. Other players ran the same HoldObject,
/// so on their machines the orphan may still look attached to this player (the game never re-syncs
/// parents), and for an orphan box the host keeps it marked as held by this player until this player
/// picks it up again (allowed by StuckBoxFix's host-side regrant) or the session is reloaded.
/// </summary>
internal static class GhostHeldItemFix
{
    internal static ConfigEntry<bool> AutoRecover;
    internal static ConfigEntry<float> DelaySeconds;
    internal static ConfigEntry<bool> Diagnostics;

    internal static void Bind(ConfigFile cfg)
    {
        AutoRecover = cfg.Bind("GhostHeldItem", "AutoRecover", true,
            "Drop items that are stuck in your hands without being held (the 'ghost in hands' bug).");
        DelaySeconds = cfg.Bind("GhostHeldItem", "DelaySeconds", 2f,
            new ConfigDescription("How long a stray item must stay in your hands before it is dropped.",
                new AcceptableValueRange<float>(0.5f, 10f)));
        Diagnostics = cfg.Bind("GhostHeldItem", "Diagnostics", true,
            "Log stray objects found in your hands and what was done about them.");
    }

    internal static void ResetState()
    {
        s_firstSeen.Clear();
        s_reported.Clear();
        s_blockedLogged.Clear();
        s_layerBeforeHold.Clear();
        s_displacedBy.Clear();
        s_nextCheck = 0f;
    }

    private static readonly Dictionary<IntPtr, float> s_firstSeen = new();
    private static readonly HashSet<IntPtr> s_reported = new();
    private static readonly HashSet<IntPtr> s_blockedLogged = new();
    /// <summary>Layer each item had right before HoldObject took it (GameObject pointer -> layer).</summary>
    private static readonly Dictionary<IntPtr, int> s_layerBeforeHold = new();
    /// <summary>Objects HoldObject left in the hands when it took a new item (pointer -> what replaced it).</summary>
    private static readonly Dictionary<IntPtr, string> s_displacedBy = new();
    private static float s_nextCheck;
    private const float CheckInterval = 0.25f;

    private static int s_ignoreRaycastLayer = -2, s_interactableLayer = -2, s_solidMask;

    private static void InitLayers()
    {
        if (s_ignoreRaycastLayer != -2) return;
        s_ignoreRaycastLayer = LayerMask.NameToLayer("Ignore Raycast");
        s_interactableLayer = LayerMask.NameToLayer("Interactable");
        // The layers IPlacingMode.OverlapBoxes tests when the game checks a placement spot.
        s_solidMask = LayerMask.GetMask(new[] { "Default", "Interactable", "QueryOnly", "WallLayer", "Ignore Raycast" });
    }

    [HarmonyPatch(typeof(PlayerObjectHolder), nameof(PlayerObjectHolder.HoldObject))]
    private static class HoldObjectPatch
    {
        private static void Prefix(PlayerObjectHolder __instance, GameObject item)
        {
            try
            {
                if (item == null || (!AutoRecover.Value && !Diagnostics.Value)) return;
                var p = HeldState.LocalPlayer();
                if (p == null || !HeldState.SameObject(p.PlayerObjectHolder, __instance)) return;
                InitLayers();
                int layer = item.layer;
                // Re-holding an item already in the hands: keep the layer it had before the first hold.
                if (layer != s_ignoreRaycastLayer || !s_layerBeforeHold.ContainsKey(item.Pointer))
                    s_layerBeforeHold[item.Pointer] = layer;

                // Remember what this hold leaves behind in the hands. Not logged here: some vanilla flows
                // (the ice-cream bucket grab: ThrowObjectToBin doesn't unparent the box) hide the old
                // object right after. The scan mentions it if the object stays visible.
                var prev = __instance.m_CurrentObject;
                var hold = __instance.m_ObjectHolder;
                if (prev != null && prev.Pointer != item.Pointer && hold != null &&
                    HeldState.SameObject(prev.transform.parent, hold))
                    s_displacedBy[prev.Pointer] = $"{item.name} at t={Time.unscaledTime:F1}";
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[GhostHeld] HoldObject prefix failed: {e}");
            }
        }
    }

    /// <summary>Called every frame from DiagnosticsBehaviour.</summary>
    internal static void Tick(float now)
    {
        if (!AutoRecover.Value && !Diagnostics.Value) return;
        if (now < s_nextCheck) return;
        s_nextCheck = now + CheckInterval;
        Scan(now, force: false, out _);
    }

    /// <summary>F9: drop stray items immediately.</summary>
    internal static void ForceCheck()
    {
        int n = Scan(Time.unscaledTime, force: true, out int blocked);
        if (n == 0 && blocked == 0) Plugin.Logger.LogInfo("[GhostHeld] F9: no stray items in your hands");
    }

    private static int Scan(float now, bool force, out int blocked)
    {
        int dropped = 0;
        blocked = 0;
        try
        {
            var p = HeldState.LocalPlayer();
            var holder = p != null ? p.PlayerObjectHolder : null;
            var hold = holder != null ? holder.m_ObjectHolder : null;
            if (hold == null) { s_firstSeen.Clear(); s_layerBeforeHold.Clear(); s_displacedBy.Clear(); return 0; }
            InitLayers();

            // HoldObject parents the held item directly to m_ObjectHolder (MoveToCarryingPosition), so
            // only its direct children matter: never furniture the player is carrying (a carried rack keeps
            // its boxes).
            var children = new List<GameObject>();
            var present = new HashSet<IntPtr>();
            for (int i = 0; i < hold.childCount; i++)
            {
                var c = hold.GetChild(i);
                if (c == null) continue;
                var go = c.gameObject;
                children.Add(go);
                present.Add(go.Pointer);
            }
            Prune(s_layerBeforeHold, present);
            Prune(s_displacedBy, present);

            var cur = holder.m_CurrentObject;
            var pi = p.PlayerInteraction;
            var bi = p.BoxInteraction;
            bool inInteraction = pi != null && pi.m_InInteraction;
            bool boxIntOn = bi != null && bi.Enable;
            bool waiting = bi != null && bi.IsWaitingAvailability;
            // Another interaction (ice-cream bucket, furniture, ...) owns the hands right now.
            if (!force && inInteraction && !boxIntOn) { s_firstSeen.Clear(); return 0; }
            // State (b): no interaction active, BoxInteraction disabled, not waiting, not placing.
            bool noInteraction = !inInteraction && !boxIntOn && !waiting && !holder.m_PlacingMode;

            var seen = new HashSet<IntPtr>();
            foreach (var go in children)
            {
                if (go == null || !go.activeInHierarchy) continue;   // a ghost is visible by definition
                var key = go.Pointer;
                bool isCurrent = cur != null && cur.Pointer == key;
                var b = go.GetComponent<Box>();
                int layer;

                if (b != null)
                {
                    if (isCurrent && !noInteraction) continue;        // really held
                    if (b.m_Racked) continue;
                    // The box the player is legitimately waiting for (pickup handshake in progress).
                    if (waiting && HeldState.SameObject(bi.m_Box, b)) continue;
                    layer = isCurrent ? -1 : LayerFor(key, s_interactableLayer);
                }
                else
                {
                    if (isCurrent) continue;   // non-box held items are left to their own interaction
                    // Only items HoldObject put there (recorded, still as HoldObject left them).
                    layer = LayerFor(key, -1);
                    if (layer < 0 || go.layer != s_ignoreRaycastLayer)
                    {
                        ReportUnknown(go);
                        continue;
                    }
                }
                if (!force && DOTween.IsTweening(go.transform)) continue;   // still animating somewhere

                seen.Add(key);
                if (!s_firstSeen.TryGetValue(key, out var since))
                {
                    s_firstSeen[key] = now;
                    if (Diagnostics.Value)
                        Plugin.Logger.LogWarning("[GhostHeld] " + (isCurrent
                                                     ? "Box in your hands with no interaction using it: "
                                                     : "Item in your hands that you are not holding: ") +
                                                 $"{Describe(go, b)} {HeldState.DescribePlayer(p)}" +
                                                 (s_displacedBy.TryGetValue(key, out var by)
                                                     ? $" (left in your hands when the game gave you {by})" : ""));
                    if (!force) continue;
                }
                if (!AutoRecover.Value && !force) continue;
                if (!force && now - since < DelaySeconds.Value) continue;
                if (!force && holder.m_PlacingMode) continue; // don't disturb an ongoing placement

                if (!IsSpotClear(go, p, hold, out var blocker))
                {
                    blocked++;
                    if (force)
                        Plugin.Logger.LogWarning($"[GhostHeld] F9: can't drop {go.name} here, it would end up inside " +
                                                 $"{blocker}. Step back into open space and press F9 again.");
                    else if (s_blockedLogged.Add(key) && Diagnostics.Value)
                        Plugin.Logger.LogInfo($"[GhostHeld] Waiting for free space to drop {go.name} (it overlaps {blocker})");
                    continue;
                }

                if (isCurrent) DropCurrentBox(p, holder, b);
                else DropOrphan(go, b, layer);
                dropped++;
                s_firstSeen.Remove(key);
                seen.Remove(key);
            }
            // Forget items that went away on their own.
            Prune(s_firstSeen, seen);
            s_blockedLogged.RemoveWhere(k => !seen.Contains(k));
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[GhostHeld] Scan failed: {e}");
        }
        return dropped;
    }

    private static void Prune<T>(Dictionary<IntPtr, T> map, HashSet<IntPtr> keep)
    {
        if (map.Count == 0) return;
        foreach (var k in new List<IntPtr>(map.Keys))
            if (!keep.Contains(k)) map.Remove(k);
    }

    /// <summary>The layer to restore: the one recorded before HoldObject, else the fallback.</summary>
    private static int LayerFor(IntPtr key, int fallback) =>
        s_layerBeforeHold.TryGetValue(key, out var l) && l != s_ignoreRaycastLayer ? l : fallback;

    private static string Describe(GameObject go, Box b) =>
        b != null ? HeldState.Describe(b)
                  : $"item[{HeldState.Path(go.transform)} layer={LayerMask.LayerToName(go.layer)}]";

    /// <summary>Anything else in the hands that isn't the held object: log once, don't touch.</summary>
    private static void ReportUnknown(GameObject go)
    {
        if (!Diagnostics.Value) return;
        if (go.GetComponentInChildren<Renderer>() == null) return;
        if (!s_reported.Add(go.Pointer)) return;
        Plugin.Logger.LogWarning($"[GhostHeld] Unknown object in your hands that you are not holding: " +
                                 $"{HeldState.Path(go.transform)} layer={LayerMask.LayerToName(go.layer)} " +
                                 $"(left alone; please send this line)");
    }

    /// <summary>
    /// False if the item, released where it is now, would overlap solid geometry on the layers the game's
    /// own placement check uses. The item's own colliders, the local player and the hold point are ignored.
    /// </summary>
    private static bool IsSpotClear(GameObject go, PlayerInstance p, Transform hold, out string blocker)
    {
        blocker = null;
        Physics.SyncTransforms();
        var root = go.transform;
        var playerRoot = p.transform;
        const float margin = 0.02f;   // touching a surface is fine
        var cols = go.GetComponentsInChildren<Collider>(false);
        for (int i = 0; i < cols.Length; i++)
        {
            var col = cols[i];
            if (col == null || !col.enabled) continue;
            Vector3 center, half;
            Quaternion rot;
            var bc = col.TryCast<BoxCollider>();
            if (bc != null)
            {
                var t = bc.transform;
                var s = t.lossyScale;
                center = t.TransformPoint(bc.center);
                half = Vector3.Scale(bc.size * 0.5f, new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z)));
                rot = t.rotation;
            }
            else
            {
                var bounds = col.bounds;
                center = bounds.center;
                half = bounds.extents;
                rot = Quaternion.identity;
            }
            half = new Vector3(Mathf.Max(half.x - margin, 0.005f), Mathf.Max(half.y - margin, 0.005f),
                               Mathf.Max(half.z - margin, 0.005f));
            var hits = Physics.OverlapBox(center, half, rot, s_solidMask, QueryTriggerInteraction.Ignore);
            for (int h = 0; hits != null && h < hits.Length; h++)
            {
                var hit = hits[h];
                if (hit == null) continue;
                var ht = hit.transform;
                if (ht.IsChildOf(root) || ht.IsChildOf(playerRoot) || ht.IsChildOf(hold)) continue;
                blocker = HeldState.Path(ht);
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// State (b): the current object is a box nothing uses. Drop it the way BoxInteraction.DropBox does,
    /// including the network message, so every client releases it from this player's hands.
    /// </summary>
    private static void DropCurrentBox(PlayerInstance p, PlayerObjectHolder holder, Box b)
    {
        Plugin.Logger.LogWarning($"[GhostHeld] Dropping stray box: {HeldState.Describe(b)}");
        var bi = p.BoxInteraction;
        var nb = b.NetworkBox;
        bool bound = bi != null && HeldState.SameObject(bi.m_Box, b);
        holder.DropObject();
        var net = bound ? bi.m_NetworkBoxInteraction : null;
        if (PhotonNetwork.IsConnected && net != null)
        {
            // Vanilla drop message (needs BoxInteraction.m_Box = this box): turns the box's transform sync
            // back on and frees it here; DropBox_RPC makes every other client drop this player's current
            // object (the same box: they ran the same HoldObject) and frees it on the host.
            net.DropBox_Broadcast();
        }
        else ReleaseNetworkState(b, nb);
        holder.SetNullCurrentObject();
        if (bound) bi.m_Box = null;
        ClearOccupy(p, b);
        Plugin.Logger.LogInfo($"[GhostHeld] Dropped: {HeldState.Describe(b)}");
    }

    /// <summary>Undo what HoldObject did to an object that is no longer the current one.</summary>
    private static void DropOrphan(GameObject go, Box b, int layer)
    {
        Plugin.Logger.LogWarning($"[GhostHeld] Dropping stray item: {Describe(go, b)}");
        var t = go.transform;
        DOTween.Kill(t);
        t.SetParent(null, true);
        go.layer = layer;

        var pm = go.GetComponent<IPlacingMode>();
        if (pm == null) pm = go.GetComponentInChildren<IPlacingMode>(true);
        if (pm != null) pm.PlacingMode(false, null);   // hologram off, triggers cleared, as DropObject does

        var cols = go.GetComponentsInChildren<Collider>(false);   // HoldObject only touched active ones
        for (int i = 0; i < cols.Length; i++) cols[i].isTrigger = false;

        PhotonView view = null;
        var nb = b != null ? b.NetworkBox : null;
        if (nb != null) view = nb.PhotonView;
        if (view == null) view = go.GetComponent<PhotonView>();
        // Like NetworkRigidbodySetter: an object owned by another client stays kinematic.
        bool simulate = !PhotonNetwork.IsConnected || view == null || view.IsMine;
        var rb = go.GetComponent<Rigidbody>();
        if (rb != null && simulate)
        {
            rb.isKinematic = false;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
        }

        if (b != null)
        {
            ReleaseNetworkState(b, nb);
            var p = HeldState.LocalPlayer();
            if (p != null) ClearOccupy(p, b);
        }
        Plugin.Logger.LogInfo($"[GhostHeld] Dropped: {Describe(go, b)}");
    }

    /// <summary>What DropBox_Broadcast does on the sender, for a box dropped without it.</summary>
    private static void ReleaseNetworkState(Box b, NetworkBox nb)
    {
        if (nb == null) return;
        var view = nb.PhotonView;
        if (view != null && view.IsMine) nb.DisableNetworkTransformSync(true);   // true = sync on
        // IsNetworkOccupied is only read on the host (AvailabilityCallback_RPC).
        if (!PhotonNetwork.IsConnected || PhotonNetwork.IsMasterClient) nb.IsNetworkOccupied = false;
    }

    private static void ClearOccupy(PlayerInstance p, Box b)
    {
        var owner = b.m_OccupyOwner;
        if (b.m_IsBoxOccupied && owner != null && owner.IsChildOf(p.transform)) b.SetOccupy(false, null);
    }
}
