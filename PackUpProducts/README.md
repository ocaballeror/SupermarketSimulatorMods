# Pack Up Products

Right click a product on a **shelf, fridge or freezer** with **empty hands** and the game gives you a **box of the right size for that product**, already in your hands, with that product inside. Right click again to add more products the normal way.

Normally right clicking a shelf does nothing unless you already hold a box, crate or paper bag. Each product only fits one box size; this mod picks the right one for you, so you never have to fetch an empty box first.

## How it works

1. Walk up to a stocked shelf slot with nothing in your hands.
2. Right click (the game's Take button) on a product.
3. A new open box appears in your hands with one of that product in it, and the product leaves the shelf.
4. Keep right clicking to take more products into the box, left click to place products back on a shelf, close it (C), put it on a rack, and so on.

The box is a normal game box: it is saved with your store, has the correct product and size, and is counted in your inventory. The product is moved, not copied, so nothing is duplicated or lost.

## Settings

`BepInEx/config/flamboyanthopper.packupproducts.cfg`, or in-game in the mod settings:

| Section | Setting | Default | What it does |
|---|---|---|---|
| General | `Enabled` | `true` | Enable the mod. Takes effect right away. |
| General | `TriggerAction` | `Take` | The game input action that packs. `Take` is the game's own Take action, right mouse button by default, and follows any key you rebind it to in the game's controls. Other action names of the game's Player controls also work. |
| Diagnostics | `VerboseLogging` | `false` | Enable verbose logging for debugging purposes: logs every step as `[PackUp]` lines in `BepInEx/LogOutput.log`, including why a click on a shelf was ignored. |

## Cost

Free. The products are already yours, and a box taken from a shelf is not a purchase.

## Who needs it (multiplayer)

- **Playing alone, or hosting: install it on the host.** The host spawns the box and everybody sees it, including guests without the mod.
- **A guest can pack too, but only if the host also has the mod.** The guest's game creates the box and the host saves it. If the host does not have the mod, a guest with the mod does nothing (the mod logs why), so no box or product can be lost.
- Guests without the mod simply don't get the feature; they see the host's and other guests' boxes normally.

## Limits

- Hands must be empty. With a box, crate or paper bag in your hands, right click keeps its normal meaning.
- One product is taken per click.
- Ice cream cone and flavour slots, and vending machines, are left to the game.
- If the game refuses the take for some reason (it shows its own warning), you end up holding an empty box for that product; throw it away or fill it.

## Compatibility

Doesn't change any game code, it only listens for the click. It reuses the game's own box spawning, pickup and take logic, so mods that change those (for example boxes in hands or stocking) keep working with the box it creates.
