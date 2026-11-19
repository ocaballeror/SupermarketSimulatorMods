# Furniture Move Cancel

Picked up a shelf, fridge, freezer, rack, checkout or other furniture by mistake? Press **Escape** (gamepad: **B**) while moving it, and it goes straight back to where it was, in the same position and rotation.

- It only works if the original spot is **still free**, and, where the mod can tell, the surface the item stood on is still there (for example, the shelf under a category sign). Otherwise the key does nothing and you keep holding the furniture.
- The key appears in the key hints list on the side of the screen while you move furniture, labelled with the game's own "Cancel" text. Despite the label, it puts the furniture back where it was; it does not box it up.
- If nothing happens right after picking something up, press Escape again: the game ignores placing for a very short moment after pickup. The mod gives no on-screen message when the key does nothing.
- New furniture you just unpacked from a box has no previous spot, so the key does nothing for it.
- The furniture is put down immediately at its original spot.
- The key follows the game's own "Back" key binding.

## Settings

`BepInEx/config/oscar.furnituremovecancel.cfg`, or in-game with BepInExConfigManager:

| Section | Setting | Default | What it does |
|---|---|---|---|
| General | `Enabled` | `true` | Enable the mod. Turning it off removes the feature right away; no restart needed. |
| Diagnostics | `VerboseLogging` | `false` | Enable verbose logging for debugging purposes (`[MoveCancel]` lines in `BepInEx/LogOutput.log`). |

## Who needs it (multiplayer)

Only the player who wants to use the key. It works for the host or a guest, whoever has it installed. Putting the furniture down uses the game's normal placement, so other players don't need the mod.

For most furniture types (racks, fridges and other generic furniture, checkouts, category signs, scales, ice cream stands, the computer), the game doesn't send a position when an item is placed. Other players place their copy wherever its streamed position has got to. So just before putting the item down, the mod sends its exact position to everyone with the game's own sync message, and the item is still placed immediately. If a piece can't do that, the mod holds it at its spot for 0.3 s instead, so the other players' copies catch up.

## Notes

- Compatible with other mods that don't change furniture placement.
