# Price Markup Keeper

Keeps your **global markup** in place when market prices change.

On the computer's pricing tab you can set every price to, say, 5% above market price in one go. But at the start of a new day the game raises or lowers the market price of a few products and leaves their selling prices as they were, so those products end up above or below your 5%. With this mod, any product whose market price changes gets its price set again to your markup on top of the new market price. That includes products that got cheaper.

Products you unlock by buying a license get your markup straight away too, so they never sell at cost.

Discounts set from the pricing tab are a percentage of the selling price, so they keep applying after the change with or without this mod.

## Settings

`BepInEx/config/oscar.pricemarkupkeeper.cfg`, or in-game in the mod settings:

| Section | Setting | Default | What it does |
|---|---|---|---|
| General | `Enabled` | `true` | Master switch. Turning it off restores normal behaviour right away; no restart needed. |
| Pricing | `KeepMarkupOnPriceChanges` | `true` | Reprice products whose market price changed at the start of the day, using the markup you last applied from the bulk pricing tab. |
| Pricing | `OnlyProductsFollowingMarkup` | `true` | Only reprice products whose price still matched your markup before the change. Prices you typed in by hand for single products are left alone. Turn it off to reprice every product whose market price changed, **including ones you priced by hand** (with no markup ever applied from the pricing tab, that means exactly market price). |
| Pricing | `PriceNewlyUnlockedProducts` | `true` | When a product license is bought (by anyone in the session), give its new products your markup on top of their market price right away. Products that already have a price are left alone. |
| Diagnostics | `VerboseLogging` | `false` | Logs every repriced or skipped product as `[Markup]` lines in `BepInEx/LogOutput.log`. |

## Using it

1. Apply a markup once from the computer's pricing tab (bulk pricing). The game remembers that percentage in your save.
2. From then on, whenever a product's market price changes at the start of a day, its price follows, and newly unlocked products start at your markup.

If prices already drifted before you installed the mod, apply the markup from the pricing tab once more to line everything up again. With `OnlyProductsFollowingMarkup` on, products that don't match the markup count as priced by hand and are left alone.

If you never used the bulk pricing tab, the saved markup is 0%, so products priced exactly at market price will keep following the market price.

## Who needs it (multiplayer)

- **Host: required.** The host decides the daily price changes and prices newly unlocked products (whoever bought the license), and sends the new prices to everyone.
- **Guests: not needed.** Guests receive the updated prices from the host the same way as when someone uses the bulk pricing tab.

## Notes

- Products unlocked before you installed this version (or with `PriceNewlyUnlockedProducts` off) may still have no price set; apply the markup from the pricing tab once to price them.
- Products whose cost doesn't actually change that day are never touched.
- Compatible with other mods that don't change pricing.
