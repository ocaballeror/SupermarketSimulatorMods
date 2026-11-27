# Auto Lights

At **7 pm** (19:00 on the in-game clock) the shop lights switch on by themselves, once per day, as if you had walked over and flipped the light switch.

- It uses the game's own light switch, so you hear the switch sound, the lever moves, and the lights are on for everyone in a multiplayer game.
- Both the **store lights** (the lamps inside the shop and the outdoor lamps) and the **storage room lights** are switched on, if you have placed lamps for them and they are off. Lights that are already on are left alone.
- It happens **once** per day. If you turn the lights off again later in the evening, they stay off.
- Loading a save that is already past 7 pm, or joining a game in the evening, doesn't switch anything: it only reacts to the clock actually reaching 7 pm while you play.
- The game itself turns the lights off again when a new day starts, so the mod switches them on again the next evening.
- If you have no light switch or no lamps, nothing happens.

## Settings

`BepInEx/config/flamboyanthopper.autolights.cfg`, or in-game with BepInExConfigManager / Mod Settings Tool:

| Section | Setting | Default | What it does |
|---|---|---|---|
| General | `Enabled` | `true` | Enable the mod. Can be turned off and on while playing. |
| Diagnostics | `VerboseLogging` | `false` | Enable verbose logging for debugging purposes (`[AutoLights]` lines in `BepInEx/LogOutput.log`). |

The time (7 pm) is fixed.

## Who needs it (multiplayer)

Only the **host** needs the mod. The host flips the switch through the game's own code, which tells everybody (and anyone who joins later) that the lights are on. Guests don't need the mod, and having it installed on a guest does nothing.

## Notes

- Compatible with other mods that don't change the light switches or the day clock.
