# Restocker Priority

Restockers fill the emptiest products first, including boxes that are still in storage racks.

In the normal game a restocker already ranks products by how empty their shelves are (items on all the product's labelled shelf slots divided by what those slots can hold, so a product that fills a shelf with only two items is counted correctly). Products at 0% come first, and a product whose shelves are nearly full is not restocked at all, so its boxes just go to storage. There is one exception: boxes in the outside delivery area are always handled before boxes in storage racks, so a product that is 60% stocked with a box outside is restocked before a product with completely empty shelves whose box is in a rack.

This mod removes that exception. When a restocker is about to take a delivery-area box (or has no restocking job although another product's box sits in a rack) and a product with strictly emptier shelves has a usable box in a rack, it restocks that emptier product first. If both products are equally full, nothing changes, and if the emptier product's box can't be used the restocker carries on as normal, so it is never left without work.

Works for every restocker, whatever its level.

## Settings

`BepInEx/config/flamboyanthopper.restockerpriority.cfg`, or in-game with BepInExConfigManager:

| Section | Setting | Default | What it does |
|---|---|---|---|
| General | `Enabled` | `true` | Enable the mod. Takes effect right away; no restart needed. |
| Diagnostics | `VerboseLogging` | `false` | Enable verbose logging for debugging purposes. |

## Who needs it (multiplayer)

**The host only.** Restockers run on the host; guests need nothing.

## Compatibility

Works with MoreEmployees, AutoStock and Restocker Double Carry (which also picks its second box in the same emptiest-first order). Boxes in delivery vehicles are not affected.
