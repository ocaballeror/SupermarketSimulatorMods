# Product Highlight

Shows you where the product in your hands belongs, with the same green outline the game already uses.

- **Holding a box of products:** every shelf, fridge and freezer that has a **label for that product** is outlined as a whole, **even when it is empty**. So you can find where a product goes before anything is on the shelf, just by its labels. (The game already outlines the matching boxes in the storage racks; this adds the shelves.)
- **Holding a paper bag for an online order:** the **groups of products** on shelves, fridges and freezers that the order still needs are outlined. Only products that are really there are marked, never empty shelves, so if nothing on the shelves matches, nothing is outlined. What the bag already holds isn't counted as needed any more. The order is the one the bag is tagged for, or the one selected in your order notebook.
- **Always up to date:** the outlines follow the labels and the stock live. Relabel a shelf, stock it, empty it, or add a product to the bag, and the outlines change within a fraction of a second. They disappear as soon as the box or bag leaves your hands (drop, throw, put in a rack, trash, hand over).

## Settings

`BepInEx/config/oscar.producthighlight.cfg`, or in-game in the mod settings:

| Section | Setting | Default | What it does |
|---|---|---|---|
| General | `Enabled` | `true` | Master switch. Takes effect right away: every outline this mod drew disappears. |
| Highlight | `ShelvesForBox` | `true` | Outline the shelves, fridges and freezers labeled with the product of the box you hold. |
| Highlight | `ProductsForOrderBag` | `true` | Outline the product groups an online-order bag still needs. |
| Diagnostics | `VerboseLogging` | `true` | Logs what the mod does (`[Highlight]` lines in `BepInEx/LogOutput.log`); only when something changes. |

## Who needs it (multiplayer)

**Only the player who wants to see the outlines.** Everything is local: it draws outlines on your screen for what *you* hold. Nothing is sent over the network and no game data changes, so the host and the other players can be unmodded, host or guest alike.

## Install

Copy `ProductHighlight.dll` into `BepInEx/plugins/` (the folder itself, not a subfolder). Requires BepInEx 6 (IL2CPP).

## Compatibility

- Doesn't patch any game method and draws its own outlines on separate objects, so it doesn't interfere with the game's own highlights (hover, racked boxes) or with other highlight mods such as Whole Truck Highlight.
- Ice cream stands, crates and trash bins aren't outlined; neither are vending machines.
