# Grid Mode Default

When you place or move furniture (shelves, fridges, freezers, racks, checkouts, vending machines, signs and so on), the game's **grid mode** is on from the start. You no longer have to press the grid key for every piece.

- Grid mode is the game's own: the grid is drawn on the floor and the piece snaps to its cells. This mod only turns it on by default, so it is limited by the game's own grid support: the grid exists for floor, storage floor and ceiling surfaces. Pieces that are placed on other surfaces (for example on walls or on top of other furniture) get no grid and behave exactly as before.
- The grid key (**X** by default, gamepad North button) still toggles it. If you turn the grid off for a piece, it stays off for the next ones until you turn it on again or restart the game (the game's own behaviour).
- The grid size keys (`+` / `-`) work as before.
- **Ctrl** is a different feature in the game ("snap" to the neighbouring furniture's snap points). It isn't changed by this mod.

## Settings

`BepInEx/config/oscar.gridmodedefault.cfg`, or in-game with BepInExConfigManager:

| Section | Setting | Default | What it does |
|---|---|---|---|
| General | `Enabled` | `true` | Enable the mod. Turning it off restores the game's own grid setting from the next piece you pick up; no restart needed. |
| Diagnostics | `VerboseLogging` | `false` | Enable verbose logging for debugging purposes (`[GridMode]` lines in `BepInEx/LogOutput.log`). |

## Who needs it (multiplayer)

Only the player who wants grid mode on by default, host or guest. It only changes how your own held furniture is aimed. Other players (with or without the mod) just see the piece placed at a grid-aligned position through the game's normal placement.

## Notes

- Compatible with other mods that don't change furniture placement or the grid.
