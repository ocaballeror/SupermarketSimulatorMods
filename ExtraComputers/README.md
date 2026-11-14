# Extra Computers

Adds a **Computer** to the furniture shop ($500 by default). Buy as many as you like. Each one arrives in a furniture box with a computer icon on it. Unbox it and place it anywhere, like any other furniture. It works like the store's own computer: shop, pricing, hiring, management, everything.

- **Every player has their own screen.** Each player sees only what they do on the computer they're using. Two players can use two computers at the same time, or one player can use an extra computer while another uses the store computer.
- Changes (prices, orders, hires, ...) are shared exactly like changes made on the store computer. If two players have the same page open, one of them may see an older view until they reopen that page (the same as playing with the store computer alone).
- Hold the use key to move an extra computer. It can be boxed up, moved and sold like other furniture. Extra computers are saved with the store.

## Settings

`BepInEx/config/oscar.extracomputers.cfg` (also editable in-game with BepInExConfigManager):

| Section | Setting | Default | Meaning |
|---|---|---|---|
| General | `Enabled` | `true` | Master switch, applied immediately. While it's off, placed computers stay as furniture but can't be used. If the game is **started** with it off, saved extra computers aren't loaded and disappear the next time the game saves. |
| ExtraComputers | `Price` | `500` | Price of one computer in the furniture shop (0–100000). |
| Diagnostics | `VerboseLogging` | `true` | Logs how the computer furniture is built and each use (tag `[ExtraComputers]` in `BepInEx/LogOutput.log`). |

## Multiplayer

**Everyone in the session needs the mod**, the host and every guest:
- A guest without it doesn't see extra computers, and gets errors when a computer box is delivered.
- The computer furniture only exists in games where the mod is installed.

## Compatibility and notes

- While you use an extra computer, the store computer's monitor looks blank **on your own screen only**, because your screen has been borrowed. Other players see it normally.
- Removing the mod: sell extra computers first. Without the mod they don't load (the game logs "Can't spawn furniture with id: 917001") and should be considered lost.
- Portable Computer Mod also borrows the computer screen. Don't open it while you're using an extra computer.
