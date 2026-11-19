# Player Crouch

Adds crouching to Supermarket Simulator. **Hold Left Ctrl** to crouch:

- Your view drops down and you walk at half speed. You can't sprint or jump while crouched.
- Your collision shape gets shorter, so you fit under low things. When you let go, you only stand up once there is room above you.
- Your character model crouches: knees bent, hips lowered, leaning forward a little. You can still walk while crouched. Other players see this if they also have the mod. (The game hides your own body in first person, so you only see your own crouch as the view dropping.)

Optional toggle mode: press once to crouch and again to stand. In this mode, jump also stands you up.

**Ctrl is also the game's "snap" key while you place furniture.** While you are placing something, the crouch key can't start a crouch, so snapping still works. If you were already crouched when you picked something up, you stay crouched until you let go of the key (hold mode). A key you were holding while placing also has to be released before it can crouch you again. The same applies in menus, at the computer and at the checkout. In toggle mode you keep whatever state you were in there. On a skateboard you always stand.

## Settings

`BepInEx/config/flamboyanthopper.playercrouch.cfg`, or in-game with BepInExConfigManager:

| Section | Setting | Default | What it does |
|---|---|---|---|
| General | `Enabled` | `true` | Enable the mod. Turning it off stands you up and removes the feature right away; no restart needed. |
| Controls | `Key` | `LeftCtrl` | Keyboard key used to crouch. |
| Controls | `GamepadButton` | `None` | Gamepad button used to crouch (None = none). Left stick press is already sprint and right stick press is ping. |
| Controls | `ToggleMode` | `false` | `false`: hold to crouch. `true`: press to crouch, press again (or jump) to stand. |
| Diagnostics | `VerboseLogging` | `false` | Enable verbose logging for debugging purposes (`[Crouch]` lines in `BepInEx/LogOutput.log`). |

Fixed values (not configurable): you walk at half speed, your eye height drops to 55% of standing height, going down or up takes 0.2 s, and your collision shape shrinks while crouched. Other players see your character bend its knees so its head sits at your crouched eye height, with a slight forward lean. Your crouch state is shared with other players, and other modded players are shown crouching.

The config file is named after the plugin id. Version 1.0.5 renamed the id to `flamboyanthopper.playercrouch`, so the settings file is now `flamboyanthopper.playercrouch.cfg`. If you had changed settings before, they start at the defaults; copy them over from the old `*.playercrouch.cfg` file and delete that one.

## Who needs it (multiplayer)

- **To crouch, you need the mod yourself.** This works for the host or a guest. Crouching only changes your own camera, speed and collision, so it can't desync anything.
- **To see someone else crouch, both of you need the mod, version 1.0.5 or newer.** The crouch state is shared as a small piece of player data through the game's online service. Players without the mod ignore it and just see you standing (they still see you move more slowly and at your real position). Versions before 1.0.5 used a different name for that data, so they can't see (or be seen by) newer versions.

## Notes

- The crouch pose is generated from the game's normal walk/idle animations, since the game has no crouch animation. While crouched, the arms keep the same pose as when standing. If a modded player crouches while carrying a box, the box lowers with them on your screen.
- You always stand up while driving a vehicle, on a skateboard, and when the mod is turned off. If you turn the mod off while crouched under something low, your collision shape stays short until there is room to stand.
- Compatible with other mods that don't change the player's movement speed, camera height or collision shape.
