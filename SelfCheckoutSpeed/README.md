# Self Checkout Speed

Customers get through **self checkout machines faster**: 2x by default, adjustable up to 10x, decimals allowed (for example 1.5x).

This speeds up everything a customer waits for at the machine:
- scanning each item;
- paying;
- an employee (customer helper) scanning the items for a customer who needs help.

## Settings

`BepInEx/config/flamboyanthopper.selfcheckoutspeed.cfg`, or in-game in the mod settings:

| Section | Setting | Default | What it does |
|---|---|---|---|
| General | `Enabled` | `true` | Enable the mod. Switches the speed-up on or off when you host or play solo. Takes effect right away. As a guest you always run at the host's speed (see below). |
| SelfCheckout | `SpeedMultiplier` | `2` | A number you type in: 1 = normal, 2 = twice as fast, 1.5 is fine too, up to 10 (a bigger number becomes 10). If your PC uses a decimal comma, type `1,5`. Changes apply from the next item scanned. |
| Diagnostics | `VerboseLogging` | `false` | Enable verbose logging for debugging purposes (every shortened wait as `[SelfCheckout]` lines in `BepInEx/LogOutput.log`; very chatty). |

## Who needs it (multiplayer)

- **Host: required.** The host runs the customers and decides when a checkout is finished. Its settings decide the speed for everyone.
- **Guests: strongly recommended.** Each guest's game animates the scanning itself. A guest with the mod always uses the **host's** speed, whatever its own settings say (even if the mod is switched off there), so the two games stay in step. If the host has no mod, the guest runs at normal speed too. If a guest still lags behind (network delay), it finishes the remaining items at once when the host says the customer is done.
- Host and guests need version **1.1.0 or newer** for this; an older host doesn't tell guests its speed, so they run at normal speed.
- In multiplayer the host never shortens the payment wait below half a second, so guests have time to finish scanning.
- A guest **without** the mod scans at normal speed and falls behind a sped-up host: it sees customers leave while their items are still being scanned. Install the mod on every guest.

## Notes

- At high multipliers the customer's scanning animation can't keep up with the items, which is only a visual effect.
- Compatible with other mods that don't change self checkouts.
