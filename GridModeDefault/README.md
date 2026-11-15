# Grid Mode Default

When you place or move furniture (shelves, fridges, freezers, racks, checkouts, vending machines, signs and so on), the game's **grid mode** is on from the start. You no longer have to press the grid key for every piece.

- Grid mode is the game's own: the grid is drawn on the floor and the piece snaps to its cells. It only appears where the game supports it, so furniture types or surfaces without a grid behave exactly as before.
- The grid key (**X** by default, gamepad North button) still toggles it. If you turn the grid off for a piece, it stays off for the next ones until you turn it on again or restart the game, unless you change the `RememberToggle` setting.
- The grid size keys (`+` / `-`) work as before.
- **Ctrl** is a different feature in the game ("snap" to the neighbouring furniture's snap points). It isn't changed by this mod.

## Settings

`BepInEx/config/oscar.gridmodedefault.cfg`, or in-game with BepInExConfigManager:

| Section | Setting | Default | What it does |
|---|---|---|---|
| General | `Enabled` | `true` | Master switch. Turning it off restores the game's own grid setting right away; no restart needed. |
| GridMode | `RememberToggle` | `true` | `true`: the grid starts on, and if you turn it off with the grid key it stays off until you turn it back on (or restart the game). `false`: every piece you pick up or place starts with the grid on again. |
| Diagnostics | `VerboseLogging` | `true` | Writes `[GridMode]` lines to `BepInEx/LogOutput.log` each time placement starts. |

## Who needs it (multiplayer)

Only the player who wants grid mode on by default, host or guest. It only changes how your own held furniture is aimed. Other players (with or without the mod) just see the piece placed at a grid-aligned position through the game's normal placement.

## Notes

- Compatible with other mods that don't change furniture placement or the grid.
