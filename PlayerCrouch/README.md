# Player Crouch

Adds crouching to Supermarket Simulator. **Hold Left Ctrl** to crouch:

- Your view drops down and you walk at half speed. You can't sprint or jump while crouched.
- Your collision shape gets shorter, so you fit under low things. When you let go, you only stand up once there is room above you.
- Your character model crouches: knees bent, hips lowered, leaning forward a little. You can still walk while crouched. Other players see this if they also have the mod.

Optional toggle mode: press once to crouch and again to stand. In this mode, jump also stands you up.

**Ctrl is also the game's "snap" key while you place furniture.** While you are placing something, the crouch key can't start a crouch, so snapping still works. If you were already crouched when you picked something up, you stay crouched until you let go of the key (hold mode). A key you were holding while placing also has to be released before it can crouch you again. The same applies in menus, at the computer and at the checkout. In toggle mode you keep whatever state you were in there. On a skateboard you always stand.

## Settings

`BepInEx/config/oscar.playercrouch.cfg`, or in-game with BepInExConfigManager:

| Section | Setting | Default | What it does |
|---|---|---|---|
| General | `Enabled` | `true` | Master switch. Turning it off stands you up and removes the feature right away; no restart needed. |
| Controls | `Key` | `LeftCtrl` | Keyboard key used to crouch. |
| Controls | `GamepadButton` | `None` | Gamepad button used to crouch (None = none). Left stick press is already sprint and right stick press is ping. |
| Controls | `ToggleMode` | `false` | `false`: hold to crouch. `true`: press to crouch, press again (or jump) to stand. |
| Crouch | `SpeedMultiplier` | `0.5` | Walking speed while crouched, as a fraction of normal speed (0.1 to 1). |
| Crouch | `CameraHeight` | `0.65` | Eye height while crouched, as a fraction of standing eye height (0.3 to 0.95). |
| Crouch | `TransitionSeconds` | `0.2` | How long crouching down or standing up takes (0.05 to 1 s). |
| Crouch | `ShrinkCollider` | `true` | Shorter collision shape while crouched. |
| Animation | `BodyDepth` | `0.4` | How low the character's hips go, as a fraction of leg length (0.1 to 0.6). |
| Animation | `LeanAngle` | `15` | Forward lean of the upper body, in degrees (0 to 40). |
| Multiplayer | `ShareCrouchState` | `true` | Tells other players when you crouch, so modded players see your character crouch. |
| Multiplayer | `AnimateOtherPlayers` | `true` | Shows other modded players crouching. |
| Diagnostics | `VerboseLogging` | `false` | Writes `[Crouch]` lines to `BepInEx/LogOutput.log`. |

## Who needs it (multiplayer)

- **To crouch, you need the mod yourself.** This works for the host or a guest. Crouching only changes your own camera, speed and collision, so it can't desync anything.
- **To see someone else crouch, both of you need the mod.** The crouch state is shared as a small piece of player data through the game's online service. Players without the mod ignore it and just see you standing (they still see you move more slowly and at your real position).

## Notes

- The crouch pose is generated from the game's normal walk/idle animations, since the game has no crouch animation. While crouched, the arms keep the same pose as when standing.
- Compatible with other mods that don't change the player's movement speed, camera height or collision shape.
