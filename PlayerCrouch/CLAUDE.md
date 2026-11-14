# PlayerCrouch: notes for agents

Player-facing info is in [README.md](README.md). This file holds implementation details and status. The shared rules and game internals are in the root [CLAUDE.md](../CLAUDE.md). The full mechanism is documented at the top of `src/Crouch.cs` (local player) and `src/CrouchPose.cs` (model posing).

## Status

v1.0.0: written, **not yet tested in-game**. Open questions that the first test answers (all logged by `[Crouch] Local player set up ...` / `Model for player ...`):
- Does the FPS vcam actually follow `FirstPersonController.CinemachineCameraTarget`? (Logged `VCam follows:`.) If not, the camera won't drop and the camera offset must move whatever the vcam follows.
- Is the player Animator humanoid (`human=True`)? The pose needs `Animator.GetBoneTransform`.
- Is the local player's own body visible to them? The pose is applied to it anyway (shadow / body when looking down).
- Do held boxes look wrong for other players while someone crouches? The hand IK (Animation Rigging) is solved before our LateUpdate, so hands move with the lowered torso.

## How it works

- **Files:** `Crouch.cs` handles the local state, input, camera, collider and network property. `CrouchPose.cs` handles the procedural pose for all players. `PlayerPatches.cs` holds the Harmony patches: `FirstPersonController.Move` prefix/postfix for speed and a `Jump` prefix. `CrouchBehaviour.cs` is the Update/LateUpdate driver.
- **No game crouch exists** (no action, no animator state). Everything is added on top.
- **Input:** read straight from `Keyboard.current[key]` and the optional gamepad button. It's ignored while `FurniturePlacer.m_PlacingMode` or `PlayerObjectHolder.m_PlacingMode` is set (Ctrl = the game's `SnapPlace`), or while `_movementInteraction` is false (UI, computer, checkout, pause); the state is frozen in those cases. `PlayerInteraction.InSkateboard` forces standing.
- **Speed:** `Move` picks `MoveSpeed` or `SprintSpeed` as its target speed. Both are swapped for `MoveSpeed * SpeedMultiplier` (lerped by the crouch amount) for the duration of the call.
- **Jump:** `Jump` is invoked through `InputActions.onJump`. The prefix skips it while crouched; in toggle mode it also clears `Want`.
- **Camera:** `CinemachineCameraTarget.localPosition.y` gets an offset in LateUpdate. The only native writer of that position is `PushCameraEffect` (a DOTween `DOLocalMove`), so we re-base whenever the value differs from our last write.
- **Collider:** `CharacterController.height/center` are untouched by game code, so they're shrunk with the bottom kept fixed. Standing up waits for a `Physics.OverlapCapsule` (crouched top → standing top, radius × 0.9, the player layer's collision mask, triggers and own hierarchy ignored).
- **Network:** the Photon player custom property `oscar.crouch` (bool) on `PhotonNetwork.LocalPlayer`. It's sent on change and re-checked every 2 s, so it self-heals after a room change. Remote state is read from `PlayerInstance.m_PhotonView.Owner.CustomProperties`. Unmodded clients just store the key. The game itself uses player properties `sid` and `png`.
- **Pose:** in LateUpdate (after the Animator and rigs), the mod lowers the hips, leans the spine (and restores the head's world rotation), and runs two-bone IK on both legs back to the animated foot positions, bending the knees toward the model's forward. An accumulation guard skips frames where the Animator didn't overwrite the bones, by comparing hips localPosition and left thigh localRotation with what we wrote.

## Pitfalls / follow-ups

- If the camera turns out to be parented under a bone or the vcam follows something else, the camera offset needs to move to that transform.
- Execution order relative to `CinemachineBrain.LateUpdate` is undefined, so the camera may lag one frame during the transition. That's invisible at the default speed.
- Possible follow-up: arms or hand IK while crouched and holding a box (seen by others).
