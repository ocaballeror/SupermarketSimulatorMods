// Improvement #1: keep the bulk markup when market prices change overnight.
//
// How the game prices products (verified in the native dumps, PriceManager / Pricing):
// - The computer's bulk pricing tab (BulkPricingSection.Commit) calls
//   PriceManager.ApplyBulkMarkup(percentAboveMarket). For every product in
//   ProductLicenseManager.m_ActiveProducts that isn't ProductSO.IsHiddenForCustomer it does
//       pricing = GetOrCreatePriceSetByPlayer(id);
//       pricing.Price = (float)Math.Round((pct / 100f + 1f) * pricing.MarketPrice, 2);
//   stores pct in SaveManager.PriceContainer.BulkMarkupRatio (read back via PriceManager.BulkMarkupRatio),
//   then FinishBulkPricingApply(changed, false, pct) refreshes the pricing viewer, the discount points,
//   fires onPriceSet per product and sends BulkPricingApplied_RPC to the other clients.
//   ApplyBulkDiscount does the same with Pricing.DiscountRate (a percentage) and BulkDiscountRatio.
// - Pricing.MarketPrice = round(CurrentCost * (1 + ProductSO.OptimumProfitRate / 100), 2).
// - At the start of a day, DayCycleManager.OnStartedNewDay invokes PriceManager.TryChangingBasePrices
//   (subscribed in PriceManager.Start, so it's only ever called through the delegate). It picks a few
//   random products and calls ChangeCurrentCost(id) for each, which rolls a new CurrentCost. Nothing
//   touches the player's selling price (m_PricesSetByPlayer), so after the change it is no longer
//   "pct above market": the root cause of the drift. It then fires onCostsChanged and, when online,
//   BasePricesChanged_Broadcast sends all price lists (including m_PricesSetByPlayer) to the guests.
// - Discounts are stored as a percentage of the selling price, so they keep applying unchanged; only
//   the markup drifts.
//
// Fix (host or single player only; guests get the result from the host):
// - TryChangingBasePrices prefix: start tracking, remember the saved BulkMarkupRatio.
// - ChangeCurrentCost prefix: before the cost changes, decide whether this product follows the markup
//   (its player price equals the markup applied to the old market price), unless that check is off.
//   Also remember the current cost: ChangeCurrentCost can leave it unchanged (ExemptFromChange, no
//   cost entry, or no other dynamic price found after 64 rerolls); such products are skipped.
// - ChangeCurrentCost postfix: set the player price to the markup applied to the new market price.
//   This happens before the game's broadcast, so the broadcast already carries the new price.
// - TryChangingBasePrices postfix: do what FinishBulkPricingApply does, minus its sale sign reroll
//   (it sets _saleIndex = Random.Range(0,4) for discounted products, which would change their sale
//   sign style every morning): PricingProductViewer.RefreshUnlockedProducts per product,
//   CalculateTotalDiscountPoints, onPriceSet per product (price tags), and when online
//   BulkPricingApplied_Broadcast (only sends BulkPricingApplied_RPC; guests copy Price, DiscountRate and
//   the unchanged _saleIndex, refresh and fire onPriceSet / onSaleSignSet).
// Guests never run TryChangingBasePrices: PriceManager.Start only subscribes it to OnStartedNewDay when
// !IsConnectedAndSlave. The authority check below is a second guard.

using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Photon.Pun;

namespace PriceMarkupKeeper;

internal static class MarkupKeeper
{
    // Prices are rounded to cents; anything closer than about half a cent counts as "the same price".
    private const float Tolerance = 0.006f;

    private static bool _tracking;
    private static float _ratio;
    private static readonly Dictionary<int, Pricing> Changed = new(); // by product id (a product can change twice a day)

    internal static void Reset()
    {
        _tracking = false;
        Changed.Clear();
    }

    /// <summary>Same formula and rounding as PriceManager.ApplyBulkMarkup.</summary>
    internal static float MarkupPrice(float marketPrice, float pct) =>
        (float)Math.Round((double)((pct / 100f + 1f) * marketPrice), 2);

    private static bool IsAuthority() => !PhotonNetwork.IsConnected || PhotonNetwork.IsMasterClient;

    internal static void BeginDay(PriceManager pm)
    {
        Reset();
        if (!Plugin.KeepMarkup.Value) return;
        if (!IsAuthority())
        {
            if (Plugin.Verbose.Value) Plugin.Logger.LogInfo("[Markup] Guest: the host reprices products, skipping");
            return;
        }
        _ratio = pm.BulkMarkupRatio;
        _tracking = true;
        if (Plugin.Verbose.Value)
            Plugin.Logger.LogInfo($"[Markup] New day price changes starting, bulk markup is {_ratio:0.##}%");
    }

    /// <summary>Before the cost changes: should this product get the markup afterwards?</summary>
    internal static bool ShouldReprice(PriceManager pm, int productID)
    {
        if (!_tracking) return false;

        var idm = NoktaSingleton<IDManager>.Instance;
        var so = idm != null ? idm.ProductSO(productID) : null;
        if (so == null || so.IsHiddenForCustomer) return false; // the bulk tab skips these too

        if (!Plugin.OnlyFollowingProducts.Value) return true;

        var p = pm.GetPriceSetByPlayer(productID);
        if (p == null)
        {
            if (Plugin.Verbose.Value)
                Plugin.Logger.LogInfo($"[Markup] Product {productID}: no price set by the player, left alone");
            return false;
        }
        float expected = MarkupPrice(p.MarketPrice, _ratio);
        bool follows = Math.Abs(p.Price - expected) < Tolerance;
        if (!follows && Plugin.Verbose.Value)
            Plugin.Logger.LogInfo($"[Markup] Product {productID}: price {p.Price:0.00} was set by hand " +
                                  $"(markup price would be {expected:0.00}), left alone");
        return follows;
    }

    /// <summary>After the cost changed: apply the markup to the new market price.</summary>
    internal static void Reprice(PriceManager pm, int productID, float oldCost)
    {
        if (pm.CurrentCost(productID) == oldCost)
        {
            if (Plugin.Verbose.Value)
                Plugin.Logger.LogInfo($"[Markup] Product {productID}: cost didn't actually change, left alone");
            return;
        }
        var p = Plugin.OnlyFollowingProducts.Value
            ? pm.GetPriceSetByPlayer(productID)
            : pm.GetOrCreatePriceSetByPlayer(productID);
        if (p == null) return;

        float market = p.MarketPrice;
        if (market <= 0f) return;
        float old = p.Price;
        float price = MarkupPrice(market, _ratio);
        p.Price = price;
        Changed[productID] = p;
        if (Plugin.Verbose.Value)
            Plugin.Logger.LogInfo($"[Markup] Product {productID}: market {market:0.00}, price {old:0.00} -> {price:0.00}");
    }

    internal static void EndDay(PriceManager pm)
    {
        if (!_tracking) return;
        _tracking = false;
        if (Changed.Count == 0)
        {
            if (Plugin.Verbose.Value) Plugin.Logger.LogInfo("[Markup] No products repriced today");
            return;
        }

        var repriced = new List<Pricing>(Changed.Values);
        Changed.Clear();

        // Same steps as PriceManager.FinishBulkPricingApply, without its sale sign reroll.
        var viewer = pm.m_PricingProductViewer;
        foreach (var p in repriced)
            if (viewer != null) viewer.RefreshUnlockedProducts(p.ProductID);
        pm.CalculateTotalDiscountPoints();
        var onPriceSet = pm.onPriceSet;
        foreach (var p in repriced)
        {
            try { onPriceSet?.Invoke(p.ProductID); }
            catch (Exception e) { Plugin.Logger.LogError($"[Markup] onPriceSet({p.ProductID}) failed: {e}"); }
        }
        if (PhotonNetwork.IsConnected)
            pm.BulkPricingApplied_Broadcast(new Il2CppReferenceArray<Pricing>(repriced.ToArray()), false, _ratio);

        Plugin.Logger.LogInfo($"[Markup] Repriced {repriced.Count} product(s) to {_ratio:0.##}% above market price");
    }
}

[HarmonyPatch(typeof(PriceManager), nameof(PriceManager.TryChangingBasePrices))]
internal static class TryChangingBasePricesPatch
{
    private static void Prefix(PriceManager __instance)
    {
        try { MarkupKeeper.BeginDay(__instance); }
        catch (Exception e)
        {
            MarkupKeeper.Reset();
            Plugin.Logger.LogError($"[Markup] BeginDay failed: {e}");
        }
    }

    private static void Postfix(PriceManager __instance)
    {
        try { MarkupKeeper.EndDay(__instance); }
        catch (Exception e) { Plugin.Logger.LogError($"[Markup] EndDay failed: {e}"); }
        finally { MarkupKeeper.Reset(); }
    }
}

[HarmonyPatch(typeof(PriceManager), nameof(PriceManager.ChangeCurrentCost))]
internal static class ChangeCurrentCostPatch
{
    // __state = the cost before the change, or NaN when the product shouldn't be repriced.
    private static void Prefix(PriceManager __instance, int productID, out float __state)
    {
        __state = float.NaN;
        try
        {
            if (MarkupKeeper.ShouldReprice(__instance, productID))
                __state = __instance.CurrentCost(productID);
        }
        catch (Exception e) { Plugin.Logger.LogError($"[Markup] Product {productID}: check failed: {e}"); }
    }

    private static void Postfix(PriceManager __instance, int productID, float __state)
    {
        if (float.IsNaN(__state)) return;
        try { MarkupKeeper.Reprice(__instance, productID, __state); }
        catch (Exception e) { Plugin.Logger.LogError($"[Markup] Product {productID}: reprice failed: {e}"); }
    }
}
