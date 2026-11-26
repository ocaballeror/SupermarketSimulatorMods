# Extra Computers

Adds a **Computer** to the furniture shop ($1000). Buy as many as you like. Each one arrives in a furniture box with a computer icon on it. Unbox it and place it anywhere, like any other furniture. It works like the store's own computer: shop, pricing, hiring, management, everything.

- **Every computer has its own screen state.** Each computer remembers which apps and tabs you left open on it; a computer you haven't used yet starts on the desktop. (Scroll positions, search text and dropdown choices are shared between computers. The cart, orders and money are shared, as always.)
- **Every player has their own screen.** Each player sees only what they do on the computer they're using. Two players can use two computers at the same time, or one player can use an extra computer while another uses the store computer.
- Changes (prices, orders, hires, ...) are shared exactly like changes made on the store computer. If two players have the same page open, one of them may see an older view until they reopen that page (the same as playing with the store computer alone).
- Hold the use key to move an extra computer. It can be moved and boxed up like other furniture (and an unopened computer box can be sold like other furniture boxes). Placed extra computers are saved with the store like other furniture.
- Everybody in the session must run the same version of the mod.
- The price is the same for everybody: a guest with the mod sees and pays the host's price.
- Only one player can use a given extra computer at a time. If somebody else is on it, you get the game's "in use by another player" message. A second extra computer, or the store computer, is always free for you.

## Settings

`BepInEx/config/flamboyanthopper.extracomputers.cfg` (also editable in-game with BepInExConfigManager):

| Section | Setting | Default | Meaning |
|---|---|---|---|
| General | `Enabled` | `true` | Enable the mod, applied immediately (if you turn it on after loading a store with it off, the shop entry appears from the next store load). While it's off, the computer leaves the shop and placed computers stay as furniture but can't be used. **Don't load a store while it's off** (including starting the game with it off) unless you've boxed up every computer first: see "Removing or disabling the mod". |
| Multiplayer | `ProtectUnmoddedPlayers` | `true` | While any other player in the room doesn't have this mod, the computer is hidden in the shop, and the host refuses to buy it or to open computer boxes. The host's setting is what counts; a guest with the mod also applies its own setting to its own shop and its own box clicks. See below. |
| Diagnostics | `VerboseLogging` | `false` | Enable verbose logging for debugging purposes (tag `[ExtraComputers]` in `BepInEx/LogOutput.log`). |

## Multiplayer

**Everyone in the session needs the mod**, the host and every guest. The computer furniture only exists in games where the mod is installed. A player without it can't load computers and gets errors from computer boxes.

**If a player without the mod is in the room** (host side, `ProtectUnmoddedPlayers` on):
- The computer disappears from the shop, a purchase with a computer in the cart is refused, and a computer box stays closed, whether the host or a guest tries to open it (the guest's own game refuses too, so the box stays in their hands). **There is no on-screen message for a refusal**: the purchase or the box simply doesn't react (a guest may only feel the click). The host's `LogOutput.log` says what was refused and names the player (tag `[ExtraComputers]`). Everything works again as soon as every player has the mod.
- This can't undo what already exists. **Before a player without the mod joins**, the host should unbox and place (or throw away) all computer boxes, and take computers out of the cart. Computers that are already placed in the store, and the store's save, still reach the newcomer: expect error messages in their log (and in the host's), and the computers won't be visible to them.

## Compatibility and notes

- An extra computer's monitor always looks blank to everyone who isn't using it, including other players watching you work on it: there is only one computer screen per game, and it is shown on the computer you're using, on your own screen only.
- While you use an extra computer, the store computer's monitor looks blank **on your own screen only**, because your screen has been borrowed. Other players see it normally.
- **Removing or disabling the mod** (removing it, starting the game with `Enabled` off, or turning it off and then loading a store): first **box up every placed computer**, then sell or throw away all computer boxes (unopened, in storage, or in a pending delivery) and remove computers from the cart. This matters: without the mod, a placed computer (furniture id 917001) can't be loaded. The game logs "Can't spawn furniture with id: 917001" (or "Furniture couldn't find : 917001") and stops loading furniture at that point. The mod keeps placed computers at the end of the saved furniture list, so normally only the computers themselves are missing for that session (they are kept in the save and return when the mod does), but a store saved without the mod after placing furniture can still lose items that come after a computer. Saved computer boxes may raise errors while the save loads; treat them as lost. Turning `Enabled` off while the game is running is fine for the current store: the computer leaves the shop and placed computers can't be used (if you're on one, you're taken off it). A computer box bought earlier may not unbox correctly until the mod is turned on again, so turn it back on before opening one.
- If the mod is installed and enabled but can't build the computer furniture (next point), placed computers in a loaded save are skipped for that session but kept in the save, and the rest of the store loads normally. They come back once the mod works again.
- The shop name is English only ("Computer").
- The game's furniture pictures don't include a computer, so the shop and the box use the game's flat computer symbol. If that isn't available, the mod renders a picture from the store computer's 3D model when a store loads, and if that fails too, a simple drawn monitor is shown instead.
- If the mod can't set up the computer furniture (the log says `Building the computer furniture failed`), the computer is hidden in the shop, buying it is refused and computer boxes stay closed, so you can't get stuck holding nothing. In multiplayer the other players treat that player as not having the mod. Send that log line to the author.
- Portable Computer Mod also borrows the computer screen. Don't open it while you're using an extra computer.
