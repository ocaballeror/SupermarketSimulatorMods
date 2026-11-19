# Paint Can Capacity

Makes wall paint cans last longer. By default each can paints **twice as many walls**. The multiplier can be set anywhere from 1x to 10x.

The liquid level shown inside the can is scaled too: a new can shows the same fill as a normal new can (about 80%) and empties gradually, over as many walls as the multiplier says (twice as many at the default 2x).

## Settings

`BepInEx/config/flamboyanthopper.paintcancapacity.cfg`, or in-game with BepInExConfigManager:

| Section | Setting | Default | What it does |
|---|---|---|---|
| General | `Enabled` | `true` | Enable the mod. Turning it off restores normal paint cans right away (including cans waiting in the game's reuse pool); no restart needed. |
| Paint | `CapacityMultiplier` | `2` | How many times longer a can lasts (1 to 10). Decimals such as 1.5 work. |
| Diagnostics | `VerboseLogging` | `false` | Enable verbose logging for debugging purposes (`[PaintCan]` lines in `BepInEx/LogOutput.log`). |

## Who needs it (multiplayer)

- **Host: required.** The host spawns purchased cans, and the paint amount travels with the can to every player.
- **Guests: optional.** A guest without the mod still gets the extra paint. Their can just shows as completely full until the paint left drops below a normal can's capacity (with the default 2x, it first drops at the 7th wall).
- If only a **guest** has the mod and the host doesn't, the cans still hold the normal amount of paint and paint the normal number of walls. The only effect is cosmetic: the liquid level looks lower on that guest (a normal can shows about 40% instead of 80% at 2x). A guest-only install is pointless.

## Notes

- Only cans **bought after** installing the mod (or changing the multiplier) get the extra paint. Cans you already own keep the paint they have. They paint the normal number of walls. Their liquid level is shown relative to a new can's larger capacity, so at 2x they look about 40% full instead of 80%, and they drain at the same rate per wall as new cans.
- If you remove the mod, cans holding extra paint keep it and show as completely full until they drop below normal capacity. Nothing breaks.
- Compatible with other mods that don't change paint cans.
