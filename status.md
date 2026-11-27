# Status

Tracking state of every bug and improvement. States and rules: `WORKFLOW.md`, section 1.

# Bug Reports

## Bug #1: Floating and Duplicated Boxes When Rearranging Racks

**Status:** reviewed  
**Code:** `SupermarketFixes/src/Patches/RackMoveFix.cs`  
**Review:** 2026-11-23, 1 round, hash `af25cea05bf4`  
**Waiting on:** In-game re-test of the 0.11.11 review follow-ups (the Swap record fix is untested).  

## Bug #2: Ghost Item Remaining in Player's Hands

**Status:** reviewed  
**Code:** `SupermarketFixes/src/Patches/GhostHeldItemFix.cs`  
**Review:** 2026-11-19, 2 rounds, hash `28b2783348e6`  
**Waiting on:** In-game test + logs, including the host's `[BoxDiag] DropBox_RPC` lines.  

## Bug #3: Player Gets Stuck When Buying Boxes from Wholesale Stores

**Status:** reviewed  
**Code:** `SupermarketFixes/src/Patches/StuckBoxFix.cs`  
**Review:** 2026-11-19, 2 rounds, hash `c60856611a18`  
**Waiting on:** In-game test + logs.  

## Bug #4: Random Multiplayer Crashes for the Guest Player

**Status:** open  
**Waiting on:** The guest's Windows crash folder and Event Viewer entry after the next crash (details in `SupermarketFixes/CLAUDE.md`, section Bug #4).  

## Bug #5: Unable to Put Products on Shelves Without an Existing Label

**Status:** reviewed  
**Code:** `SupermarketFixes/src/Patches/BoxPlaceFix.cs`, `SupermarketFixes/src/Patches/CratePlaceFix.cs`  
**Review:** 2026-11-19, 3 rounds, hash `f10bf778aa0e` (both files)  
**Waiting on:** In-game test (steps in `SupermarketFixes/CLAUDE.md`, "Test (Bug #5)", with VerboseLogging on); send the `[CratePlace]` / `[BoxPlace]` log lines.  

## Bug #6: Boxes Sometimes Remain Floating When Removing Boxes from a Stack

**Status:** reviewed  
**Code:** `SupermarketFixes/src/Patches/FloatingBoxFix.cs`  
**Review:** 2026-11-27, 2 rounds, hash `a63f3e2b9cb5`  
**Waiting on:** In-game test of 0.11.14 on host and guest (VerboseLogging on); send the `[FloatBox]` lines, with the time and what floated (`nearRack=` separates this from Bug #1).  

## Bug #7: Cleaning Lady Is Visually Misaligned When Cleaning Stains

**Status:** reviewed  
**Code:** `SupermarketFixes/src/Patches/JanitorCleanAlignFix.cs`  
**Review:** 2026-11-19, 3 rounds, hash `562ffcb529ce`  
**Waiting on:** In-game test of v0.11.9 on the host (steps in `SupermarketFixes/CLAUDE.md`, Bug #7 + #8); send the `[JanitorAlign]` lines.  

## Bug #8: Cleaning Lady Ignores Collisions with Shelves and Walls

**Status:** reviewed  
**Code:** `SupermarketFixes/src/Patches/JanitorCleanAlignFix.cs` (shared with Bug #7)  
**Review:** 2026-11-19, 3 rounds, reviewed with Bug #7, hash `562ffcb529ce`  
**Waiting on:** In-game test together with Bug #7 (stains next to shelves, walls, trash bins).  

## Bug #9: Delivered Orders Remain in the Other Player's Pending Orders

**Status:** reviewed  
**Code:** `SupermarketFixes/src/Patches/CourierOrdersFix.cs`  
**Review:** 2026-11-19, 2 rounds, hash `4705f219c088`  
**Waiting on:** In-game test of v0.11.10 (host and modded guest; steps in `SupermarketFixes/CLAUDE.md`, Bug #9); send the `[CourierOrders]` lines from both.  

## Bug #10: Order Bag Counts Reset While Picking Up Items for a Delivery

**Status:** implemented  
**Code:** `SupermarketFixes/src/Patches/OrderBagCountsFix.cs`  
**Waiting on:** In-game test of v0.11.13 (hold an open bag with products, then let another order expire or hand one to the courier; steps in `SupermarketFixes/CLAUDE.md`, Bug #10); send the `[OrderBagCounts]` lines.  

## Bug #11: Same Order Can Be Taken Twice and the Pending Notification Stays for Other Players

**Status:** implemented  
**Code:** `SupermarketFixes/src/Patches/LabeledOrderFix.cs`  
**Waiting on:** In-game test of v0.11.13 on host and a modded guest (take an order, label the bag, drop it; steps in `SupermarketFixes/CLAUDE.md`, Bug #11); send the `[LabeledOrders]` lines.  

# Improvements and Feature Requests

## Improvement #1: Make Global Pricing Settings Persist Across Days

**Status:** reviewed  
**Code:** `PriceMarkupKeeper/`  
**Review:** 2026-11-19, 3 rounds, hash `540ebcd2c600`  
**Waiting on:** In-game test of v1.1.3 (buy a license after applying a bulk markup; check the new products' prices; check prices after a day change). The mod's cfg file was renamed in 1.1.3, so settings start at defaults.  

## Improvement #2: Make Paint Cans Last Twice as Long

**Status:** reviewed  
**Code:** `PaintCanCapacity/`  
**Review:** 2026-11-23, 1 round, hash `1ad884bbf536`  
**Waiting on:** In-game re-test of 1.0.5 (typed multiplier field, per-object restore).  

## Improvement #3: Add Player Crouching

**Status:** reviewed  
**Code:** `PlayerCrouch/`  
**Review:** 2026-11-19, 3 rounds, hash `1cf972758683`  
**Waiting on:** In-game test of v1.0.7 on both machines (crouch pose and gaze while carrying a box visible on the other player's screen; steps in the mod's `CLAUDE.md`); send the `Model for player` and `Remote look-at target` log lines.  

## Improvement #4: Make Self-Checkout Machines Faster

**Status:** implemented  
**Code:** `SelfCheckoutSpeed/`  
**Waiting on:** In-game test of v1.2.1 (diagnostics only: with VerboseLogging on, send both machines' `[SelfCheckout]` lines when it happens, and say which PC shows it and which kind of checkout).  

## Improvement #5: Allow Level 10 Restockers to Carry Two Boxes

**Status:** implemented  
**Code:** `RestockerDoubleCarry/`  
**Waiting on:** In-game test of v1.4.0 (route planning for two boxes; send the `[DoubleCarry]` trip lines).  

## Improvement #6: Allow Furniture to Be Returned to Its Original Position

**Status:** reviewed  
**Code:** `FurnitureMoveCancel/`  
**Review:** 2026-11-23, 1 round, hash `6cf40f900376`  
**Waiting on:** In-game re-test of 1.0.5 (Esc hint now shows while moving a category sign).  

## Improvement #7: Add Additional Computers as Placeable Furniture

**Status:** reviewed  
**Code:** `ExtraComputers/`  
**Review:** 2026-11-19, 5 rounds, hash `b8e345bffb33`  
**Waiting on:** In-game test of v1.4.4 (steps in the mod's `CLAUDE.md`, "Test for v1.4.0"); send the `[ExtraComputers]` lines.  

## Improvement #8: Make Grid Mode the Default for Furniture Placement

**Status:** reviewed  
**Code:** `GridModeDefault/`  
**Review:** 2026-11-23, 1 round, hash `c4dcd1e5c4f3`  
**Waiting on:** Nothing; code unchanged by the review (docs only). Already confirmed in-game 2026-11-15.  

## Improvement #9: Add Bulk Item Transfer Between Shelves and Containers

**Status:** implemented  
**Code:** `BulkShelfTransfer/`  
**Waiting on:** In-game test of v0.2.4 (shift-click with an open bag: products the order needs, other products, and putting back; send the `[Bulk]` lines, especially `not started (...)`).  

## Improvement #10: Allow Players to Carry Two Boxes at Once

**Status:** reviewed  
**Code:** `PlayerDoubleCarry/`  
**Review:** 2026-11-27, 1 round, hash `2c1fb675e5c7`  
**Waiting on:** In-game test of v0.2.6 (hold left click on a wholesale product while holding a box; send the `[PlayerCarry]` lines).  

## Improvement #11: More clear tags

**Status:** reviewed  
**Code:** `ClearTags/`  
**Review:** 2026-11-23, 3 rounds, hash `03ad0b6b1be6`  
**Waiting on:** In-game test of v0.3.8 (hover on any part of a racked box; steps in the mod's `CLAUDE.md`); send the `[ClearTags]` lines.  

## Improvement #12: Auto courier load

**Status:** reviewed  
**Code:** `AutoCourierLoad/`  
**Review:** 2026-11-23, 3 rounds, hash `a499838099a9`  
**Waiting on:** In-game test of v0.2.5 (bag dropped next to the idle courier; steps in the mod's `CLAUDE.md`); send the `[AutoCourier]` lines.  

## Improvement #13: Highlight same products

**Status:** reviewed  
**Code:** `ProductHighlight/`  
**Review:** 2026-11-27, 1 round, hash `6c0f5a343bbf`  
**Waiting on:** In-game test of v0.1.7 (outlines only while the order bag is open; steps in the mod's `CLAUDE.md`); send the `[Highlight]` lines.  

## Improvement #14: Pack up products

**Status:** reviewed  
**Code:** `PackUpProducts/`  
**Review:** 2026-11-27, 2 rounds, hash `4e4d946a6d84`  
**Waiting on:** In-game test of v0.1.9 with VerboseLogging on (the cause of the failure is not known; the `[PackUp]` lines name what blocks the right click).  

## Improvement #15: Employee clothes

**Status:** implemented  
**Code:** `EmployeeClothes/`  
**Waiting on:** In-game test of v1.1.0 (the new Players type, with a second player present; the guest colour check; steps in the mod's `CLAUDE.md`); send the `[EmployeeClothes]` lines from both machines.  

## Improvement #16: Auto lights

**Status:** implemented  
**Code:** `AutoLights/`  
**Waiting on:** In-game test of v1.0.0 (lights come on at 19:00; steps in the mod's `CLAUDE.md`); send the `[AutoLights]` lines.  

## Improvement #17: Restocker priority

**Status:** implemented  
**Code:** `RestockerPriority/`  
**Waiting on:** In-game test of v1.0.0 (a 60% product with a box outside versus a 0% product with a box in a rack; steps in the mod's `CLAUDE.md`); send the `[RestockerPriority]` lines.  
