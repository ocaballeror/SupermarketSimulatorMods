# PriceMarkupKeeper: notes for agents

Player-facing info is in [README.md](README.md). This file holds implementation details and status. The shared rules and game internals are in the root [CLAUDE.md](../CLAUDE.md). The full root-cause analysis is in the doc comment at the top of `src/MarkupPatches.cs`.

## Status

v1.0.0: written, builds, **awaiting in-game test**. `VerboseLogging` defaults to on until the user confirms; switch it to off afterwards.

## Game mechanism

- Bulk pricing tab → `PriceManager.ApplyBulkMarkup(pct)`: `price = (float)Math.Round((pct/100f + 1f) * Pricing.MarketPrice, 2)` for each active, non-`IsHiddenForCustomer` product. `pct` is saved in `SaveManager.PriceContainer.BulkMarkupRatio` (`PriceManager.BulkMarkupRatio`). Then `FinishBulkPricingApply(list, isDiscount, ratio)` refreshes UI, fires `onPriceSet` and sends `BulkPricingApplied_RPC`.
- `Pricing.MarketPrice = round(CurrentCost * (1 + ProductSO.OptimumProfitRate/100), 2)`.
- New day: `DayCycleManager.OnStartedNewDay` → `PriceManager.TryChangingBasePrices` (delegate only, `CallerCount(0)`; Harmony's native detour still catches it) → `ChangeCurrentCost(id)` per changed product (its only caller). The player price isn't touched, hence the drift. Then `BasePricesChanged_Broadcast` sends all lists to guests when online.
- Discounts (`Pricing.DiscountRate`) are percentages and don't drift.

## Patches

| Target | Kind | Purpose |
|---|---|---|
| `PriceManager.TryChangingBasePrices` | prefix | start tracking (host / offline only), read `BulkMarkupRatio` |
| `PriceManager.ChangeCurrentCost` | prefix | decide if the product follows the markup (price == markup on the *old* market price, ±0.006); remember the old `CurrentCost` |
| `PriceManager.ChangeCurrentCost` | postfix | if the cost really changed (it may not: `ExemptFromChange`, no cost entry, 64 failed rerolls), set price to markup on the new market price; collect it, deduped by id |
| `PriceManager.TryChangingBasePrices` | postfix | `FinishBulkPricingApply`'s steps minus its sale sign reroll: `RefreshUnlockedProducts`, `CalculateTotalDiscountPoints`, `onPriceSet`, and `BulkPricingApplied_Broadcast` when online |

## Multiplayer

- Runs only when `!PhotonNetwork.IsConnected || IsMasterClient`. The price is changed before the game's own `BasePricesChanged_Broadcast`, so that already carries it. The extra `BulkPricingApplied_RPC` (via `BulkPricingApplied_Broadcast`, reliable, same PhotonView, so it arrives after) makes unmodded guests fire `onPriceSet` and refresh tags. It also sets the guest's saved `BulkMarkupRatio` to the value the host has, which is harmless.
- Guests never run `TryChangingBasePrices`: `PriceManager.Start` subscribes it to `OnStartedNewDay` only when `!IsConnectedAndSlave` (verified in the dump by the reviewer).

## Pitfalls / follow-ups

- `FinishBulkPricingApply` isn't called on purpose: it rerolls `_saleIndex` (`Random.Range(0,4)`) for products with `DiscountRate > 0.1`, which would change their sale sign style every morning. If the game adds steps to it in an update, mirror them in `MarkupKeeper.EndDay`.
- Products unlocked after the bulk apply have no player price (selling price falls back to `CurrentCost`), so with `OnlyProductsFollowingMarkup` on they're skipped. Not requested; a possible follow-up is to apply the markup on unlock.
- If the user never used bulk pricing, the saved ratio is 0, so products priced exactly at market follow the market (documented in README).
