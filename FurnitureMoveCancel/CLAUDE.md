# FurnitureMoveCancel: notes for agents

Player-facing info is in [README.md](README.md). This file holds implementation details and status. The shared rules and game internals are in the root [CLAUDE.md](../CLAUDE.md). Full details are in the doc comment at the top of `src/MoveCancel.cs`.

## Status

v1.0.0: works in-game. Changed to place immediately (no hold) and send the exact position first; awaiting re-test in multiplayer.

## How it works

- Every movable piece (furniture, shelves, racks, checkouts, vending machines, ...) is moved by the local player's `FurniturePlacer`:
  - The interaction sets `m_CurrentPlacingMode` and calls `StartPlacingMode()`.
  - A prefix on that call records the piece's position and rotation before the first raycast moves it.
- Pressing **Back** (polled from the player's `PlayerInput` action, so rebinding works):
  1. Moves `FurniturePlacingMode.Furniture` back to the recorded spot.
  2. Runs the same checks the game uses, in the same way:
     - `PlacementBlockedArea.IsBlocked`, but only for items aimed through walls, as in `FurniturePlacer.EnforceBlockedAreas`;
     - `OverlapBoxes()`;
     - a surface check: short raycasts from the pivot (down, up, back, forward) must hit a collider whose tag is in `FurniturePlacer.m_CurrentPlacingTag`. The game normally sets `PlacedOnCorrectSurface` from its per-frame raycast; this replaces that while frozen, and catches cases where the surface was itself moved or sold;
     - `FurniturePlacer.CheckPlaceFurniture()`.
  3. If the spot is valid:
     - Online, it calls `PhotonTransformViewOptimized.BroadcastCurrentTransform()` on the piece's view. This is the game's reliable `SyncTransform` RPC: receivers call `SetPositionAndRotation` and reset their interpolation target. It only sends when this client owns the view, which the mover does while carrying. Reliable RPCs arrive in order, so this one is processed before the place RPC.
     - Then the mod invokes `PlayerInteraction.onUse(true)` in the same frame, as if the player clicked, so each furniture type is placed and synced by its own vanilla code.
     - If the piece has no such view, a prefix on `FurniturePlacer.Update` holds it at the origin for at least 0.3 s first. `ReturnDelaySeconds` adds an optional hold in every case.
     - If the interaction refuses (its short cooldown right after pickup), the player can press again.
  4. If it's not valid, the piece goes back to the crosshair.
- Furniture unpacked from a box (ice cream stands included) is skipped. Unboxing goes through `FurnitureBoxInteraction.CheckFurnitureType`, which starts placement synchronously. A pickup in the same frame as that call is treated as from a box.
- The hint is added with `InteractionHintsManager.AddHint(InputActionID.CANCEL, "Back", "_cancel", player)`, and re-added whenever the game clears the hint list. When removing it, the mod checks that the hint slot still holds its own item, so it never removes another interaction's hint.
