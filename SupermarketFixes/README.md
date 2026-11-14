# Supermarket Fixes

Fixes for bugs in Supermarket Simulator, mostly ones that show up in online co-op. Every fix can be switched on or off separately.

## Fixes

### Bug #1: moving a rack leaves ghost or duplicate boxes behind (v0.6.0, awaiting in-game confirmation)

After moving a rack, some of its boxes could stay floating at the old spot. Some could be picked up; others were ghosts you could walk through. Some boxes also ended up duplicated. It mostly happened to multiplayer guests.

Three causes are fixed:

- **Rack updates from the host** could leave a guest with a stray copy of a box next to the real one.
- **Joining a game in progress:** boxes that had been put on racks earlier in the session reached the joining guest as loose boxes sitting where they were. The rack also got its own copy of each one, so moving the rack left a pickable box floating behind. Now a guest puts those boxes back into their rack within a few seconds of the store loading.
- **Grabbing a box straight off a rack** (clicking the box itself, often one of those floating boxes) took the box but left the rack thinking it still had it. Players who joined later saw a ghost box there, and loading the save created a real duplicate. Now the box is removed from the rack properly.

Ghost boxes already saved into a rack before this version are not removed automatically. F8 lists them as `STALE` (see `Diagnostics` below).

### Bug #2: an image of an item stays in your hands after putting it down (v0.4.0, awaiting in-game confirmation)

After putting a held box down, a copy of it could stay in front of the camera, blocking the view, until the game was reloaded. Now a stray box left in your hands is dropped to the floor automatically after 2 seconds.

Limitation: this only fixes your own view. Other players may still see the box attached to you, and the host still counts it as held by you, so only you can pick it up again until the game is reloaded.

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
| RackMove | `Diagnostics` | `true` | Logs `[RackMove]` consistency checks when racks are picked up or placed. **F8** scans the whole store for orphan, duplicate and stale (ghost) rack boxes. |
| GhostHeldItem | `AutoRecover` | `true` | Drops boxes that are stuck in your hands without being held. |
| GhostHeldItem | `DelaySeconds` | `2` | How long (0.5–10 s) a stray box must stay in your hands before it is dropped. |
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
- **Bug #5: whoever has the problem.** It only repairs your own controls, so it must be installed on the machine where placing fails. Nothing it does is sent over the network, so other players don't need it.
- **Bugs #2 and #3: whoever gets stuck.** Both fixes repair the state of your own player, so they only help on the machine where the problem shows up. The host should run the mod too: `RegrantToSameHolder` runs there and lets a guest's retry succeed. A guest without the mod is not affected by it.
