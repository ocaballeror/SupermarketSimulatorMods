# Bulk Shelf Transfer

Move a whole shelf slot's worth of products with one click instead of holding the mouse button.

- **Shift + right click** (Take) while holding a container and looking at a shelf, fridge or freezer slot: all products of that slot go into the container, until the slot is empty or the container is full.
- **Shift + left click** (Use): all products in the container that fit the slot go onto it, until the container is empty or the slot is full.

It works with:

| Container | Fill from shelf | Empty onto shelf |
|---|---|---|
| Product box (from the computer orders) | yes | yes |
| Online-order paper bag | yes | yes (only the products that match the slot) |
| Multi-product crate | yes | yes (only the products that match the slot) |

**Paper bags for online orders:** when you are packing a bag for an order (the order selected in your notebook) that no other player and no courier is already handling, Shift + right click only takes as many of that product as the order still needs. If the order doesn't contain that product, or already has enough of it in the bag, Shift + right click does nothing (so a held button can't over-fill the order). Without such an order, it fills the bag as far as it can.

The game's own rules apply unchanged: a box only takes one kind of product and only if it is the right box size, a shelf slot only takes one kind of product of the right display type (an empty slot is relabelled as usual), and the box, bag, crate and slot capacities are respected. If the game refuses (wrong product, wrong box size, full), you get the game's usual warning and nothing moves.

The products still fly between the shelf and the container with the game's normal animation, only played faster, so each product's flight is shortened to fit between the transfers.

Vending machines, the baker's crate and the security guard's crate are left as they are (hold the button as usual). Clicking without Shift works exactly as before.

## Settings

`BepInEx/config/oscar.bulkshelftransfer.cfg`, or in-game with BepInExConfigManager:

| Section | Setting | Default | What it does |
|---|---|---|---|
| General | `Enabled` | `true` | Enable the mod. Turning it off removes the mod's effect immediately; no restart needed. |
| Keys | `ModifierKey` | `LeftShift` | Key to hold while clicking. The clicks themselves are the game's **Take** (right click) and **Use** (left click) bindings, so if you rebind those in the game's settings the mod follows. `None` makes every Take/Use click a bulk transfer. |
| Diagnostics | `VerboseLogging` | `false` | Enable verbose logging for debugging purposes (`[Bulk]` lines in `BepInEx/LogOutput.log` for each bulk transfer). |

Products are moved one every 0.2 seconds (never faster than 0.02 s online). The modifier is a keyboard key; there is no gamepad shortcut.

## Who needs it (multiplayer)

Only the player who wants to use it, host or guest. Each product still moves through the game's normal per-product actions and network messages, so the other players (with or without the mod) see the products move as usual, just in quick succession and at the normal animation speed.

When a guest fills or empties a **box**, every product goes through the host as in the normal game, so it can take a moment longer to finish on a slow connection. While a bulk transfer is running and its products are still on their way (at most about two seconds after it ends), the box stays in your hands: clicks, drop and throw on that box are ignored until everything has arrived. This keeps the box contents the same on every screen.

On the host and offline, a second Shift-click during a running transfer is ignored too.

With the crate, the transfer stops if you look away from the shelf slot before it is done.

## Notes

- Compatible with mods that don't change how products are placed on or taken from shelves.
