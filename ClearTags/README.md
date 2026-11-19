# Clear Tags

Makes the product tags easier to read.

- **Bigger tags on boxes.** The small tag with the product icon on every box (and the icon tag on furniture boxes) is drawn **2x bigger** by default. The factor is a setting. The tag always stays on the box lid instead of hanging over the edge: it is moved as needed, and on small boxes (or with a big factor) it is drawn smaller than requested, never smaller than the original.
- **Hover panel.** Put your crosshair on a tag and a small panel in the game's own menu style (dark rounded panel, the game's font, product icon at the left) appears under the crosshair.
  - **Box tag** (also boxes in racks, and the box in your hands): the product icon, **brand and name** (for example `Kilemol Cleaner`), the **quantity** (items inside out of the box capacity, for example `12 / 24`) and the **box price**.
  - **Shelf, rack and vending machine tags**: the product icon and brand and name only. This also works when the crosshair is on the **price tag** of a shelf or vending machine item (racks have no price tag).
  - **Furniture box tag**: the furniture's icon, name and price (what the furniture shop charges). A paint-can box shows its name only.
  - **Box price** is what one full box of that product costs to buy at today's market price (the wholesale price).

Only the tags on boxes and furniture boxes get bigger. The label icons on shelves, racks and vending machines are left exactly as the game draws them; they only get the hover panel.

## Settings

`BepInEx/config/flamboyanthopper.cleartags.cfg`, or in-game in the mod settings:

| Section | Setting | Default | What it does |
|---|---|---|---|
| General | `Enabled` | `true` | Enable the mod. Takes effect right away: tags go back to normal size and the hover panel disappears. |
| Tags | `ScaleMultiplier` | `2` | A number you type in: 1 = normal size, 2 = twice as big, 1.5 is fine too, from 0.5 to 5. On small boxes the tag is shrunk so it stays on the lid (never below the original size). Applies at once, also to boxes already in the store. If your PC uses a decimal comma, type `1,5`. |
| Hover | `ShowHoverText` | `true` | Show the hover panel. |
| Hover | `TextSize` | `30` | Text size of the hover panel, 8 to 48; the whole panel scales with it and with your screen resolution. |
| Diagnostics | `VerboseLogging` | `false` | Enable verbose logging for debugging purposes (`[ClearTags]` lines in `BepInEx/LogOutput.log`). |

The words in the hover panel ("Quantity", "Box price", "Price") are English whatever the game language; product and furniture names follow the game language. Brand names are shown as the game stores them.

## Who needs it (multiplayer)

**Only the player who wants the bigger tags and the hover text.** Everything is local: it changes how boxes look on your screen and draws a panel on your screen. Nothing is sent over the network and no game data changes. The host and the other players can be unmodded, and every player who installs it gets their own settings.

## Install

Copy `ClearTags.dll` into `BepInEx/plugins/` (the folder itself, not a subfolder). Requires BepInEx 6 (IL2CPP).

## Compatibility

- Doesn't change any game logic, so it should work with other mods. A mod that resizes the same box tag objects could conflict.
- The hover panel reuses the game's own font and panel style. If the game's UI can't be found (for example after a game update), the mod falls back to a plain dark text box.
