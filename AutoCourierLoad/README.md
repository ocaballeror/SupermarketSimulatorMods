# Auto Courier Load

When your courier comes back from a delivery, he **picks up the finished delivery bags lying around his parking space by himself** and sets off again when there is nothing more to wait for. You no longer have to hand him each bag and press E.

## What he does

1. He is back from a delivery and waiting at his parking space.
2. He looks at everything within a few metres of the parking space (4 m by default) for paper bags that are **closed**, have the **customer's name tag** on them, belong to an order that is **still active**, and that **nobody is holding** (not you, not another player, not another courier).
3. He loads them in his trunk, the ones whose orders run out soonest first, up to what his trunk holds (3 for a new courier, more when he levels up). Once he has loaded something he keeps watching the spot for as long as he waits, so a bag you bring later is still taken.
4. Once he has loaded something, he leaves for the delivery, exactly as if you had pressed E on him, as soon as one of these is true:
   - the bags in his trunk cover **every active online order** (there is nothing else to wait for);
   - one of the orders in his trunk has **less than a minute left** (the countdown on the order's note);
   - optionally, his trunk is **full** (`LeaveWhenFull`, off by default: with more active orders than trunk slots he waits for you or for a deadline).

Bags that you hold, bags in his trunk, open bags and bags without a name tag are left alone. A bag the courier himself just put back in his drop area (for example because he refused it) is not loaded again until somebody moves it.

If he finds nothing, he simply waits at his spot like before, and you can still hand him bags and press E as usual.

## Settings

`BepInEx/config/oscar.autocourierload.cfg`, or in-game in the mod settings. Numbers are typed in, not dragged. If your PC uses a decimal comma, type `0,5`.

| Section | Setting | Default | What it does |
|---|---|---|---|
| General | `Enabled` | `true` | Master switch. Takes effect right away, no restart. |
| AutoCourierLoad | `ScanRadius` | `4` | How far from his parking space (metres, 0.5 to 20) he looks for bags. |
| AutoCourierLoad | `MaxBags` | `0` | The most bags he loads this way. 0 = as many as his trunk holds. A number higher than his trunk size changes nothing. Bags you hand him yourself count too. It also sets what counts as "full" for `LeaveWhenFull`. |
| AutoCourierLoad | `LeaveUnderSeconds` | `60` | He leaves at once when an order in his trunk has less than this many seconds left (0 to 600). 0 = never leave because of a deadline. |
| AutoCourierLoad | `LeaveWhenFull` | `false` | He also leaves as soon as his trunk is full (or `MaxBags` is reached), even if other orders are still active. Off: he leaves only when his trunk covers every active order or an order is about to run out. |
| AutoCourierLoad | `KeepScanningSeconds` | `30` | After he is back, bags you put down at his spot within this many seconds are still picked up (0 to 600). 0 = only the first moments after he arrives. Once he has loaded something he keeps looking for as long as he waits. |
| Diagnostics | `Diagnostics` | `true` | Writes `[AutoCourier]` lines to `BepInEx/LogOutput.log`. Turn off once it works for you. |

## Who needs it (multiplayer)

- **Host (or solo): required.** The courier is simulated on the host. Everything the mod does goes through the game's own hand-over and "send him" actions, so the other players see the bag fly into the trunk and the courier leave exactly as if the host had done it.
- **Guests: nothing to install.** A guest with or without the mod works the same, and a guest's mod does nothing. Bags a guest is holding are never taken.

## Compatibility

- Does not change any game data and does not depend on other mods; it can sit next to Supermarket Fixes (including its courier-orders fix).
- Mods that change how the courier is hired or how many bags his trunk holds are fine: the mod follows his real trunk limit.

## Install

Copy `AutoCourierLoad.dll` straight into `BepInEx/plugins/` (not into a subfolder).
