/*
 * Improvement #6: while moving furniture (shelves, fridges, freezers, racks, checkouts, ...), press
 * Escape (gamepad: B) to put it back exactly where it was picked up, if that spot is still free.
 * If it is not free, the key does nothing. The key is added to the key hints list.
 *
 * How moving furniture works (native dumps):
 *   - Every movable piece goes through the local player's FurniturePlacer: the interaction
 *     (FurnitureInteraction, DisplayInteraction, RackInteraction, CheckoutInteraction, ...) sets
 *     FurniturePlacer.m_CurrentPlacingMode and calls FurniturePlacer.StartPlacingMode(), whose first
 *     PlacingRaycast() already moves the piece to the crosshair. So the original transform is
 *     captured in a StartPlacingMode prefix.
 *   - FurniturePlacer.Update() -> PlacingRaycast() moves FurniturePlacingMode.Furniture (a Transform)
 *     to the crosshair every frame and sets PlacedOnCorrectSurface from the floor/wall raycast.
 *   - Validity = FurniturePlacer.CheckPlaceFurniture() = FurniturePlacingMode.AvailablePosition =
 *     no overlapping colliders (IPlacingMode.OverlapBoxes fills m_Triggers, and syncs transforms itself)
 *     && PlacedOnCorrectSurface. PlacedOnCorrectSurface comes from ResolvePlacement's raycast: the hit
 *     collider's tag must be in FurniturePlacer.m_CurrentPlacingTag. Blocked areas
 *     (PlacementBlockedArea.IsBlocked) clear it, but only when CurrentFurnitureAimsThroughWalls.
 *     The surface can be movable furniture (a category sign sits on a shelf), so the mod raycasts for a
 *     correctly tagged surface at the origin instead of assuming it is still there.
 *   - Putting it down = the player's Use click: PlayerInteraction.onUse(true) -> the active
 *     interaction's OnUse -> CheckPlaceFurniture -> broadcast + Place...(). Invoking onUse(true)
 *     therefore places any furniture type through the game's own code, including multiplayer sync.
 *   - Escape is the "Back" input action (also gamepad B). While moving furniture the player is "in an
 *     interaction" (PlayerInteraction.m_InInteraction), and EscapeMenuManager.Pause() ignores Escape
 *     then, so the pause menu doesn't open. Nothing else uses Back in this mode.
 *   - Furniture that has just come out of a furniture box also goes through StartPlacingMode; it has no
 *     previous spot. Unboxing (ice cream stands included) runs FurnitureBoxInteraction.CheckFurnitureType
 *     -> PlayerInteraction.Interact -> ... -> StartPlacingMode synchronously, so a pickup in the same
 *     frame as CheckFurnitureType is treated as "from a box" and ignored.
 *
 * On Escape: move the piece to its original position/rotation, run the checks, and if valid, freeze the
 * placer (skip FurniturePlacer.Update) for ReturnDelaySeconds (default 0 = place in the same frame),
 * re-check, then invoke onUse(true).
 * If invalid, restore the hologram where it was.
 *
 * Multiplayer: purely local to the player who moves the furniture; the placement itself uses the
 * vanilla path. Works for host or guest, whoever has the mod installed. The place RPCs of Furniture,
 * Rack, Checkout, CategorySign, Scale, IceCream stand and Computer carry NO position: other clients
 * place their copy wherever the Photon transform view has streamed it so far. So before placing, the
 * mod sends the exact pose with PhotonTransformViewOptimized.BroadcastCurrentTransform (reliable,
 * ordered before the place RPC). Only if the piece has no such view does it fall back to holding the
 * piece for at least MinOnlineDelay so the stream catches up. (Display, Vending, StorageLight and
 * StoreLight send pos/rot in their place RPCs anyway.)
 */
using System;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FurnitureMoveCancel;

internal static class MoveCancel
{
    // Captured when the local player picks a piece up.
    private static FurniturePlacer _placer;
    private static IntPtr _placingModePtr;
    private static Vector3 _originPos;
    private static Quaternion _originRot;
    private static string _what;

    // Escape pressed and the spot was free: holding the piece at its origin until _placeAt.
    private static bool _returning;
    private static float _placeAt;

    private static IntPtr _hintItem; // our InteractionHintItem, so we never remove someone else's
    private static int _fromBoxFrame = -1;

    private static InputAction _backAction;
    private static IntPtr _backActionOwner;

    private const InputActionID HintId = InputActionID.CANCEL;

    /// <summary>Online, other clients need this long to receive the streamed origin (see header).</summary>
    internal const float MinOnlineDelay = 0.3f;

    internal static void Reset()
    {
        RemoveHint();
        _placer = null;
        _placingModePtr = IntPtr.Zero;
        _returning = false;
        _fromBoxFrame = -1;
        _backAction = null;
        _backActionOwner = IntPtr.Zero;
    }

    private static void Log(string msg)
    {
        if (Plugin.Verbose.Value) Plugin.Logger.LogInfo("[MoveCancel] " + msg);
    }

    // ---------------------------------------------------------------- capture

    internal static void OnStartPlacing(FurniturePlacer placer)
    {
        if (placer.m_PlacingMode) return; // StartPlacingMode returns early in this case
        var player = placer.OwnPlayerInstance;
        if (player == null || !player.IsLocalPlayerInstance) return;

        // Any previous origin belongs to an earlier move.
        RemoveHint();
        _placer = null;
        _returning = false;
        // Re-resolve the Back action on every pickup: a new PlayerInstance after a scene change can
        // reuse the old one's address while the cached InputAction belongs to a disposed asset.
        _backAction = null;
        _backActionOwner = IntPtr.Zero;

        if (_fromBoxFrame == Time.frameCount)
        {
            Log("new furniture from a box: no original spot to return to");
            return;
        }
        var pm = placer.m_CurrentPlacingMode;
        var t = pm != null ? pm.Furniture : null;
        if (t == null) return;

        _placer = placer;
        _placingModePtr = pm.Pointer;
        _originPos = t.position;
        _originRot = t.rotation;
        _what = SafeName(t);
        Log($"picked up {_what} at {_originPos}");
    }

    internal static void MarkFromBox() => _fromBoxFrame = Time.frameCount;

    /// <summary>True while the placer must not move the piece (it's being put back).</summary>
    internal static bool IsFrozen(FurniturePlacer placer) =>
        _returning && _placer != null && placer.Pointer == _placer.Pointer;

    // ---------------------------------------------------------------- per frame

    internal static void Tick()
    {
        if (_placer == null) return;
        FurniturePlacingMode pm;
        try
        {
            pm = StillMoving();
            if (pm == null)
            {
                // Placed, boxed up, cancelled or destroyed: forget the origin.
                if (_returning) Log($"{_what} put back at its original spot");
                RemoveHint();
                _placer = null;
                _returning = false;
                return;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[MoveCancel] state check: {e}");
            Reset();
            return;
        }

        try
        {
            if (Plugin.ShowHint.Value) EnsureHint();
            else RemoveHint();

            if (_returning)
            {
                if (Time.unscaledTime >= _placeAt) FinishReturn(pm);
            }
            else if (BackPressed())
            {
                StartReturn(pm);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[MoveCancel] {e}");
            _returning = false;
        }
    }

    /// <summary>The placing mode we captured, if the local player is still moving that piece.</summary>
    private static FurniturePlacingMode StillMoving()
    {
        if (_placer == null || !_placer.m_PlacingMode) return null;
        var pm = _placer.m_CurrentPlacingMode;
        if (pm == null || pm.Pointer != _placingModePtr) return null;
        return pm;
    }

    // ---------------------------------------------------------------- return

    private static void StartReturn(FurniturePlacingMode pm)
    {
        var t = pm.Furniture;
        if (t == null) return;
        var holoPos = t.position;
        var holoRot = t.rotation;
        bool holoSurface = pm.PlacedOnCorrectSurface;

        if (!TryPutAtOrigin(pm, out string why))
        {
            // Not free: leave the piece where the player was holding it. The key does nothing.
            t.SetPositionAndRotation(holoPos, holoRot);
            pm.PlacedOnCorrectSurface = holoSurface;
            pm.OverlapBoxes();
            Log($"Escape pressed, but the original spot of {_what} is not free ({why}); doing nothing");
            return;
        }

        float delay = Mathf.Max(0f, Plugin.ReturnDelay.Value);
        string how = "offline";
        if (Photon.Pun.PhotonNetwork.IsConnected)
        {
            if (BroadcastExactTransform(t))
                how = "exact position sent to other players";
            else
            {
                delay = Mathf.Max(delay, MinOnlineDelay);
                how = "no transform sync found, holding so the streamed position catches up";
            }
        }
        _returning = true;
        _placeAt = Time.unscaledTime + delay;
        Log($"Escape pressed: returning {_what} to {_originPos} ({how}; placing in {delay:0.##}s)");
        if (delay <= 0f) FinishReturn(pm);
    }

    /// <summary>
    /// Send the piece's exact pose to every client with the game's own reliable "SyncTransform" RPC
    /// (PhotonTransformViewOptimized.BroadcastCurrentTransform). Receivers SetPositionAndRotation and
    /// reset their interpolation target to it. Reliable RPCs are delivered in order, so it arrives
    /// before the place RPC that onUse(true) sends right after, and the other clients place their copy
    /// at the origin with no waiting. Only sends when we own the view (the mover does while carrying).
    /// </summary>
    private static bool BroadcastExactTransform(Transform piece)
    {
        try
        {
            var view = piece.GetComponentInParent<PhotonTransformViewOptimized>(true);
            if (view == null) view = piece.GetComponentInChildren<PhotonTransformViewOptimized>(true);
            if (view == null) return false;
            // The view sends its own transform: it must have moved with the piece.
            if (!view.transform.IsChildOf(piece)) return false;
            var pv = view.photonView;
            if (pv == null || !pv.IsMine) return false;
            view.BroadcastCurrentTransform();
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[MoveCancel] exact position broadcast failed: {e.Message}");
            return false;
        }
    }

    private static void FinishReturn(FurniturePlacingMode pm)
    {
        if (!TryPutAtOrigin(pm, out string why))
        {
            // Something moved into the spot during the delay: give control back. PlacedOnCorrectSurface
            // may still be forced/stale here; the next PlacingRaycast recomputes it.
            _returning = false;
            Log($"original spot of {_what} got blocked ({why}); cancelled");
            return;
        }

        var player = _placer.OwnPlayerInstance;
        var onUse = player != null && player.PlayerInteraction != null ? player.PlayerInteraction.onUse : null;
        if (onUse == null)
        {
            _returning = false;
            Plugin.Logger.LogWarning("[MoveCancel] no Use handler found; cannot place");
            return;
        }

        // Same as the player clicking Use: the active interaction places it through vanilla code.
        onUse.Invoke(true);

        if (StillMoving() != null)
        {
            // The interaction refused (e.g. its short place cooldown right after picking up).
            _returning = false;
            Log($"the game did not accept placing {_what} yet; press Escape again");
        }
        // Success is logged and cleaned up by Tick once the placer leaves placing mode.
    }

    /// <summary>Move the piece to its origin and run the game's own placement check there.</summary>
    private static bool TryPutAtOrigin(FurniturePlacingMode pm, out string why)
    {
        var t = pm.Furniture;
        t.SetPositionAndRotation(_originPos, _originRot);
        // Same rule as FurniturePlacer.EnforceBlockedAreas: only for pieces aimed through walls.
        if (_placer.CurrentFurnitureAimsThroughWalls && PlacementBlockedArea.IsBlocked(_originPos))
        {
            why = "blocked area";
            return false;
        }
        pm.OverlapBoxes(); // syncs transforms and refreshes the overlapping-collider list
        // The per-frame surface raycast isn't running while frozen, so check the surface ourselves:
        // what the piece stood on may have been moved or sold (e.g. the shelf under a category sign).
        if (!SurfaceStillThere(t))
        {
            why = "the surface it stood on is gone";
            return false;
        }
        pm.PlacedOnCorrectSurface = true;
        if (!_placer.CheckPlaceFurniture())
        {
            why = "something is in the way";
            return false;
        }
        why = null;
        return true;
    }

    private const float SurfaceProbe = 0.5f;    // how far from the pivot the surface may be
    private static readonly Vector3[] ProbeDirs = { Vector3.down, Vector3.up, Vector3.back, Vector3.forward };

    /// <summary>
    /// True if a collider with one of the placer's placing tags (the tags ResolvePlacement accepts)
    /// touches the piece at its origin: short raycasts from the pivot down, up, back and forward
    /// (local axes), ignoring the piece's own colliders.
    /// </summary>
    private static bool SurfaceStillThere(Transform piece)
    {
        var tags = _placer.m_CurrentPlacingTag;
        if (tags == null || tags.Count == 0) return true; // nothing to check against
        int mask = _placer.CurrentPlacingRaycastLayer.value;
        foreach (var local in ProbeDirs)
        {
            var dir = piece.TransformDirection(local);
            var from = _originPos - dir * SurfaceProbe;
            var hits = Physics.RaycastAll(from, dir, SurfaceProbe * 2f, mask, QueryTriggerInteraction.Ignore);
            if (hits == null) continue;
            foreach (var hit in hits)
            {
                var col = hit.collider;
                if (col == null || col.transform.IsChildOf(piece)) continue;
                if (tags.Contains(col.tag)) return true;
            }
        }
        return false;
    }

    // ---------------------------------------------------------------- input and hint

    private static bool BackPressed()
    {
        var player = _placer.OwnPlayerInstance;
        if (player != null && _backActionOwner != player.Pointer)
        {
            _backAction = null;
            _backActionOwner = player.Pointer;
            try
            {
                var asset = player.InputActions?.m_PlayerInput?.actions;
                _backAction = asset?.FindAction("Back", false);
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"[MoveCancel] Back action lookup failed: {e.Message}"); }
            if (_backAction == null)
                Plugin.Logger.LogWarning("[MoveCancel] 'Back' input action not found; using the Escape key directly");
        }
        if (_backAction != null) return _backAction.WasPressedThisFrame();
        var kb = Keyboard.current;
        return kb != null && kb.escapeKey.wasPressedThisFrame;
    }

    private static void EnsureHint()
    {
        var player = _placer.OwnPlayerInstance;
        if (player == null) return;
        if (!NoktaSingleton<InteractionHintsManager>.HasInstance) return;
        var hm = NoktaSingleton<InteractionHintsManager>.Instance;
        if (hm == null || hm.Hints == null) return;
        // The interactions clear and rebuild their hints at times; re-add ours whenever it's missing.
        // If the slot is used by another hint, leave it alone.
        if (hm.Hints.ContainsKey((int)HintId)) return;
        // "_cancel" is the game's own localized "Cancel" text; "Back" gives the Escape / B key icon.
        hm.AddHint(HintId, "Back", "_cancel", player);
        _hintItem = hm.Hints.ContainsKey((int)HintId) ? hm.Hints[(int)HintId].Pointer : IntPtr.Zero;
    }

    private static void RemoveHint()
    {
        if (_hintItem == IntPtr.Zero) return;
        var ours = _hintItem;
        _hintItem = IntPtr.Zero;
        try
        {
            if (!NoktaSingleton<InteractionHintsManager>.HasInstance) return;
            var hm = NoktaSingleton<InteractionHintsManager>.Instance;
            var player = _placer != null ? _placer.OwnPlayerInstance : null;
            // Only if the slot still holds our item: the next interaction may already have added its own.
            if (hm != null && player != null && hm.Hints != null && hm.Hints.ContainsKey((int)HintId)
                && hm.Hints[(int)HintId].Pointer == ours)
                hm.RemoveHint(HintId, player);
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[MoveCancel] RemoveHint: {e.Message}"); }
    }

    private static string SafeName(Component c)
    {
        try { return c.gameObject.name; } catch { return "furniture"; }
    }
}

// ==================================================================== patches

[HarmonyPatch(typeof(FurniturePlacer), nameof(FurniturePlacer.StartPlacingMode))]
internal static class StartPlacingModePatch
{
    private static void Prefix(FurniturePlacer __instance)
    {
        try { MoveCancel.OnStartPlacing(__instance); }
        catch (Exception e) { Plugin.Logger.LogError($"[MoveCancel] StartPlacingMode prefix: {e}"); }
    }
}

/// <summary>Holds the piece at its original spot while it is being put back.</summary>
[HarmonyPatch(typeof(FurniturePlacer), "Update")]
internal static class PlacerUpdatePatch
{
    private static bool Prefix(FurniturePlacer __instance)
    {
        try { return !MoveCancel.IsFrozen(__instance); }
        catch { return true; }
    }
}

/// <summary>
/// Furniture unpacked from a box (ice cream stands included) has no previous spot. Unboxing starts
/// placement synchronously inside this call, so marking the frame is enough and can't get stuck.
/// </summary>
[HarmonyPatch(typeof(FurnitureBoxInteraction), nameof(FurnitureBoxInteraction.CheckFurnitureType))]
internal static class FromBoxPatch
{
    private static void Prefix()
    {
        try { MoveCancel.MarkFromBox(); } catch { }
    }
}
