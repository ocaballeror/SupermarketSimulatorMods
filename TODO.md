# Bug Reports

## Bug #1: Floating and Duplicated Boxes When Rearranging Racks

**Status:** DONE

When rearranging racks (not shelves), some of the boxes already in the rack remain floating at their original location instead of disappearing and reappearing with the rack at its new location.

In some cases, the boxes end up being duplicated: they remain floating at their original location but also move with the rack.

Some floating boxes can be picked up and moved, while others remain as ghost items. These ghost items can be passed through but cannot be interacted with, and they remain until the game is reloaded.

This issue mostly affects the other computer (the guest, not the host) during online multiplayer.

## Bug #2: Ghost Item Remaining in Player's Hands

**Status:** REVIEWED — Awaiting in-game test and logs  
**Last reviewed:** 2026-11-15  
**File:** `SupermarketFixes/src/Patches/GhostHeldItemFix.cs`

Sometimes, a player holds an item and then puts it down on the floor, but a ghost image of the item remains in their hands.

The player can freely interact with everything else, meaning the object is no longer considered held by the game. However, it is still rendered in their hands until the game is reloaded.

This does not interfere with gameplay, but it blocks the player's view and looks strange and confusing.

## Bug #3: Player Gets Stuck When Buying Boxes from Wholesale Stores

**Status:** REVIEWED — Awaiting in-game test and logs  
**Last reviewed:** 2026-11-15  
**File:** `SupermarketFixes/src/Patches/StuckBoxFix.cs`

When buying boxes from wholesale stores, sometimes the box remains stationary at the location where it was purchased.

The player can move around, but they are not visually holding the box. However, their controls are still intercepted by it.

As a result:
- The player does not appear to be holding anything.
- Left-clicking opens and closes the stationary box.
- Dropping or throwing the box does nothing.
- The player cannot interact with anything else.

The player is effectively stuck and must reload the game to resolve the issue.

This seems to happen especially often with ice cream cones purchased from the general goods store.

## Bug #4: Random Multiplayer Crashes for the Guest Player

**Status:** OPEN

The game randomly crashes for the other player (the guest, not the host) during multiplayer sessions.

There is currently no additional information available, as no specific cause or reproducible sequence has been identified.

**Question:** What can I do to record logs or other diagnostic information that would help debug this issue?

## Bug #5: Unable to Put Products on Shelves Without an Existing Label

**Status:** OPEN

Sometimes, players cannot put items from a crate onto a new shelf that does not yet have a label for that product.

This is particularly noticeable when a different product was previously stored on the shelf and its label was subsequently cleared.

The shelf should allow the new product to be placed immediately. However, fairly often, this does not work.

The current workaround is:
1. Unselect the crate.
2. Open the crate again.
3. The game then allows the player to place the product on the shelf.

## Bug #6: Boxes Sometimes Remain Floating When Removing Boxes from a Stack

**Status:** OPEN

When there is a pile of boxes, such as after placing a large online order, sometimes removing the box directly underneath another box does not cause the upper box to fall.

Instead, the upper box remains floating in the air until someone picks it up.

Removing another box from underneath the floating box does cause it to fall.

This does not seem to be related to Bug #1.

## Bug #7: Cleaning Lady Is Visually Misaligned When Cleaning Stains

**Status:** OPEN

The cleaning lady successfully cleans stains on the floor, but she is not visually aligned with the stain.

It looks as though she is cleaning the wrong section of the floor, even though the cleaning action succeeds and the stain is removed.

## Bug #8: Cleaning Lady Ignores Collisions with Shelves and Walls

**Status:** OPEN

Possibly related to Bug #7.

The cleaning lady ignores collisions with shelves and walls and often inserts herself into walls when trying to clean a stain on the floor.

Ideally, she should find an accessible position from which to clean the stain.

Overlapping with other characters is acceptable, since the game mostly ignores collisions between characters.

## Bug #9: Delivered Orders Remain in the Other Player's Pending Orders

**Status:** OPEN

In multiplayer, when a player gives an online order bag to the courier, the order disappears from the active order count on that player's screen, but not on the other player's screen.

More generally, orders that have already been picked up by the courier or are currently being delivered should be removed from the list of pending orders.

Players should no longer see these orders in their pending order lists.

Currently, orders are only removed when they have been delivered to the customer.

---

# Improvements and Feature Requests

## Improvement #1: Make Global Pricing Settings Persist Across Days

**Status:** DONE  
**Completed:** 2026-11-15

Make prices respect the global settings configured through the pricing tab on the computer.

For example, I normally set prices to 5% above market value. However, at the beginning of the next day, some product prices change and no longer follow the previously configured rule.

When the player sets a global pricing or discount rule, that rule should continue to apply to products whose market prices increase or decrease at the beginning of a new day.

## Improvement #2: Make Paint Cans Last Twice as Long

**Status:** DONE  
**Completed:** 2026-11-14

Make paint cans last twice as long as they currently do.

## Improvement #3: Add Player Crouching

**Status:** DONE  
**Completed:** 2026-11-15

Allow players to crouch by pressing `Ctrl`.

Characters should also play an appropriate crouching animation when this happens.

## Improvement #4: Make Self-Checkout Machines Faster

**Status:** DONE  
**Completed:** 2026-11-14

Make customers using self-checkout machines 2x faster.

The speed multiplier should be configurable as a floating-point value in the mod settings. For example, `1.5x` should be a valid setting.

Allow values up to `10x`.

## Improvement #5: Allow Level 10 Restockers to Carry Two Boxes

**Status:** IN PROGRESS  
**Project:** `RestockerDoubleCarry/`  
**Current task:** Rework logic so the top box is placed down before unpacking.

Make level 10 restockers capable of carrying two product boxes at a time.

### Expected behavior

1. The restocker goes to storage (or to the outside delivery area where new boxes arrive).
2. The restocker picks up two boxes.
3. The restocker goes to restock the first product.
4. The restocker then handles the second product.
5. The restocker returns to storage with any remaining boxes, if applicable.

### Rework requested after in-game testing (2026-11-15)

The initial implementation allowed restockers to carry two boxes but also allowed them to unpack both boxes simultaneously.

The logic should instead follow the same restrictions as Improvement #10: restockers cannot unpack boxes while carrying two.

They must first place one box on the floor or on a rack before unpacking the other.

### Required workflow

When the restocker arrives at the shelves:

1. Place the top box on the floor.
2. Unpack the other box.
3. Branch depending on whether the unpacked box is empty:

   **A. If the unpacked box is empty:**
   - Take it to the trash can and dispose of it, as usual.
   - Return to the previously placed box.
   - Pick up the remaining box.
   - Unpack it.

   **B. If the unpacked box is not empty:**
   - Return to storage with the remaining box.
   - Continue the normal restocking workflow.

The implementation should be efficient and avoid unnecessary trips.

## Improvement #6: Allow Furniture to Be Returned to Its Original Position

**Status:** DONE  
**Completed:** 2026-11-15

When rearranging furniture (shelves, fridges, freezers, racks, etc.), allow players to press `Esc` to return the furniture to its original position before it was picked up.

This should only work if the original position is still available.

If the item cannot be placed back in its original position, pressing `Esc` should do nothing.

Also, add this functionality to the list of help keys displayed on the side of the screen.

## Improvement #7: Add Additional Computers as Placeable Furniture

**Status:** IN PROGRESS  
**Project:** `ExtraComputers/`  
**Current task:** Fix prefab build failure and icon following in-game testing.

Create a new furniture type: the computer.

### Purchasing and placement

- Each computer should cost `$500`.
- Players should be able to purchase as many computers as they want.
- Purchased computers should arrive in a box, with a small icon displayed outside, just like other furniture pieces.
- Players should be able to place computers anywhere they want.
- Each computer should function like any other existing computer.

### Independent sessions

Each computer must have its own independent session.

This means:
- The screen of one computer must not be mirrored on another.
- Players should be able to make changes independently on each computer.
- Each computer should maintain its own session and state.

### Multiplayer synchronization

If keeping computer pages synchronized when another player makes changes would require significant work, real-time synchronization is not mandatory.

For now, it is acceptable for two players viewing the same page on different computers to see different versions of that page.

One player may therefore have an outdated view while the other is making changes.

However, if implementing synchronization is straightforward, or even simpler than keeping sessions independent, real-time updates across computers are preferred.

## Improvement #8: Make Grid Mode the Default for Furniture Placement

**Status:** DONE  
**Completed:** 2026-11-15

Make grid mode the default placement mode when placing furniture of any kind.

## Improvement #9: Add Bulk Item Transfer Between Shelves and Containers

**Status:** REVIEWED — Awaiting in-game test and logs  
**Last reviewed:** 2026-11-15  
**Project:** `BulkShelfTransfer/`

Allow players to transfer multiple products at once using `Shift + Right Click`.

### Supported transfers

**From shelves to containers:**

Allow players to transfer all items from a shelf, fridge, or freezer into a crate or paper bag, instead of having to pick up products individually.

**From containers to shelves:**

Allow players to transfer all compatible items from a crate or paper bag into a shelf, fridge, or freezer at once.

### Item type restrictions

Keep in mind that:
- Crates and paper bags can contain more than one type of product.
- Shelves can only contain one type of product.

When transferring items into a shelf:
- Only transfer products matching the shelf's current product type.
- Follow the same compatibility rules as the base game.
- Respect the shelf's available capacity.

### Capacity restrictions

Both crates and paper bags have size limits.

All bulk transfers must respect these limits and must never exceed the available capacity of the destination container.

### Animation

Use the game's standard animation for products moving between shelves and containers.

However, make the animation significantly faster so that products move almost instantly.

The products should still follow the standard animation rather than simply teleporting in and out of the containers.

### Configuration

Make the key binding configurable in the mod settings.

## Improvement #10: Allow Players to Carry Two Boxes at Once

**Status:** REVIEWED — Awaiting in-game test and logs  
**Last reviewed:** 2026-11-15  
**Project:** `PlayerDoubleCarry/`

Allow players to pick up and carry two boxes at a time.

### Restrictions

When carrying two boxes:
- Players cannot open either box.
- Players can only carry the boxes and place them somewhere else.

As soon as the player drops, throws, or places one of the boxes, they should be able to open the remaining box and place its products on shelves normally, following the game's standard behavior.
