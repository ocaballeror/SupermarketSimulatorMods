# Self Checkout Speed

Customers get through **self checkout machines faster**: 2x by default, adjustable up to 10x, decimals allowed (for example 1.5x).

This speeds up everything a customer waits for at the machine:
- scanning each item;
- paying.

## Settings

`BepInEx/config/oscar.selfcheckoutspeed.cfg`, or in-game with BepInExConfigManager:

| Section | Setting | Default | What it does |
|---|---|---|---|
| General | `Enabled` | `true` | Master switch. Turning it off restores normal speed right away; no restart needed. |
| SelfCheckout | `SpeedMultiplier` | `2` | 1 = normal, 2 = twice as fast, up to 10. Changes apply from the next item scanned. |
| Diagnostics | `VerboseLogging` | `false` | Logs every shortened wait as `[SelfCheckout]` lines in `BepInEx/LogOutput.log`. Very chatty. |

## Who needs it (multiplayer)

- **Host: required.** The host runs the customers and decides when a checkout is finished.
- **Guests: strongly recommended.** Each guest's game scans the items itself, at its own speed. A guest without the mod scans at normal speed and falls behind the host, by about *items × scan time × (1 − 1/multiplier)*. Until it catches up, that guest sees and hears the machine still scanning after the customer has paid and left, and possibly while the next customer starts at the same machine. This should only affect what the guest sees, but it hasn't been tested yet. With the mod on both sides they stay in step.

## Notes

- Self checkouts where an employee (customer helper) is helping the customer are not affected; those run at the employee's speed.
- At high multipliers the customer's scanning animation can't keep up with the items, which is only a visual effect.
- Compatible with other mods that don't change self checkouts.
