# Player Double Carry

Carry two boxes at once.

## How to use

1. Pick up a box as usual. Make sure it is **closed** (press C if it is open).
2. Look at another box (on the floor, at the delivery spot, in a pile) and **left-click** (Use, the normal pick-up click). It joins the first one; you now carry both, the new box on top.
3. While you carry two boxes neither can be opened: the left-click that would open a box does nothing.
4. Everything you normally do with a box in hand works on the **top** box: drop/place it (G), throw it (R), put it on a rack, in a trash bin, or on a placement area.
5. As soon as the top box leaves your hands, the other box comes up into your hands and is a normal held box again: open it, restock shelves, and so on.

Only loose boxes can be picked up as the second box, not boxes straight from a rack. A third box can't be added.

While you hold a single closed box, left-clicking **while looking at another box** now picks that box up instead of opening the one in your hands. To open your box, look away from other boxes (for example at the shelf you are about to fill).

## Settings

`BepInEx/config/oscar.playerdoublecarry.cfg`, also editable in-game with BepInExConfigManager.

| Section | Setting | Default | Description |
|---|---|---|---|
| General | `Enabled` | `true` | Master switch, applies immediately. If you carry two boxes when you turn it off, the second box still comes back to your hands when you put the first one down. |
| Carry | `ShowOtherPlayersStack` | `true` | Show the two boxes another player carries stacked instead of inside each other. Cosmetic, only affects what you see. |
| Diagnostics | `VerboseLogging` | `true` | Log each step (tag `[PlayerCarry]` in `BepInEx/LogOutput.log`). |

## Multiplayer

- **Only the player who wants to carry two boxes needs the mod.** It works whether that player is the host or a guest; the host and the other players can be unmodded.
- The game's own pick-up messages are used, so every player agrees on who holds which box, and other players can't take either box out of your hands.
- Players **without** the mod see your two boxes at the same spot in your hands (they look like one box, or one inside the other if they differ in size). Players with the mod see them stacked.
- Not recommended: leaving the game while carrying two boxes. The game only puts down the box it knows you hold; the other one may disappear for the remaining players until the save is reloaded (not tested).
- A player who joins while you carry two boxes only sees the top one in your hands until the second comes up.

## Install

Copy `PlayerDoubleCarry.dll` into `BepInEx/plugins/` (the folder itself, not a subfolder). Requires BepInEx 6 (IL2CPP).

## Compatibility

- Works alongside Supermarket Fixes (its "ghost item in hands" fix leaves the second box alone).
- Other mods that change how boxes are picked up, held or opened may conflict.
