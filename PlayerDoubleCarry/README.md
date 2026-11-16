# Player Double Carry

Carry two boxes at once.

## How to use

1. Pick up a box as usual. Make sure it is **closed** (press C if it is open).
2. Look at another **closed** box (on the floor, at the delivery spot, in a pile) and **left-click** (Use, the normal pick-up click). It joins the first one; you now carry both, the new box on top.
3. While you carry two boxes neither can be opened or used: the left-click that would open a box (or place products) does nothing, and right-click does not take products.
4. Everything you normally do with a box in hand works on the **top** box: drop/place it (G), throw it (R), put it on a rack, in a trash bin, or on a placement area.
5. As soon as the top box leaves your hands, the other box comes up into your hands and is a normal held box again: open it, restock shelves, and so on.

**At the wholesale store:** while you hold a single closed box, look at a product on a shelf, fridge or freezer and **hold left click** for the usual purchase time. The box is bought as normal (same price and money check) and lands on top of the one you hold. There is no progress circle while you hold; just keep the button down and keep looking at the product. Letting go or looking away early cancels it.

Only loose, closed boxes can be picked up as the second box, not boxes straight from a rack. A third box can't be added.

While you hold a single closed box, left-clicking **while looking at another box** now picks that box up instead of opening the one in your hands. To open your box, look away from other boxes (for example at the shelf you are about to fill).

As a guest, the host has to approve the second box like any pickup. If the host doesn't answer in time, your box stays locked for a few seconds (as the game does while it waits), and if the answer still comes it is stacked normally.

## Settings

`BepInEx/config/oscar.playerdoublecarry.cfg`, also editable in-game in the mod settings.

| Section | Setting | Default | Description |
|---|---|---|---|
| General | `Enabled` | `true` | Master switch, applies immediately. If you carry two boxes (or are waiting for the host to approve one) when you turn it off, that is finished first: the second box still comes back to your hands when you put the first one down. |
| Carry | `BuySecondBoxAtWholesale` | `true` | At the wholesale store, hold left click on a product while holding a closed box to buy a second box and carry both. |
| Carry | `ShowOtherPlayersStack` | `true` | Show the two boxes another player carries stacked instead of inside each other. Cosmetic, only affects what you see. |
| Diagnostics | `VerboseLogging` | `true` | Log each step (tag `[PlayerCarry]` in `BepInEx/LogOutput.log`). |

## Multiplayer

- **Only the player who wants to carry two boxes needs the mod.** It works whether that player is the host or a guest; the host and the other players can be unmodded.
- The game's own pick-up messages are used, so every player agrees on who holds which box, and other players can't take either box out of your hands.
- Players **without** the mod see your two boxes at the same spot in your hands (they look like one box, or one inside the other if they differ in size). Players with the mod see them stacked.
- **Leaving while carrying two boxes** (quitting, crash, disconnect): the game only puts down the box it knows the player holds. With the mod installed on the **host**, the other box is put down on the host too, so it stays in the game. Other guests only keep it if they have the mod as well; without it, the box disappears for them until the save is reloaded. In a two-player game a modded host is enough.
- A player who joins while you carry two boxes sees the bottom box wherever the game's join-time sync puts it (probably not in your hands) until it comes up into your hands; they can't pick it up meanwhile.

## Install

Copy `PlayerDoubleCarry.dll` into `BepInEx/plugins/` (the folder itself, not a subfolder). Requires BepInEx 6 (IL2CPP).

## Compatibility

- Works alongside Supermarket Fixes (its "ghost item in hands" fix leaves the second box alone).
- Other mods that change how boxes are picked up, held or opened may conflict.
