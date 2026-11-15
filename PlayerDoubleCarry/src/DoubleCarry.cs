using System;
using System.Collections.Generic;
using __Project__.Scripts.Multiplayer.NetworkInteractions;
using DG.Tweening;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Photon.Pun;
using UnityEngine;

namespace PlayerDoubleCarry;

/// <summary>
/// Improvement #10: a player can carry two boxes. While two are carried neither can be opened; once the
/// top one is put down (dropped, thrown, placed on a rack / in a trash bin / on a placement area...) the
/// other one comes back into the hands as a normal held box.
///
/// How a box is held (native analysis of GameAssembly.dll):
///  - Picking up is a Use click on the box: PlayerInteraction.Interact -> Box.InstantInteract.
///    Offline: Box.PickUp(). Host: AvailabilityCallback_RPC(own id) directly. Guest: NetworkBox.AskAvailability
///    (BoxInteraction.IsWaitingAvailability, 3 s timeout) -> the host checks IsNetworkOccupied / NPC claims ->
///    GRANT: PickUp_Broadcast (IsNetworkOccupied = true, PickUP_RPC to All, ownership to the player);
///    REJECT: AvailabilityRejected_Response -> EndAvailabilityWait -> BoxInteraction.CancelInteracitonForOnlyMultiplayer
///    (= end the box interaction). The timeout coroutine sets m_AvailabilityTimedOut and calls the same cancel;
///    EndAvailabilityWait returns and clears that flag, and a late reply skips the cancel when it was set.
///  - PickUP_RPC runs on every client: Box.PickUp(player) -> that player's BoxInteraction.set_Interactable(box)
///    (m_Box, m_IsBoxOpen = box.IsOpen) and PlayerObjectHolder.HoldObject(box, BoxSO.PositionOffset, 0), which
///    parents the box to m_ObjectHolder and stores it as m_CurrentObject, overwriting the previous one without
///    releasing it. Pickup does NOT close an open box (the CloseBox in InstantInteract/PickUp only runs for a
///    racked box, and m_Racked is cleared just before), and nothing closes a box when it is put down.
///  - Every release (drop/placing mode, throw, rack, trash, placement area, network drop) only acts on the
///    current object / BoxInteraction.m_Box, then ends the box interaction (BoxInteraction disabled).
///  - BoxInteraction.OnUse(true) sets m_holdingUse, then: placement area / placing mode / rack slot (unless the
///    box is in its 0.7 s open-close animation) / trash bin / placement area target each take their own branch;
///    otherwise an open box does nothing more (Update places products while m_holdingUse) and a closed box opens.
///    OnTake drives taking products back into an open box.
///  - The host's PlayerInstance.OnPlayerLeftRoom only throws the leaving player's BoxInteraction.m_Box out of the
///    hands; anything else under their hold point is destroyed with their avatar.
///
/// Mechanism:
///  - OnUse prefix (local player): in exactly the "would open the held box" case, if the player looks at
///    another loose, closed box (same ray as PlayerInteraction.Raycast), call that box's InstantInteract instead,
///    i.e. the normal pickup path, so the host grants it and every client (unmodded ones included) runs
///    HoldObject for it. With two boxes carried, OnUse(true) with nothing targeted and OnTake(true) are
///    swallowed: no opening, no product placing or taking.
///  - HoldObject postfix: when the requested box replaces the held one, the old box ("bottom") is moved out
///    of m_ObjectHolder to its parent and kept just under the new box ("top"), which is now the game's
///    current box. It is not a child of m_ObjectHolder, so SupermarketFixes' ghost-item watchdog (which only
///    looks at direct children) leaves it alone. The bottom box keeps everything HoldObject did to it
///    (kinematic, "Ignore Raycast") and its colliders are switched off so it can't get in the way of placing
///    the top box; on the host it stays IsNetworkOccupied. HoldObject prefix: if a late grant for the requested
///    box arrives while the first box is in placing mode, placing is cancelled first so the box can be stacked.
///  - CancelInteracitonForOnlyMultiplayer prefix: a refused or timed-out request for the second box must not
///    end the interaction with the box still in the hands. After a timeout the box interaction stays frozen
///    (IsWaitingAvailability) for a few more seconds, so a late grant meets the first box still held normally;
///    the request is remembered a while longer and no new second-box request is sent meanwhile.
///  - Tick: once BoxInteraction no longer holds the top box, wait PromoteDelay, then hand the bottom box back
///    the vanilla way: online PickUP_RPC(own user id) on that box to All (every client re-runs Box.PickUp ->
///    HoldObject for this player, the host keeps it occupied), offline Box.PickUp(player); then bind the
///    local box interaction like PlayerInteraction.Interact does (SetCurrentInteraction(BOX)).
///    PlayerInteraction.Interact is blocked in that short window so nothing else is picked up meanwhile.
///  - On other players' machines (modded): the HoldObject postfix only lowers the displaced box under the new
///    one (cosmetic). Unmodded machines keep both boxes at the same spot in that player's hands.
///  - OnPlayerLeftRoom prefix (every modded client): before the leaving player's avatar is destroyed, release
///    every box under their hold point that isn't their BoxInteraction.m_Box (unparent, solid, not occupied,
///    transform sync on; physics on the host), so the box isn't destroyed with the avatar.
/// </summary>
internal static class DoubleCarry
{
    private const float StackTweenTime = 0.25f;
    /// <summary>Pause between the top box leaving the hands and the bottom box coming up.</summary>
    private const float PromoteDelay = 0.25f;
    /// <summary>After a timed-out request, keep the box interaction frozen until this long after the request.</summary>
    private const float FreezeSeconds = 5f;
    /// <summary>A requested second box that hasn't arrived after this long is forgotten.</summary>
    private const float RequestExpiry = 15f;
    private const float StackGap = 0.005f;

    // Local player state. Fields hold interop wrappers; "== null" is Unity's destroyed check.
    private static Box s_requested;          // second box asked for (pickup in flight)
    private static Box s_requestedOver;      // box that was in the hands when it was asked for
    private static float s_requestedAt;
    private static bool s_frozen;            // we re-set IsWaitingAvailability after the game's timeout
    private static Box s_top;                // current box (the game's held box) while two are carried
    private static Box s_bottom;             // the box kept under it
    private static Vector3 s_bottomLocal;    // bottom box pose in m_ObjectHolder space
    private static Quaternion s_bottomRot = Quaternion.identity;
    private static readonly List<Collider> s_disabledColliders = new();
    private static float s_topReleasedAt = -1f;
    private static bool s_waitLogged;
    private static float s_blockedLoggedAt = -10f;

    /// <summary>True while the local player carries a second box (or is about to get it back).</summary>
    internal static bool Busy => !ReferenceEquals(s_bottom, null);

    /// <summary>A second box was asked for and hasn't arrived or been refused yet.</summary>
    private static bool Requesting => !ReferenceEquals(s_requested, null);

    /// <summary>The mod still has something to finish (keep the patches until it's done).</summary>
    internal static bool HasWork => Busy || Requesting;

    private static bool Promoting => Busy && s_topReleasedAt >= 0f;

    internal static void ResetState()
    {
        RestoreColliders();
        s_requested = null;
        s_requestedOver = null;
        s_frozen = false;
        s_top = null;
        s_bottom = null;
        s_topReleasedAt = -1f;
        s_waitLogged = false;
    }

    // ------------------------------------------------------------------ helpers

    private static void Info(string msg) { if (Plugin.Verbose.Value) Plugin.Logger.LogInfo("[PlayerCarry] " + msg); }

    private static bool Same(UnityEngine.Object a, UnityEngine.Object b) => a != null && b != null && a.Pointer == b.Pointer;

    private static PlayerInstance LocalPlayer()
    {
        if (!NoktaSingleton<PlayerManager>.HasInstance) return null;
        var pm = NoktaSingleton<PlayerManager>.Instance;
        var p = pm != null ? pm.LocalPlayer : null;
        return p != null ? p : null;
    }

    private static bool Online => PhotonNetwork.IsConnected;

    private static string Describe(Box b)
    {
        if (b == null) return "<none>";
        int view = -1;
        try { var nb = b.NetworkBox; if (nb != null && nb.PhotonView != null) view = nb.PhotonView.ViewID; } catch { }
        int product = -1;
        try { var d = b.Data; if (d != null) product = d.ProductID; } catch { }
        return $"box[{b.name} product={product} view={view}]";
    }

    /// <summary>The box is the one the game considers held: bound, current object, in the hands.</summary>
    private static bool HeldNormally(PlayerInstance p, Box box)
    {
        if (box == null) return false;
        var holder = p.PlayerObjectHolder;
        var bi = p.BoxInteraction;
        if (holder == null || bi == null || !bi.Enable || !Same(bi.m_Box, box)) return false;
        var hold = holder.m_ObjectHolder;
        return Same(holder.m_CurrentObject, box.gameObject) && hold != null && Same(box.transform.parent, hold);
    }

    /// <summary>OnUse(true) would reach the "open the box / use its products" part (mirrors the game's checks).</summary>
    private static bool NothingTargeted(BoxInteraction bi)
    {
        var box = bi.m_Box;
        if (box == null || bi.IsWaitingAvailability) return false;
        if (bi.m_TargetPlacementArea != null || bi.m_PlacingMode) return false;
        if (bi.m_CurrentRackSlot != null && !box.m_InAnimationTransition) return false;
        if (bi.m_CurrentTrashBin != null || bi.m_CurrentPlacementArea != null) return false;
        return true;
    }

    /// <summary>The exact OnUse(true) branch in which the game opens the held box.</summary>
    private static bool WouldOpenBox(BoxInteraction bi) => NothingTargeted(bi) && !bi.m_Box.IsOpen;

    /// <summary>The loose box the player is looking at, if any (same ray as PlayerInteraction.Raycast).</summary>
    private static Box LookedAtBox(PlayerInstance p, Box held)
    {
        var pi = p.PlayerInteraction;
        if (pi == null) return null;
        var cam = pi.m_MainCamera;
        if (cam == null) return null;
        var t = cam.transform;
        if (!Physics.Raycast(t.position, t.forward, out var hit, pi.m_InteractionDistance, pi.m_InteractableLayer.value))
            return null;
        var col = hit.collider;
        if (col == null) return null;
        var box = col.gameObject.GetComponent<Box>();   // the game takes the interactable from the hit collider itself
        if (box == null || Same(box, held)) return null;
        // Same checks as a vanilla click; online the host still decides (occupied by someone, claimed by an NPC).
        if (!box.gameObject.activeInHierarchy || box.m_Racked) return null;
        if (Online && box.NetworkBox == null) return null;
        return box;
    }

    private static void LogBlocked(string msg)
    {
        float now = Time.unscaledTime;
        if (now - s_blockedLoggedAt < 1f) return;
        s_blockedLoggedAt = now;
        Info(msg);
    }

    private static Bounds BoxBounds(Box box, out Vector3 scale)
    {
        scale = box.transform.localScale;
        var b = box.LocalBounds;
        if (b.extents.y < 0.001f) b = new Bounds(new Vector3(0f, 0.15f, 0f), new Vector3(0.4f, 0.3f, 0.4f));
        return b;
    }

    /// <summary>Local position (m_ObjectHolder space) that puts <paramref name="bottom"/> right under
    /// <paramref name="top"/> held at <paramref name="topPos"/>.</summary>
    private static Vector3 UnderPosition(Box bottom, Box top, Vector3 topPos)
    {
        var bt = BoxBounds(top, out var st);
        var bb = BoxBounds(bottom, out var sb);
        float topUnderside = topPos.y + (bt.center.y - bt.extents.y) * st.y;
        float cx = topPos.x + bt.center.x * st.x;
        float cz = topPos.z + bt.center.z * st.z;
        return new Vector3(cx - bb.center.x * sb.x,
                           topUnderside - StackGap - (bb.center.y + bb.extents.y) * sb.y,
                           cz - bb.center.z * sb.z);
    }

    /// <summary>Bottom box pose in the space of its current parent (m_ObjectHolder's parent).</summary>
    private static void BottomPoseInParent(Transform hold, out Vector3 pos, out Quaternion rot)
    {
        var m = Matrix4x4.TRS(hold.localPosition, hold.localRotation, hold.localScale);
        pos = m.MultiplyPoint3x4(s_bottomLocal);
        rot = hold.localRotation * s_bottomRot;
    }

    private static void RemoveOpenHint(PlayerInstance p)
    {
        if (!NoktaSingleton<InteractionHintsManager>.HasInstance) return;
        var hm = NoktaSingleton<InteractionHintsManager>.Instance;
        if (hm != null) hm.RemoveHint(InputActionID.USE, p);
    }

    private static void DisableColliders(Box box)
    {
        RestoreColliders();
        var cols = box.gameObject.GetComponentsInChildren<Collider>(false);
        for (int i = 0; i < cols.Length; i++)
        {
            var c = cols[i];
            if (c == null || !c.enabled) continue;
            c.enabled = false;
            s_disabledColliders.Add(c);
        }
    }

    private static void RestoreColliders()
    {
        foreach (var c in s_disabledColliders)
            if (c != null) c.enabled = true;
        s_disabledColliders.Clear();
    }

    private static void ForgetRequest(PlayerInstance p)
    {
        if (s_frozen && p != null)
        {
            var bi = p.BoxInteraction;
            if (bi != null && Same(bi.m_Box, s_requestedOver)) bi.IsWaitingAvailability = false;
        }
        s_frozen = false;
        s_requested = null;
        s_requestedOver = null;
    }

    // ------------------------------------------------------------------ second pickup

    private static void RequestSecondBox(PlayerInstance p, Box held, Box target)
    {
        s_requested = target;
        s_requestedOver = held;
        s_requestedAt = Time.unscaledTime;
        s_frozen = false;
        Info($"Picking up a second box: {Describe(target)} on top of {Describe(held)} " +
             $"({(!Online ? "offline" : PhotonNetwork.IsMasterClient ? "host" : "guest, asking the host")})");
        // The game's own pickup: offline and on the host this holds the box right away (HoldObject postfix
        // stacks it); a guest gets PickUP_RPC from the host, or a refusal (CancelPatch).
        target.InstantInteract();
    }

    /// <summary>HoldObject prefix: a late grant for the requested box while the first box is being placed.</summary>
    internal static void BeforeHold(PlayerObjectHolder holder, GameObject item)
    {
        if (!Requesting || item == null || s_requested == null || item.Pointer != s_requested.gameObject.Pointer) return;
        var owner = holder.m_PlayerInstance;
        if (owner == null || !owner.IsLocalPlayerInstance) return;
        if (s_requestedOver == null || !Same(holder.m_CurrentObject, s_requestedOver.gameObject)) return;
        var bi = owner.BoxInteraction;
        if (bi != null && bi.m_PlacingMode)
        {
            Info("Second box arrived while you were placing the first one; placing cancelled so both can be carried");
            bi.OnBack();   // the game's cancel: clears m_PlacingMode, CancelPlacingMode, hints
        }
        else if (holder.m_PlacingMode)
        {
            holder.CancelPlacingMode();
        }
    }

    /// <summary>HoldObject postfix: <paramref name="prev"/> was the held object before <paramref name="item"/>.</summary>
    internal static void AfterHold(PlayerObjectHolder holder, GameObject item, Vector3 posOffset, Vector3 rotOffset, GameObject prev)
    {
        if (prev == null || item == null || prev.Pointer == item.Pointer) return;
        var hold = holder.m_ObjectHolder;
        if (hold == null || !Same(prev.transform.parent, hold) || !prev.activeInHierarchy) return;
        var bottom = prev.GetComponent<Box>();
        var top = item.GetComponent<Box>();
        if (bottom == null || top == null) return;

        var owner = holder.m_PlayerInstance;
        if (owner != null && owner.IsLocalPlayerInstance)
        {
            if (!Same(top, s_requested) || !Same(bottom, s_requestedOver)) return;   // not ours: leave it to the game
            Stack(owner, hold, bottom, top, posOffset, rotOffset);
            return;
        }

        // Another player's hands: only make it look right (they hold the box under the new one).
        if (!Plugin.RemoteStackVisuals.Value) return;
        var t = prev.transform;
        t.DOKill(false);
        t.DOLocalMove(UnderPosition(bottom, top, posOffset), StackTweenTime, false);
        t.DOLocalRotate(rotOffset, StackTweenTime, RotateMode.Fast);
        Info($"{(owner != null ? owner.name : "?")} carries two boxes: {Describe(top)} on {Describe(bottom)}");
    }

    private static void Stack(PlayerInstance p, Transform hold, Box bottom, Box top, Vector3 posOffset, Vector3 rotOffset)
    {
        s_requested = null;
        s_requestedOver = null;
        s_frozen = false;
        s_top = top;
        s_bottom = bottom;
        s_topReleasedAt = -1f;
        s_waitLogged = false;
        s_bottomLocal = UnderPosition(bottom, top, posOffset);
        s_bottomRot = Quaternion.Euler(rotOffset);

        // Offline, Box.PickUp() doesn't bind BoxInteraction (PlayerInteraction.Interact does that after the
        // click, which didn't run here); online Box.PickUp(player) already did.
        var bi = p.BoxInteraction;
        if (bi != null && !Same(bi.m_Box, top)) bi.Interactable = new IInteractable(top.Pointer);

        // Open targets are refused, but a box can change state while a guest's request is in flight, and the
        // first box may have been opened while a late grant was pending. Neither may be used now.
        if (top.IsOpen)
        {
            top.CloseBox(false);
            if (bi != null) bi.m_IsBoxOpen = false;
        }
        if (bottom.IsOpen) bottom.CloseBox(false);

        var t = bottom.transform;
        t.DOKill(false);
        var parent = hold.parent;
        if (parent != null)
        {
            t.SetParent(parent, true);
            BottomPoseInParent(hold, out var pos, out var rot);
            t.DOLocalMove(pos, StackTweenTime, false);
            t.DOLocalRotateQuaternion(rot, StackTweenTime);
        }
        else
        {
            // No parent to move it to; stay a child of the hold point.
            t.DOLocalMove(s_bottomLocal, StackTweenTime, false);
            t.DOLocalRotateQuaternion(s_bottomRot, StackTweenTime);
        }
        DisableColliders(bottom);
        RemoveOpenHint(p);
        Info($"Carrying two boxes: {Describe(top)} on top of {Describe(bottom)}");
    }

    // ------------------------------------------------------------------ per frame

    internal static void Tick()
    {
        float now = Time.unscaledTime;
        if (Requesting) TickRequest(now);
        if (!Busy) return;

        var p = LocalPlayer();
        if (s_bottom == null || p == null)
        {
            Plugin.Logger.LogWarning("[PlayerCarry] The second box or the player is gone (scene change?); resetting");
            ResetState();
            return;
        }
        var holder = p.PlayerObjectHolder;
        var hold = holder != null ? holder.m_ObjectHolder : null;
        var bi = p.BoxInteraction;
        var pi = p.PlayerInteraction;
        if (hold == null || bi == null || pi == null) return;

        // Keep the bottom box under the hold point (it follows its parent; this copes with the hold point
        // moving inside it).
        var bt = s_bottom.transform;
        if (!DOTween.IsTweening(bt, false))
        {
            if (Same(bt.parent, hold)) { bt.localPosition = s_bottomLocal; bt.localRotation = s_bottomRot; }
            else if (Same(bt.parent, hold.parent))
            {
                BottomPoseInParent(hold, out var pos, out var rot);
                bt.localPosition = pos;
                bt.localRotation = rot;
            }
        }

        if (s_topReleasedAt < 0f)
        {
            if (s_top != null && bi.Enable && Same(bi.m_Box, s_top)) return;   // still carrying both
            Info($"Top box left your hands ({Describe(s_top)}); taking {Describe(s_bottom)} next");
            s_top = null;
            s_topReleasedAt = now;
            return;
        }
        if (now - s_topReleasedAt < PromoteDelay) return;
        if (pi.m_InInteraction || holder.m_PlacingMode)
        {
            if (!s_waitLogged) Info("Waiting for the current interaction to end before taking the second box");
            s_waitLogged = true;
            return;
        }
        TakeBottomBox(p);
    }

    private static void TickRequest(float now)
    {
        var p = LocalPlayer();
        float age = now - s_requestedAt;
        if (s_requested == null || p == null || age > RequestExpiry)
        {
            Info("Second box never arrived; forgetting the request");
            ForgetRequest(p);
            return;
        }
        if (!s_frozen) return;
        var bi = p.BoxInteraction;
        if (bi == null || !Same(bi.m_Box, s_requestedOver))
        {
            s_frozen = false;   // the first box is gone some other way; nothing to unfreeze
            return;
        }
        if (!bi.IsWaitingAvailability)
        {
            // Cleared by a late reply that didn't hand us the box (a late refusal skips the cancel).
            Info($"The host answered late without giving {Describe(s_requested)}; forgetting the request");
            s_frozen = false;
            s_requested = null;
            s_requestedOver = null;
            return;
        }
        if (age > FreezeSeconds)
        {
            Info($"Still no answer for {Describe(s_requested)}; you can use {Describe(s_requestedOver)} again " +
                 "(if the host still hands the box over in the next seconds, it is stacked)");
            bi.IsWaitingAvailability = false;
            s_frozen = false;
        }
    }

    /// <summary>Make the bottom box the held box again, on every client.</summary>
    private static void TakeBottomBox(PlayerInstance p)
    {
        var box = s_bottom;
        // Clear first (this also switches its colliders back on): the calls below refresh the hints, and the
        // "open" hint must come back now.
        ResetState();

        var holder = p.PlayerObjectHolder;
        var pi = p.PlayerInteraction;
        var bi = p.BoxInteraction;
        box.transform.DOKill(false);

        var nb = box.NetworkBox;
        if (Online && nb != null && nb.PhotonView != null)
        {
            // The game's own "this player picked up this box" message. Everyone (this client immediately)
            // runs Box.PickUp(player): binds that player's BoxInteraction and HoldObjects it. The host still
            // has the box occupied by this player from the first pickup.
            var args = new Il2CppReferenceArray<Il2CppSystem.Object>(1);
            args[0] = p.UserId;
            nb.PhotonView.RPC("PickUP_RPC", RpcTarget.All, args);
        }
        if (!Same(holder.m_CurrentObject, box.gameObject) || !Same(box.transform.parent, holder.m_ObjectHolder))
            box.PickUp(p);

        // What PlayerInteraction.Interact does after a successful pickup click.
        if (!bi.Enable)
        {
            pi.SetCurrentInteractable(null);   // drop whatever the crosshair highlighted in the gap
            pi.m_CurrentInteractable = new IInteractable(box.Pointer);
            pi.SetCurrentInteraction(InteractactableType.BOX);
            pi.m_CurrentInteractable = null;
        }
        Info($"Second box back in your hands: {Describe(box)} (held={HeldNormally(p, box)})");
    }

    /// <summary>Release boxes the leaving player carried besides their bound box (see class comment).</summary>
    private static void RescueLeavingPlayersBoxes(PlayerInstance leaving)
    {
        var holder = leaving.PlayerObjectHolder;
        var hold = holder != null ? holder.m_ObjectHolder : null;
        if (hold == null) return;
        var bi = leaving.BoxInteraction;
        var bound = bi != null ? bi.m_Box : null;
        bool master = PhotonNetwork.IsMasterClient;

        var boxes = new List<Box>();
        for (int i = 0; i < hold.childCount; i++)
        {
            var c = hold.GetChild(i);
            var b = c != null ? c.GetComponent<Box>() : null;
            if (b != null && !Same(b, bound)) boxes.Add(b);
        }
        int interactable = LayerMask.NameToLayer("Interactable");
        foreach (var b in boxes)
        {
            var go = b.gameObject;
            var t = b.transform;
            t.DOKill(false);
            t.SetParent(null, true);
            if (interactable >= 0) go.layer = interactable;
            var cols = go.GetComponentsInChildren<Collider>(false);
            for (int i = 0; i < cols.Length; i++) { cols[i].enabled = true; cols[i].isTrigger = false; }
            b.SetOccupy(false, null);
            var nb = b.NetworkBox;
            if (nb != null)
            {
                nb.IsNetworkOccupied = false;
                nb.DisableNetworkTransformSync(true);   // despite the name: true turns sync back on
            }
            if (master)
            {
                var rb = b.RigidBody;
                if (rb != null)
                {
                    rb.isKinematic = false;
                    rb.interpolation = RigidbodyInterpolation.Interpolate;
                }
            }
            Plugin.Logger.LogInfo($"[PlayerCarry] {leaving.name} left while carrying {Describe(b)}; put it down");
        }
    }

    // ------------------------------------------------------------------ patches

    [HarmonyPatch(typeof(BoxInteraction), nameof(BoxInteraction.OnUse))]
    private static class OnUsePatch
    {
        private static bool Prefix(BoxInteraction __instance, bool down)
        {
            try
            {
                if (!down) return true;
                var p = __instance.PlayerInstance;
                if (p == null || !p.IsLocalPlayerInstance) return true;
                if (Busy)
                {
                    if (!NothingTargeted(__instance)) return true;   // rack / trash / placement area / placing: top box
                    LogBlocked("Can't open or use a box while carrying two; put the top one down first");
                    return false;
                }
                if (!WouldOpenBox(__instance)) return true;
                if (!Plugin.Enabled.Value) return true;
                var held = __instance.m_Box;
                if (!HeldNormally(p, held)) return true;
                var target = LookedAtBox(p, held);
                if (target == null) return true;
                if (Requesting)
                {
                    LogBlocked($"Still waiting for the host about {Describe(s_requested)}; try again in a few seconds");
                    return false;
                }
                if (target.IsOpen)
                {
                    LogBlocked($"{Describe(target)} is open; close it first to carry it as a second box");
                    return false;
                }
                RequestSecondBox(p, held, target);
                return false;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[PlayerCarry] OnUse prefix failed: {e}");
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(BoxInteraction), nameof(BoxInteraction.OnTake))]
    private static class OnTakePatch
    {
        private static bool Prefix(BoxInteraction __instance, bool down)
        {
            try
            {
                if (!down || !Busy || Promoting) return true;
                var p = __instance.PlayerInstance;
                if (p == null || !p.IsLocalPlayerInstance) return true;
                LogBlocked("Can't take products into a box while carrying two");
                return false;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[PlayerCarry] OnTake prefix failed: {e}");
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(PlayerObjectHolder), nameof(PlayerObjectHolder.HoldObject))]
    private static class HoldObjectPatch
    {
        private static void Prefix(PlayerObjectHolder __instance, GameObject item, out GameObject __state)
        {
            __state = null;
            try
            {
                BeforeHold(__instance, item);
                __state = __instance.m_CurrentObject;
            }
            catch (Exception e) { Plugin.Logger.LogError($"[PlayerCarry] HoldObject prefix failed: {e}"); }
        }

        private static void Postfix(PlayerObjectHolder __instance, GameObject item, Vector3 positionOffset,
                                    Vector3 rotationOffset, GameObject __state)
        {
            try { AfterHold(__instance, item, positionOffset, rotationOffset, __state); }
            catch (Exception e) { Plugin.Logger.LogError($"[PlayerCarry] HoldObject postfix failed: {e}"); }
        }
    }

    /// <summary>A refused / timed-out request for the second box must not end the first box's interaction.</summary>
    [HarmonyPatch(typeof(BoxInteraction), nameof(BoxInteraction.CancelInteracitonForOnlyMultiplayer))]
    private static class CancelPatch
    {
        private static bool Prefix(BoxInteraction __instance)
        {
            try
            {
                if (!Requesting) return true;
                var p = __instance.PlayerInstance;
                if (p == null || !p.IsLocalPlayerInstance) return true;
                if (!Same(__instance.m_Box, s_requestedOver) || !HeldNormally(p, s_requestedOver)) return true;
                var nbi = __instance.m_NetworkBoxInteraction;
                bool timedOut = nbi != null && nbi.m_AvailabilityTimedOut;
                if (timedOut)
                {
                    // Keep the box interaction frozen a little longer, like the game's own wait, so a late grant
                    // finds the first box still held normally and the two get stacked.
                    __instance.IsWaitingAvailability = true;
                    s_frozen = true;
                    Info($"No answer from the host yet for {Describe(s_requested)}; waiting a little longer, " +
                         $"you keep holding {Describe(s_requestedOver)}");
                }
                else
                {
                    Info($"The host refused {Describe(s_requested)} (someone else has it); you keep holding {Describe(s_requestedOver)}");
                    s_requested = null;
                    s_requestedOver = null;
                    s_frozen = false;
                }
                return false;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[PlayerCarry] Cancel prefix failed: {e}");
                return true;
            }
        }
    }

    /// <summary>No new pickups/interactions in the short gap before the second box comes back.</summary>
    [HarmonyPatch(typeof(PlayerInteraction), nameof(PlayerInteraction.Interact))]
    private static class InteractPatch
    {
        private static bool Prefix(PlayerInteraction __instance)
        {
            try
            {
                if (!Promoting) return true;
                var p = __instance.PlayerInstance;
                return p == null || !p.IsLocalPlayerInstance;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[PlayerCarry] Interact prefix failed: {e}");
                return true;
            }
        }
    }

    /// <summary>The "open" hint is wrong while two boxes are carried.</summary>
    [HarmonyPatch(typeof(BoxInteraction), nameof(BoxInteraction.UpdateOpeningBoxHint))]
    private static class OpenHintPatch
    {
        private static void Postfix(BoxInteraction __instance)
        {
            try
            {
                if (!Busy || Promoting) return;
                var p = __instance.PlayerInstance;
                if (p == null || !p.IsLocalPlayerInstance) return;
                var box = __instance.m_Box;
                if (box != null && !box.IsOpen) RemoveOpenHint(p);
            }
            catch (Exception e) { Plugin.Logger.LogError($"[PlayerCarry] Hint postfix failed: {e}"); }
        }
    }

    /// <summary>A player leaving with two boxes: don't let the second one be destroyed with their avatar.</summary>
    [HarmonyPatch(typeof(PlayerInstance), nameof(PlayerInstance.OnPlayerLeftRoom))]
    private static class PlayerLeftPatch
    {
        private static void Prefix(PlayerInstance __instance, Photon.Realtime.Player otherPlayer)
        {
            try
            {
                if (otherPlayer == null || __instance == null || __instance.IsLocalPlayerInstance) return;
                // The leaving player may already be gone from the room's player list, so compare the actor
                // number stored on the view (and the user id the game remembers) rather than view.Owner.
                var view = __instance.m_PhotonView;
                bool isLeaving = view != null && view.OwnerActorNr == otherPlayer.ActorNumber;
                if (!isLeaving && !string.IsNullOrEmpty(otherPlayer.UserId))
                    isLeaving = otherPlayer.UserId == __instance.LastPlayerUserId;
                if (isLeaving) RescueLeavingPlayersBoxes(__instance);
            }
            catch (Exception e) { Plugin.Logger.LogError($"[PlayerCarry] Player-left prefix failed: {e}"); }
        }
    }
}
