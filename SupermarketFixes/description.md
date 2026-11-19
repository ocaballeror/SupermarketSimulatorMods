# Supermarket Fixes

Fixes for bugs in Supermarket Simulator, mostly ones that show up in online co-op. Every fix can be switched on or off separately.

## Fixes

- **Ghost and duplicate boxes after moving a rack.** Boxes no longer stay floating at the old spot or get duplicated, especially for guests.
- **Item stuck in your hands.** A stray image of an item left in front of your camera after putting it down is cleared automatically.
- **Stuck on a box after buying it in a market.** Your controls are freed and the game retries picking the box up.
- **Can't place items from an open box onto a shelf.** Placing works without having to close and reopen the box. Crates are fixed too.
- **Boxes floating in the air** after the box under them is taken now fall.
- **Cleaning lady** stands properly and mops the stain itself, instead of beside it or inside a wall.
- **Courier orders** disappear from your pending-orders notebook once the courier has them.
- **F9 "my hands are stuck" key.** Press it if your hands or controls seem stuck on an item.

## Multiplayer

Most fixes repair your own game, so install it on every machine that has the problem. The host should have it too, so boxes taken off racks are saved correctly. The cleaning lady fix is only needed on the host.

## Settings

Each fix can be switched on or off in the settings.

## Installation

Requires BepInEx 6 (IL2CPP). Copy `SupermarketFixes.dll` into `BepInEx/plugins/`.
