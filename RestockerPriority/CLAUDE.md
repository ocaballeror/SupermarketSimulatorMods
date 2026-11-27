# RestockerPriority: notes for agents

Player-facing info is in [README.md](README.md). The full analysis is in the doc comment at the top of `src/Priority.cs`. Restocker AI basics: `RestockerDoubleCarry/CLAUDE.md` (Game reference: restockers).

## Status

v1.0.0 (2026-11-27, user finding "restockers should have a priority system ... check that this is not how it's working at the moment"). Result of the check: the game already ranks products by fill ratio; the only deviation is delivery-area boxes before rack boxes. The mod is one `SearchHandler.PerformSearch` postfix that corrects that. Untested in-game.

## How the vanilla choice works (verified in the dumps)

- `SearchHandler.GetDisplaySlotsRequiredRefill()` = `CalculateProductFullnessWeights(DisplayManager.m_DisplayedProducts)` flattened, keeping slots with `DisplaySlot.IsSlotRequiredFill` (no label, empty, or count <= 0.75 x `GridLayoutInStorage.productCount`).
- Per product: `sum(slot.ProductCount (virtual, vtable slot 8)) / (ProductSO.GridLayoutInStorage.productCount * slotCount)`, products with capacity <= 0 skipped, sorted ascending with `Single.CompareTo` (`SearchHandler+<>c.<CalculateProductFullnessWeights>b__28_0`). `m_DisplayedProducts` holds every labelled slot (`DisplayManager.AddDisplaySlot`), empty ones too. `DisplayManager.GetShelfFillStatus(pid)` returns `(items, capacity)` with the same definition. The sort is `List.Sort` (insertion sort for small lists, so stable: ties keep dictionary order).
- `PerformSearch` with no box in hand: (1) delivery-vehicle box (`HasBoxInVehicle`, not product-ordered); (2) if `PickUpBoxGround`: walk the ordered slots, first product with a delivery-area box (`CheckStreetForRequiredProduct`, not occupied/targeted) whose `SearchBestFitDisplaySlotsForRestock` is non-empty wins, and a local flag records that some product had a street box; (3) only if that flag is unset: walk again for rack boxes (`HasRackSlotForRequiredProduct`); (4) street box to rack (storage), (5) empty boxes. Slots reserved by another restocker (`DisplaySlot.IsOccupiedByOthers`) are skipped, so several restockers spread over products.
- `SearchBestFitDisplaySlotsForRestock` sorts the candidate slots by `ProductCount` ascending, skips slots occupied by others, adds slots with free capacity until the box is covered, then writes `m_OccupiedRestocker` (+0x70) on each returned slot (the fetch run's `Exit` clears it).
- Other mods: AutoStock (market cart), MoreEmployees (spawning), ChattyEmployees (patches `ClerkIdleState`) don't touch the search (`strings` check, 2026-11-27).

## What the mod does

Postfix on `PerformSearch` (host only, `[HarmonyBefore("flamboyanthopper.restockerdoublecarry")]`, priority High). Only when the clerk has no box in hand, vanilla chose a `StreetBoxTarget` with display targets, and its product P is not at 0%. Walk `GetDisplaySlotsRequiredRefill()` up to P; for each earlier product Q (not occupied by others) with `GetShelfFillStatus` ratio strictly below P's: street box (same checks as vanilla), else rack box. First usable job (non-empty `SearchBestFit`) replaces `BoxTarget`/`DisplayTargets` (`RackTarget` null) and P's reservations are cleared. Not changed: vehicle boxes, rack picks (already emptiest-first), ties, storage jobs.

## Interplay with RestockerDoubleCarry

No dependency. DoubleCarry's `AfterSearch` starts a trip from whatever box 1 the search left, so this postfix must run first (HarmonyBefore, soft). DoubleCarry's `TryFindSecondJob` applies the same rule itself since 1.4.0 (per product in fill order: delivery area, then rack), so the pair respects the priority with or without this mod.

## In-game test steps

`VerboseLogging` on. Product A at about 60% on its shelves with a box in the delivery area, product B at 0% (labelled, empty) with a box only in a rack, one restocker idle. Expected log: `restocker #N: product B (0%) from a storage rack goes before product A (60%, delivery area)` and the restocker fetches B's rack box first. Without the mod it takes A's box first. Also check: two products equally full keep the vanilla order; a product above 75% on all slots is never restocked (its boxes go to storage); no `[RestockerPriority]` errors. Send those lines.
