using System;
using BepInEx.Configuration;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Photon.Pun;
using UnityEngine;

namespace SupermarketFixes.Patches;

/// <summary>
/// Bug #3: after buying a box in a local market, the box stays on the shelf, the player's hands look
/// empty, but every input goes to that box (left click opens/closes it, drop/throw do nothing) until
/// the game is reloaded.
///
/// How buying works (native analysis of GameAssembly.dll):
///  - PurchasableProduct.HoldingInteract -> Purchase: pays, spawns the box at the product's position
///    (BoxGenerator.SpawnBoxLocalMarket -> PhotonNetwork.Instantiate, owned by the buyer), then fakes a
///    "take" click on it: PlayerInteraction.SetCurrentInteractable(box) + Interact().
///  - Interact -> Box.InstantInteract. Offline, or on the host, the box is picked up synchronously.
///    On a guest it only sends NetworkBox.AskAvailability (BoxInteraction.IsWaitingAvailability = true,
///    3 s timeout) and returns true, so Interact immediately binds BoxInteraction to the box
///    (SetCurrentInteraction(Box) -> BoxInteraction.m_Box = box) BEFORE the box is in the player's hands.
///  - The host answers with AvailabilityCallback_RPC: GRANT -> PickUP_RPC -> Box.PickUp(player) ->
///    PlayerObjectHolder.HoldObject; REJECT (box IsNetworkOccupied or claimed by an NPC) ->
///    AvailabilityRejected_Response -> BoxInteraction.CancelInteracitonForOnlyMultiplayer; no answer
///    within 3 s -> the same cancel.
///  - BoxInteraction.OnUse (open/close) works on m_Box directly, but drop and throw go through
///    PlayerObjectHolder, which acts only on the object it actually holds. So "BoxInteraction bound to a
///    box that PlayerObjectHolder isn't holding, and no longer waiting" is exactly the reported state.
///    The game's own diagnostics call it "M_BOX_NOT_HELD_OBJECT" / "M_BOX_NOT_UNDER_PLAYER"
///    (PlayerInteraction.BoxDiagWatchdog writes "[BoxDiag] WATCHDOG MISMATCH" to Player.log, even on
///    unmodded builds), but only log it.
///  Two native paths lead there (not yet confirmed which one happens in-game):
///   a) Box.InstantInteract on a guest returns true WITHOUT asking the host when box.NetworkBox is
///      Unity-null (destroyed): bound, not waiting, not held. Player.log would show no "AskAvailability".
///      This can't happen on the purchase path while online (Purchase dereferences box.NetworkBox for
///      Setup_Broadcast before it calls Interact, so it would throw first); it only applies to other clicks.
///   b) An exception inside Box.PickUp after it bound BoxInteraction but before HoldObject, after
///      EndAvailabilityWait already cleared the wait. Player.log would show "[BoxDiag] PickUP_RPC BEFORE"
///      with no matching "PickUP_RPC AFTER".
///  Note: PlayerObjectHolder.m_CurrentObject is written by HoldObject; no native caller of
///  SetNullCurrentObject / set_CurrentObject was found, and drops don't clear it, so "held" must also check
///  that the object is still in the hands.
///
/// Fix (runs for this machine's own player, so it must be installed on the machine that gets stuck):
///  - Watchdog: if the stuck state lasts StuckSeconds (or a pickup wait lasts 6 s, i.e. the game's own
///    3 s timeout never ran; so an answer that never arrives is handled after about 6 s + StuckSeconds), end the box
///    interaction the way the game does on a rejected pickup, reset the BoxInteraction flags DropBox
///    would reset, then retry the pickup once through the normal path (same calls Purchase makes).
///    If it gets stuck again, only free the controls; the box stays where it is. The one-retry guard is
///    per box and is cleared once the box is confirmed held (or after RetryAgainSeconds), so a later
///    incident, or a LeanPool-reused Box object, gets its retry too. There is no cap on recoveries: a
///    recovery only happens after the player clicked the box again, so nothing can loop.
///  - F9 on a pickup that is still waiting for the host does nothing until the game's own 3 s timeout has
///    passed, so it can't cancel a valid pending pickup.
///  - Host side: if a player asks for a box the host already considers held by that same player
///    (what a retry after a lost pickup looks like), grant it again instead of rejecting it forever.
/// </summary>
internal static class StuckBoxFix
{
    internal static ConfigEntry<bool> AutoRecover;
    internal static ConfigEntry<float> StuckSeconds;
    internal static ConfigEntry<bool> RetryPickup;
    internal static ConfigEntry<bool> RegrantToSameHolder;

    /// <summary>The game's own pickup wait times out after 3 s; a wait twice that long is stuck.</summary>
    private const float MaxWaitSeconds = 6f;
    /// <summary>The game's own pickup timeout; F9 doesn't cancel a wait younger than this.</summary>
    private const float GameWaitTimeoutSeconds = 3f;
    /// <summary>A retried box may be retried again after this long, even if it was never seen held.</summary>
    private const float RetryAgainSeconds = 30f;
    /// <summary>Retry only boxes within reach, so a retry can't grab something across the store.</summary>
    private const float MaxRetryDistance = 5f;

    internal static void Bind(ConfigFile cfg)
    {
        AutoRecover = cfg.Bind("StuckBox", "AutoRecover", true,
            "If your controls stay locked onto a box that is not in your hands (e.g. a box just bought in a " +
            "market that stayed on the shelf), free them automatically after a short delay.");
        StuckSeconds = cfg.Bind("StuckBox", "StuckSeconds", 2f,
            new ConfigDescription("How long the stuck state must last before it is fixed.",
                new AcceptableValueRange<float>(0.5f, 10f)));
        RetryPickup = cfg.Bind("StuckBox", "RetryPickup", true,
            "After freeing the controls, try once more to pick the box up the normal way.");
        RegrantToSameHolder = cfg.Bind("StuckBox", "RegrantToSameHolder", true,
            "Host only: when a player asks to pick up a box the host thinks that same player is already " +
            "holding, allow it instead of refusing. Lets a stuck player's retry succeed.");
    }

    internal static void ResetState()
    {
        s_box = IntPtr.Zero;
        s_since = -1f;
        s_waitBox = IntPtr.Zero;
        s_waitSince = -1f;
        s_retried = IntPtr.Zero;
        s_retriedAt = -1f;
    }

    private static IntPtr s_box;          // box currently seen as stuck
    private static float s_since = -1f;
    private static IntPtr s_waitBox;      // box we are waiting on the host for
    private static float s_waitSince = -1f;
    private static IntPtr s_retried;      // box we already retried, so a retry can't loop; cleared once held
    private static float s_retriedAt = -1f;

    /// <summary>
    /// BoxInteraction is bound to a box that this player isn't actually holding, or has been waiting for
    /// the host's answer far longer than the game's own timeout.
    /// </summary>
    internal static Box FindStuckBox(PlayerInstance p, float now, bool force, out string reason)
    {
        reason = null;
        var bi = p.BoxInteraction;
        if (bi == null || !bi.Enable) { s_waitBox = IntPtr.Zero; return null; }
        var box = bi.m_Box;
        if (box == null) { s_waitBox = IntPtr.Zero; return null; }

        if (bi.IsWaitingAvailability)
        {
            if (s_waitBox != box.Pointer) { s_waitBox = box.Pointer; s_waitSince = now; }
            float waited = now - s_waitSince;
            if (waited < (force ? GameWaitTimeoutSeconds : MaxWaitSeconds)) return null;
            reason = $"waiting for the host for {waited:F1}s";
            return box;
        }
        s_waitBox = IntPtr.Zero;

        var holder = p.PlayerObjectHolder;
        var hold = holder != null ? holder.m_ObjectHolder : null;
        // m_CurrentObject is never cleared on release, so also require the box to still be in the hands
        // (or held in placing mode, where it is unparented on purpose).
        bool held = holder != null && HeldState.SameObject(holder.m_CurrentObject, box.gameObject) &&
                    (holder.m_PlacingMode || (hold != null && box.transform.IsChildOf(hold)));
        if (held)
        {
            if (s_retried == box.Pointer) s_retried = IntPtr.Zero; // pickup worked; a later incident may retry
            return null;
        }
        reason = "bound to a box that is not in your hands";
        return box;
    }

    /// <summary>Called every frame from DiagnosticsBehaviour.</summary>
    internal static void Tick(float now)
    {
        var p = HeldState.LocalPlayer();
        string reason = null;
        if (!AutoRecover.Value)
        {
            // Keep tracking how long a pickup has been waiting, so F9 can tell a stale wait from a fresh one.
            s_box = IntPtr.Zero;
            if (p != null) FindStuckBox(p, now, false, out reason);
            return;
        }
        var box = p != null ? FindStuckBox(p, now, false, out reason) : null;
        if (box == null) { s_box = IntPtr.Zero; return; }

        if (box.Pointer != s_box)
        {
            s_box = box.Pointer;
            s_since = now;
            return;
        }
        if (now - s_since < StuckSeconds.Value) return;
        s_box = IntPtr.Zero;

        Recover(p, box, $"{reason}, for {StuckSeconds.Value:F1}s");
    }

    /// <summary>F9: fix it now if the state is present.</summary>
    internal static void ForceCheck()
    {
        var p = HeldState.LocalPlayer();
        string reason = null;
        var box = p != null ? FindStuckBox(p, Time.unscaledTime, true, out reason) : null;
        if (box != null) Recover(p, box, $"F9, {reason}");
        else if (p != null && p.BoxInteraction != null && p.BoxInteraction.Enable && p.BoxInteraction.IsWaitingAvailability)
            Plugin.Logger.LogInfo("[StuckBox] F9: still waiting for the host to answer the pickup; try again in a few seconds");
        else Plugin.Logger.LogInfo("[StuckBox] F9: controls are not stuck on a box");
    }

    private static void Recover(PlayerInstance p, Box box, string why)
    {
        try
        {
            Plugin.Logger.LogWarning($"[StuckBox] Controls stuck on a box ({why}). " +
                                     $"{HeldState.DescribePlayer(p)} {HeldState.Describe(box)}");
            var bi = p.BoxInteraction;
            var pi = p.PlayerInteraction;
            var owner = box.m_OccupyOwner;
            bool ours = owner != null && owner.IsChildOf(p.transform);
            bool takenByOther = box.m_IsBoxOccupied && owner != null && !ours;

            // Same cleanup the game does when the host rejects a pickup or it times out...
            if (bi.IsWaitingAvailability && bi.m_NetworkBoxInteraction != null)
                bi.m_NetworkBoxInteraction.EndAvailabilityWait();
            bi.CancelInteracitonForOnlyMultiplayer();
            // ...plus what BoxInteraction.DropBox resets: a drop pressed while stuck leaves m_PlacingMode
            // set, which would make the next box drop on its first click.
            bi.m_Box = null;
            bi.m_PlacingMode = false;
            bi.m_holdingUse = false;
            bi.m_holdingTake = false;
            if (box.m_IsBoxOccupied && ours) box.SetOccupy(false, null);
            // IsNetworkOccupied is host-only state; on the host our own retry goes through
            // AvailabilityCallback_RPC directly and would otherwise be refused.
            var nb = box.NetworkBox;
            if (nb != null && !takenByOther && (!PhotonNetwork.IsConnected || PhotonNetwork.IsMasterClient))
                nb.IsNetworkOccupied = false;
            Plugin.Logger.LogInfo("[StuckBox] Controls freed");

            if (!RetryPickup.Value || takenByOther || pi == null) return;
            if (s_retried == box.Pointer && Time.unscaledTime - s_retriedAt < RetryAgainSeconds)
            {
                Plugin.Logger.LogWarning("[StuckBox] Already retried this box; leaving it where it is. " +
                                         "Look at it and click to pick it up again.");
                return;
            }
            string skip = RetrySkipReason(p, box);
            if (skip != null)
            {
                if (Plugin.VerboseLogging.Value) Plugin.Logger.LogInfo($"[StuckBox] Not retrying the pickup: {skip}");
                return;
            }
            s_retried = box.Pointer;
            s_retriedAt = Time.unscaledTime;
            pi.SetCurrentInteractable(box.Cast<IInteractable>());
            pi.Interact(false, false);
            if (Plugin.VerboseLogging.Value)
                Plugin.Logger.LogInfo($"[StuckBox] Retried pickup: {HeldState.DescribePlayer(p)}");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[StuckBox] Recovery failed: {e}");
        }
    }

    /// <summary>Retrying a racked box would pull it out without updating its slot (Bug #1 ghosts).</summary>
    private static string RetrySkipReason(PlayerInstance p, Box box)
    {
        if (!box.gameObject.activeInHierarchy) return "box is inactive";
        if (box.m_Racked || box.GetComponentInParent<RackSlot>(true) != null) return "box is on a rack";
        float d = Vector3.Distance(box.transform.position, p.transform.position);
        if (d > MaxRetryDistance) return $"box is {d:F1} m away";
        return null;
    }

    // ------------------------------------------------------------------ host side

    [HarmonyPatch(typeof(NetworkBox), nameof(NetworkBox.AvailabilityCallback_RPC))]
    private static class RegrantPatch
    {
        private static void Prefix(NetworkBox __instance, string userID)
        {
            try
            {
                if (!RegrantToSameHolder.Value || __instance == null || !__instance.IsNetworkOccupied) return;
                var box = __instance.Box;
                var owner = box != null ? box.m_OccupyOwner : null;
                if (owner == null || !box.m_IsBoxOccupied) return;
                var pm = NoktaSingleton<PlayerManager>.Instance;
                var requester = pm != null ? pm.GetPlayerInstanceByUserID(userID) : null;
                if (requester == null || owner.Pointer != requester.transform.Pointer) return;

                __instance.IsNetworkOccupied = false;
                Plugin.Logger.LogInfo($"[StuckBox] {requester.name} asked again for a box it already holds; " +
                                      $"granting it again. {HeldState.Describe(box)}");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[StuckBox] Regrant check failed: {e}");
            }
        }
    }
}
