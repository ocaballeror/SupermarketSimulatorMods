# GridModeDefault: notes for agents

Player-facing info is in [README.md](README.md). This file holds implementation details and status. The shared rules and game internals are in the root [CLAUDE.md](../CLAUDE.md). Full details are in the doc comment at the top of `src/GridDefaultPatch.cs`.

## Status

v1.0.3: settings reduced (user's request). Only `Enabled` and `VerboseLogging` (default false) remain. `RememberToggle` (true) is now a `const` in `Plugin.cs`; the `false` path in `GridDefaultPatch` is kept but unreachable unless the constant is changed.

v1.0.2: **confirmed working in-game by the user (2026-11-15).** 1.0.2 only switches `VerboseLogging` to default off (existing configs keep their value).

## How it works

- "Grid mode" is `GridManager` (a `NoktaSingleton`): `m_GridEnabled` (+0xB0) makes `ModifyTargetPosition` quantise the held piece, and draws the grid. The toggle is the `GridToggle` input action (X / gamepad North), not Ctrl. Ctrl is `SnapPlace`, a separate snap-to-furniture feature (`m_SnapPlace`, `TrySnapPlacing`).
- `OnGridTogglePressed` flips `m_GridEnabled` and copies it into `m_GridEnabledPreference` (+0xB1). Nothing initialises or saves the preference, so it's false each session.
- `OnFurnitureStartPlacingMode` (subscribed to the local player's `FurniturePlacer.OnFurnitureStartPlacingMode`, invoked by `StartPlacingMode` for every furniture, boxed or moved) enables and draws the grid when the preference is true and the piece has matching surface areas. `OnFurniturePlacedOrBoxed` turns the grid off again but keeps the preference.
- The mod: prefix on `GridManager.OnFurnitureStartPlacingMode` sets `m_GridEnabledPreference = true`. Once per GridManager instance (tracked by pointer; a postfix on `Awake` resets it) when `Plugin.RememberToggle` is true (a constant since 1.0.3), so the player's X toggle sticks; on every placement when false. Disabling the mod restores the original preference.
- No transpiler, no inlining concern (only reached through a delegate).

## Multiplayer

Local only: GridManager only wires the local player's placer and input; nothing networked is touched. Furniture placement RPCs and transform sync are unchanged, so an unmodded host or guest sees normal placements.

## Pitfalls and open questions

- The first `PlacingRaycast` in `StartPlacingMode` runs before the delegate, so the first frame is not quantised; the next frame is. Not expected to be visible.
- Grid areas come from `GridManager.BuildAllAreas`: manual `GridAreaAnchor`s plus auto-discovery of objects tagged `Floor`, `Storage Floor` and `Ceiling`. Which furniture prefab uses which placing tag lives in prefab assets, not in the dumps, so which types get no grid can only be found in-game (test step 6). Disabling the mod live restores only the preference; a piece already held keeps its grid until placed.
- Furniture without matching surface areas (log line "No matching active areas" from the game) stays in free placement; that's the game's rule, not the mod's.
- Grid size (`m_CurrentCellSizeIndex`) is untouched.
- Not confirmed in-game: that the delegate target patch is hit (look for the `[GridMode]` line).

## In-game test

1. Install, start the game, load a store. Pick up a shelf (hold the interact key on it) and also unpack a new furniture box.
2. Expected: the grid shows immediately and the piece snaps to cells, without pressing X. Try a fridge, a rack, a checkout, a vending machine.
3. Press X: grid turns off (free placement); place the piece, pick up another: it stays off (`RememberToggle` is the constant true).
4. Press Ctrl while placing: still snaps to neighbouring furniture as before.
5. Turn `Enabled` off in the config manager: next pickup behaves as vanilla.
6. Send back (turn on `VerboseLogging` in the cfg first; it's off by default): `LogOutput.log` lines containing `[GridMode]` (and any `Failed to apply` / `GridManager` warnings), and which furniture types did not show the grid.
