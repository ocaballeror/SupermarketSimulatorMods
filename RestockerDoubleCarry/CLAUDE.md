# RestockerDoubleCarry: notes for agents

Player-facing info is in [README.md](README.md). This file holds implementation details and status. The shared rules and game internals (including how the restocker AI works) are in the root [CLAUDE.md](../CLAUDE.md). The full analysis is in the doc comment at the top of `src/DoubleCarry.cs`.

## Status

v1.1.2 (2026-11-15, after the second in-game test): the user saw the top box stay floating when the restocker put the bottom one down. Cause: a stacking tween left running on the remaining box (on guests nothing killed the lift that `CarryBox(stash)` starts on the other box before the drop), and with empty hands nothing lowered a still-held box. `ArrangeStack` now always kills and restarts the current box's move to the hands, and on `CarryBox(null)` it lowers every other box still in the holder (the leaving box is identified by a `CarryBox_Internal` prefix that records the previous `m_CurrentBox`). Runs on host and modded guests. Not yet reviewed or tested in-game.

v1.1.1: **rework after the first in-game test** (2026-11-15), review round 1 of the rework applied in 1.1.1 (see below). The test showed restockers fetching two boxes correctly, but then restocking from both while holding both. The user wants the #10 rule: never unpack while holding two. Now the clerk puts the top box on the floor at the first display, unpacks the other, trashes it (empty) or racks it (leftovers) as vanilla does, then comes back for the floor box. Not yet reviewed or tested in-game. `VerboseLogging` defaults to on until confirmed.

Rework review round 1 (1.1.1): `plan.Floor` is kept until the box is back in the clerk's hands (a failed fetch run retries on the next empty-handed search; the 180 s give-up timer starts at the first fetch attempt, `FloorRetryStart`); `FetchFromFloor` uses `GetDisplaySlotsRequiredRefillWithoutCondition` like vanilla's carried-box branch and preserves `m_ExcludedProductId` across `Reset()`; `MoveAside` checks the spot with `Physics.CheckBox` (away from the display, then left, then right, else leave it where it fell), sets `rb.position/rotation` too (the dropped body interpolates), and re-sends the pose after 1.5 s (`DOVirtual.DelayedCall` → `BroadcastCurrentTransform`), since clerk pickups turn the box's transform sync off on all clients (`DisableNetworkTransformSync(false)` in `PickupGround`/`PickupRack`) and nothing re-enables it, so each client simulates the falling box on its own. Not changed: `PickUpBoxGround` doesn't gate fetching the clerk's own floor box (documented in README); `ReleaseAll` still drops box 1 when the mod is turned off mid-walk to box 2 (rare, documented; keeping it would need retargeting `TakeBox`).

Earlier review rounds (1.0.1/1.0.2): probe reservations restored (`TryFindSecondJob(reserve: false)`), `Plan.ReservedSecond` released when a trip dies, `HandOver` does `DOKill` first, `RestoreOwners` null-safe, `ReleaseAll` skips `ReleaseReserved` while `IsFetching`.

Open questions to confirm in-game:
- Does the stacked box sit correctly on top (uses `Box.LocalBounds`, i.e. the main `BoxCollider`)? (Confirmed fetching works; stack look not reported.)
- Where does the set-down box land (`MoveAside`: ≥0.6 m from the clerk, away from the display or to the side, bottom 5 cm above the floor)? Does it end up in the way, inside another shelf, or on top of something?
- Does the clerk come back for it (log `going back for the box it put down`)? It's picked up through a `StreetBoxTarget` although it isn't in the delivery area; `TakeBox` is assumed not to care where a street box is.
- Does an unmodded guest see the set-down box in the right spot (the host sends the pose with `BroadcastCurrentTransform` right after the drop RPC; only when the host owns the box's PhotonView)?

## In-game test steps

Setup: a restocker at level 10+ (or lower `MinimumLevel`), two products needing restock with boxes available (storage racks and/or the outside delivery area), `VerboseLogging` on. Report the `[DoubleCarry]` lines from `BepInEx/LogOutput.log` (a normal trip logs: two-box trip, has box 1, has both boxes, put box ... on the floor, going back for the box it put down, trip finished).

1. Watch a whole trip: box 1, box 2, walk to the first shelf, box 2 put on the floor beside the restocker, product 1 restocked from box 1 only, box 1 trashed (empty) or racked (leftovers), restocker walks back to the floor box, restocks product 2, leftovers to a rack. Check the stack looks right on the way (box 2 on top of box 1), and that no product goes onto a shelf while two boxes are in hand.
1b. Fill product 2's shelves by hand while box 2 is on the floor: the restocker should take box 2 straight to a rack (`... is full on the shelves, taking it to storage`). Pick box 2 up yourself instead: `the box it put down is gone`.
2. Carry animation: expected cosmetic quirk: the carry pose may turn off while a box is only stashed (e.g. after `GoToTrash` clears the hands), until the next search. Report whether the animation looks wrong while two boxes are carried.
3. Guest (unmodded): both products appear on the guest's shelves, leftovers end up in the guest's racks, no box stays floating. A guest with the mod should see the stack.
4. Second box from the delivery area and from a rack; box 1 from a vehicle.
5. Interrupts: fire or deactivate a restocker mid-trip (both boxes put down, no `[DoubleCarry]` errors); switch `Enabled` off mid-trip; another restocker taking box 2 first.
6. Reservations: after a trip (and after an interrupted one), other restockers still refill both products' displays (no display stays "taken" by the first clerk). Log lines `probe reservation of N display slot(s) undone` and `released N reserved display slot(s)` are expected occasionally.
7. `new IBoxTarget(ptr)` wrapping of `StreetBoxTarget` and `RackSlotBoxTarget` is untested in-game; any exception from the search postfix or prefix (`[DoubleCarry] search ... failed`) means it failed. Send those lines.

## How it works

- Restockers are `SupermarketSimulator.Clerk.Clerk` objects running a state machine that only ticks on the host/offline. `ClerkIdleState.OnTick` calls `SearchHandler.PerformSearch` (every 3 s, and on the first tick after entering Idle). The search results (`BoxTarget`, `DisplayTargets`, `RackTarget`) pick the next state.
- The mod never replaces a game method. It steers the vanilla states through the search results, using a per-clerk `Plan` with phases:
  - `None` → `FetchFirst`: in a `PerformSearch` postfix, the vanilla search found a restock job for a non-carried box, the clerk is at least `MinimumLevel` (`EmployeeLevelManager.TryGet(LevelKind, LevelEmployeeId).Level`), and another product has a job (`TryFindSecondJob`, a copy of the vanilla street-then-rack search). The next `ClerkRestockingState` run is "fetch only".
  - "Fetch only" = in a `ClerkRestockingState.Enter` prefix, remove the tasks after `TakeBox` from `m_Sequence.m_Tasks` (normally `[MoveTo, TakeBox, RestockAllDisplays, GoToTrash]`). They're restored in the `Exit` postfix, and the full list is always restored first in `Enter`.
  - `FetchFirst` → `FetchSecond` (`PerformSearch` prefix, returns false): still holding box 1, so set `BoxTarget`/`DisplayTargets` to job 2 (product ≠ box 1's), fetch only. `TakeBox` → `Clerk.CarryBox(box2)` makes box 2 current. Box 1 stays parented to `m_BoxHolder`, and that is the "stash".
  - `FetchSecond` → `DeliverFirst`: `CarryBox(box1)` and let the vanilla search restock product 1. Box 2 is the stash, stacked on top.
  - Set down: `RestockDisplay.OnBegin` prefix (the clerk reached a display and is about to open its box; unique RVA). If a plan box is stashed: `DropStash` (CarryBox(stash) → `DropBoxToGround` → CarryBox(box1)), then `MoveAside` puts it next to the clerk (away from the display, else left/right, first free spot by `Physics.CheckBox`) and sends the pose (again after 1.5 s) (`NetworkBox.m_PhotonTransformView.BroadcastCurrentTransform`, only if the host owns it). It becomes `Plan.Floor`.
  - `DeliverFirst`/`Unwind` searches with a box in hand: pure vanilla (more displays, `GoToTrash` if empty, `RackingState` if leftovers). If no display was ever reached the stash is still in hand; racking with both is allowed (not unpacking), and the stash is handed over once the hands are empty.
  - First search with empty hands and `Plan.Floor` still lying there (`IsOnFloor`: active, unparented, not occupied by another): `FetchFromFloor` fills the results itself (`Reset`, `BoxTarget = StreetBoxTarget(box)`, displays via `SearchBestFitDisplaySlotsForRestock(box, GetDisplaySlotsRequiredRefill(pid))`, else `RackTarget` via `IsMergeAvailableWithBox` / `HasSpaceInRackSlotForProduct`, as the vanilla street-to-rack branch; `DisplayTargets` must stay a non-null empty list, since the Idle → Restocking condition reads `.Count`). Nowhere to go, or the fetch run failed: return to vanilla and retry on the next empty-handed search, up to 180 s from the first attempt (`FloorRetrySeconds`). `plan.Floor` is cleared only once the box is in the clerk's hands again, gone, or given up on. While a plan exists `AfterSearch` starts no new trip.
  - `Unwind`: hands empty and nothing stashed or on the floor → plan ends.
- Why the floor box waits for its clerk: `DropBoxToGround_Internal` closes the box, `Box.DropBox` (DOKill, unparent, physics on), `SetOccupy(false)`, `InventoryManager.UnOccupyProduct`, layer Interactable, `StorageStreet.SubscribeBox`. But every street search (`CheckStreetForRequiredProduct` and the street-to-rack branch) also requires `StorageStreet.IsWithinRestockableArea`, so no restocker search picks up a box inside the store.
- Stash = a plan box (`First`/`Second`) that is alive, active, parented to `BoxHolder` and not `m_CurrentBox` (`IsStashed`).
- `Clerk.DropBoxToGround` postfix: also drop the stash (`CarryBox(stash)` + `DropBoxToGround()`), except during `ClerkIdleState.OnTick` (the idle "nowhere to put it" timer drops only the box in hand; the stash is handed over at the next search). This covers `EmployeeManager.DespawnRestocker` and `SetRestockerManagementData` (deactivation).
- Deactivated restocker (`ManagementData.IsActive` false at the next search): drop the stash and end the plan. `SetRestockerManagementData` only drops the box in hand, and only if it is carrying one.
- Errors in the search prefix call `AbortPlan`: hand the stash back if the hands are empty, else drop it. A plan box is never left in the hands.
- Disabling the mod (`ReleaseAll`): hand the stash over if the hands are empty, else drop it. A clerk in `FetchSecond` that is still walking to box 2 with box 1 in hand drops box 1, because the restored full sequence would otherwise take box 2 and strand box 1. Shortened sequences are restored.
- Shortened sequences are tracked only while shortened (`Stripped`: state → original task list), so nothing keeps fired clerks alive.
- Visuals: a `Clerk.CarryBox_Internal` prefix records the previous `m_CurrentBox`; the postfix (runs on host and guests) re-tweens the current box to the hands (kills any running stack tween first) and stacks every other active `Box` child of `BoxHolder` above it. With empty hands it lowers the boxes still held, skipping the previous current box: `GoToTrash` calls `CarryBox(null)` while the trashed box is still parented there; `RackBox.PlaceBox` re-parents into the rack (`RackSlot.AddBox`) before `CarryBox(null)`. Not touched when the previous box is unknown.

## Multiplayer

- Host-only logic. Guests get the game's own RPCs (`ClerkCarryBox_RPC`, `ClerkPickupRack_RPC`, place product, place box, destroy), so they stay in sync.
- `CarryBox_Internal` on a guest never un-parents the previous box either. Without the mod, both boxes sit at the hands' origin, one inside the other. With the mod, the postfix stacks them.
- Display slots are reserved (`DisplaySlot+0x70`) only for the duration of one restocking run, as in vanilla. Both `SearchBestFitDisplaySlotsForRestock` overloads write the field on every returned slot and only `ClerkRestockingState.Exit` clears it (unconditionally, for its dependency list), so the trip-start probe undoes its own reservations. Between legs of the trip another restocker may take the same product's displays. The result is a wasted box trip, not a desync.
- Not handled: a guest joining mid-trip gets the stashed box at its synced world position. It isn't re-parented, because the sync only knows the current box (`NetworkClerk.OnSyncRequested`). It could even be picked up there. The next host `CarryBox` RPC for that box fixes it. If it was taken, `IsStashed` turns false and the trip goes on without it.

## Pitfalls / follow-ups

- `SetDependencies` is inlined into `Clerk.OnBeforeTransition`; don't patch it.
- `RackSlotBoxTarget`/`StreetBoxTarget` interop classes don't derive from `IBoxTarget`. Wrap them: `new IBoxTarget(obj.Pointer)`.
- `TryFindSecondJob` differs slightly from vanilla: it still tries racks after street boxes were found but rejected, and it skips product ids `< 0` (vanilla: `0`).
- Vehicle boxes (`VehicleBoxTarget`) can be box 1 (vanilla picks it) but are never chosen as box 2.
