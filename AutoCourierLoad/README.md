# Auto Courier Load

When your courier is waiting at his parking space (after a delivery, or just standing there), he **picks up the finished delivery bags lying around his parking space by himself** and sets off again when there is nothing more to wait for. You no longer have to hand him each bag and press E.

## What he does

1. He is back from a delivery, or has been standing at his parking space for a while, and a bag lands near him (dropped next to him, or already lying there when he arrives).
2. He looks at everything within a few metres of the parking space (4 m by default) for paper bags that are **closed**, have the **customer's name tag** on them, belong to an order that is **still active**, and that **nobody is holding** (not you, not another player, not another courier).
3. He loads them in his trunk, the ones whose orders run out soonest first, up to what his trunk holds (3 for a new courier, more when he levels up). Once he has loaded something he keeps watching the spot for as long as he waits, so a bag you bring later is still taken.
4. Once he has loaded something, he leaves for the delivery, exactly as if you had pressed E on him, as soon as one of these is true:
   - the bags in his trunk cover **every active online order** (there is nothing else to wait for);
   - one of the orders in his trunk has **less than a minute left** (the countdown on the order's note);
   - his trunk is **full**, even if other orders are still active.

Bags that you hold, bags in his trunk, open bags and bags without a name tag are left alone. A bag the courier himself just put back in his drop area (for example because he refused it) is not loaded again until somebody moves it. The same goes for a bag you take back out of his trunk: he leaves it alone until you have carried it more than the scan radius away from his parking space.

If he finds nothing, he simply waits at his spot like before, and you can still hand him bags and press E as usual. Dropping a bag next to him later, even long after he came back, wakes this up again.

## Settings

`BepInEx/config/flamboyanthopper.autocourierload.cfg`, or in-game in the mod settings. Numbers are typed in, not dragged. If your PC uses a decimal comma, type `0,5`.

| Section | Setting | Default | What it does |
|---|---|---|---|
| General | `Enabled` | `true` | Enable the mod. Takes effect right away, no restart. |
| AutoCourierLoad | `ScanRadius` | `4` | How far from his parking space (metres, 0.5 to 20) he looks for bags. |
| Diagnostics | `VerboseLogging` | `false` | Enable verbose logging for debugging purposes (`[AutoCourier]` lines in `BepInEx/LogOutput.log`). |

## Who needs it (multiplayer)

- **Host (or solo): required.** The courier is simulated on the host. Everything the mod does goes through the game's own hand-over and "send him" actions, so the other players see the bag fly into the trunk and the courier leave exactly as if the host had done it.
- **Guests: nothing to install.** A guest with or without the mod works the same, and a guest's mod does nothing. Bags a guest is holding are never taken.

## Compatibility

- Does not change any game data and does not depend on other mods; it can sit next to Supermarket Fixes (including its courier-orders fix).
- Mods that change how the courier is hired or how many bags his trunk holds are fine: the mod follows his real trunk limit.

## Install

Copy `AutoCourierLoad.dll` straight into `BepInEx/plugins/` (not into a subfolder).
