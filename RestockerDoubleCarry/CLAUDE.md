# RestockerDoubleCarry: notes for agents

Player-facing info is in [README.md](README.md). This file holds implementation details and status. The shared rules and game internals (including how the restocker AI works) are in the root [CLAUDE.md](../CLAUDE.md). The full analysis is in the doc comment at the top of `src/DoubleCarry.cs`.

## Status

v1.0.2: written and builds, **awaiting in-game test** (TODO Improvement #5). `VerboseLogging` defaults to on until confirmed.

Review round 1 applied in 1.0.1: the second-job probe no longer leaves display slots reserved (`TryFindSecondJob(reserve: false)` snapshots and restores `DisplaySlot.m_OccupiedRestocker`); reservations of the real box-2 job (`Plan.ReservedSecond`) are released in `EndPlan`/`ReleaseAll` if the trip dies before the fetch run's `Exit`; stash hand-overs go through `HandOver` (`DOKill` first, so stacking and carry tweens don't fight).

Round 2 (1.0.2): `RestoreOwners` now counts/rewrites only slots whose owner really changed (null-safe compare); `ReleaseAll` skips `ReleaseReserved` for a clerk whose fetch-only run is still active (`IsFetching`), since that run's `Exit` clears the reservation.

Open questions to confirm in-game:
- Does the stacked box sit correctly on top (uses `Box.LocalBounds`, i.e. the main `BoxCollider`)?
- Is the restocker's walk/carry animation fine with the second box?
- Does an unmodded guest stay in sync through a whole trip (both products appear on the guest's shelves, leftovers end up in the guest's racks)?

## In-game test steps

Setup: a restocker at level 10+ (or lower `MinimumLevel`), two products needing restock with boxes available (storage racks and/or the outside delivery area), `VerboseLogging` on. Report the `[DoubleCarry]` lines from `BepInEx/LogOutput.log` (a normal trip logs: two-box trip, has box 1, has both boxes, product done, taking the remaining box back, trip finished).

1. Watch a whole trip: box 1, box 2, restock product 1, restock product 2, leftovers back to a rack. Check the stack looks right (box 2 sits on top of box 1, not inside it or floating).
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
  - `FetchSecond` → `DeliverFirst`: `CarryBox(box1)` and let the vanilla search restock product 1 (and trash box 1 if empty).
  - `DeliverFirst` → `Unwind`: `CarryBox(stash)` (box 2), and vanilla restocks product 2.
  - `Unwind`: whenever the hands are empty and a plan box is still stashed, `CarryBox(stash)` and vanilla racks it. The plan ends when nothing is stashed.
- Stash = a plan box (`First`/`Second`) that is alive, active, parented to `BoxHolder` and not `m_CurrentBox` (`IsStashed`).
- `Clerk.DropBoxToGround` postfix: also drop the stash (`CarryBox(stash)` + `DropBoxToGround()`), except during `ClerkIdleState.OnTick` (the idle "nowhere to put it" timer drops only the box in hand; the stash is handed over at the next search). This covers `EmployeeManager.DespawnRestocker` and `SetRestockerManagementData` (deactivation).
- Deactivated restocker (`ManagementData.IsActive` false at the next search): drop the stash and end the plan. `SetRestockerManagementData` only drops the box in hand, and only if it is carrying one.
- Errors in the search prefix call `AbortPlan`: hand the stash back if the hands are empty, else drop it. A plan box is never left in the hands.
- Disabling the mod (`ReleaseAll`): hand the stash over if the hands are empty, else drop it. A clerk in `FetchSecond` that is still walking to box 2 with box 1 in hand drops box 1, because the restored full sequence would otherwise take box 2 and strand box 1. Shortened sequences are restored.
- Shortened sequences are tracked only while shortened (`Stripped`: state → original task list), so nothing keeps fired clerks alive.
- Visuals: a `Clerk.CarryBox_Internal` postfix (runs on host and guests) stacks every active non-current `Box` child of `BoxHolder` above the current box, using DOTween like the game does. It does nothing when the hands are empty: `GoToTrash` calls `CarryBox(null)` while the trashed box is still parented there.

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
