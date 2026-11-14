# Price Markup Keeper

Keeps your **global markup** in place when market prices change.

On the computer's pricing tab you can set every price to, say, 5% above market price in one go. But at the start of a new day the game raises or lowers the market price of a few products and leaves their selling prices as they were, so those products end up above or below your 5%. With this mod, any product whose market price changes gets its price set again to your markup on top of the new market price. That includes products that got cheaper.

Discounts set from the pricing tab are a percentage of the selling price, so they keep applying after the change with or without this mod.

## Settings

`BepInEx/config/oscar.pricemarkupkeeper.cfg`, or in-game with BepInExConfigManager:

| Section | Setting | Default | What it does |
|---|---|---|---|
| General | `Enabled` | `true` | Master switch. Turning it off restores normal behaviour right away; no restart needed. |
| Pricing | `KeepMarkupOnPriceChanges` | `true` | Reprice products whose market price changed at the start of the day, using the markup you last applied from the bulk pricing tab. |
| Pricing | `OnlyProductsFollowingMarkup` | `true` | Only reprice products whose price still matched your markup before the change. Prices you typed in by hand for single products are left alone. Turn it off to reprice every product whose market price changed. |
| Diagnostics | `VerboseLogging` | `true` | Logs every repriced or skipped product as `[Markup]` lines in `BepInEx/LogOutput.log`. |

## Using it

1. Apply a markup once from the computer's pricing tab (bulk pricing). The game remembers that percentage in your save.
2. From then on, whenever a product's market price changes at the start of a day, its price follows.

If prices already drifted before you installed the mod, apply the markup from the pricing tab once more to line everything up again. With `OnlyProductsFollowingMarkup` on, products that don't match the markup count as priced by hand and are left alone.

If you never used the bulk pricing tab, the saved markup is 0%, so products priced exactly at market price will keep following the market price.

## Who needs it (multiplayer)

- **Host: required.** The host decides the daily price changes and sends them to everyone, including the new selling prices.
- **Guests: not needed.** Guests receive the updated prices from the host the same way as when someone uses the bulk pricing tab.

## Notes

- Only the daily market price changes are handled. Products you unlock after applying the markup don't have a price set yet. With `OnlyProductsFollowingMarkup` on they're left alone until you apply the markup from the pricing tab again (or set their price yourself).
- Products whose cost doesn't actually change that day are never touched.
- Compatible with other mods that don't change pricing.
