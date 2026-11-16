# Restocker Double Carry

Experienced restockers carry **two boxes per trip**, both when they restock shelves and when they take delivered boxes to storage. A restocker at level 10 or higher (configurable) works like this:

1. It picks up a box for its next job, as the game decides: a box for a product that needs restocking (from a storage rack, the outside delivery area or a delivery vehicle), or a delivered box that goes to storage.
2. If there is a second job, it fetches a second box for it before leaving. The second box rides on top of the first. The second job can be:
   - restocking a **different** product, with a box from a storage rack or from the outside delivery area;
   - taking another box from the outside delivery area to storage.

   So a trip can be two restocks, two boxes for storage, or one of each.
3. With both boxes in hand, it deals first with the box whose destination (its shelf, or its storage rack) is the shorter walk from where it picked up the second box.
4. Putting a box in a storage rack (including topping up a box of the same product already there) works with both boxes in hand: the restocker racks one and keeps carrying the other. **But a restocker never unpacks onto a shelf while holding two boxes.** When it reaches a shelf with two boxes in hand, it puts the other box on the floor next to it, then opens the box for that shelf and restocks.
5. Then it finishes with the box it unpacked:
   - **If the box is empty**, it takes it to the trash (as it normally would), then comes back for the box it left on the floor and deals with that one: it unpacks it at its own shelves, or takes it to storage.
   - **If the box still has products** and the box on the floor goes to storage too, it picks the floor box up again and takes both to storage (the closer rack first).
   - **If the box still has products** and the box on the floor is for a shelf, it takes the leftovers to storage, then comes back for the floor box and unpacks it.
6. Whatever is left in the last box goes to storage, or in the trash if it's empty, as usual.

The box goes on the floor beside the restocker, away from the shelf (or to its left or right if that spot is taken). If the floor box's product was filled on the shelves in the meantime, the restocker takes it straight to storage. If there's nowhere to put it yet, or it can't reach it, it does other work and tries again later (it gives up after 3 minutes of trying; the box then stays on the floor like any box a restocker drops).

The restocker always goes back for a box it put down itself, even when its "pick up boxes from the ground" setting is off: that setting is about boxes in the outside delivery area. Other restockers don't pick up a box lying inside the store, so it waits for the one that put it there. A player can still pick it up; the restocker then just carries on.

If there's no second job when it picks up the first box, or the second box is gone by the time it gets there, the restocker just carries on with one box. Lower-level restockers aren't affected.

All the restocker's own settings (use unlabeled racks, pick up boxes from the ground, drop empty boxes, restock shelves) are respected: for example a restocker that may not pick up boxes from the ground never takes a delivered box to storage as its second job. The second box is chosen the same way the game chooses the first, with these differences:
- it is always a box from the outside area or from a storage rack: a box in a delivery vehicle can be the first box but never the second;
- a second restock job is always for a different product than the first box;
- the restocker never chooses a rack itself: each box is put in storage exactly as the game normally does (a rack labelled for that product with room left, or an empty unlabelled rack only if the restocker may use unlabelled racks). Before it takes a box for storage as part of a pair, it checks with those same rules, and the rack sizes the game uses for each box size (a rack fits 1, 2 or 4 boxes depending on the box), that every box in the pair that may need a rack place will have one. Otherwise it takes just one box, so it isn't left holding a box with nowhere to put it.

## Settings

`BepInEx/config/oscar.restockerdoublecarry.cfg`, or in-game with BepInExConfigManager:

| Section | Setting | Default | What it does |
|---|---|---|---|
| General | `Enabled` | `true` | Master switch. Turning it off takes effect right away; no restart needed. A restocker carrying two boxes at that moment puts the second one down, or takes it in hand if its hands are empty. |
| Restockers | `MinimumLevel` | `10` | Restockers at this level or higher carry two boxes, for restocking and for taking deliveries to storage (1 to 100). |
| Restockers | `StackSecondBox` | `true` | Shows the second box stacked on top of the first in the restocker's hands while it carries two. When one of the two boxes leaves the hands, the other one moves down into the hands. |
| Diagnostics | `VerboseLogging` | `true` | Writes each step of a two-box trip to `BepInEx/LogOutput.log` (tag `[DoubleCarry]`). |

## Who needs it (multiplayer)

**The host.** Restockers are run by the host, so the host's settings decide everything.

Guests don't need the mod: every pick-up, product placement, racking and box put-away uses the game's normal network messages, so everyone stays in sync. Without the mod, a guest sees the two boxes in the same spot, one inside the other, so it looks like the restocker carries only one. A guest with the mod installed sees them stacked.

## Notes

- If a restocker is fired or deactivated while carrying two boxes, both are put down on the floor. A box it had already put down at a shelf stays there.
- Turning the mod off while a box waits on the floor leaves it there, and so does saving and reloading at that moment. Restockers don't pick up boxes inside the store, so move it yourself. Turning the mod off while a restocker is on its way to pick up a second box makes it put the box it holds down where it is.
- With several restockers, another restocker may refill one of the two products in the meantime. Nothing breaks: the extra stock just goes back to storage.
- A guest who joins in the middle of a two-box trip may see the second box floating near the restocker until the restocker next handles it.
- A restocker that can't find anywhere to put a leftover box behaves as in the normal game: it waits, then puts the box on the floor. It then deals with the other box.
- Saving while a restocker carries two boxes saves both boxes where they are, just as the game does for the single box a restocker carries.
- Compatible with MoreEmployees. It may conflict with other mods that change how restockers choose their work.
