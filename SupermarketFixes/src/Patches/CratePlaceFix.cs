using System;
using BepInEx.Configuration;
using HarmonyLib;

namespace SupermarketFixes.Patches;

/// <summary>
/// Bug #5, crate variant: with a crate selected, left click on an empty shelf slot that has no label
/// (typically one whose product was taken off and whose label was cleared) sometimes places nothing.
/// Deselecting and re-selecting the crate makes it work.
///
/// Native analysis (CrateInteraction.&lt;OnTakeLoop&gt;, the crate -> shelf loop; the game has the
/// names of OnUse/OnTake swapped):
///  - For a slot that has a product or a label, the loop only asks Crate.IsTargetProductExist(slot).
///  - For an EMPTY slot WITHOUT a label (DisplaySlot.ProductID is 0) it instead uses the crate's
///    "scroll selection", CrateInteraction.m_ScrollCurrentGroupID (the product group you scrolled to):
///    ProductSO(m_ScrollCurrentGroupID) must exist, have the slot's display type, and the crate must
///    still contain such a product. If not, the loop shows an error hint and places nothing.
///  - m_ScrollCurrentGroupID is only repaired by RefreshScrollHighlight, which runs from
///    RefreshActiveHighlight when the targeted slot changes and after each placement. When the last
///    product of the selected group has left the crate, a refresh sets the id to -1 and only the NEXT
///    refresh picks the first group again. In between (and while the reticle stays on one slot) every
///    click fails; EnableCrate (re-selecting the crate) is the other thing that picks a group.
///    Not yet confirmed in-game; the [CratePlace] lines will show it.
///  - Other possible causes (what the VerboseLogging line described below lets you tell apart): the
///    selected group's display type doesn't match the slot's display; Crate.IsTargetProductExist fails
///    for a labeled slot; a guard-crate take is pending; the click lands in the one frame before the
///    prefix runs.
///
/// Fix (local player's crate only; the selection is local UI state, nothing is networked): in a
/// CrateInteraction.Update prefix, when the targeted slot is an empty unlabeled DisplaySlot and the
/// scroll selection doesn't point at a product still in the crate, call RefreshScrollHighlight (up to
/// twice: the first call may only reset the id to -1), exactly what the game itself does a moment later.
/// If the repair doesn't produce a valid group, a warning says so (always logged).
///
/// VerboseLogging: when left click (m_IsOnTake) is held on a display slot for 1.5 s and the crate's
/// product count didn't change, one [CratePlace] line (per press) lists the slot, the scroll selection,
/// the groups in the crate and the pending-take flag, so a different cause can be told from the log.
/// </summary>
internal static class CratePlaceFix
{
    internal static ConfigEntry<bool> RepairSelection;

    internal static void Bind(ConfigFile cfg)
    {
        RepairSelection = cfg.Bind("BoxPlace", "RepairCrateSelection", true,
            "If a crate refuses to put products on an empty shelf slot without a label until you " +
            "deselect and reselect the crate, repair the crate's product selection automatically.");
    }

    internal static void ResetState()
    {
        s_loggedError = false; s_lastLogged = -100f; s_triedSlot = s_triedCrate = IntPtr.Zero;
        s_pressCrate = s_pressSlot = IntPtr.Zero; s_pressLogged = false;
    }

    private const float NoPlaceLogSeconds = 1.5f;
    private static IntPtr s_pressCrate, s_pressSlot;
    private static int s_pressCount;
    private static float s_pressSince;
    private static bool s_pressLogged;

    private static bool s_loggedError;
    private static IntPtr s_triedSlot, s_triedCrate;
    private static int s_triedCount, s_triedId;
    private static float s_lastLogged = -100f;

    internal static void LogError(string where, Exception e)
    {
        if (s_loggedError) return;
        s_loggedError = true;
        Plugin.Logger.LogError($"[CratePlace] {where} failed (further errors suppressed): {e}");
    }

    private static bool CrateHasGroup(Crate crate, int id)
    {
        if (id < 0) return false;
        var list = crate.m_Product;
        if (list == null) return false;
        for (int i = 0; i < list.Count; i++)
        {
            var p = list[i];
            if (p == null) continue;
            var so = p.m_ProductSO;
            if (so != null && so.ID == id) return true;
        }
        return false;
    }

    internal static void BeforeUpdate(CrateInteraction ci)
    {
        var local = HeldState.LocalPlayer();
        if (local == null) return;
        var owner = ci.PlayerInstance;
        if (owner == null || owner.Pointer != local.Pointer) return;

        var crate = ci.m_Crate;
        var slot = ci.m_CurrentDisplaySlot;
        if (crate == null || slot == null || !crate.HasProduct) return;
        if (slot.HasProduct) return;
        int label;
        try { label = slot.ProductID; } catch (Exception) { return; }
        if (label > 0) return; // vanilla uses the slot itself there

        int id = ci.m_ScrollCurrentGroupID;
        if (CrateHasGroup(crate, id)) return;

        // Don't retry every frame if a repair can't find a group (e.g. HasProduct disagrees with the list).
        int count = crate.m_Product != null ? crate.m_Product.Count : -1;
        if (s_triedSlot == slot.Pointer && s_triedCrate == crate.Pointer && s_triedCount == count && s_triedId == id) return;
        s_triedSlot = slot.Pointer; s_triedCrate = crate.Pointer; s_triedCount = count; s_triedId = id;

        for (int attempt = 0; attempt < 2; attempt++)
        {
            ci.RefreshScrollHighlight();
            if (CrateHasGroup(crate, ci.m_ScrollCurrentGroupID)) break;
        }

        int after = ci.m_ScrollCurrentGroupID;
        bool ok = CrateHasGroup(crate, after);
        float now = UnityEngine.Time.unscaledTime;
        if (now - s_lastLogged > 2f)
        {
            s_lastLogged = now;
            if (ok)
                Plugin.Logger.LogInfo($"[CratePlace] Crate selection pointed at nothing in the crate (group {id}); " +
                                      $"repaired to {after} ({HeldState.Role}), crate products={count}.");
            else
                Plugin.Logger.LogWarning($"[CratePlace] Crate selection pointed at nothing in the crate (group {id}); " +
                                         $"could not repair it (id is now {after}) ({HeldState.Role}), crate products={count}. " +
                                         DescribeState(ci, crate, slot));
        }
    }

    private static string SafeSlotId(DisplaySlot slot)
    {
        try { return slot.ProductID.ToString(); } catch (Exception) { return "?"; }
    }

    private static string DescribeState(CrateInteraction ci, Crate crate, DisplaySlot slot)
    {
        try
        {
            var display = slot.m_Display;
            string slotType = display != null ? display.DisplayType.ToString() : "?";
            int sel = ci.m_ScrollCurrentGroupID;
            string selType = "?";
            try
            {
                var so = NoktaSingleton<IDManager>.Instance.ProductSO(sel);
                selType = so != null ? so.ProductDisplayType.ToString() : "none";
            }
            catch (Exception) { }
            var groups = new System.Text.StringBuilder();
            var seen = new System.Collections.Generic.HashSet<int>();
            var list = crate.m_Product;
            for (int i = 0; list != null && i < list.Count; i++)
            {
                var so = list[i]?.m_ProductSO;
                if (so == null || !seen.Add(so.ID)) continue;
                if (groups.Length > 0) groups.Append(',');
                groups.Append(so.ID).Append(':').Append(so.ProductDisplayType);
            }
            bool exists = false;
            try { exists = crate.IsTargetProductExist(slot); } catch (Exception) { }
            return $"slot[product={SafeSlotId(slot)} hasProduct={slot.HasProduct} full={slot.Full} displayType={slotType}] " +
                   $"selection={sel}({selType}) crateGroups=[{groups}] isTargetProductExist={exists} " +
                   $"guardTakePending={ci.IsGuardCrateTakePending} crateCount={list?.Count ?? -1}";
        }
        catch (Exception e) { return $"describe failed: {e.Message}"; }
    }

    /// <summary>VerboseLogging: one line per "held left click on a shelf slot for a while and the crate's count didn't change".</summary>
    internal static void TrackPress(CrateInteraction ci)
    {
        var local = HeldState.LocalPlayer();
        var owner = ci.PlayerInstance;
        var crate = ci.m_Crate;
        var slot = ci.m_CurrentDisplaySlot;
        if (local == null || owner == null || owner.Pointer != local.Pointer ||
            !ci.m_IsOnTake || crate == null || slot == null)
        {
            s_pressCrate = IntPtr.Zero;
            return;
        }
        float now = UnityEngine.Time.unscaledTime;
        int count = crate.m_Product != null ? crate.m_Product.Count : -1;
        if (s_pressCrate != crate.Pointer || s_pressSlot != slot.Pointer || s_pressCount != count)
        {
            s_pressCrate = crate.Pointer; s_pressSlot = slot.Pointer; s_pressCount = count;
            s_pressSince = now; s_pressLogged = false;
            return;
        }
        if (s_pressLogged || now - s_pressSince < NoPlaceLogSeconds) return;
        s_pressLogged = true;
        Plugin.Logger.LogInfo($"[CratePlace] Holding left click on a shelf for {now - s_pressSince:F1}s placed nothing " +
                              $"({HeldState.Role}): hasProduct={crate.HasProduct} {DescribeState(ci, crate, slot)}");
    }
}

[HarmonyPatch(typeof(CrateInteraction), nameof(CrateInteraction.Update))]
internal static class CratePlace_Update_Patch
{
    private static void Prefix(CrateInteraction __instance)
    {
        try
        {
            if (Plugin.VerboseLogging.Value) CratePlaceFix.TrackPress(__instance);
            if (CratePlaceFix.RepairSelection.Value) CratePlaceFix.BeforeUpdate(__instance);
        }
        catch (Exception e) { CratePlaceFix.LogError("Update prefix", e); }
    }
}
