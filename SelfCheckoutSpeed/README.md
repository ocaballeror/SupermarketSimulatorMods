# Self Checkout Speed

Checkouts get through customers **faster**: 2x by default, adjustable up to 10x, decimals allowed (for example 1.5x). The mod is named after its first feature; it now covers cashiers too.

This speeds up the scanning of the customer's items:
- at **self checkout machines**;
- when an **employee (customer helper)** scans the items for a customer who needs help at a self checkout;
- when a **cashier employee** scans the items at a regular checkout.

The payment that follows scanning (the customer paying the cashier or the machine) is **not** sped up; it takes as long as in the normal game.

## Settings

`BepInEx/config/flamboyanthopper.selfcheckoutspeed.cfg`, or in-game in the mod settings:

| Section | Setting | Default | What it does |
|---|---|---|---|
| General | `Enabled` | `true` | Enable the mod. Switches the speed-up on or off when you host or play solo. Takes effect right away. As a guest you always run at the host's speed (see below). |
| SelfCheckout | `SpeedMultiplier` | `2` | A number you type in: 1 = normal, 2 = twice as fast, 1.5 is fine too, up to 10 (a bigger number becomes 10). It applies to self checkouts, employee help and cashiers alike. If your PC uses a decimal comma, type `1,5`. Changes apply from the next item scanned. |
| Diagnostics | `VerboseLogging` | `false` | Enable verbose logging for debugging purposes (every shortened wait as `[SelfCheckout]` lines in `BepInEx/LogOutput.log`; very chatty). |

## Who needs it (multiplayer)

- **Host: required.** The host runs the customers and employees and decides when a checkout is finished. Its settings decide the speed for everyone.
- **Cashiers: guests don't need the mod.** Only the host runs the cashier scanning, and guests just see each item being scanned as the host reports it, so they follow the host's pace automatically.
- **Self checkouts: guests strongly recommended.** Each guest's game animates the scanning at a self checkout itself. A guest with the mod always uses the **host's** speed, whatever its own settings say (even if the mod is switched off there), so the two games stay in step. If the host has no mod, the guest runs at normal speed too. If a guest still lags behind (network delay), it finishes the remaining items at once when the host says the customer is done.
- Host and guests need version **1.1.0 or newer** for this; an older host doesn't tell guests its speed, so they run at normal speed.
- A guest **without** the mod scans self checkouts at normal speed and falls behind a sped-up host: it sees customers leave while their items are still being scanned. Install the mod on every guest.

## Notes

- At high multipliers the scanning animation (customer or cashier) can't keep up with the items, which is only a visual effect.
- Compatible with other mods that don't change checkouts.
