using System;
using BepInEx.Configuration;
using HarmonyLib;

namespace SupermarketFixes.Patches;

/// <summary>
/// Bug #10: while filling an online-order paper bag, the counts shown on the notebook ("2/5" next to
/// each product) sometimes all drop to zero although the bag still holds everything. Closing and
/// reopening the bag (C, then open) shows the right numbers again.
///
/// Root cause (native analysis of GameAssembly.dll, OnlineOrderInteraction / OrderNotebookContainer):
///  - The counts on screen are not read from the bag. They belong to ONE notebook, the container's
///    CurrentInstance (m_NotebookInstances[0], or the empty notebook when there is none). When a bag is
///    taken, opened, swapped to another order, or an order is selected, the interaction pushes the bag's
///    contents into that notebook with OrderNotebook.SetEntryData(bag.m_SortedProducts); afterwards every
///    product added or removed goes through OnPaperBagProductAdded / Removed ->
///    OrderNotebookContainer.OnProductAdded / OnProductRemoved -> CurrentInstance only.
///  - A notebook leaves the list through OrderNotebookContainer.RemoveInstance (reached from
///    OnOrderRemoved: the order was delivered, expired or cancelled, by anyone including the courier;
///    from OnlineOrderInteraction.OrderExpired through RemoveInstanceForOrder; and, with this mod pack,
///    from CourierOrdersFix when the courier takes an order). If the removed notebook was the current
///    one, the next notebook becomes CurrentInstance. Nothing pushes the bag in hand into it: it shows
///    its own entries at count 0, and the following takes count up from zero in it.
///  - Reopening the bag runs OnPaperBagOpened -> EnableAfterSpawn, which calls SetEntryData with the bag
///    again; that is why the numbers come back.
///  - The notebook usually is the current one when the player has not scrolled away from the order whose
///    labelled bag they just dropped (see Bug #11), so a courier (the player's, or AutoCourierLoad's)
///    loading that labelled bag removes exactly the notebook the player is filling a new bag against.
///    A customer interacting with the shelf slot is not involved: nothing in the take path touches the
///    notebook list (the take RPC only removes the product object on the other clients).
///
/// Fix (local UI only; nothing is networked): a postfix on OrderNotebookContainer.RemoveInstance, for the
/// local player's container only. If the local OnlineOrderInteraction has a bag in hand that holds
/// products, the (possibly new) current notebook is filled from the bag again with the same call the
/// game uses on take/open. Redoing it when the removed notebook was not the current one is harmless
/// (same data). Runs on whichever machine shows the wrong counts; no host/guest requirement.
/// </summary>
internal static class OrderBagCountsFix
{
    internal static ConfigEntry<bool> ResyncBagCounts;

    internal static void Bind(ConfigFile cfg)
    {
        ResyncBagCounts = cfg.Bind("OrderBagCounts", "ResyncBagCounts", true,
            "Keep the product counts on the order notebook right while you fill a paper bag, even when " +
            "another order is delivered, expires or is taken by the courier at that moment.");
    }

    internal static void ResetState() { }

    private static bool s_loggedError;

    private static void LogError(string where, Exception e)
    {
        if (s_loggedError) return;
        s_loggedError = true;
        Plugin.Logger.LogError($"[OrderBagCounts] {where} failed (further errors suppressed): {e}");
    }

    internal static void AfterRemoveInstance(OrderNotebookContainer container)
    {
        if (!Plugin.Active || ResyncBagCounts == null || !ResyncBagCounts.Value || container == null) return;
        var local = OnlineOrderService.GetLocalNotebookContainer();
        if (local == null || local.Pointer != container.Pointer) return;
        var player = NoktaSingleton<PlayerManager>.HasInstance ? NoktaSingleton<PlayerManager>.Instance.LocalPlayer : null;
        var oi = player != null ? player.OnlineOrderInteraction : null;
        if (oi == null || !oi.enabled) return;
        var bag = oi.CurrentOrderPaperBag;
        if (bag == null || !bag.HasProduct) return;
        var nb = container.CurrentInstance;
        if (nb == null) return;
        var list = bag.m_SortedProducts;
        if (list == null) return;
        nb.SetEntryData(list.TryCast<Il2CppSystem.Collections.Generic.IEnumerable<PaperBagProductInstance>>());
        if (Plugin.VerboseLogging != null && Plugin.VerboseLogging.Value)
            Plugin.Logger.LogInfo($"[OrderBagCounts] a notebook was removed while a bag with {bag.ProductCount} product(s) is in hand; counts on the current notebook refilled from the bag");
    }

    [HarmonyPatch(typeof(OrderNotebookContainer), nameof(OrderNotebookContainer.RemoveInstance))]
    internal static class RemoveInstance_Patch
    {
        private static void Postfix(OrderNotebookContainer __instance)
        {
            try { AfterRemoveInstance(__instance); }
            catch (Exception e) { LogError("RemoveInstance postfix", e); }
        }
    }
}
