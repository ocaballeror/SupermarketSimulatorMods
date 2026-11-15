# PaintCanCapacity: notes for agents

Player-facing info is in [README.md](README.md). This file holds implementation details and status. The shared rules and game internals are in the root [CLAUDE.md](../CLAUDE.md). Full details are in the doc comment at the top of `src/PaintPatches.cs`.

## Status

v1.0.1: done, confirmed in-game (1.0.0). 1.0.1 is a retrospective-review pass: docs corrected, hot-toggle reset now includes inactive (pooled) cans; host behaviour unchanged.

## How it works

- Each paint can type is a `BucketSo` ScriptableObject. Its `bucketData.paintAmount` is how much paint a **new** can starts with: `DeliveryManager.Delivery` passes it to `BoxGenerator.SpawnBox`, which copies it into the box. From then on the amount travels with the can (box data, save file, Photon data).
- A `PaintBucket` holds `paintCount`. Painting a wall subtracts 1, and the can is empty at 0. `m_MaxPaintCount` (set on the prefab) is only used for the liquid level, `clamp01(paintCount / m_MaxPaintCount)`.
- **Patches:**
  - Postfix on `IDManager.BucketSo(id)` (every purchase looks the can type up through it): scales the type's starting amount.
  - Prefix on `PaintBucket.Initialize`: sets the can's `m_MaxPaintCount` to `max(vanilla max × multiplier, paint in the can)`.
    - The vanilla max always comes from the type's untouched prefab: `IDManager.BucketSo(PaintBucket.bucketID).BucketPrefab.m_MaxPaintCount`, the same lookup the game uses when boxing a can up.
    - The `max(...)` means a can never looks emptier than its contents.
- Can type amounts are remembered per ScriptableObject, which persists for the session. Changing the multiplier or disabling the mod restores both values live.
- Display maths: vanilla cans hold 8 paint with a prefab max of 10 (80%). At x2 a new can is 16/20 = 80%; a pre-mod can is 8/20 = 40% (`max(Scale(vanilla), paint)` only matters when paint exceeds the scaled max). Both drain 1/20 per wall.
- Guest-only install: the `Initialize` prefix still runs locally, so vanilla 8-paint cans show 40% (display only; paint amounts unchanged). Unmodded guest with a modded host: 16 paint over the prefab max 10 shows full until paint < 10.
- `ApplyToAll` / `RestoreAll` use `FindObjectsOfType(type, true)` so LeanPool-despawned (inactive) cans are rescaled too (`Initialize` never writes `m_MaxPaintCount`). `RefreshFill` is only called on active cans; failures log under VerboseLogging. Scene objects only, so no prefab assets are touched.
