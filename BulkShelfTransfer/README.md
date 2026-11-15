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

The game's own rules apply unchanged: a box only takes one kind of product and only if it is the right box size, a shelf slot only takes one kind of product of the right display type (an empty slot is relabelled as usual), and the box, bag, crate and slot capacities are respected. If the game refuses (wrong product, wrong box size, full), you get the game's usual warning and nothing moves.

The products still fly between the shelf and the container with the game's normal animation, only much faster (configurable), so it takes a fraction of a second.

Vending machines, the baker's crate and the security guard's crate are left as they are (hold the button as usual). Clicking without Shift works exactly as before.

## Settings

`BepInEx/config/oscar.bulkshelftransfer.cfg`, or in-game with BepInExConfigManager:

| Section | Setting | Default | What it does |
|---|---|---|---|
| General | `Enabled` | `true` | Master switch. Turning it off removes the mod's effect immediately; no restart needed. |
| Keys | `ModifierKey` | `LeftShift` | Key to hold while clicking. The clicks themselves are the game's **Take** (right click) and **Use** (left click) bindings, so if you rebind those in the game's settings the mod follows. `None` makes every Take/Use click a bulk transfer. |
| Keys | `BulkTake` | `true` | Modifier + Take fills the container from the shelf. |
| Keys | `BulkPlace` | `true` | Modifier + Use empties the container onto the shelf. |
| Containers | `Boxes` | `true` | Use bulk transfer with product boxes. |
| Containers | `PaperBags` | `true` | Use bulk transfer with online-order paper bags. |
| Containers | `Crates` | `true` | Use bulk transfer with the multi-product crate. |
| Speed | `SecondsBetweenItems` | `0.02` | Time between two products (0 to 0.5 s; 0 = one per frame). |
| Speed | `AnimationSpeed` | `5` | How much faster the products fly (1 = the game's normal speed, up to 20). Only affects your own screen. |
| Diagnostics | `VerboseLogging` | `true` | Writes `[Bulk]` lines to `BepInEx/LogOutput.log` for each bulk transfer. |

The modifier is a keyboard key; there is no gamepad shortcut.

## Who needs it (multiplayer)

Only the player who wants to use it, host or guest. Each product still moves through the game's normal per-product actions and network messages, so the other players (with or without the mod) see the products move as usual, just in quick succession and at the normal animation speed.

When a guest fills or empties a **box**, every product goes through the host as in the normal game, so it can take a moment longer to finish on a slow connection. While those products are still on their way, a second Shift-click on the same box is ignored for up to two seconds.

## Notes

- Compatible with mods that don't change how products are placed on or taken from shelves.
- Status: written, not yet tested in-game.
