# FurnitureMoveCancel: notes for agents

Player-facing info is in [README.md](README.md). This file holds implementation details and status. The shared rules and game internals are in the root [CLAUDE.md](../CLAUDE.md). Full details are in the doc comment at the top of `src/MoveCancel.cs`.

## Status

v1.0.2: **confirmed working in-game by the user (2026-11-15).** 1.0.2 only switches `VerboseLogging` to default off (existing configs keep their value). The notes below predate that test.

Open questions and pitfalls:
- **Multiplayer path unconfirmed in-game:** `BroadcastCurrentTransform()` before the place RPC (host and guest as mover; unmodded guest watching).
- **The surface probe is a heuristic** (rays of +-0.5 m from the pivot along four local axes). It is calibrated at pickup (`_surfaceCalibrated`): if the probe finds no support while the piece still stands at its valid spot, the surface check is skipped for that piece and only the overlap check runs (it then can't notice a removed supporting piece). The `picked up ...` log line says which case applies. If a piece type shows "surface check off", the probe needs a better rule for it.
- **No on-screen feedback** when Escape does nothing (blocked spot, or the interaction's short place cooldown right after pickup, `CLIENT_PLACE_DELAY`). The README tells the player to press again. The interaction can also refuse for rules the mod can't see (for example `StorageLightInteraction.CheckFloorIsStorage`); the piece then stays at the origin until the next `PlacingRaycast` (a frame).
- The hint text is the game's `_cancel` ("Cancel"), though the key returns the piece to its old spot. No better existing hint key; the README explains it.
- `ReturnDelaySeconds` and the 0.3 s fallback hold (`MinOnlineDelay`, only when no `PhotonTransformViewOptimized` is found) re-run the origin check just before placing; with delay 0 the check is not repeated.
- `IceCreamInteraction.OnBack` (also bound to Back) is a no-op while the stand is being moved: it is gated on `IceCreamStand.m_IsInPlacement` (+0x98), set by `StartPlacement`/`StartPlacementNetwork` and cleared by `EndPlacement`. `StartPlacement` calls `FurniturePlacer.StartPlacingMode`, so stands are handled like other furniture. (Checked in the dumps; not yet seen in-game.)
- `Plugin.Unload` leaves the `MoveCancelBehaviour` component alive but inert (`Update` returns when the mod is inactive).
- `FurniturePlacer.m_CurrentPlacingTag` is written (inlined setter) by the interaction before it calls `StartPlacingMode`, so it is valid in the pickup prefix.

## In-game test steps

1. Pick up a shelf, a fridge, a rack and a checkout (offline first). Note the key hint list on the side: a "Back / Escape: Cancel" entry should show. Press Escape: the piece snaps back and is placed; the pause menu must not open.
2. Pick up a piece, then put a box or another piece into its original spot (from a second player, or by dropping something there from another angle) and press Escape: nothing happens and you keep holding it.
3. Pick up a category sign, remove or move the shelf under it (from a second player or earlier), then Escape: nothing should happen.
4. Pick up the ice cream stand, a wall/ceiling fixture (storage light, store light, security camera) and a vending machine; Escape on each.
5. Unbox a new furniture box: Escape must not return anything (no previous spot).
6. Multiplayer: do 1 as host with an unmodded guest watching, and as an unmodded host with a modded guest; the other side must see the piece at the original spot, with no ghost or offset.
7. Press Escape in the first moment after picking up: it may do nothing; pressing again should work.

Send back the `[MoveCancel]` lines from `BepInEx/LogOutput.log`: `picked up <piece> at ...; surface probe ...` (shows which pieces have the surface check off), `Escape pressed: returning ...` (how it was sent), `... not free (<reason>)`, `... put back at its original spot`, `the game did not accept placing ...`. For multiplayer also the other player's description of what they saw.

## How it works

- Every movable piece (furniture, shelves, racks, checkouts, vending machines, ...) is moved by the local player's `FurniturePlacer`:
  - The interaction sets `m_CurrentPlacingMode` and calls `StartPlacingMode()`.
  - A prefix on that call records the piece's position and rotation before the first raycast moves it.
- Pressing **Back** (polled from the player's `PlayerInput` action, so rebinding works):
  1. Moves `FurniturePlacingMode.Furniture` back to the recorded spot.
  2. Runs the same checks the game uses, in the same way:
     - `PlacementBlockedArea.IsBlocked`, but only for items aimed through walls, as in `FurniturePlacer.EnforceBlockedAreas`;
     - `OverlapBoxes()`;
     - a surface check, only if the same probe found support at pickup (calibration): short raycasts from the pivot (down, up, back, forward) must hit a collider whose tag is in `FurniturePlacer.m_CurrentPlacingTag`. The game normally sets `PlacedOnCorrectSurface` from its per-frame raycast; this replaces that while frozen, and catches cases where the surface was itself moved or sold;
     - `FurniturePlacer.CheckPlaceFurniture()`.
  3. If the spot is valid:
     - Online, it calls `PhotonTransformViewOptimized.BroadcastCurrentTransform()` on the piece's view. This is the game's reliable `SyncTransform` RPC: receivers call `SetPositionAndRotation` and reset their interpolation target. It only sends when this client owns the view, which the mover does while carrying. Reliable RPCs arrive in order, so this one is processed before the place RPC.
     - Then the mod invokes `PlayerInteraction.onUse(true)` in the same frame, as if the player clicked, so each furniture type is placed and synced by its own vanilla code.
     - If the piece has no such view, a prefix on `FurniturePlacer.Update` holds it at the origin for at least 0.3 s first. `ReturnDelaySeconds` adds an optional hold in every case.
     - If the interaction refuses (its short cooldown right after pickup, or a rule of its own), the player can press again.
  4. If it's not valid, the piece goes back to the crosshair.
- Furniture unpacked from a box (ice cream stands included) is skipped. Unboxing goes through `FurnitureBoxInteraction.CheckFurnitureType`, which starts placement synchronously. A pickup in the same frame as that call is treated as from a box.
- The hint is added with `InteractionHintsManager.AddHint(InputActionID.CANCEL, "Back", "_cancel", player)`, and re-added whenever the game clears the hint list. When removing it, the mod checks that the hint slot still holds its own item, so it never removes another interaction's hint.
