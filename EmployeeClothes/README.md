# Employee Clothes

Choose the colour of your employees' clothes. A new **Employee Clothes** card appears in the computer's **Customization** tab, next to the store name and entrance options. It costs **$500 to open**. Once it is open you can try as many colours as you like and change them as often as you want; closing it (and opening it again later) costs again.

The employees keep their models, hair, faces and outfits. Only their clothes (shirts, trousers, shoes, jackets, dresses, hats...) change colour, with the folds and seams of the original outfit still showing.

## How to use it

1. At the computer, open the **Customization** tab and press **Open** on the Employee Clothes card. You pay the fee, and the screen opens (the card is greyed out when you can't afford it).
2. The screen shows a **preview** of all the models of one employee type (for example the six restockers). Use the **< >** arrows to switch between the eight types: Restocker, Cashier, Customer helper, Ice cream helper, Baker, Janitor, Security guard, Courier.
3. Pick a colour with the colour picker (the same one used for the store name) or one of the swatches under it. The preview changes while you pick.
4. Press **Apply**. Every employee of that type changes colour straight away, including employees you hire later, and the choice is remembered when you save and reload.
5. Switch on **Apply to all** to give every type the colour you picked. The switch turns itself off after Apply and when you change type, so you can then pick another type and apply a different colour to just that one. Apply, Apply to all and Reset are all free while the screen is open.
6. **Reset this type** brings back the original clothes (for all types if Apply to all is on). **Done** closes the screen. Switching to another tab of the computer or leaving the computer also closes it.

## Settings

`BepInEx/config/oscar.employeeclothes.cfg`, or in-game in the mod settings:

| Section | Setting | Default | What it does |
|---|---|---|---|
| General | `Enabled` | `true` | Master switch. Off puts the original clothes back and hides the card, right away. |
| Customization | `Price` | `500` | What it costs to open the screen. Type a number; 0 is free. |
| Appearance | `ShadedGarments` | `true` | Trousers, shoes and waistcoats are a darker shade of the chosen colour (70%, 50%, 80%) so an outfit isn't one flat colour. Off: every garment is exactly the chosen colour. |
| Appearance | `PreviewLight` | `30` | Brightness of the lights in the model preview (1 to 300). Change it if the preview is too dark or washed out. |
| Multiplayer | `SyncToGuests` | `true` | Host only: share the colours with guests who also have this mod. |
| Diagnostics | `VerboseLogging` | `true` | Writes what the mod does to `BepInEx/LogOutput.log` (lines tagged `[EmployeeClothes]`). On while the mod is new. |

## Multiplayer

- **Host (or solo): required.** Only the host can open the card and pick colours (it shows "Host only" for guests) and pays the fee.
- **Guests: optional.** A guest **with** the mod sees the host's colours on the employees (and when joining later). A guest **without** the mod simply sees the original clothes; nothing breaks.
- The colours are shared through a Photon room property, which the game ignores.

## Where the colours are kept

The host's colours are saved per save slot in `BepInEx/config/oscar.employeeclothes.saves.txt` (one line per save file) every time you press Apply or Reset, not with the game's own save. The $500 is taken when you open the screen and belongs to the game's money, so if you quit without saving the game, the money goes back to the last save but the colours stay. If you start a new game in a slot that already had colours, press Reset for each type (or delete that line).

## Good to know

- **Not recoloured:** two of the restockers (the last two in the preview) have their trousers painted on the same texture as their skin, and the mod can't tell the two apart. Their top, dress and shoes change, their trousers don't.
- **Gamepad:** the new card may not be reachable with a gamepad, and the panel is made for the mouse. Use the mouse for now.
- The preview and the first recolouring of each outfit build their textures once, so the first time you open the screen or apply a colour can take a moment.
- Compatible with the other mods in this collection. A mod that replaces employee materials itself may fight with this one.

## Installing

Copy `EmployeeClothes.dll` into `BepInEx/plugins/` (the folder itself, no subfolder). See the repository README.
