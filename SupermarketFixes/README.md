# Supermarket Fixes

Fixes for bugs in Supermarket Simulator, mostly ones that show up in online co-op. Every fix can be switched on or off separately.

## Fixes

### Bug #1: moving a rack leaves ghost or duplicate boxes behind

After moving a rack, some of its boxes could stay floating at the old spot. Some could be picked up; others were ghosts you could walk through. Some boxes also ended up duplicated. It mostly happened to multiplayer guests.

Three causes are fixed:

- **Rack updates from the host** could leave a guest with a stray copy of a box next to the real one.
- **Joining a game in progress:** boxes that had been put on racks earlier in the session reached the joining guest as loose boxes sitting where they were. The rack also got its own copy of each one, so moving the rack left a pickable box floating behind. Now a guest puts those boxes back into their rack within a few seconds of the store loading.
- **Grabbing a box straight off a rack** (clicking the box itself, often one of those floating boxes) took the box but left the rack thinking it still had it. Players who joined later saw a ghost box there, and loading the save created a real duplicate. Now the box is removed from the rack properly, and its position is shared with other players again once it leaves the rack, as when it's taken from the rack the normal way (players with the mod).

Ghost boxes already saved into a rack before this version are not removed automatically. F8 lists them as `STALE` (see `Diagnostics` / `VerboseLogging` below).

**For guests without the mod (optional, host setting, off by default):** with `DematerializeRackedBoxesOnHost` on, the host turns each box that has been resting on a rack for about 10 seconds into an ordinary racked box, the same kind the game creates when it loads a save, for every player. It uses the game's own network message, so guests without the mod handle it too. The host then has no "live" boxes on racks for a guest to receive as floating copies. This is experimental: the game ships with this mechanism switched off, and it hasn't been tested in-game yet. Boxes that are alone in their rack slot are left as they are, because the game's own message would delete such a box.

### Bug #2: an image of an item stays in your hands after putting it down

After putting a held item down, a copy of it could stay in front of the camera, blocking the view, until the game was reloaded. This is suspected to happen to multiplayer guests when the host's answer to a box pickup arrives late, while the guest is already holding or using something else. Now a stray item left in your hands (a box, or another item such as a furniture box, paint bucket or paper bag) is dropped automatically after 2 seconds. It is never dropped inside a shelf or wall: if the spot in front of you is blocked, it waits until you step into open space.

Where it can, the mod uses the game's own drop, so other players see the item leave your hands too. In one case it can't: when the stray item is one you were holding before the late box arrived. Then only your own view is fixed. Other players may still see it attached to you until the game is reloaded, and if it's a box or a furniture box, the host still counts it as held by you (when you are the host yourself, the mod clears this). For a box, a host running this mod (with `RegrantToSameHolder`) lets you pick it up again; with a host without the mod, nobody can pick that box up until the game is reloaded. For a furniture box that was dropped from a guest's hands, nobody can pick it up again until the game is reloaded, even with a modded host.

### Bug #3: stuck on a box after buying it in a market

After buying a box in a market (often ice cream cones in the general goods store), the box could stay on the shelf while your controls stayed locked onto it: clicking opened and closed it, and you couldn't drop or throw it. Now, after 2 seconds in that state, your controls are freed and the game tries once more to put the box in your hands. This works every time it happens, not just the first few. If you're a guest and the host never answers your pickup request, that is handled after about 8 seconds. Pressing F9 while a pickup is still waiting for the host does nothing until 3 seconds have passed, so it can't cancel a pickup that is about to succeed.

### Bug #5: can't put items from a box onto a shelf until the box is closed and reopened

Sometimes holding left click with an open box on an empty shelf slot (often one whose old label had just been cleared) placed nothing, until you closed the box and opened it again. The game could end up treating an open box as closed: for example when you pressed close while the box was still in its opening animation, or in multiplayer when a late "place/take item" answer from the host reopened a box you had just closed. Now, as soon as you hold left click with an open box, it's treated as open again and items go onto the shelf. If the game also still thinks you're holding right click after you've let go (that blocks placing too), that's released after half a second.

The same symptom with a **crate** (left click on an empty shelf slot that has no label places nothing until you deselect and reselect the crate) is also handled: if the crate's remembered product choice points at something that is no longer in the crate, it is repaired when you aim at such a slot.

If it still happens, the log says why (see `Diagnostics` / `VerboseLogging` below). Please send those lines.

### Bug #6: boxes stay floating after the box under them is taken

In a pile of boxes (for example a big online order), taking the box under another one sometimes left the upper box hanging in the air until someone picked it up. The likely reason is that boxes at rest go to sleep and don't notice that their support is gone. Now, right after a box is picked up, the mod wakes the sleeping boxes within a couple of metres so they fall if nothing holds them, and every 5 seconds it checks the whole store for sleeping boxes with nothing under them. The checks are cheap (a handful of boxes after a pickup).

If it still happens, the log has `[FloatBox]` lines describing the box that stayed in the air. Please send those.

### Bug #7 and Bug #8: the cleaning lady mops next to the stain, or pushes into walls to reach it

The cleaning lady walked right onto the middle of a floor stain and then mopped the floor in front of her, so it looked as if she was cleaning the wrong spot (the stain still disappeared). Next to a shelf or wall she also pushed herself into it. Now she picks a spot where she fits, away from walls, shelves, racks and boxes (other people don't count), and turns so that her mop points at the centre of the stain. She prefers the spot from which her mop lands exactly on the centre. If there is no room there, she stands a little nearer or further away, so the mop is still on the stain. If there's no room anywhere near, she cleans from further away (mop aimed at the stain but short of it), or, in a tight corner, from on top of the stain as in the unmodded game. In the rare case that she fits nowhere around the stain at all, not even on it, she still cleans it from the best spot she can walk to and may overlap a shelf or wall; the log then has a `[JanitorAlign]` warning. The stain is always cleaned; she never skips one.

While she cleans, the mod also checks where her mop really is and uses that for the next stains, so the alignment can improve after the first stain. The way she walks to her spot is unchanged, so she can still brush past corners on the way.

With `VerboseLogging` on, the log has `[JanitorAlign]` lines that say where she stood and how far her mop was from each stain. Please send those.

### Bug #9: orders the courier already has stay in the pending list

In co-op, when a player handed an online-order bag to the courier, the order's number dropped from that player's new-orders counter but stayed on the other player's, and on every screen the order stayed in the pending-orders notebook until the customer actually had the bag. Now, as soon as the courier has an order (in the trunk or out for delivery), it is removed from your notebook and from the counter. If the order is taken back from the courier, it returns to the notebook. The order itself is untouched: the courier still delivers it and you still get paid.

This only changes what your own screen shows, so every player who wants it needs the mod (see below).

With `VerboseLogging` on, the log has `[CourierOrders]` lines when an order is hidden or shown again. Please send those.

### F9: "my hands are stuck" key

Press **F9** if your hands or controls seem stuck on an item. It fixes either of the two problems above right away, and writes what it found to `BepInEx/LogOutput.log`. Please send those lines.

## Settings

`BepInEx/config/flamboyanthopper.supermarketfixes.cfg`, or in-game with BepInExConfigManager:

| Section | Setting | Default | What it does |
|---|---|---|---|
| General | `Enabled` | `true` | Enable the mod. Turning it off removes every fix right away; no restart needed. |
| Diagnostics | `VerboseLogging` | `false` | Enable verbose logging for debugging purposes. Turns on the extra `[RackMove]`, `[GhostHeld]`, `[StuckBox]`, `[FloatBox]`, `[BoxPlace]`, `[JanitorAlign]` and `[CourierOrders]` log lines (recoveries and failures are always logged). With it on, **F8** scans the whole store for orphan, duplicate and stale (ghost) rack boxes. |
| RackMove | `AdoptNetworkedBoxesOnRebuild` | `true` | When a guest rebuilds a rack slot from network data, keeps its networked boxes in the slot instead of orphaning them. This is the main ghost-box fix. |
| RackMove | `GuardDelayedInstancing` | `true` | Ignores delayed "draw this box as racked" calls for boxes no longer in a slot, or on a rack being carried. |
| RackMove | `FinishBoxTweensBeforePickup` | `true` | Before a rack is picked up, finishes any box animations still running on it. |
| RackMove | `AdoptRackedBoxesAfterJoining` | `true` | Guest only: after joining a game in progress, puts boxes the host has on racks back into their rack instead of leaving loose copies that float when the rack moves. |
| RackMove | `RemoveFromRackOnDirectPickup` | `true` | When a box is picked up straight off a rack, removes it from the rack properly, so it doesn't come back as a ghost for players who join later or as a duplicate when the save is loaded. Matters most on the host. |
| RackMove | `DematerializeRackedBoxesOnHost` | `false` | Host only, experimental. About 10 seconds after a box is put on a rack, turns it into an ordinary racked box for everyone, so guests without the mod can't get floating copies of it. Boxes alone in their slot are skipped. |
| GhostHeldItem | `AutoRecover` | `true` | Drops items that are stuck in your hands without being held. |
| GhostHeldItem | `DelaySeconds` | `2` | How long (0.5–10 s) a stray item must stay in your hands before it is dropped. |
| StuckBox | `AutoRecover` | `true` | Frees your controls when they stay locked onto a box that isn't in your hands. |
| StuckBox | `StuckSeconds` | `2` | How long (0.5–10 s) that state must last before it is fixed. |
| StuckBox | `RetryPickup` | `true` | After freeing your controls, tries once more to pick the box up the normal way. |
| StuckBox | `RegrantToSameHolder` | `true` | Host only: if a player asks for a box the host thinks that player already holds, allows it instead of refusing. This lets a stuck player's retry succeed. |
| BoxPlace | `ResyncOpenState` | `true` | If the box in your hands is open but the game treats it as closed (clicking a shelf places nothing until you close and reopen the box), fixes it as soon as you hold left click. |
| BoxPlace | `ReleaseStuckTake` | `true` | If the game still thinks you're holding right click (take) after you let go, which also stops placing, releases it. |
| BoxPlace | `RepairCrateSelection` | `true` | If a crate refuses to put products on an empty shelf slot without a label until you deselect and reselect the crate, repairs the crate's product selection automatically. |
| FloatingBox | `WakeUnsupportedBoxes` | `true` | Boxes that float after the box under them was taken are woken up so they fall. |
| JanitorCleanAlign | `AlignToStain` | `true` | The cleaning lady aims her mop at the centre of the stain she cleans and stands where she fits, instead of standing on the stain (or inside a wall or shelf). Only needed on the host (or in single player). |
| CourierOrders | `HideCourierOrders` | `true` | Orders the courier has already taken (in the trunk or out for delivery) are removed from your pending-orders notebook and the new-orders counter. They come back if the order is taken back from the courier. Only affects your own screen. |

## Who needs it (multiplayer)

- **Bug #1: both.** The floating boxes come from things that happen on the guest's machine (rack updates from the host, joining a game in progress), so the guest needs the mod for those. The host needs it so that grabbing a box off a rack doesn't leave a ghost record in the save. When a box is grabbed off a rack, a host with the mod sends the game's own "remove this box from the rack" message to everyone, so guests without the mod stay in sync too.
  - **What a guest without the mod still sees:** boxes that were put on a rack during the session can still stay floating when that rack is moved, if the guest joined the game after they were racked, or after the host's employees restocked that rack. Some of those boxes can be picked up, others are ghosts you walk through. A host with the mod still keeps the rack's real contents (and the save) right.
  - A host can reduce this for guests without the mod by turning on `DematerializeRackedBoxesOnHost` (experimental, see above). Boxes that are alone in their rack slot are still affected, and so are boxes racked less than about 10 seconds before the guest joins or the rack is moved.
- **Bug #6: the machine that owns the box.** Normally that's the host, which spawns the delivery boxes, so a host with the mod fixes those piles for everyone (guests see the box fall through the game's normal position sync). A box a guest has picked up and put down stays owned by that guest, and only that guest's machine can wake it: a guest who moves boxes around should have the mod too.
- **Bug #5: whoever has the problem.** It only repairs your own controls, so it must be installed on the machine where placing fails. Nothing it does is sent over the network, so other players don't need it.
- **Bug #2: the guest.** The suspected cause (a late pickup answer from the host) can only happen on a guest, so the guest who sees the item in their hands needs the mod. Installing it only on the host doesn't fix anything in a guest's hands. The host can run it too: its `RegrantToSameHolder` lets a guest pick up a box again after the mod dropped it from their hands.
- **Bug #3: whoever gets stuck.** The fix repairs the state of your own player, so it only helps on the machine where the problem shows up. The host should run the mod too: `RegrantToSameHolder` runs there and lets a guest's retry succeed. A guest without the mod is not affected by it. If the host does not run the mod, a guest's fix still frees the guest's controls, but the host may keep counting that box as held by the guest: nobody can pick that box up until the game is reloaded, and other players may see it in the guest's hands.
- **Bugs #7 and #8: the host** (or the player, in single player). The cleaning lady is controlled by the host, and other players see where she stands and which way she faces through the game's normal sync, so guests don't need the mod for this fix.
- **Bug #9: every player who wants it, host and guests alike.** The pending-orders notebook and the counter are local to each player's game, and the game has no message that only hides an order, so nothing the host does can change an unmodded guest's screen. A guest without the mod keeps seeing courier orders in the list as before. A host with the mod gets a correct list on its own screen; so does every guest with the mod. Nothing is sent over the network, so mixed setups don't desync.
