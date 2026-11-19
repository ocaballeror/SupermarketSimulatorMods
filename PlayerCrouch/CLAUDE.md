# PlayerCrouch: notes for agents

Player-facing info is in [README.md](README.md). This file holds implementation details and status. The shared rules and game internals are in the root [CLAUDE.md](../CLAUDE.md). The full mechanism is documented at the top of `src/Crouch.cs` (local player) and `src/CrouchPose.cs` (model posing).

## Status

v1.0.4: settings reduced (commit "simplify mod settings"). Only `Enabled`, `Key`, `GamepadButton`, `ToggleMode` and `VerboseLogging` (the cfg key `[Diagnostics] VerboseLogging` already existed, so existing cfgs keep their stored value; the default is now false for new configs) remain as config; `SpeedMultiplier` 0.5, `CameraHeight` 0.55, `TransitionTime` 0.2, `ShrinkCollider`, `BodyDepth` 0.4, `LeanAngle` 15, `ShareWithOthers` and `AnimateOthers` are `internal const` in `Plugin.cs`. Only the C# field was renamed (`Verbose` -> `VerboseLogging`); the cfg key was not. Behaviour is otherwise as in 1.0.3 (note that the default crouch eye height is now 0.55 instead of 0.65).

v1.0.3 (rework after the 2026-11-15 session, not yet tested in-game): the user saw no crouch pose on the other player's screen with the mod on both sides. The logs showed the network state arriving both ways (`Player '<name>' crouched`), so the pose step failed silently: it took the Animator of the first `PlayerAnimation` in the player (`GetComponentInChildren<PlayerAnimation>(true)`), which is the normally inactive `Man_01_Rig_Test Variant` skin (`man_rig_test`), and returned without a log when it wasn't active. Every skin has its own Animator (see Game reference: player models). `FindAnimator` now uses `PlayerInstance.SkinController.ActiveSkin.m_Animator` (+ that skin's `PlayerIKHandler`), falling back to the first active humanoid Animator; re-resolved every 2 s and whenever the current one goes inactive (skin change); a warning is logged once if none is found. Check the `Model for player ...: animator '...' (active skin '...')` lines.

v1.0.2: **confirmed working in-game by the user (2026-11-15).** 1.0.2 only switched the verbose log setting to default off (existing configs keep their value). The open questions below predate that test; keep them as hints if a problem shows up later.
- Camera fallback (v1.0.1): if the FPS vcam's `Follow` is a different transform inside the player than `CinemachineCameraTarget`, the offset is applied to that one instead (log: `Camera offset target is now '<name>'`). Re-evaluated every 2 s while standing. Check the `VCam follows:` line; if the camera still doesn't drop, the vcam follows something this fallback doesn't pick.
- Leg IK (v1.0.1): `Model for player ...` lines now include `legIK(L=<constraint weight>/<rig weight> R=...)`. Expect `0.00` outside vehicles. A `Leg IK is active ... not posing it` line while on foot means the guard fires wrongly; check that line. Test sitting in a vehicle as a remote player too.
- Does the FPS vcam actually follow `FirstPersonController.CinemachineCameraTarget`? (Logged `VCam follows:`.) If not, the camera won't drop and the camera offset must move whatever the vcam follows.
- Is the player Animator humanoid (`human=True`)? The pose needs `Animator.GetBoneTransform`.
- The local player's own body renderers are disabled (`PlayerSkin.Toggle(!m_IsLocal)`), so the local pose is invisible (no shadow either); it is applied anyway, harmlessly.
- Do held boxes look wrong for other players while someone crouches? The hand IK (Animation Rigging) is solved before our LateUpdate, so hands move with the lowered torso.

## How it works

- **Files:** `Crouch.cs` handles the local state, input, camera, collider and network property. `CrouchPose.cs` handles the procedural pose for all players. `PlayerPatches.cs` holds the Harmony patches: `FirstPersonController.Move` prefix/postfix for speed and a `Jump` prefix. `CrouchBehaviour.cs` is the Update/LateUpdate driver.
- **No game crouch exists** (no action, no animator state). Everything is added on top.
- **Input:** read straight from `Keyboard.current[key]` and the optional gamepad button. It's ignored while `FurniturePlacer.m_PlacingMode` or `PlayerObjectHolder.m_PlacingMode` is set (Ctrl = the game's `SnapPlace`), or while `_movementInteraction` is false (UI, computer, checkout, pause); the state is frozen in those cases. `PlayerInteraction.InSkateboard` forces standing.
- **Speed:** `Move` picks `MoveSpeed` or `SprintSpeed` as its target speed. Both are swapped for `MoveSpeed * SpeedMultiplier` (lerped by the crouch amount) for the duration of the call.
- **Jump:** `Jump` is invoked through `InputActions.onJump`. The prefix skips it while crouched; in toggle mode it also clears `Want`.
- **Camera:** `CinemachineCameraTarget.localPosition.y` gets an offset in LateUpdate. The only native writer of that position is `PushCameraEffect` (a DOTween `DOLocalMove`), so we re-base whenever the value differs from our last write.
- **Collider:** `CharacterController.height/center` are untouched by game code, so they're shrunk with the bottom kept fixed. Standing up waits for a `Physics.OverlapCapsule` (crouched top → standing top, radius × 0.9, the player layer's collision mask, triggers and own hierarchy ignored).
- **Network:** the Photon player custom property `flamboyanthopper.crouch` (bool) on `PhotonNetwork.LocalPlayer`. It's sent on change and re-checked every 2 s, so it self-heals after a room change. Remote state is read from `PlayerInstance.m_PhotonView.Owner.CustomProperties`. Unmodded clients just store the key. The game itself uses player properties `sid` and `png`.
- **Pose:** on the active skin's Animator (`FindAnimator`), in LateUpdate (after the Animator and rigs), the mod lowers the hips, leans the spine (and restores the head's world rotation), and runs two-bone IK on both legs back to the animated foot positions, bending the knees toward the model's forward. An accumulation guard skips frames where the Animator didn't overwrite the bones, by comparing hips localPosition and left thigh localRotation with what we wrote.

## Review round 1 (v1.0.1)

- **Hold mode while blocked:** when input is blocked (placing, `_movementInteraction` false), `Want &= held`, so releasing the key stands you up; a key held while blocked sets `_suppressUntilRelease`, so it can't start a crouch until released (Ctrl = snap would otherwise crouch you when placing ends). Toggle mode is frozen while blocked.
- **Stale network state:** `Release()` publishes `flamboyanthopper.crouch=false` if `true` was sent (player gone while still in the room, mod disabled).
- **`ReadInput`** catches any `Exception`.
- **Leg IK vs vehicles:** `PlayerIKHandler.SetLeftLeg`/`SetRightLeg` have no callers in the dumps (searched `native-all`, `native`, `native-pun`). The only IK switch is `NetworkVehicle.SeatPlayer`/`UnseatPlayer` calling `SetWeight("Vehicle", w)` on the rig layers. `CrouchPose` skips a model whose leg constraint and Rig weights are both above 0.01. Unverified in-game; see the legIK log lines.
- **Photon property `flamboyanthopper.crouch`:** the only game code reacting to player property updates is `RoomPlayerPropsListener.OnPlayerPropertiesUpdate` → `RoomSettingsManager.OnPlayerPropertiesUpdateHook`, which only does `ContainsKey("png")` and `ContainsKey("sid")` on the changed table; any other key is ignored. `RackManager.OnPlayerPropertiesUpdate` is the empty shared method. So unmodded clients can't react to our key.
- Not changed: the `Vector3`/`Quaternion` approximate equality in the accumulation guard (harmless: the hips drop at the `T > 0.001` cutoff is about 3e-4 m, far above the 1e-5 tolerance), and `CrouchBehaviour` still being added while `Enabled=false` (it must exist so the master switch can re-enable the mod live; the cost is one bool check per frame).

## Pitfalls / follow-ups

- If the camera turns out to be parented under a bone or the vcam follows something else, the camera offset needs to move to that transform.
- Execution order relative to `CinemachineBrain.LateUpdate` is undefined, so the camera may lag one frame during the transition. That's invisible at the default speed.
- Possible follow-up: arms or hand IK while crouched and holding a box (seen by others).

## Game reference: player models

- The player prefab has `Network Objects/Skins/<skin>` children, one per selectable character (`Man_01_Rig_Test Variant`, `Man_01`, `Man_02`, `Woman_01`, `Woman_02`; verified in the `level2` asset with UnityPy). **Each skin has its own** humanoid `Animator` (e.g. `man_rig_test`, `man (1)`), `PlayerAnimation`, rigs and `PlayerIKHandler`; the chosen one is `ActiveSkin`; `SetSkinIndex` activates it and only hides the others' renderers (`PlayerSkin.Toggle(!m_IsLocal)`), so don't assume the others are inactive. The shown one is `PlayerInstance.SkinController.ActiveSkin` (`PlayerSkin.m_Animator`, `m_Renderers`).
- `GetComponentInChildren<PlayerAnimation>(true)` returns the inactive rig-test variant: don't use it.
- `PlayerAnimation` holds Animation Rigging rigs (`m_LookAtRig`; hand and leg IK in `PlayerIKHandler`). The locomotion blend tree is fed from `CharacterController.velocity`. There is no crouch state. Animators are humanoid (`human=True`, confirmed in-game).
