using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SupermarketFixes.Patches;

/// <summary>
/// Bug #5: with an open box in hand, holding left click on an empty shelf slot sometimes places nothing
/// (reported mostly for slots whose old label was just cleared). Closing the box (C) and opening it
/// again (left click) makes it work.
///
/// How placing works (native analysis of GameAssembly.dll):
///  - BoxInteraction.Update raycasts every frame (CheckForInteraction -> SetCurrentDisplaySlot) and then
///    calls TryPlaceProductToDisplay only when ALL of these hold: m_holdingUse (Use button down),
///    m_Box.IsOpen, !m_holdingTake (Take button not down), m_IsBoxOpen, Box.HasProducts, and
///    Time.time - m_lastPlaceTime >= m_ProductInteractionInterval * product factor.
///  - CheckPlaceProductToSlot then only refuses a display slot if the product's display type doesn't
///    match the display, if the slot HasProduct (ProductCount > 0) with a DIFFERENT product, or if the
///    slot is Full. Labels are never checked: an empty slot with another product's label is accepted,
///    and DisplaySlot.AddProduct relabels it. So clearing a label can't change whether placing is
///    allowed. The block has to be in the BoxInteraction state.
///  - m_IsBoxOpen is BoxInteraction's own copy of "the box is open", separate from Box.IsOpen
///    (BoxData.IsOpen). It is written in only three places:
///      set_Interactable (when a box is bound): m_IsBoxOpen = box.IsOpen
///      OnClose (C key): Box.CloseBox(false), then m_IsBoxOpen = false UNCONDITIONALLY
///      OnUse on a closed box: Box.OpenBox(), then DOVirtual.DelayedCall(m_MinTimeToStartPlacingAfter
///        OpeningBox) -> m_IsBoxOpen = true (also unconditional, even if the box didn't open)
///  - BoxInteraction.OnUse on a box that is ALREADY open only sets m_holdingUse and returns.
///  So if the box ends up open while m_IsBoxOpen is false, every click does nothing until the player
///  closes the box (m_IsBoxOpen = false, box closes) and opens it again (box opens, m_IsBoxOpen = true
///  after the delay): exactly the reported workaround. Native paths into that state:
///   a) Box.OpenBox / Box.CloseBox ignore the request while Box.m_InAnimationTransition is set (0.7 s
///      after every open/close, SetInAnimationTransition). Pressing C within 0.7 s of opening: the box
///      stays open, but OnClose still sets m_IsBoxOpen = false.
///   b) Online, NetworkBoxInteraction.PlaceProduct_RPC and TakeProduct_RPC call Box.OpenBox() on the
///      box if it is closed. A guest's place/take requests are answered by the host after a round
///      trip; if the player closed the box in between, the late RPC opens it again behind
///      BoxInteraction's back (m_IsBoxOpen stays false). Taking the old product off the shelf into a
///      box and then closing it is the typical lead-up to "a slot whose label I just cleared".
///   Not yet confirmed in-game which one happens; the [BoxPlace] log lines will show it.
///  - m_holdingTake has the same kind of hazard: it is only cleared by the Take release
///    (PlayerInteraction.OnTake canceled -> onTake(false)) or by DropBox/OnDisable. PlayerInteraction
///    drops input callbacks while paused or in a menu, so a release that happens there is lost and
///    m_holdingTake stays true, which also blocks placing (and keeps trying to take items instead).
///
/// Fix (this machine's own player only; all of it is local input state, nothing is networked):
///  - ResyncOpenState: while the player holds Use with an open box but m_IsBoxOpen is false, the box
///    isn't animating, and the game's own "just opened" delay isn't pending, set m_IsBoxOpen = true.
///    Done in a BoxInteraction.Update prefix, so placing starts in the same frame.
///  - ReleaseStuckTake: if m_holdingTake is set but the Take action hasn't been pressed for 0.5 s,
///    clear it. (The game never simulates a held Take; ClickSimulate only fakes Use.)
///  - VerboseLogging: if Use is held on a display slot for 1.5 s and the box's product count didn't change,
///    log one line with every placing condition, so a remaining cause can be identified from the log.
/// </summary>
internal static class BoxPlaceFix
{
    internal static ConfigEntry<bool> ResyncOpenState;
    internal static ConfigEntry<bool> ReleaseStuckTake;

    /// <summary>Extra time on top of the game's own open delay before we consider its callback lost.</summary>
    private const float OpenDelayMargin = 0.15f;
    /// <summary>How long Take must be physically released while the game still thinks it's held.</summary>
    private const float StuckTakeSeconds = 0.5f;
    /// <summary>How long Use must be held on a slot without anything being placed before we log why.</summary>
    private const float NoPlaceLogSeconds = 1.5f;

    internal static void Bind(ConfigFile cfg)
    {
        ResyncOpenState = cfg.Bind("BoxPlace", "ResyncOpenState", true,
            "If the box in your hands is open but the game still treats it as closed (clicking a shelf " +
            "places nothing until you close and reopen the box), fix it as soon as you hold left click.");
        ReleaseStuckTake = cfg.Bind("BoxPlace", "ReleaseStuckTake", true,
            "If the game still thinks you are holding the take button (right click) after you let go of " +
            "it, which also stops you from placing items, release it.");
    }

    internal static void ResetState()
    {
        s_openedBox = IntPtr.Zero;
        s_openedAt = -100f;
        s_takeReleasedSince = -1f;
        s_takePlayer = IntPtr.Zero;
        s_takeAction = null;
        s_pressBox = IntPtr.Zero;
        s_pressSlot = IntPtr.Zero;
        s_pressLogged = false;
        s_loggedError = false;
    }

    private static IntPtr s_openedBox;      // box the player last tried to open with a click
    private static float s_openedAt = -100f;
    private static float s_takeReleasedSince = -1f;
    private static IntPtr s_takePlayer;     // player whose Take action is cached
    private static InputAction s_takeAction;
    private static IntPtr s_pressBox;       // current "holding Use on a slot" session, for diagnostics
    private static IntPtr s_pressSlot;
    private static float s_pressSince;
    private static int s_pressCount;
    private static bool s_pressLogged;
    private static bool s_loggedError;

    internal static void LogError(string where, Exception e)
    {
        if (s_loggedError) return;
        s_loggedError = true;
        Plugin.Logger.LogError($"[BoxPlace] {where} failed (further errors suppressed): {e}");
    }

    private static PlayerInstance LocalOwner(BoxInteraction bi)
    {
        var p = HeldState.LocalPlayer();
        if (p == null) return null;
        var mine = p.BoxInteraction;
        return mine != null && mine.Pointer == bi.Pointer ? p : null;
    }

    /// <summary>Before vanilla OnUse: remember when a click tries to open a closed box.</summary>
    internal static void BeforeOnUse(BoxInteraction bi, bool down)
    {
        if (!down || LocalOwner(bi) == null) return;
        var box = bi.m_Box;
        if (box == null || bi.IsWaitingAvailability || box.IsOpen) return;
        // Vanilla may open the box now and set m_IsBoxOpen after its delay. Recording this when the
        // click goes elsewhere (rack, trash) only postpones a resync by that delay, which is harmless.
        s_openedBox = box.Pointer;
        s_openedAt = Time.unscaledTime;
    }

    /// <summary>Before vanilla Update: repair the flags that would block placing this frame.</summary>
    internal static void BeforeUpdate(BoxInteraction bi)
    {
        if (bi.m_PlacingMode) return;
        var box = bi.m_Box;
        if (box == null) { s_pressBox = IntPtr.Zero; return; }
        var player = LocalOwner(bi);
        if (player == null) return;
        float now = Time.unscaledTime;

        if (ReleaseStuckTake.Value) CheckStuckTake(bi, player, box, now);
        else s_takeReleasedSince = -1f;

        if (ResyncOpenState.Value && bi.m_holdingUse && !bi.IsWaitingAvailability && box.IsOpen &&
            !bi.m_IsBoxOpen && !box.m_InAnimationTransition)
        {
            bool openDelayPending = s_openedBox == box.Pointer &&
                                    now - s_openedAt < bi.m_MinTimeToStartPlacingAfterOpeningBox + OpenDelayMargin;
            if (!openDelayPending)
            {
                bi.m_IsBoxOpen = true;
                Plugin.Logger.LogInfo($"[BoxPlace] Box is open but was treated as closed; fixed so placing works " +
                                      $"({HeldState.Role}, last open click {(s_openedBox == box.Pointer ? $"{now - s_openedAt:F1}s ago" : "not on this box")}). " +
                                      HeldState.Describe(box));
            }
        }

        if (Plugin.VerboseLogging.Value) TrackPress(bi, box, now);
    }

    private static void CheckStuckTake(BoxInteraction bi, PlayerInstance player, Box box, float now)
    {
        if (!bi.m_holdingTake) { s_takeReleasedSince = -1f; return; }
        var take = TakeAction(player);
        if (take == null || take.IsPressed()) { s_takeReleasedSince = -1f; return; }
        if (s_takeReleasedSince < 0f) { s_takeReleasedSince = now; return; }
        if (now - s_takeReleasedSince < StuckTakeSeconds) return;
        bi.m_holdingTake = false;
        s_takeReleasedSince = -1f;
        Plugin.Logger.LogInfo($"[BoxPlace] Take button was released but still counted as held; released it ({HeldState.Role}). " +
                              HeldState.Describe(box));
    }

    private static InputAction TakeAction(PlayerInstance player)
    {
        if (s_takePlayer == player.Pointer && s_takeAction != null) return s_takeAction;
        s_takePlayer = player.Pointer;
        s_takeAction = null;
        try
        {
            var asset = player.InputActions?.m_PlayerInput?.actions;
            s_takeAction = asset?.FindAction("Take", false);
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[BoxPlace] Take action lookup failed: {e.Message}"); }
        return s_takeAction;
    }

    /// <summary>
    /// VerboseLogging: one log line per "held Use on a display slot for a while and nothing was placed".
    /// Runs before vanilla Update, so the slot is the one targeted during the previous frame.
    /// </summary>
    private static void TrackPress(BoxInteraction bi, Box box, float now)
    {
        var slot = bi.m_CurrentDisplaySlot;
        if (!bi.m_holdingUse || slot == null || !box.IsOpen) { s_pressBox = IntPtr.Zero; return; }
        var data = box.Data;
        int count = data != null ? data.ProductCount : -1;
        if (s_pressBox != box.Pointer || s_pressSlot != slot.Pointer || count != s_pressCount)
        {
            // New press, new slot, or something was placed: restart the timer.
            s_pressBox = box.Pointer;
            s_pressSlot = slot.Pointer;
            s_pressCount = count;
            s_pressSince = now;
            s_pressLogged = false;
            return;
        }
        if (s_pressLogged || now - s_pressSince < NoPlaceLogSeconds) return;
        s_pressLogged = true;
        Plugin.Logger.LogInfo($"[BoxPlace] Holding left click on a shelf for {now - s_pressSince:F1}s placed nothing ({HeldState.Role}): " +
                              $"boxOpen={box.IsOpen} treatedAsOpen={bi.m_IsBoxOpen} animating={box.m_InAnimationTransition} " +
                              $"takeHeld={bi.m_holdingTake} waiting={bi.IsWaitingAvailability} hasProducts={SafeHasProducts(box)} " +
                              $"sinceLastPlace={Time.time - bi.m_lastPlaceTime:F2}s interval={bi.m_ProductInteractionInterval:F2} " +
                              $"{DescribeSlot(slot, box)} {HeldState.Describe(box)}");
    }

    private static string SafeHasProducts(Box box)
    {
        try { return box.HasProducts.ToString(); } catch (Exception e) { return $"?({e.Message})"; }
    }

    private static string DescribeSlot(DisplaySlot slot, Box box)
    {
        try
        {
            var product = box.Product;
            var display = slot.m_Display;
            var label = slot.m_Label;
            string id;
            try { id = slot.ProductID.ToString(); } catch { id = "?"; }
            return $"slot[product={id} count={slot.ProductCount} hasProduct={slot.HasProduct} full={slot.Full} " +
                   $"label={(label != null && label.gameObject.activeSelf)} " +
                   $"displayType={(display != null ? display.DisplayType.ToString() : "?")} " +
                   $"boxProductType={(product != null ? product.ProductDisplayType.ToString() : "?")} " +
                   $"boxProductId={(product != null ? product.ID : -1)} slotType={slot.GetIl2CppType().Name}]";
        }
        catch (Exception e) { return $"slot[describe failed: {e.Message}]"; }
    }
}

[HarmonyPatch(typeof(BoxInteraction), nameof(BoxInteraction.OnUse))]
internal static class BoxPlace_OnUse_Patch
{
    private static void Prefix(BoxInteraction __instance, bool down)
    {
        try
        {
            if (BoxPlaceFix.ResyncOpenState.Value) BoxPlaceFix.BeforeOnUse(__instance, down);
        }
        catch (Exception e) { BoxPlaceFix.LogError("OnUse prefix", e); }
    }
}

[HarmonyPatch(typeof(BoxInteraction), nameof(BoxInteraction.Update))]
internal static class BoxPlace_Update_Patch
{
    private static void Prefix(BoxInteraction __instance)
    {
        try { BoxPlaceFix.BeforeUpdate(__instance); }
        catch (Exception e) { BoxPlaceFix.LogError("Update prefix", e); }
    }
}
