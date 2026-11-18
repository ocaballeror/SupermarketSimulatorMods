# Employee Clothes

Choose the color of your employees' clothes. A new **Employee Clothes** card appears in the computer's **Customization** tab, next to the store name and entrance options. It costs **$500 to open**. Once it is open you can try as many colors as you like and change them as often as you want; closing it (and opening it again later) costs again.

The employees keep their models, hair, faces and outfits. Only their clothes (shirts, trousers, jackets, dresses, hats, vests...) change color; shoes, hair and skin stay as they are, with the folds and seams of the original outfit still showing.

## How to use it

1. At the computer, open the **Customization** tab and press **Open** on the Employee Clothes card. You pay the fee, and the screen opens (the card is greyed out when you can't afford it).
2. The screen shows a **preview** of all the models of one employee type (for example the six restockers). Use the **< >** arrows to switch between the types: Restocker, Cashier, Customer helper, Ice cream helper, Baker, Janitor, Security guard, Courier. Like the Hiring tab, it only lists the types you can use: the ice cream helper and the baker need the Ice Cream / Bakery DLC, and a type is hidden while you have nobody of that type hired and can't hire any yet (for example the baker without ovens).
3. Pick a color with the color picker (the same one used for the store name) or one of the swatches under it. The preview changes while you pick.
4. Press **Apply**. Every employee of that type changes color straight away, including employees you hire later, and the choice is remembered when you save and reload.
5. Switch on **Apply to all** to give every type the color you picked. The switch stays on while you change type, Apply or Reset, and the game remembers it the next time you open the screen (even after a restart); switch it off to edit one type at a time. Apply, Apply to all and Reset are all free while the screen is open.
6. **Reset this type** brings back the original clothes (all types, while Apply to all is on). **Done** closes the screen. Switching to another tab of the computer or leaving the computer also closes it.

## Settings

`BepInEx/config/oscar.employeeclothes.cfg`, or in-game in the mod settings:

| Section | Setting | Default | What it does |
|---|---|---|---|
| General | `Enabled` | `true` | Master switch. Off puts the original clothes back and hides the card, right away. |
| Customization | `Price` | `500` | What it costs to open the screen. Type a number; 0 is free. |
| Customization | `ApplyToAll` | `false` | State of the "Apply to all" switch. It is saved here when you flip it. While on, Apply and Reset cover every type, including types that aren't unlocked yet. |
| Appearance | `ShadedGarments` | `true` | Trousers and waistcoats are a darker shade of the chosen color (70%, 80%) so an outfit isn't one flat color. Off: every garment is exactly the chosen color. |
| Appearance | `PreviewLight` | `30` | Brightness of the lights in the model preview (1 to 300). Change it if the preview is too dark or washed out. |
| Multiplayer | `SyncToGuests` | `true` | Host only: share the colors with guests who also have this mod. |
| Diagnostics | `VerboseLogging` | `true` | Writes what the mod does to `BepInEx/LogOutput.log` (lines tagged `[EmployeeClothes]`). On while the mod is new. |

## Multiplayer

- **Host (or solo): required.** Only the host can open the card and pick colors (it shows "Host only" for guests) and pays the fee.
- **Guests: optional.** A guest **with** the mod sees the host's colors on the employees (and when joining later). A guest **without** the mod simply sees the original clothes; nothing breaks.
- The colors are shared through a Photon room property, which the game ignores.

## Where the colors are kept

The host's colors are saved per save slot in `BepInEx/config/oscar.employeeclothes.saves.txt` (one line per save file) every time you press Apply or Reset, not with the game's own save. The $500 is taken when you open the screen and belongs to the game's money, so if you quit without saving the game, the money goes back to the last save but the colors stay. If you start a new game in a slot that already had colors, press Reset for each type (or delete that line).

## Good to know

- **Not recolored:** shoes and boots, hair, skin and faces, eyes, glasses and the name badges keep their original colors. On the outfits that share one texture with such parts (the two women restockers' jeans, the baker's jeans, the guard's trousers and torso), only the clothes themselves change.
- **Gamepad:** the new card may not be reachable with a gamepad, and the panel is made for the mouse. Use the mouse for now.
- The preview and the first recoloring of each outfit build their textures once, so the first time you open the screen or apply a color can take a moment.
- Compatible with the other mods in this collection. A mod that replaces employee materials itself may fight with this one.

## Installing

Copy `EmployeeClothes.dll` into `BepInEx/plugins/` (the folder itself, no subfolder). See the repository README.
