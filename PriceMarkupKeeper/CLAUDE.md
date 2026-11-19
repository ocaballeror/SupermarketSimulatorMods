# PriceMarkupKeeper: notes for agents

Player-facing info is in [README.md](README.md). This file holds implementation details and status. The shared rules and game internals are in the root [CLAUDE.md](../CLAUDE.md). The full root-cause analysis is in the doc comment at the top of `src/MarkupPatches.cs`.

## Status

v1.1.3: plugin GUID changed from the old `<prefix>.pricemarkupkeeper` to `flamboyanthopper.pricemarkupkeeper` (commit 37476db), which also renames the cfg file to `flamboyanthopper.pricemarkupkeeper.cfg`. Existing installs start with a fresh cfg at the default values (`Enabled` true, `VerboseLogging` false); the old cfg file is left behind unused and can be deleted. No behaviour change.

v1.1.2: settings reduced (commit "simplify mod settings"). Only `Enabled` and `VerboseLogging` remain (the default of `VerboseLogging` became false); `KeepMarkup`, `OnlyFollowingProducts` and `PriceNewProducts` are now always on, and their off-branches (reprice hand-priced products too, create price sets for unpriced products) were removed as dead code.

v1.1.1 (rework, not yet tested in-game): newly unlocked products get the markup (and the saved bulk discount) as soon as their license is bought (user request after the 2026-11-15 session). Review round 1 (1.1.1): `EnsurePricingDataComplete` before the loop (`GetOrCreatePriceSetByPlayer` throws on a missing `pricingDatas` entry before the pricing tab was opened), per-product try/catch with `FinishBulkPricingApply` in a `finally` so whatever was priced is always sent, entries created with an unusable market price are still sent, the bulk discount is applied too (the original request covers "pricing or discount" rules). Not changed: the authority check `!IsConnected || IsMasterClient` (same as the in-game-confirmed daily repricing).

v1.0.2: daily repricing **confirmed working in-game by the user (2026-11-15).**

## Game mechanism

- `PriceManager` (a `NoktaSingletonPunCallbacks`) keeps per-product `Pricing {ProductID, Price, LastChangeDate, DiscountRate, _saleIndex}` in several lists: `m_CurrentCosts` (cost per unit), `m_PricesSetByPlayer` (selling prices; `GetPriceSetByPlayer(id)` / `GetOrCreatePriceSetByPlayer(id)`, which creates one at market price), `m_AveragePrices`, `m_DailyPriceChanges`, `m_PreviousPrices`, `pricingDatas`. They're saved in `SaveManager.PriceContainer`, along with the bulk tab's `BulkMarkupRatio` / `BulkDiscountRatio`. With no player price set, the selling price falls back to `CurrentCost`. A single typed price goes `PriceSet(pricing)` → RPC `SetPricingList`. Active (licensed) product ids: `ProductLicenseManager.m_ActiveProducts`.
- Bulk pricing tab → `PriceManager.ApplyBulkMarkup(pct)`: `price = (float)Math.Round((pct/100f + 1f) * Pricing.MarketPrice, 2)` for each active, non-`IsHiddenForCustomer` product. `pct` is saved in `SaveManager.PriceContainer.BulkMarkupRatio` (`PriceManager.BulkMarkupRatio`). Then `FinishBulkPricingApply(list, isDiscount, ratio)` refreshes UI, fires `onPriceSet` and sends `BulkPricingApplied_RPC`.
- `Pricing.MarketPrice = round(CurrentCost * (1 + ProductSO.OptimumProfitRate/100), 2)`.
- New day: `DayCycleManager.OnStartedNewDay` → `PriceManager.TryChangingBasePrices` (delegate only, `CallerCount(0)`; Harmony's native detour still catches it) → `ChangeCurrentCost(id)` per changed product (its only caller). The player price isn't touched, hence the drift. Then `BasePricesChanged_Broadcast` sends all lists to guests when online.
- Discounts (`Pricing.DiscountRate`) are percentages and don't drift.

## Patches

| Target | Kind | Purpose |
|---|---|---|
| `PriceManager.TryChangingBasePrices` | prefix | start tracking (host / offline only), read `BulkMarkupRatio` |
| `PriceManager.ChangeCurrentCost` | prefix | skip products with no `m_CurrentCosts` entry (vanilla skips them too, and `CurrentCost(id)` / `MarketPrice` would create one); decide if the product follows the markup (price == markup on the *old* market price, ±0.006); remember the old `CurrentCost` |
| `PriceManager.ChangeCurrentCost` | postfix | if the cost really changed (it may not: `ExemptFromChange`, no cost entry, 64 failed rerolls), set price to markup on the new market price; collect it, deduped by id |
| `ProductLicenseManager.PurchaseLicense` | postfix | buyer's side of a license purchase; `NewProductPricer.OnLicenseUnlocked` (host / offline only) |
| `ProductLicenseManager.PurchaseLicense_Order` | postfix | every other client's side (from `PurchaseLicense_RPC`); so the host prices a license whoever bought it |
| `PriceManager.TryChangingBasePrices` | postfix | `FinishBulkPricingApply`'s steps minus its sale sign reroll and minus `RefreshUnlockedProducts` (the game's own loop already calls it per product right after `ChangeCurrentCost`, i.e. after our price change): `CalculateTotalDiscountPoints`, `onPriceSet`, and `BulkPricingApplied_Broadcast` when online |

## Multiplayer

- Runs only when `!PhotonNetwork.IsConnected || IsMasterClient`. The price is changed before the game's own `BasePricesChanged_Broadcast`, so that already carries it. The extra `BulkPricingApplied_RPC` (via `BulkPricingApplied_Broadcast`, reliable, same PhotonView, so it arrives after) makes unmodded guests fire `onPriceSet` and refresh tags. It also sets the guest's saved `BulkMarkupRatio` to the value the host has, which is harmless.
- Guests never run `TryChangingBasePrices`: `PriceManager.Start` subscribes it to `OnStartedNewDay` only when `!IsConnectedAndSlave` (verified in the dump by the reviewer).

## Pitfalls / follow-ups

- The daily path (`MarkupKeeper.EndDay`) doesn't call `FinishBulkPricingApply` on purpose: it rerolls `_saleIndex` (`Random.Range(0,4)`) for products with `DiscountRate > 0.1`, which would change their sale sign style every morning. If the game adds steps to it in an update, mirror them in `MarkupKeeper.EndDay`.
- New licenses (v1.1.x): `EnsurePricingDataComplete`, then for each product of `IDManager.ProductLicenseSO(id).Products` that isn't `IsHiddenForCustomer` and has no player price, `GetOrCreatePriceSetByPlayer` + `Price = markup(MarketPrice)` (+ `DiscountRate = BulkDiscountRatio` when > 0), then the game's own `FinishBulkPricingApply(list, false, ratio)` (and `(list, true, discount)` for a discount) (viewer refresh, discount points, `onPriceSet`, `BulkPricingApplied_RPC`). Its sale reroll only touches `DiscountRate > 0.1`; with a saved bulk discount new products have that, so it rerolls their sale sign (correct), and the two calls send two `BulkPricingApplied_RPC`s (harmless). The saved bulk discount is always applied to new products too (design choice, per the TODO's "global pricing or discount rule"; there is no switch for it). Only `ApplyBulkMarkup`/`ApplyBulkDiscount` call `GetOrCreatePriceSetByPlayer`, so a freshly unlocked product has no player price (it would sell at `CurrentCost`).
- Not covered: re-activating a disabled license/product (`SetLicenseState`/`SetProductState`); those products normally kept their price.
- If the user never used bulk pricing, the saved ratio is 0, so products priced exactly at market follow the market (documented in README).

## How to test the v1.1.x rework

1. `VerboseLogging` on, bulk markup applied (say 5%). Buy a license on the host: `[Markup] License N unlocked (bought here): priced K new product(s) at 5% above market price (sent to guests)`. The pricing tab shows market × 1.05 for the new products; their price tags are set.
2. With a bulk discount applied earlier, new products also get that discount. Pricing tab never opened this session: the prices still appear (`EnsurePricingDataComplete`).
3. Online, buy a license on the guest: the host logs `(bought by another player)`; the guest's pricing tab shows the same prices.

## How to test in-game (host)

1. Keep `VerboseLogging` on. Apply a bulk markup (say 5%) from the pricing tab, then sleep to the next day.
2. Look in `LogOutput.log` for `[Markup] New day: the game tried to change N product(s), markup 5%, K to reprice`. It appears once per day, even when N = 0, and it is the proof that the `TryChangingBasePrices` and `ChangeCurrentCost` detours fire through the `OnStartedNewDay` delegate. If it never appears, the delegate detour doesn't work: report it. (N can be 0 on days the game skips price changes.)
3. For days with K > 0, expect `[Markup] Product <id>: market X, price A -> B` lines and `Repriced K product(s) ... (sent to guests)` when online. Check that the pricing tab shows B = X * 1.05 and the price tags changed.
4. A product priced by hand shows `price ... was set by hand ... left alone` and keeps its price.
5. Guest (unmodded, online): after the host's `Repriced ... (sent to guests)` line, the guest's pricing tab and price tags should show the new prices without reloading. Not verifiable from the dumps, so confirm in-game; if tags stay stale, the extra `BulkPricingApplied_RPC` isn't refreshing them and `BasePricesChanged_Broadcast` alone is in effect.
