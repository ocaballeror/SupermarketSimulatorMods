# Paint Can Capacity

Makes wall paint cans last longer. By default each can paints **twice as many walls**. The multiplier can be set anywhere from 1x to 10x.

The liquid level shown inside the can is scaled too, so a new can looks full and empties gradually as usual.

## Settings

`BepInEx/config/oscar.paintcancapacity.cfg`, or in-game with BepInExConfigManager:

| Section | Setting | Default | What it does |
|---|---|---|---|
| General | `Enabled` | `true` | Master switch. Turning it off restores normal paint cans right away; no restart needed. |
| Paint | `CapacityMultiplier` | `2` | How many times longer a can lasts (1 to 10). Decimals such as 1.5 work. |
| Diagnostics | `VerboseLogging` | `false` | Writes `[PaintCan]` lines to `BepInEx/LogOutput.log`. |

## Who needs it (multiplayer)

- **Host: required.** The host spawns purchased cans, and the paint amount travels with the can to every player.
- **Guests: optional.** A guest without the mod still gets the extra paint. Their can just shows as full until it has used up more than a normal can's worth of paint.
- If only a **guest** has the mod and the host doesn't, nothing changes: cans have normal capacity and look normal.

## Notes

- Only cans **bought after** installing the mod (or changing the multiplier) get the extra paint. Cans you already own keep the paint they have. They show as full and paint the normal number of walls; their level drops faster than a new can's, because they hold less paint.
- If you remove the mod, cans holding extra paint keep it and show as full until they drop below normal capacity. Nothing breaks.
- Compatible with other mods that don't change paint cans.
