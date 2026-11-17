# Clear Tags

Makes the product tags easier to read.

- **Bigger tags on boxes.** The small tag with the product icon on every box (and the icon tag on furniture boxes) is drawn **2x bigger** by default. The factor is a setting.
- **Hover text.** Put your crosshair on a tag and a small label under it shows the **product name**, the **quantity**, and the **price of the box**.
  - **Box tag** (also boxes in racks, and the box in your hands): quantity is the items inside out of the box capacity (for example `12 / 24`).
  - **Shelf tag** (the product label on a shelf slot): quantity is the items on that slot out of its capacity.
  - **Rack tag** (the label on a rack slot): quantity is the items in all boxes in that slot and how many boxes there are.
  - **Vending machine tag**: items on that slot.
  - **Furniture box tag**: the name of the furniture inside and its price (what the furniture shop charges); no quantity.
  - **Box price** is what one full box of that product costs to buy at today's market price (the wholesale price), shown for every kind of tag.

Only the tags on boxes and furniture boxes get bigger. The label icons on shelves, racks and vending machines are left exactly as the game draws them; they only get the hover text.

## Settings

`BepInEx/config/oscar.cleartags.cfg`, or in-game in the mod settings:

| Section | Setting | Default | What it does |
|---|---|---|---|
| General | `Enabled` | `true` | Master switch. Takes effect right away: tags go back to normal size and the hover text disappears. |
| Tags | `ScaleMultiplier` | `2` | A number you type in: 1 = normal size, 2 = twice as big, 1.5 is fine too, from 0.5 to 5. Applies at once, also to boxes already in the store. If your PC uses a decimal comma, type `1,5`. |
| Hover | `ShowHoverText` | `true` | Show the hover text. |
| Hover | `TextSize` | `18` | Font size of the hover text, 8 to 48 (scales with your screen resolution). |
| Diagnostics | `VerboseLogging` | `true` | Logs what the mod does (`[ClearTags]` lines in `BepInEx/LogOutput.log`). |

The words in the hover text ("Quantity", "Box price") are English whatever the game language; product names follow the game language.

## Who needs it (multiplayer)

**Only the player who wants the bigger tags and the hover text.** Everything is local: it changes how boxes look on your screen and draws a label on your screen. Nothing is sent over the network and no game data changes. The host and the other players can be unmodded, and every player who installs it gets their own settings.

## Install

Copy `ClearTags.dll` into `BepInEx/plugins/` (the folder itself, not a subfolder). Requires BepInEx 6 (IL2CPP).

## Compatibility

- Doesn't change any game logic, so it should work with other mods. A mod that resizes the same box tag objects could conflict.
- The hover text is drawn on top of the screen with Unity's built-in GUI, so it also shows on top of other overlays.
