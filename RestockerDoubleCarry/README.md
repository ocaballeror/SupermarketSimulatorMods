# Restocker Double Carry

Experienced restockers carry **two boxes per trip**. A restocker at level 10 or higher (configurable) works like this:

1. It picks up a box for a product that needs restocking, from a storage rack or from the outside area where deliveries are dropped.
2. If a **different** product also needs restocking and a box of it is available, the restocker fetches that box too. The second box rides on top of the first.
3. **A restocker never unpacks while holding two boxes.** When it reaches the first shelf, it puts the top box on the floor next to it, then opens the other box and restocks that product.
4. Then it finishes with the box it unpacked:
   - **If the box is empty**, it takes it to the trash (as it normally would), then comes back for the box it left on the floor and unpacks that one.
   - **If the box still has products**, it takes it to storage (a rack), then comes back for the box on the floor and unpacks that one.
5. Whatever is left in the second box goes to storage, or in the trash if it's empty, as usual.

If the second product's shelves were filled in the meantime, the restocker takes the box from the floor straight to storage. If there's nowhere to put it yet, it does other work and comes back for it later (it gives up after 3 minutes; the box then stays on the floor like any box a restocker drops). Other restockers don't pick up a box lying inside the store, so it waits for the one that put it there. A player can still pick it up; the restocker then just carries on.

If there's no second job when it picks up the first box, or the second box is gone by the time it gets there, the restocker just carries on with one box. Lower-level restockers aren't affected.

All the restocker's own settings (use unlabeled racks, pick up boxes from the ground, drop empty boxes, restock shelves) are respected. The second box is chosen the same way the game chooses the first, except that it is always a box from the outside area or from a storage rack: a box in a delivery vehicle can be the first box but never the second.

## Settings

`BepInEx/config/oscar.restockerdoublecarry.cfg`, or in-game with BepInExConfigManager:

| Section | Setting | Default | What it does |
|---|---|---|---|
| General | `Enabled` | `true` | Master switch. Turning it off takes effect right away; no restart needed. A restocker carrying two boxes at that moment puts the second one down, or takes it in hand if its hands are empty. |
| Restockers | `MinimumLevel` | `10` | Restockers at this level or higher carry two boxes (1 to 100). |
| Restockers | `StackSecondBox` | `true` | Shows the second box stacked on top of the first in the restocker's hands (on the way from storage to the shelves). |
| Diagnostics | `VerboseLogging` | `true` | Writes each step of a two-box trip to `BepInEx/LogOutput.log` (tag `[DoubleCarry]`). |

## Who needs it (multiplayer)

**The host.** Restockers are run by the host, so the host's settings decide everything.

Guests don't need the mod: every pick-up, product placement and box put-away uses the game's normal network messages, so everyone stays in sync. Without the mod, a guest sees the two boxes in the same spot, one inside the other, so it looks like the restocker carries only one. A guest with the mod installed sees them stacked.

## Notes

- If a restocker is fired or deactivated while carrying two boxes, both are put down on the floor. A box it had already put down at a shelf stays there.
- Turning the mod off while a box waits on the floor leaves it there; restockers don't pick up boxes inside the store, so move it yourself.
- With several restockers, another restocker may refill one of the two products in the meantime. Nothing breaks: the extra stock just goes back to storage.
- A guest who joins in the middle of a two-box trip may see the second box floating near the restocker until the restocker next handles it.
- A restocker that can't find anywhere to put a leftover box behaves as in the normal game: it waits, then puts the box on the floor. It then deals with the other box.
- Saving while a restocker carries two boxes saves both boxes where they are, just as the game does for the single box a restocker carries.
- Compatible with MoreEmployees. It may conflict with other mods that change how restockers choose their work.
