# EmployeeClothes: notes for agents

Player-facing info is in [README.md](README.md). Shared rules and game internals are in the root [CLAUDE.md](../CLAUDE.md). The full mechanism (how the game builds employees, how the recolouring works, the UI, multiplayer) is the doc comment at the top of `src/Plugin.cs`; this file summarises it, lists the game knowledge only this mod uses, and has the test steps.

## Status

v1.0.0 (TODO Improvement #15): first implementation, written without being able to run the game. **Nothing has been verified in-game.** Every part below marked "unverified" can fail; the log lines in the test steps say which.

## Files

| File | What |
|---|---|
| `Plugin.cs` | Config, patches, the mechanism comment |
| `ClothesStore.cs` | Colour per employee type, save-file persistence, Photon room property (host publishes, guests read) |
| `Recolor.cs` | Shade maps, material copies, applying a colour to a renderer / model |
| `Employees.cs` | Finds live employees (`FindObjectsOfType` per class) and sweeps them every 0.4 s; the `ClothesDriver` MonoBehaviour |
| `Preview.cs` | Camera + RenderTexture + model clones |
| `TabUi.cs` | The Customization card and the panel |
| `Patches.cs` | `CustomizationTab.OnEnable` (inject card), `IDManager.Awake` (new scene: reset caches) |

## Game reference: employees and their clothes

Verified with UnityPy on the shipped assets (`tools/unity_tree.py`, 2026-11-17).
- **Prefabs** (`sharedassets2` plain, `resources.assets` networked copy with `PhotonView`): `Restocker`, `Restocker_02`..`_06` (all `Clerk` + `ClerkAnimationController`), `Cashier`, `Woman_Cashier`, `CustomerHelper`, `SecurityGuard`, `Janitor`, `Courier`, `Baker`, `IceCreamHelper_01`. The HR ScriptableObjects (`IDManager.m_Restockers` ... `m_Couriers`, field `<X>Prefab`, ice cream helper `EmployeePrefab`) point at the plain ones. Each type has several SOs/prefabs (6 restockers, 2 cashiers).
- **Layout:** root (layer 6, scripts, `Highlightable`) > `Animated Model` (Animator with the controller + `LODGroup`; Janitor: `FemaleScientistPFB`, Courier: `Courier`, IceCream: `Ice_Cream_Helper`) > mesh object with `LOD0/1/2` `SkinnedMeshRenderer`s (or a single one) > bones `mixamorig:*`. A child Animator of the same name has no controller. Also `BoostIndicator`, `InfoDisplay` (name tag TextMeshPro, layer 11), SecurityGuard `Hover Info`. The preview clones only the top Animator's GameObject.
- **Materials:** all URP/Lit, `_BaseColor` white (a few 0.8 or 0.74), colour comes only from the `_BaseMap` texture (== `_MainTex`). Textures aren't readable (`isReadable` false), 512 to 2048, DXT / crunched; some have streaming mipmaps. No runtime customisation of employees exists (`CharacterCustomize`, `BmanCustomize`, `UrbanGuy_*`, `GiantMan_*`, `BWoman*`, `BigGuy_*` are another character kit used by customers or unused), and `EmployeeTypeColorsSO` is only the HR card accent.
- **Names lie:** clothes are `BusinessMan_Shirt/Pants/Shoes/Jacket/Waistcoat/Handkerchief_MAT_V*`, `Female_Shirt/Pants/Shoes_V*`, `Female_Scientist_TOP/BTM_MAT`, `Dress_Mat`, `Bakery_Dres_Mat`, `Bakery_Pants_Mat`, `Ice_Cream_Helper_Dress_Mat`, `Woman_02_Shoes_Mat`, `URP_UrbanGuy_Trousers_MAT_V*`, `Hat_Mat`, `Helmet_Mat`, `BouncerBody_MAT` (guard suit), and the restocker's T-shirt/collar are materials named **`Hair_Mat`** with textures `Low_Hair_Tshirt_Mat_*` / `Low_Hair_Yaka_Mat_*`. So the classifier looks at "material name | base texture name". `BritneyBodyMAT` (Restocker_05/06) is an atlas with jeans, top, hands and face together: excluded. Not clothes: faces `Man_Face*`/`Female_HeadE`/`*_Skin*`/`Female_Body`, hair, eyes, eyelashes, glasses, `blinn1`/`Cashi`/`Janitor_Ticket_Mat` (name badge texture `loww_standardSurface1_*`), `M_Mop`.
- `Highlightable` doesn't touch materials (checked its dump: no Renderer/Material calls), so swapping `sharedMaterials` isn't undone by hover highlighting. Not verified at runtime.

## Game reference: Customization tab and the colour picker

- Scene `level3`: `---GAME---/Computer &&/Screen/Management App (695x431, scale 0.001)/Tabs (width 0 x 367)/CustomizationTab` (`WindowTab`, `CustomizationTab`, `GamepadSelectableParent`; all objects are on layer 0). Children: `BG`, `Locked`, `Unlocked` > `Sections Scroll View` > `Viewport` > `Content` (`GridLayoutGroup`, cells 300x120, two columns) with the cards `Store Name UI` (`ChangeNameItem`), `Enterence Position UI` (`DoorPlaceItem`), `Enterence Apperence UI` (`EntranceChangeItem`). Card children: `BG`, `Title` (TMP + `LocalizeStringEvent`, child `Description`), `Cost Info` (`Cost`, `Cost Text`), `Purcase Button` (`Image`, `Button`, `MouseClickSFX`, child `Text (TMP)`). `UpdateUI` toggles `Locked`/`Unlocked` by the store upgrade level (`NetworkSectionManager.GetStoreUpgradeLevel`).
- `ChangeNameItem.Start` finds the `DoorSwitchCamera`, adds the button listener (`SwitchCameraToDoor` -> `DoorSwitchCamera.Initialize(price)`) and keeps the button's `interactable` = `MoneyManager.HasMoney(m_Price)`. The store name UI itself is `NameChangeCanvas` (inactive, `PopUps`, `ChangeNameOverlay` singleton, screen-space) with two colour pickers (`Text Color Picker`/`Background Color Picker` > `Color picker` 183x307).
- **Charge pattern** (`ChangeNameOverlay.ApproveStoreName`): offline/host `MoneyManager.MoneyTransition(-price, UPGRADE_COSTS (3), true)`; a guest calls `NetworkMoneyManager.OnMoneyTransition_MasterRequest(-price, 3, true)`. (The enum also has `CUSTOMIZATION`; the game uses 3 here.)
- **`ColorPicker`** (`Image` + `ColorPicker`, shader `UI/ColorPicker`): `Initialize(Color)` takes the Image and RectTransform, sets HSV, and if `image.material.shader.name != "UI/ColorPicker"` makes `generatedMaterial = new Material(colorPickerShader)` (hideFlags 61) and assigns it to the image; `ApplyColor` sets the shader vector and invokes `onColorChanged(Color)`, so `Initialize` fires the event too. `OnDestroy` destroys `generatedMaterial`, so a copy must null that field first (done). `Update` feeds the rect aspect to the shader. Pointer handling uses `eventData.pressEventCamera`, so it works on a world-space canvas. `get_color` converts HSV.
- Other game uses of room properties: `Money`, `roomStartTime`, `RackCount` (each listener only reads its own key).

## How it works (summary)

- **Recolour:** per source texture a "shade map" (luminance relative to the texture's mean, `shade = 1 + (L - mean) * 1.4` clamped 0.45..1.45, stored as grey / 1.45, alpha kept), made through a `RenderTexture` blit (sRGB) + `ReadPixels`, max 1024, DXT5 compressed. Per (original material, slot) a `new Material(orig)` with the shade map in `_BaseMap`/`_MainTex` and `_BaseColor`/`_Color` = colour * 1.45 * garment shade. Slots: 0..7 = `HrEmployeeKind`, 100 = preview. `ApplyToRenderer` swaps `sharedMaterials` entries; null colour swaps the original back (`OriginalOf` maps copy -> original). Garment shade (ShadedGarments): shoes 0.5, pants/trousers/BTM 0.7, waistcoat 0.8.
- **Why not a tint:** the textures are already coloured (blue shirts, black suits); multiplying `_BaseColor` can only darken. A prototype of the shade formula on the real textures (Python, 2026-11-17) gave clean, evenly coloured garments with folds and logos kept.
- **Sweeper:** `ClothesDriver.Update` every 0.4 s: `ClothesStore.Tick` (load/publish/read), `Employees.Sweep` (all 8 employee classes via `Object.FindObjectsOfType`, only while a colour is set or `Recolor.NeedsRestore`), `TabUi.Tick` (card enabled state, fallback injection every 3 s, closes the panel if the screen/tab got hidden). 25 ms budget per sweep for shade maps; a texture with streaming mips waits up to 2.5 s for mip 0.
- **Card:** the store-name card is instantiated under an **inactive holder** (so no `Awake` runs), `ChangeNameItem`, `LocalizeStringEvent` and `LocalizeAutoSize` are destroyed, button listeners (runtime and persistent) removed, then re-parented into `Content`. Panel = child of the tab over a clone of the tab's `BG`; texts/buttons are clones of the card's `Title`/`Purcase Button`; picker cloned from the store-name colour picker; 12 swatches (fallback if the picker can't work); `Apply to all` is a button with an "[x]" label.
- **Preview:** `EmployeeClothes.PreviewStage` at y = -5000 on an unnamed layer (like ExtraComputers' icon), camera orthographic into a 1024x512 `RenderTexture` shown by a `RawImage`; two point lights (URP ignores light culling masks, so no directional light: it would light the store); models = `Object.Instantiate(<top Animator's GameObject>)` of every prefab of the type, in a row (1.15 m apart), rotating 24 deg/s, `LODGroup.ForceLOD(0)`, root motion off.
- **Multiplayer:** host/solo only edits. Host: colours -> `oscar.employeeclothes.saves.txt` per save file name (`SaveManager.CurrentSaveFilePath`) and the room property `oscar.employeeclothes` = `Restocker=ff8800;Cashier=2255aa`. Guests (`PhotonNetwork.IsConnectedAndSlave`): read the room property every 0.4 s and sweep; never write. The card is disabled for guests ("Host only"). Unmodded guests see original clothes. Money goes through `MoneyManager.MoneyTransition` on the host, which the game already syncs.

## Design decisions the user may want to change

- Charged when the card is opened, not when applying, matching "$500 to access each time"; closing without applying still costs.
- One colour per type; "Apply to all" writes the same colour to all 8 types, the types stay individually editable afterwards.
- Colourise (one flat colour with the outfit's shading) instead of a hue shift; trousers/shoes darker by default (`ShadedGarments`).
- Guests can't change colours (no custom RPC; a guest's choice would also need the host to be charged).
- Persistence in a text file by save file name; a new game in an old slot inherits the slot's colours.

## Pitfalls / open questions (unverified)

- The preview: URP camera rendering to a RenderTexture from a script-made camera (no `UniversalAdditionalCameraData`), lighting with two point lights at 30 (setting `PreviewLight`), whether the Animator controllers show an idle pose or a T-pose, whether `Renderer.bounds` is usable on a fresh skinned clone (otherwise a 1.8 m person is assumed), whether clone `Awake`s misbehave (`RigBuilder`, `jiggleBone`).
- The shade map copy: sRGB RenderTexture round trip (if previews/clothes look too dark or too light, the gamma assumption is wrong), `Il2CppStructArray` data at `+0x20` (verified against the indexer on the first and last element, falls back to the slow indexer; logged as `fast copy`), `Texture2D.Compress`.
- The card: whether the cloned card looks right in the 2-column grid (the 4th slot is empty in vanilla), whether `GamepadUISelectable` on the clone causes trouble with a gamepad, the panel layout in the 695x367 tab (the picker keeps the original 183:307 aspect), whether the picker's shader exists (`UI/ColorPicker`; the swatches are the fallback).
- `Il2CppSystem.Action<Color>` for the picker event: if hooking fails the panel polls `ColorPicker.color` per frame (logged).
- A mod that swaps employee materials itself (or `MoreEmployees` adding new models with other material names) isn't handled; new clothes names need adding to `Recolor.ClothesRx`.
- The restockers `Restocker_05/06` keep their trousers (atlas shared with skin). A fix needs per-pixel colourising with skin protection, which doesn't fit a live tint; it could be done at Apply time on the CPU.
- Employee prefab materials aren't modified, only renderers' `sharedMaterials` on instances; all material copies and shade maps are destroyed on mod disable and on a new scene (`IDManager.Awake` patch).

## First in-game test (send these back)

Log tag `[EmployeeClothes]` in `BepInEx/LogOutput.log` (host, and the guest if it has the mod). Debug lines are on by default (`VerboseLogging`).
1. Start: `Employee Clothes 1.0.0 loaded (enabled)` and `Patches applied (2/2)`.
2. Load a save with a few employees, open the computer, Customization tab: the 4th card "Employee Clothes" (`Card added to the Customization tab (colour picker cloned)`). A screenshot of the tab helps (card layout).
3. Press Open: $500 leaves the money (`Charged $500 ...`), the panel opens. Check: preview shows the models (`Preview: N model(s) for Restocker`, `Preview camera: ...`, `Preview is running ...`; a screenshot please: too dark? T-pose? nothing?), the arrows switch type, the picker works (drag, preview recolours), the swatches work.
4. Apply: employees of that type change colour in the store (`Applied #... to Restocker`, `First shade map: ...`, `Restocker: N employee(s) in the scene, colour #...`). Look at shirts, trousers, shoes, hats and at the LODs (walk away). Tick Apply to all, apply, switch type, apply another colour, check that one type differs. Reset.
5. Save, quit, reload: the colours are back (`Colours for save '<file>': ...`). Hire a new employee: it comes in with the colour.
6. Toggle `Enabled` off in the config manager: original clothes back and the card gone; on again: back.
7. Multiplayer: host applies a colour: `Published the colours to the room`. Modded guest: `Colours from the host: ...` and recoloured employees; the card shows "Host only"; a late joiner gets the colours. Unmodded guest: original clothes, nothing else different.
8. Anything with `failed`/`Error` in the log, and any performance hitch (the first shade maps take a moment).

## Review notes

None yet (first version).
