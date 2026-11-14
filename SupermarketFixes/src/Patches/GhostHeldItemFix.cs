using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using DG.Tweening;
using Photon.Pun;
using UnityEngine;

namespace SupermarketFixes.Patches;

/// <summary>
/// Bug #2: after putting a held item down on the floor, an image of it stays in the player's hands
/// (blocking the view) although the player can interact with everything else, until a reload.
///
/// How holding works (native analysis of GameAssembly.dll):
///  - PlayerObjectHolder.HoldObject(item) parents the item to the hold point (m_ObjectHolder), switches
///    it to the "Ignore Raycast" layer, makes its colliders triggers and its Rigidbody kinematic, and
///    stores it in m_CurrentObject, OVERWRITING any previous object without releasing it.
///  - Dropping (DropObject / placing mode / throw) only ever releases m_CurrentObject.
///  So any second object that gets parented to the hands is never released: it stays in view, can't be
///  raycast (so it can't be clicked), and nothing "holds" it. That matches the report.
///
/// Suspected trigger (not confirmed yet): in multiplayer a guest's pickup request times out after
/// 3 s (NetworkBoxInteraction.AvailabilityTimeoutRoutine). If the host's grant (NetworkBox.PickUP_RPC)
/// arrives later while the player already holds or uses something else, the game logs
/// "[BoxDiag] PickUP_RPC arrived AFTER timeout" / "[Box] Pickup kabulü zaman aşımından sonra geldi..."
/// and STILL calls Box.PickUp, which HoldObject()s the late box on top of the current item. If the
/// player was busy with another interaction, Box.PickUp also binds the late box to the (disabled)
/// BoxInteraction, so once that interaction ends the late box is m_CurrentObject with nothing bound to
/// it: in the hands, but no interaction can drop it.
///
/// Fix (runs for this machine's own player, so it must be installed on the machine that shows the
/// ghost): after a short delay a watchdog drops, the way PlayerObjectHolder.DropObject would
/// (unparent, normal layer, solid colliders, physics on, not occupied):
///  - visible boxes under the hold point that are not the held object, and
///  - the held box itself when no interaction is active or bound to it (no vanilla flow leaves a box in
///    the hands like that: every release ends with InteractionEnd after the box left the hold point).
/// Skipped: inactive boxes (the ice-cream bucket box is parked inactive in the hands while the bucket is
/// used), racked boxes, tweening boxes, and everything while another interaction owns the hands.
/// Unknown non-box objects in the hands are only logged, so they can be identified.
///
/// Limitation: this only fixes this machine's view. Other players ran the same PickUP_RPC, so on their
/// machines the box may still look attached to this player (the game never re-syncs parents), and the
/// host keeps it marked as held by this player until this player picks it up again (allowed by
/// StuckBoxFix's host-side regrant) or the session is reloaded.
/// </summary>
internal static class GhostHeldItemFix
{
    internal static ConfigEntry<bool> AutoRecover;
    internal static ConfigEntry<float> DelaySeconds;
    internal static ConfigEntry<bool> Diagnostics;

    internal static void Bind(ConfigFile cfg)
    {
        AutoRecover = cfg.Bind("GhostHeldItem", "AutoRecover", true,
            "Drop boxes that are stuck in your hands without being held (the 'ghost in hands' bug).");
        DelaySeconds = cfg.Bind("GhostHeldItem", "DelaySeconds", 2f,
            new ConfigDescription("How long a stray box must stay in your hands before it is dropped.",
                new AcceptableValueRange<float>(0.5f, 10f)));
        Diagnostics = cfg.Bind("GhostHeldItem", "Diagnostics", true,
            "Log stray objects found in your hands and what was done about them.");
    }

    internal static void ResetState()
    {
        s_firstSeen.Clear();
        s_reported.Clear();
        s_nextCheck = 0f;
    }

    private static readonly Dictionary<IntPtr, float> s_firstSeen = new();
    private static readonly HashSet<IntPtr> s_reported = new();
    private static float s_nextCheck;
    private const float CheckInterval = 0.25f;

    /// <summary>Called every frame from DiagnosticsBehaviour.</summary>
    internal static void Tick(float now)
    {
        if (!AutoRecover.Value && !Diagnostics.Value) return;
        if (now < s_nextCheck) return;
        s_nextCheck = now + CheckInterval;
        Scan(now, force: false);
    }

    /// <summary>F9: drop stray boxes immediately.</summary>
    internal static void ForceCheck()
    {
        int n = Scan(Time.unscaledTime, force: true);
        if (n == 0) Plugin.Logger.LogInfo("[GhostHeld] F9: no stray boxes in your hands");
    }

    private static int Scan(float now, bool force)
    {
        int dropped = 0;
        try
        {
            var p = HeldState.LocalPlayer();
            var holder = p != null ? p.PlayerObjectHolder : null;
            if (holder == null) { s_firstSeen.Clear(); return 0; }
            var cur = holder.m_CurrentObject;
            var curT = cur != null ? cur.transform : null;
            var pi = p.PlayerInteraction;
            var bi = p.BoxInteraction;
            bool inInteraction = pi != null && pi.m_InInteraction;
            bool boxIntOn = bi != null && bi.Enable;
            // Another interaction (ice-cream bucket, furniture, ...) owns the hands right now.
            if (!force && inInteraction && !boxIntOn) { s_firstSeen.Clear(); return 0; }
            // Nothing is using the held object: no interaction, not waiting, not placing.
            bool currentUnbound = !inInteraction && !boxIntOn && !(bi != null && bi.IsWaitingAvailability) &&
                                  !holder.m_PlacingMode;

            var seen = new HashSet<IntPtr>();
            // HoldObject parents the held item directly to m_ObjectHolder (MoveToCarryingPosition), so
            // only look there: never at furniture the player is carrying (a carried rack keeps its boxes).
            var hold = holder.m_ObjectHolder;
            var boxes = hold != null ? hold.GetComponentsInChildren<Box>(true) : null;
            for (int i = 0; boxes != null && i < boxes.Length; i++)
            {
                var b = boxes[i];
                if (b == null || seen.Contains(b.Pointer)) continue;
                var t = b.transform;
                if (!b.gameObject.activeInHierarchy) continue;   // a ghost is visible by definition
                bool isCurrent = curT != null && t.IsChildOf(curT);
                if (isCurrent && !currentUnbound) continue;      // really held
                if (b.m_Racked || b.GetComponentInParent<RackSlot>(true) != null) continue;
                if (!force && DOTween.IsTweening(t)) continue;   // still animating somewhere

                seen.Add(b.Pointer);
                if (!s_firstSeen.TryGetValue(b.Pointer, out var since))
                {
                    s_firstSeen[b.Pointer] = now;
                    if (Diagnostics.Value)
                        Plugin.Logger.LogWarning($"[GhostHeld] Box in your hands that you are not holding" +
                                                 (isCurrent ? " (held object, nothing bound to it): " : ": ") +
                                                 $"{HeldState.Describe(b)} {HeldState.DescribePlayer(p)}");
                    if (!force) continue;
                }
                if (!AutoRecover.Value && !force) continue;
                if (!force && now - since < DelaySeconds.Value) continue;
                if (!force && holder.m_PlacingMode) continue; // don't disturb an ongoing placement
                Drop(p, b, isCurrent ? holder : null);
                dropped++;
                s_firstSeen.Remove(b.Pointer);
                seen.Remove(b.Pointer);
            }
            // Forget boxes that went away on their own.
            if (s_firstSeen.Count > seen.Count)
                foreach (var k in new List<IntPtr>(s_firstSeen.Keys))
                    if (!seen.Contains(k)) s_firstSeen.Remove(k);

            if (Diagnostics.Value) ReportUnknown(holder, curT);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[GhostHeld] Scan failed: {e}");
        }
        return dropped;
    }

    /// <summary>Anything else in the hands that isn't the held object: log once, don't touch.</summary>
    private static void ReportUnknown(PlayerObjectHolder holder, Transform curT)
    {
        var hold = holder.m_ObjectHolder;
        if (hold == null) return;
        for (int i = 0; i < hold.childCount; i++)
        {
            var c = hold.GetChild(i);
            if (c == null || (curT != null && c.IsChildOf(curT)) || !c.gameObject.activeInHierarchy) continue;
            if (c.GetComponentInChildren<Box>(true) != null) continue; // handled above
            if (c.GetComponentInChildren<Renderer>() == null) continue;
            if (!s_reported.Add(c.Pointer)) continue;
            Plugin.Logger.LogWarning($"[GhostHeld] Unknown object in your hands that you are not holding: " +
                                     $"{HeldState.Path(c)} layer={LayerMask.LayerToName(c.gameObject.layer)} " +
                                     $"(left alone; please send this line)");
        }
    }

    /// <summary>Undo what HoldObject did. For the holder's own current object, use the game's DropObject;
    /// for an orphan it would release the wrong object, so do the same steps by hand.</summary>
    private static void Drop(PlayerInstance p, Box b, PlayerObjectHolder holderOfCurrent)
    {
        Plugin.Logger.LogWarning($"[GhostHeld] Dropping stray box: {HeldState.Describe(b)}");
        var nb = b.NetworkBox;
        if (holderOfCurrent != null)
        {
            holderOfCurrent.DropObject();
            holderOfCurrent.SetNullCurrentObject();
            var bi = p.BoxInteraction;
            if (bi != null && HeldState.SameObject(bi.m_Box, b)) bi.m_Box = null;
        }
        else
        {
            var t = b.transform;
            DOTween.Kill(t);
            t.SetParent(null, true);
            b.gameObject.layer = LayerMask.NameToLayer("Interactable");

            var cols = b.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < cols.Length; i++) cols[i].isTrigger = false;

            var view = nb != null ? nb.PhotonView : null;
            bool simulate = view == null || view.IsMine; // the owner drives the physics of networked boxes
            var rb = b.m_RigidBody;
            if (rb != null && simulate)
            {
                rb.isKinematic = false;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
            }
        }

        var owner = b.m_OccupyOwner;
        if (b.m_IsBoxOccupied && owner != null && owner.IsChildOf(p.transform)) b.SetOccupy(false, null);
        // IsNetworkOccupied is only read on the host (AvailabilityCallback_RPC).
        if (nb != null && (!PhotonNetwork.IsConnected || PhotonNetwork.IsMasterClient)) nb.IsNetworkOccupied = false;

        Plugin.Logger.LogInfo($"[GhostHeld] Dropped: {HeldState.Describe(b)}");
    }
}
