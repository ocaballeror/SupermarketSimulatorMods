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
///
/// Fix (local player's crate only; the selection is local UI state, nothing is networked): in a
/// CrateInteraction.Update prefix, when the targeted slot is an empty unlabeled DisplaySlot and the
/// scroll selection doesn't point at a product still in the crate, call RefreshScrollHighlight (up to
/// twice: the first call may only reset the id to -1), exactly what the game itself does a moment later.
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

    internal static void ResetState() { s_loggedError = false; s_lastLogged = -100f; }

    private static bool s_loggedError;
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
        if (slot.HasProduct || slot.ProductID > 0) return; // vanilla uses the slot itself there

        int id = ci.m_ScrollCurrentGroupID;
        if (CrateHasGroup(crate, id)) return;

        for (int attempt = 0; attempt < 2; attempt++)
        {
            ci.RefreshScrollHighlight();
            if (CrateHasGroup(crate, ci.m_ScrollCurrentGroupID)) break;
        }

        float now = UnityEngine.Time.unscaledTime;
        if (now - s_lastLogged > 2f)
        {
            s_lastLogged = now;
            Plugin.Logger.LogInfo($"[CratePlace] Crate selection pointed at nothing in the crate (group {id}); " +
                                  $"repaired to {ci.m_ScrollCurrentGroupID} ({HeldState.Role}), crate products={crate.m_Product?.Count ?? -1}.");
        }
    }
}

[HarmonyPatch(typeof(CrateInteraction), nameof(CrateInteraction.Update))]
internal static class CratePlace_Update_Patch
{
    private static void Prefix(CrateInteraction __instance)
    {
        try
        {
            if (CratePlaceFix.RepairSelection.Value) CratePlaceFix.BeforeUpdate(__instance);
        }
        catch (Exception e) { CratePlaceFix.LogError("Update prefix", e); }
    }
}
