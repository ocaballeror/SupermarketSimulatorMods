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
//   sign style every morning): CalculateTotalDiscountPoints, onPriceSet per product (price tags), and
//   when online BulkPricingApplied_Broadcast (only sends BulkPricingApplied_RPC; guests copy Price,
//   DiscountRate and the unchanged _saleIndex, refresh and fire onPriceSet / onSaleSignSet).
//   PricingProductViewer.RefreshUnlockedProducts is not called: the game's own loop in
//   TryChangingBasePrices already calls it for each product right after ChangeCurrentCost, i.e. after
//   our ChangeCurrentCost postfix has set the new price.
// - ChangeCurrentCost skips products that have no m_CurrentCosts entry, but PriceManager.CurrentCost(id)
//   (and so Pricing.MarketPrice) creates one. The prefix therefore checks for an existing entry first and
//   never touches such products, so the mod can't give them a cost change vanilla wouldn't.
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
    private static int _costCalls; // ChangeCurrentCost calls seen today (proves the hooks fire)
    private static readonly Dictionary<int, Pricing> Changed = new(); // by product id (a product can change twice a day)

    internal static void Reset()
    {
        _tracking = false;
        _costCalls = 0;
        Changed.Clear();
    }

    /// <summary>True when m_CurrentCosts already has an entry (CurrentCost(id) would create one otherwise).</summary>
    private static bool HasCostEntry(PriceManager pm, int productID)
    {
        var list = pm.m_CurrentCosts;
        if (list == null) return false;
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            if (c != null && c.ProductID == productID) return true;
        }
        return false;
    }

    /// <summary>Same formula and rounding as PriceManager.ApplyBulkMarkup.</summary>
    internal static float MarkupPrice(float marketPrice, float pct) =>
        (float)Math.Round((double)((pct / 100f + 1f) * marketPrice), 2);

    private static bool IsAuthority() => !PhotonNetwork.IsConnected || PhotonNetwork.IsMasterClient;

    internal static void BeginDay(PriceManager pm)
    {
        Reset();
        // Defensive: guests never get here (PriceManager.Start doesn't subscribe the handler for them).
        if (!IsAuthority()) return;
        _ratio = pm.BulkMarkupRatio;
        _tracking = true;
    }

    /// <summary>Before the cost changes: should this product get the markup afterwards?</summary>
    internal static bool ShouldReprice(PriceManager pm, int productID)
    {
        if (!_tracking) return false;
        _costCalls++;

        // Vanilla skips products without a cost entry; don't let our reads create one for it.
        if (!HasCostEntry(pm, productID)) return false;

        var idm = NoktaSingleton<IDManager>.Instance;
        var so = idm != null ? idm.ProductSO(productID) : null;
        if (so == null || so.IsHiddenForCustomer) return false; // the bulk tab skips these too

        var p = pm.GetPriceSetByPlayer(productID);
        if (p == null)
        {
            if (Plugin.VerboseLogging.Value)
                Plugin.Logger.LogInfo($"[Markup] Product {productID}: no price set by the player, left alone");
            return false;
        }
        float expected = MarkupPrice(p.MarketPrice, _ratio);
        bool follows = Math.Abs(p.Price - expected) < Tolerance;
        if (!follows && Plugin.VerboseLogging.Value)
        {
            Plugin.Logger.LogInfo($"[Markup] Product {productID}: price {p.Price:0.00} was set by hand " +
                                  $"(markup price would be {expected:0.00}), left alone");
        }
        return follows;
    }

    /// <summary>After the cost changed: apply the markup to the new market price.</summary>
    internal static void Reprice(PriceManager pm, int productID, float oldCost)
    {
        if (pm.CurrentCost(productID) == oldCost)
        {
            if (Plugin.VerboseLogging.Value)
                Plugin.Logger.LogInfo($"[Markup] Product {productID}: cost didn't actually change, left alone");
            return;
        }
        var p = pm.GetPriceSetByPlayer(productID);
        if (p == null) return;

        float market = p.MarketPrice;
        if (market <= 0f) return;
        float old = p.Price;
        float price = MarkupPrice(market, _ratio);
        p.Price = price;
        Changed[productID] = p;
        if (Plugin.VerboseLogging.Value)
            Plugin.Logger.LogInfo($"[Markup] Product {productID}: market {market:0.00}, price {old:0.00} -> {price:0.00}");
    }

    internal static void EndDay(PriceManager pm)
    {
        if (!_tracking) return;
        _tracking = false;
        // One summary line per day while verbose; it also proves the TryChangingBasePrices and
        // ChangeCurrentCost hooks fired (costCalls = products the game tried to change today).
        if (Plugin.VerboseLogging.Value)
            Plugin.Logger.LogInfo($"[Markup] New day: the game tried to change {_costCalls} product(s), " +
                                  $"markup {_ratio:0.##}%, {Changed.Count} to reprice");
        if (Changed.Count == 0) return;

        var repriced = new List<Pricing>(Changed.Values);
        Changed.Clear();

        // Same steps as PriceManager.FinishBulkPricingApply, without its sale sign reroll. The viewer
        // refresh isn't repeated: the game's own loop does it after each ChangeCurrentCost.
        pm.CalculateTotalDiscountPoints();
        var onPriceSet = pm.onPriceSet;
        foreach (var p in repriced)
        {
            try { onPriceSet?.Invoke(p.ProductID); }
            catch (Exception e) { Plugin.Logger.LogError($"[Markup] onPriceSet({p.ProductID}) failed: {e}"); }
        }
        bool sent = false;
        if (PhotonNetwork.IsConnected)
        {
            pm.BulkPricingApplied_Broadcast(new Il2CppReferenceArray<Pricing>(repriced.ToArray()), false, _ratio);
            sent = true;
        }

        Plugin.Logger.LogInfo($"[Markup] Repriced {repriced.Count} product(s) to {_ratio:0.##}% above market price" +
                               (sent ? " (sent to guests)" : ""));
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

// Rework 2026-11-15: newly unlocked products get the markup right away.
//
// Buying a license (verified in the dumps): the buyer's game runs
// ProductLicenseManager.PurchaseLicense(licenseID) (from LicenseItem.Purchase), which adds the license to
// the unlocked/active lists, calls UpdateUnlockedProducts and, online, NetworkLicenseManager
// .PurchaseLicense_Broadcast. Every other client runs PurchaseLicense_Order(licenseID) from
// PurchaseLicense_RPC, which does the same without broadcasting. So the host runs exactly one of the two
// whoever bought it, and the products are unlocked when either returns.
//
// A newly unlocked product has no player price (m_PricesSetByPlayer entry), so it sells at CurrentCost,
// with no profit at all. Postfix both, on the host or offline only: for each product of the license that
// isn't IsHiddenForCustomer and has no player price yet, GetOrCreatePriceSetByPlayer (creates one at
// market price, as the pricing tab does) and set Price to the saved bulk markup on that market price,
// and DiscountRate to the saved bulk discount (BulkDiscountRatio) if there is one, as ApplyBulkDiscount
// does. Then FinishBulkPricingApply(list, false, ratio), the game's own end of a bulk apply: refreshes
// the pricing viewer, discount points, onPriceSet (price tags), and online BulkPricingApplied_RPC to the
// guests; with a discount, a second FinishBulkPricingApply(list, true, discount) as ApplyBulkDiscount's
// own finish (it picks the sale sign style). EnsurePricingDataComplete runs first because
// GetOrCreatePriceSetByPlayer throws without the product's pricingDatas entry.
internal static class NewProductPricer
{
    private static bool IsAuthority() => !PhotonNetwork.IsConnected || PhotonNetwork.IsMasterClient;

    internal static void OnLicenseUnlocked(int licenseID, string via)
    {
        if (!IsAuthority()) return;
        var idm = NoktaSingleton<IDManager>.Instance;
        var pm = NoktaSingleton<PriceManager>.Instance;
        if (idm == null || pm == null) return;
        var license = idm.ProductLicenseSO(licenseID);
        var products = license != null ? license.Products : null;
        if (products == null) return;

        // GetOrCreatePriceSetByPlayer reads the product's pricingDatas entry and throws if it's missing. The
        // pricing tab fills that list before its own bulk apply (LoadPricingData), and WholeSaleManager calls
        // EnsurePricingDataComplete first; this hook can run before the pricing tab was ever opened.
        try { pm.EnsurePricingDataComplete(); }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Markup] EnsurePricingDataComplete failed: {e.Message}"); }

        float ratio = pm.BulkMarkupRatio;
        int discount = (int)Math.Round(pm.BulkDiscountRatio);
        if (discount < 0 || discount > 100) discount = 0;
        var changed = new Il2CppSystem.Collections.Generic.List<Pricing>();
        int skipped = 0;
        try
        {
            for (int i = 0; i < products.Count; i++)
            {
                var so = products[i];
                if (so == null || so.IsHiddenForCustomer) continue;
                int id = so.ID;
                try
                {
                    if (pm.GetPriceSetByPlayer(id) != null)
                    {
                        skipped++;
                        if (Plugin.VerboseLogging.Value)
                            Plugin.Logger.LogInfo($"[Markup] New product {id}: already has a price, left alone");
                        continue;
                    }
                    // Creates the entry at market price (as the pricing tab does). From here on it must be
                    // sent to the guests, so it goes into `changed` even if the market price is unusable.
                    var p = pm.GetOrCreatePriceSetByPlayer(id);
                    if (p == null) continue;
                    float market = p.MarketPrice;
                    if (market > 0f) p.Price = MarkupKeeper.MarkupPrice(market, ratio);
                    if (discount > 0) p.DiscountRate = discount; // as ApplyBulkDiscount does
                    changed.Add(p);
                    if (Plugin.VerboseLogging.Value)
                        Plugin.Logger.LogInfo($"[Markup] New product {id}: market {market:0.00}, price {p.Price:0.00}" +
                                              (discount > 0 ? $", discount {discount}%" : ""));
                }
                catch (Exception e)
                {
                    Plugin.Logger.LogError($"[Markup] New product {id}: pricing failed: {e.Message}");
                }
            }
        }
        finally
        {
            // Always finish what was priced, so the host's prices reach the guests and the price tags.
            if (changed.Count > 0)
            {
                pm.FinishBulkPricingApply(changed, false, ratio);
                // A second pass for the discount, like ApplyBulkDiscount's own finish: it also picks a sale
                // sign style for the newly discounted products and tells guests isDiscount.
                if (discount > 0) pm.FinishBulkPricingApply(changed, true, discount);
                Plugin.Logger.LogInfo($"[Markup] License {licenseID} unlocked ({via}): priced {changed.Count} new product(s) " +
                                      $"at {ratio:0.##}% above market price" + (discount > 0 ? $", {discount}% discount" : "") +
                                      (PhotonNetwork.IsConnected ? " (sent to guests)" : ""));
            }
            else if (Plugin.VerboseLogging.Value)
                Plugin.Logger.LogInfo($"[Markup] License {licenseID} unlocked ({via}): nothing to price ({skipped} already priced)");
        }
    }
}

[HarmonyPatch(typeof(ProductLicenseManager), nameof(ProductLicenseManager.PurchaseLicense))]
internal static class PurchaseLicensePatch
{
    private static void Postfix(int licenseID)
    {
        try { NewProductPricer.OnLicenseUnlocked(licenseID, "bought here"); }
        catch (Exception e) { Plugin.Logger.LogError($"[Markup] Pricing license {licenseID} failed: {e}"); }
    }
}

[HarmonyPatch(typeof(ProductLicenseManager), nameof(ProductLicenseManager.PurchaseLicense_Order))]
internal static class PurchaseLicenseOrderPatch
{
    private static void Postfix(int licenseID)
    {
        try { NewProductPricer.OnLicenseUnlocked(licenseID, "bought by another player"); }
        catch (Exception e) { Plugin.Logger.LogError($"[Markup] Pricing license {licenseID} failed: {e}"); }
    }
}
