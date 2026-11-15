# Extra Computers

Adds a **Computer** to the furniture shop ($500 by default). Buy as many as you like. Each one arrives in a furniture box with a computer icon on it. Unbox it and place it anywhere, like any other furniture. It works like the store's own computer: shop, pricing, hiring, management, everything.

- **Every player has their own screen.** Each player sees only what they do on the computer they're using. Two players can use two computers at the same time, or one player can use an extra computer while another uses the store computer.
- Changes (prices, orders, hires, ...) are shared exactly like changes made on the store computer. If two players have the same page open, one of them may see an older view until they reopen that page (the same as playing with the store computer alone).
- Hold the use key to move an extra computer. It can be moved and boxed up like other furniture (and an unopened computer box can be sold like other furniture boxes). Placed extra computers are saved with the store like other furniture.
- Only one player can use a given extra computer at a time. If somebody else is on it, you get the game's "in use by another player" message. A second extra computer, or the store computer, is always free for you.

## Settings

`BepInEx/config/oscar.extracomputers.cfg` (also editable in-game with BepInExConfigManager):

| Section | Setting | Default | Meaning |
|---|---|---|---|
| General | `Enabled` | `true` | Master switch, applied immediately. While it's off, the computer leaves the shop and placed computers stay as furniture but can't be used. If the game is **started** with it off, saved computers and computer boxes can't be loaded (see "Removing or disabling the mod"). |
| ExtraComputers | `Price` | `500` | Price of one computer in the furniture shop (0–100000). The **host's** value is used by everybody: a guest with the mod sees and pays the host's price, whatever their own setting says. |
| Multiplayer | `ProtectUnmoddedPlayers` | `true` | While any other player in the room doesn't have this mod, the computer is hidden in the shop (on every modded client), and the host refuses to buy it or to open computer boxes. See below. |
| Diagnostics | `VerboseLogging` | `true` | Logs how the computer furniture is built and each use (tag `[ExtraComputers]` in `BepInEx/LogOutput.log`). |

## Multiplayer

**Everyone in the session needs the mod**, the host and every guest. The computer furniture only exists in games where the mod is installed. A player without it can't load computers and gets errors from computer boxes.

**If a player without the mod is in the room** (host side, `ProtectUnmoddedPlayers` on):
- The computer disappears from the shop, a purchase with a computer in the cart is refused, and a computer box stays closed, whether the host or a guest tries to open it (the guest's own game refuses too, so the box stays in their hands). **There is no on-screen message for a refusal**: the purchase or the box simply doesn't react (a guest may only feel the click). The host's `LogOutput.log` says what was refused and names the player (tag `[ExtraComputers]`). Everything works again as soon as every player has the mod.
- This can't undo what already exists. **Before a player without the mod joins**, the host should unbox and place (or throw away) all computer boxes, and take computers out of the cart. Computers that are already placed in the store, and the store's save, still reach the newcomer: expect error messages in their log (and in the host's), and the computers won't be visible to them.

## Compatibility and notes

- While you use an extra computer, the store computer's monitor looks blank **on your own screen only**, because your screen has been borrowed. Other players see it normally.
- **Removing or disabling the mod** (also starting the game with `Enabled` off): first unbox and place or sell every computer box, remove computers from the cart, and box up or accept losing the placed computers. Saved computer boxes (unopened, in storage, or in a pending delivery) and placed computers (furniture id 917001) can't be loaded without the mod: the game logs "Can't spawn furniture with id: 917001" for placed ones, and the boxes may raise errors while the save loads. Treat anything left as lost. Turning `Enabled` off while the game is running removes the computer from the shop and makes placed computers unusable; a computer box bought earlier may not unbox correctly until the mod is turned on again, so turn it back on before opening one.
- The shop name is English only ("Computer").
- Portable Computer Mod also borrows the computer screen. Don't open it while you're using an extra computer.
