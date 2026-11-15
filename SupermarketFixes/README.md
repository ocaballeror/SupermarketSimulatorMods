# Supermarket Fixes

Fixes for bugs in Supermarket Simulator, mostly ones that show up in online co-op. Every fix can be switched on or off separately.

## Fixes

### Bug #1: moving a rack leaves ghost or duplicate boxes behind (v0.7.0, awaiting in-game confirmation)

After moving a rack, some of its boxes could stay floating at the old spot. Some could be picked up; others were ghosts you could walk through. Some boxes also ended up duplicated. It mostly happened to multiplayer guests.

Three causes are fixed:

- **Rack updates from the host** could leave a guest with a stray copy of a box next to the real one.
- **Joining a game in progress:** boxes that had been put on racks earlier in the session reached the joining guest as loose boxes sitting where they were. The rack also got its own copy of each one, so moving the rack left a pickable box floating behind. Now a guest puts those boxes back into their rack within a few seconds of the store loading.
- **Grabbing a box straight off a rack** (clicking the box itself, often one of those floating boxes) took the box but left the rack thinking it still had it. Players who joined later saw a ghost box there, and loading the save created a real duplicate. Now the box is removed from the rack properly, and its position is shared with other players again once it leaves the rack, as when it's taken from the rack the normal way (players with the mod).

Ghost boxes already saved into a rack before this version are not removed automatically. F8 lists them as `STALE` (see `Diagnostics` below).

**For guests without the mod (optional, host setting, off by default):** with `DematerializeRackedBoxesOnHost` on, the host turns each box that has been resting on a rack for about 10 seconds into an ordinary racked box, the same kind the game creates when it loads a save, for every player. It uses the game's own network message, so guests without the mod handle it too. The host then has no "live" boxes on racks for a guest to receive as floating copies. This is experimental: the game ships with this mechanism switched off, and it hasn't been tested in-game yet. Boxes that are alone in their rack slot are left as they are, because the game's own message would delete such a box.

### Bug #2: an image of an item stays in your hands after putting it down (v0.6.1, awaiting in-game confirmation)

After putting a held item down, a copy of it could stay in front of the camera, blocking the view, until the game was reloaded. This is suspected to happen to multiplayer guests when the host's answer to a box pickup arrives late, while the guest is already holding or using something else. Now a stray item left in your hands (a box, or another item such as a furniture box, paint bucket or paper bag) is dropped automatically after 2 seconds. It is never dropped inside a shelf or wall: if the spot in front of you is blocked, it waits until you step into open space.

Where it can, the mod uses the game's own drop, so other players see the item leave your hands too. In one case it can't: when the stray item is one you were holding before the late box arrived. Then only your own view is fixed. Other players may still see it attached to you until the game is reloaded, and if it's a box, the host still counts it as held by you. A host running this mod (with `RegrantToSameHolder`) lets you pick it up again; with a host without the mod, nobody can pick that box up until the game is reloaded.

### Bug #3: stuck on a box after buying it in a market (v0.4.0, awaiting in-game confirmation)

After buying a box in a market (often ice cream cones in the general goods store), the box could stay on the shelf while your controls stayed locked onto it: clicking opened and closed it, and you couldn't drop or throw it. Now, after 2 seconds in that state, your controls are freed and the game tries once more to put the box in your hands.

### Bug #5: can't put items from a box onto a shelf until the box is closed and reopened (v0.5.0, awaiting in-game confirmation)

Sometimes holding left click with an open box on an empty shelf slot (often one whose old label had just been cleared) placed nothing, until you closed the box and opened it again. The game could end up treating an open box as closed: for example when you pressed close while the box was still in its opening animation, or in multiplayer when a late "place/take item" answer from the host reopened a box you had just closed. Now, as soon as you hold left click with an open box, it's treated as open again and items go onto the shelf. If the game also still thinks you're holding right click after you've let go (that blocks placing too), that's released after half a second.

If it still happens, the log says why (see `Diagnostics` below). Please send those lines.

### F9: "my hands are stuck" key

Press **F9** if your hands or controls seem stuck on an item. It fixes either of the two problems above right away, and writes what it found to `BepInEx/LogOutput.log`. Please send those lines.

## Settings

`BepInEx/config/oscar.supermarketfixes.cfg`, or in-game with BepInExConfigManager:

| Section | Setting | Default | What it does |
|---|---|---|---|
| General | `Enabled` | `true` | Master switch. Turning it off removes every fix right away; no restart needed. |
| RackMove | `AdoptNetworkedBoxesOnRebuild` | `true` | When a guest rebuilds a rack slot from network data, keeps its networked boxes in the slot instead of orphaning them. This is the main ghost-box fix. |
| RackMove | `GuardDelayedInstancing` | `true` | Ignores delayed "draw this box as racked" calls for boxes no longer in a slot, or on a rack being carried. |
| RackMove | `FinishBoxTweensBeforePickup` | `true` | Before a rack is picked up, finishes any box animations still running on it. |
| RackMove | `AdoptRackedBoxesAfterJoining` | `true` | Guest only: after joining a game in progress, puts boxes the host has on racks back into their rack instead of leaving loose copies that float when the rack moves. |
| RackMove | `RemoveFromRackOnDirectPickup` | `true` | When a box is picked up straight off a rack, removes it from the rack properly, so it doesn't come back as a ghost for players who join later or as a duplicate when the save is loaded. Matters most on the host. |
| RackMove | `DematerializeRackedBoxesOnHost` | `false` | Host only, experimental. About 10 seconds after a box is put on a rack, turns it into an ordinary racked box for everyone, so guests without the mod can't get floating copies of it. Boxes alone in their slot are skipped. |
| RackMove | `Diagnostics` | `true` | Logs `[RackMove]` consistency checks when racks are picked up or placed. **F8** scans the whole store for orphan, duplicate and stale (ghost) rack boxes. |
| GhostHeldItem | `AutoRecover` | `true` | Drops items that are stuck in your hands without being held. |
| GhostHeldItem | `DelaySeconds` | `2` | How long (0.5–10 s) a stray item must stay in your hands before it is dropped. |
| GhostHeldItem | `Diagnostics` | `true` | Logs `[GhostHeld]` lines about stray objects in your hands. |
| StuckBox | `AutoRecover` | `true` | Frees your controls when they stay locked onto a box that isn't in your hands. |
| StuckBox | `StuckSeconds` | `2` | How long (0.5–10 s) that state must last before it is fixed. |
| StuckBox | `RetryPickup` | `true` | After freeing your controls, tries once more to pick the box up the normal way. |
| StuckBox | `RegrantToSameHolder` | `true` | Host only: if a player asks for a box the host thinks that player already holds, allows it instead of refusing. This lets a stuck player's retry succeed. |
| StuckBox | `Diagnostics` | `true` | Logs `[StuckBox]` lines about pickups and recoveries. |
| BoxPlace | `ResyncOpenState` | `true` | If the box in your hands is open but the game treats it as closed (clicking a shelf places nothing until you close and reopen the box), fixes it as soon as you hold left click. |
| BoxPlace | `ReleaseStuckTake` | `true` | If the game still thinks you're holding right click (take) after you let go, which also stops placing, releases it. |
| BoxPlace | `Diagnostics` | `true` | Logs `[BoxPlace]` lines explaining why nothing was placed when you hold left click on a shelf for 1.5 s. |

## Who needs it (multiplayer)

- **Bug #1: both.** The floating boxes come from things that happen on the guest's machine (rack updates from the host, joining a game in progress), so the guest needs the mod for those. The host needs it so that grabbing a box off a rack doesn't leave a ghost record in the save. When a box is grabbed off a rack, a host with the mod sends the game's own "remove this box from the rack" message to everyone, so guests without the mod stay in sync too.
  - **What a guest without the mod still sees:** boxes that were put on a rack during the session can still stay floating when that rack is moved, if the guest joined the game after they were racked, or after the host's employees restocked that rack. Some of those boxes can be picked up, others are ghosts you walk through. A host with the mod still keeps the rack's real contents (and the save) right.
  - A host can reduce this for guests without the mod by turning on `DematerializeRackedBoxesOnHost` (experimental, see above). Boxes that are alone in their rack slot are still affected, and so are boxes racked less than about 10 seconds before the guest joins or the rack is moved.
- **Bug #5: whoever has the problem.** It only repairs your own controls, so it must be installed on the machine where placing fails. Nothing it does is sent over the network, so other players don't need it.
- **Bug #2: the guest.** The suspected cause (a late pickup answer from the host) can only happen on a guest, so the guest who sees the item in their hands needs the mod. Installing it only on the host doesn't fix anything in a guest's hands. The host can run it too: its `RegrantToSameHolder` lets a guest pick up a box again after the mod dropped it from their hands.
- **Bug #3: whoever gets stuck.** The fix repairs the state of your own player, so it only helps on the machine where the problem shows up. The host should run the mod too: `RegrantToSameHolder` runs there and lets a guest's retry succeed. A guest without the mod is not affected by it.
