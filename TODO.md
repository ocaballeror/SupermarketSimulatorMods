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

**Status:** OPEN — Guest crash logs analysed 2026-11-15 (no cause in them); need the Windows crash report folder

The game randomly crashes for the other player (the guest, not the host) during multiplayer sessions.

There is currently no additional information available, as no specific cause or reproducible sequence has been identified.

**Question:** What can I do to record logs or other diagnostic information that would help debug this issue?

## Bug #5: Unable to Put Products on Shelves Without an Existing Label

**Status:** NEEDS REWORK — Still happening after the fix (in-game test 2026-11-15)

Sometimes, players cannot put items from a crate onto a new shelf that does not yet have a label for that product.

This is particularly noticeable when a different product was previously stored on the shelf and its label was subsequently cleared.

The shelf should allow the new product to be placed immediately. However, fairly often, this does not work.

The current workaround is:
1. Unselect the crate.
2. Open the crate again.
3. The game then allows the player to place the product on the shelf.

## Bug #6: Boxes Sometimes Remain Floating When Removing Boxes from a Stack

**Status:** OPEN — Still happening (in-game test 2026-11-15)

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

**Status:** REVIEWED 2026-11-15 — Rework v1.1.1 in `PriceMarkupKeeper/`, awaiting in-game test  
**First completed:** 2026-11-15

Make prices respect the global settings configured through the pricing tab on the computer.

For example, I normally set prices to 5% above market value. However, at the beginning of the next day, some product prices change and no longer follow the previously configured rule.

When the player sets a global pricing or discount rule, that rule should continue to apply to products whose market prices increase or decrease at the beginning of a new day.

### Rework requested after in-game testing (2026-11-15)

Auto pricing should also work for newly unlocked products. It should be applied immediately after their license is unlocked.

## Improvement #2: Make Paint Cans Last Twice as Long

**Status:** DONE  
**Completed:** 2026-11-14

Make paint cans last twice as long as they currently do.

## Improvement #3: Add Player Crouching

**Status:** IN PROGRESS — Fix v1.0.3 in `PlayerCrouch/src/CrouchPose.cs` (`FindAnimator`), under review  
**First completed:** 2026-11-15

Allow players to crouch by pressing `Ctrl`.

Characters should also play an appropriate crouching animation when this happens.

### Rework requested after in-game testing (2026-11-15)

The mod works, but there is no animation for the player crouching, so it does not show on the other player's screen, even with the mod installed on both. Make the character actually visually crouch on screen.

## Improvement #4: Make Self-Checkout Machines Faster

**Status:** IN PROGRESS — Rework v1.1.0 in `SelfCheckoutSpeed/`, under review  
**First completed:** 2026-11-14

Make customers using self-checkout machines 2x faster.

The speed multiplier should be configurable as a floating-point value in the mod settings. For example, `1.5x` should be a valid setting.

Allow values up to `10x`.

### Rework requested after in-game testing (2026-11-15)

- Customers leave before their products are done when the client and the host have different settings.
- The setting should be typed into a text field in the in-game mod settings, not set with a slider.
- The higher speed should also apply when the customer needs help from an employee.

## Improvement #5: Allow Level 10 Restockers to Carry Two Boxes

**Status:** REVIEWED 2026-11-15 — Fix v1.1.3 in `RestockerDoubleCarry/`, awaiting in-game test  
**Last reviewed:** 2026-11-15  
**Project:** `RestockerDoubleCarry/`

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

### Rework requested after in-game testing (2026-11-15)

When restockers are carrying two boxes and they drop the bottom one, the top one should move down so it takes its place. Right now the top one stays floating.

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

**Status:** NEEDS REWORK — See the rework request below (2026-11-15)  
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

### Rework requested after in-game testing (2026-11-15)

Shift-clicking should work with paper bags for delivery too.

- If the bag is being prepared for an active delivery, shift-clicking only takes the number of items the order still needs.
- Otherwise, load as many products of that type as the bag can carry.
- "Active delivery" means there is a delivery order present, and the currently selected delivery is not already being handled by another player or the courier.

## Improvement #10: Allow Players to Carry Two Boxes at Once

**Status:** NEEDS REWORK — See the rework request below (2026-11-15)  
**Last reviewed:** 2026-11-15  
**Project:** `PlayerDoubleCarry/`

Allow players to pick up and carry two boxes at a time.

### Restrictions

When carrying two boxes:
- Players cannot open either box.
- Players can only carry the boxes and place them somewhere else.

As soon as the player drops, throws, or places one of the boxes, they should be able to open the remaining box and place its products on shelves normally, following the game's standard behavior.

### Rework requested after in-game testing (2026-11-15)

It should also work at the wholesale store. When the player is holding a box and goes to a shelf, fridge or freezer at the wholesale store, they should still be able to click and hold to buy another box and stack it on top of the one they already hold. The same limit of at most 2 boxes in hand still applies.

## Improvement #11: More clear tags

**Status:** NEW

Features:
- Make the tags on boxes that show a small icon of what's inside 2x bigger. This number is configurable in the settings.
- Pointing the player's reticle at one of these tags shows a hover text with the name of the product, the quantity of items inside and the price of the box itself.
- This works also for the tags in shelves and racks.

## Improvement #12: Auto courier load

**Status:** NEW

When the courier comes back from a delivery, he scans a small area around and including his regular parking space, and if there are any delivery bags that are closed and tagged with the customer's name whose delivery is active at the moment, he immediately takes it and loads it in his trunk. This works for up to 3 bags at a time (or whatever his trunk limit happens to be). if the bags he collected cover all the active online orders at the moment, or if one of those deliveries has less than one minute left, he leaves for delivery, as if the player went to him and pressed E to send him.

## Improvement #13: Highlight same products

**Status:** NEW

Currently, hen holding a box of products, the game highlights boxes of the same type that are in the storage racks, with a green outline. Do the same for shelves/fridges/freezers that are labeled with this type of product, even if they're empty at the moment. Unlike the storage, where the boxes are highlighted, mark the whole shelf/fridge/freezer in this case, since the shelves could all be empty but we still want to find where those products would go, based on their labels.

Additionally, when holding a paper bag for an online order, highlight all the groups of products on shelves/fridges/freezers that have the products that the order needs. In this case mark the products and not the shelves, so that if there is no product of this type on any of the shelves then nothing is highlighted. We don't want to see empty shelves since online orders require the products to actually be there.

In both cases, make sure that the highlighting is updated live, since the labels and the
stock of each shelf can change after the player picked up the box/bag. The highlighting
needs to be updated to match

## Improvement #14: Pack up products

Right clicking on a product that is in a shelf/fridge/freezer currently does nothing, as
it requires the user to use the crate tool or a paper bag to pack products in a specific
way.

This mod makes it so that right clicking on a product that is on the shelf spawns a box
that is the right size for this type of product, and immediately puts that item in the
box. Keep in mind that boxes in this game have different sizes, and each product only has
one box size that is the right one for it, and the game does not allow to put items into
the wrong box type, so make sure to spawn the right one.

## Improvement #15: Employee clothes

**Status:** NEW

Create a new entry in the customization menu from the game's computer that allows the
player to pick the color scheme of the clothes of the employees. It costs $500 to access
each time.

When selected, the game shows a preview of all the character models for a type of
employee, with arrows to switch between types. It then offers a color palette picker like
the one that already exists in game and is used for selecting the colors of the store's
name at the front (another option in the customization menu). The color is then applied
to the clothes of the employees, which still retain the same character models and clothes
that are built in, just with the new colors. A checkbox is offered called "Apply to all"
that will apply it all of the employee types, although the user can still switch to
another type and override the color for that one specifically.

