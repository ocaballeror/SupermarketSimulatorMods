# RestockerPriority: notes for agents

Player-facing info is in [README.md](README.md). The full analysis is in the doc comment at the top of `src/Priority.cs`. Restocker AI basics: `RestockerDoubleCarry/CLAUDE.md` (Game reference: restockers).

## Status

v1.0.2 (review round 2). The postfix takes `bool __runOriginal` and does nothing when a prefix skipped the original search (RestockerDoubleCarry's `FetchFromFloor`, `ApplyJob` for box 2 and `RackCarried` all do), so the restockable-area test is only a second guard (the set-down spot may lie inside the area). The no-job/storage-job case is skipped when the restocker's "pick up boxes from the ground" is off (vanilla's rack pass can only be skipped after seeing a delivery-area box). Known costs/risks: with that setting on, an idle restocker with no job repeats the rack pass for products needing refill every ~3 s (about 7 searches/s with 20 restockers; state can't tell whether vanilla's pass already ran); and a rack job that fails every run (e.g. unreachable rack) now wins over storage on every search where such restockers previously did storage. Watch the `[RestockerPriority]` lines for the same product repeating.

v1.0.1 (review fixes). (1) Never touches a `StreetBoxTarget` whose box is outside `StorageStreet.IsWithinRestockableArea` (vanilla never picks one; that is RestockerDoubleCarry's `FetchFromFloor`/regroup result, which would otherwise be swapped for an emptier product on every search until its 180 s give-up). (2) Also acts when vanilla ended with a delivery-area storage/throw-away job or no job (vanilla skips its rack pass once any product had a street box, even one targeted by another restocker or with all displays reserved). Never when a box is in hand, and never on vehicle or rack results. (3) The delivery-area branch was dropped (vanilla's pass already rejected every earlier product); only rack boxes are tried. (4) A product whose emptiest slot is reserved by another restocker is no longer skipped: its next slot is tried.

v1.0.0 (2026-11-27, user finding "restockers should have a priority system ... check that this is not how it's working at the moment"). Result of the check: the game already ranks products by fill ratio; the only deviation is delivery-area boxes before rack boxes. The mod is one `SearchHandler.PerformSearch` postfix that corrects that. Untested in-game.

## How the vanilla choice works (verified in the dumps)

- `SearchHandler.GetDisplaySlotsRequiredRefill()` = `CalculateProductFullnessWeights(DisplayManager.m_DisplayedProducts)` flattened, keeping slots with `DisplaySlot.IsSlotRequiredFill` (no label, empty, or count <= 0.75 x `GridLayoutInStorage.productCount`).
- Per product: `sum(slot.ProductCount (virtual, vtable slot 8)) / (ProductSO.GridLayoutInStorage.productCount * slotCount)`, products with capacity <= 0 skipped, sorted ascending with `Single.CompareTo` (`SearchHandler+<>c.<CalculateProductFullnessWeights>b__28_0`). `m_DisplayedProducts` holds every labelled slot (`DisplayManager.AddDisplaySlot`), empty ones too. `DisplayManager.GetShelfFillStatus(pid)` returns `(items, capacity)` with the same definition. The sort is `List.Sort` (insertion sort for small lists, so stable: ties keep dictionary order).
- `PerformSearch` with no box in hand: (1) delivery-vehicle box (`HasBoxInVehicle`, not product-ordered); (2) if `PickUpBoxGround`: walk the ordered slots, first product with a delivery-area box (`CheckStreetForRequiredProduct`, not occupied/targeted) whose `SearchBestFitDisplaySlotsForRestock` is non-empty wins, and a local flag records that some product had a street box; (3) only if that flag is unset: walk again for rack boxes (`HasRackSlotForRequiredProduct`); (4) street box to rack (storage), (5) empty boxes. Slots reserved by another restocker (`DisplaySlot.IsOccupiedByOthers`) are skipped, so several restockers spread over products.
- `SearchBestFitDisplaySlotsForRestock` sorts the candidate slots by `ProductCount` ascending, skips slots occupied by others, adds slots with free capacity until the box is covered, then writes `m_OccupiedRestocker` (+0x70) on each returned slot (the fetch run's `Exit` clears it).
- Other mods: AutoStock (market cart), MoreEmployees (spawning), ChattyEmployees (patches `ClerkIdleState`) don't touch the search (`strings` check, 2026-11-27).

## What the mod does

Postfix on `PerformSearch` (host only, `[HarmonyBefore("flamboyanthopper.restockerdoublecarry")]`, priority High). Only when the clerk has no box in hand and vanilla ended with a delivery-area restock (product P, not at 0%), a delivery-area storage/throw-away job, or no job; a street box outside the restockable area is skipped. Walk `GetDisplaySlotsRequiredRefill()` up to P; for each earlier product Q (a slot not occupied by others) with `GetShelfFillStatus` ratio strictly below P's (any product when there was no restock job): rack box. First usable job (non-empty `SearchBestFit`) replaces `BoxTarget`/`DisplayTargets` (`RackTarget` null) and P's reservations are cleared. Not changed: vehicle boxes, rack picks (already emptiest-first), ties, storage jobs.

## Interplay with RestockerDoubleCarry

No dependency. DoubleCarry's `AfterSearch` starts a trip from whatever box 1 the search left, so this postfix must run first (HarmonyBefore, soft). DoubleCarry's `TryFindSecondJob` applies the same rule itself since 1.4.0 (per product in fill order: delivery area, then rack), so the pair respects the priority with or without this mod.

## In-game test steps

`VerboseLogging` on. Product A at about 60% on its shelves with a box in the delivery area, product B at 0% (labelled, empty) with a box only in a rack, one restocker idle. Expected log: `restocker #N: product B (0%) from a storage rack goes before product A (60%, delivery area)` and the restocker fetches B's rack box first. Without the mod it takes A's box first. Also check: two products equally full keep the vanilla order; a product above 75% on all slots is never restocked (its boxes go to storage); no `[RestockerPriority]` errors. Send those lines.
