# EmployeeClothes: notes for agents

Player-facing info is in [README.md](README.md). Shared rules and game internals are in the root [CLAUDE.md](../CLAUDE.md). The full mechanism (how the game builds employees, how the recoloring works, the UI, multiplayer) is the doc comment at the top of `src/Plugin.cs`; this file summarises it, lists the game knowledge only this mod uses, and has the test steps.

## Status

v1.0.10 (review round 1 of Improvement #15; not verified in-game): **colors are tied to the game save.** Before, Apply/Reset wrote the slot's line at once, so "Apply, quit without saving, reload" gave the colors back for free (the money went back, the colors stayed) and a new game in a used slot inherited the old colors. Now `ClothesStore.MarkChanged()` (Apply/Reset) only marks the colors dirty; the line is written by a postfix on the private `SaveManager.Save(string saveName)` (verified in the ISIL: `Save()` -> `Save(SaveInfo)` -> `Save(string)`, the leaf that does the `ES3.StoreCachedFile`; every in-game save path ends there, name `slot_N.es3`) under that file name, only if dirty or "fresh". A postfix on `SaveManager.Clear` (called by the main menu's `Awake`, `NewGame`, `CreateLoadNewSave(_MP)`, `OnSlotPressed_MP` and `DailyStatisticsScreen.StartNewGame`) empties the colors in memory and sets "fresh", so the save that follows a new game writes "no colors" (removes the slot's line); a postfix on `SaveManager.ApplySaveData` (continue/load/load-for-MP) clears "fresh" again as a safety net (the main menu's own `Clear` comes before a load; verified in the ISIL that the existing-slot branch of `OnSlotPressed_MP` skips `Save`, and `OnNewScene` resets "fresh" at scene start anyway, so the hook is harmless but not strictly needed). Known, accepted corner cases (review round 2): `Save(string)` catches its own errors (shows the save-error message and returns normally), so the colors are still committed when the game file failed to write; and a new game started while the mod is disabled (hooks unpatched) leaves the slot's old line, which a later enabled session in that game would read. `OnNewScene` now clears the colors in memory too (no flash of the previous save's colors for up to 1 s). Other review changes: diagnostic lines ("First shade map", "Masked copy", "Preview is running", "Card added", "Colors for save", "Published", "Colors from the host") now go through `Plugin.Debug`; the renderer registry is pruned also while no color is set; the preview lights are switched off with the camera; the fallback card scan gives up when the tab layout is unexpected and skips non-scene objects; dead code removed (`ClampedFloat.cs`, `Preview.OnMaterialsReset`, `ClothesStore.Touch`, `TabUi.Injected`, the constant `SyncToGuests`); stale comments fixed. 14 patches now (was 11).

v1.0.9 ("remember the Apply to all switch between sessions"; hardening, not verified in-game): the user reported the switch is not remembered across game restarts although v1.0.7/1.0.8 store it as the global `#applyall` line. **No definite defect was found by reading the code**: the getter reads the line, `Open` copies it into `_applyAll` and `SetSlider`s the switch (no event), `OnToggled`/`OnToggleAll` write it, `Save()` keeps the line, the old->new file migration only copies when the new file doesn't exist, `ToggleSwitch.SwitchSlider` invokes `onValueChanged(newValue)` (verified in the ISIL; `OnEnable`/`SetSlider` never invoke it). What the evidence shows: the game folder still has the **v1.0.7** dll (`oscar.employeeclothes.saves.txt` from 2026-11-18 12:05, no `#applyall` line, written by an older build; the last session ran with `Enabled = false`), so a build that persists the switch was never actually exercised in-game. Changes, so that the state shown is always the state stored and a failure is visible in the log: (1) `ClothesStore.ReloadApplyToAll()` re-reads the line from the file at every `Open` (the field is only a cache, no more "read once"), (2) the setter always writes (the `_applyAll == value` shortcut is gone), reads the file back and logs a Warn `Saving the Apply to all switch did not stick ...` if the line isn't what it wrote, (3) `TabUi.SyncSwitch()` (at Apply, Reset and `EndSession`): if the game's switch shows a different value than `_applyAll` (callback missed) it adopts the switch and saves, (4) Debug lines `Apply to all read from '<file>': ... -> True|False` at every open and `Saved Apply to all: <v> in '<file>'` at every flip. Global (not per save slot), host/solo only, the old file name is still migrated.

v1.0.8: the identifiers were renamed from the `oscar.` prefix to `flamboyanthopper.` (GUID, room property key, config and saves file names). `ClothesStore.FilePath` does a one-time, fail-open migration: if `flamboyanthopper.employeeclothes.saves.txt` doesn't exist and the old `oscar.employeeclothes.saves.txt` does, the old file is copied over before the first read. The old room key is no longer read or written (it only lives for the session). Not verified in-game yet.

v1.0.7 (settings reduced, commit "simplify mod settings"): only `Enabled` and `VerboseLogging` (the cfg key `[Diagnostics] VerboseLogging` already existed, so existing cfgs keep their stored value; the default is now false for new configs) remain settings. `Price` (**1000**, was 500), `ShadedGarments` (true), `SyncToGuests` (true) and `PreviewLight` (30) are `const`s in `Plugin.cs`. `ApplyToAll` is **no longer a cfg entry**: it is `ClothesStore.ApplyToAll` (property, read lazily once, written when flipped), stored as one global line `#applyall<TAB>1` in `flamboyanthopper.employeeclothes.saves.txt` (absent = false; the `#` can't clash with a save file name, `Save()` keeps the line, older versions ignore it; not written by guests). The live cfg-follow (`OnApplyAllConfigChanged`) and `Recolor.OnShadeSettingChanged` were removed. The old notes below that say "$500" or mention these cfg entries describe earlier versions.

v1.0.6 (round 3 of the user's in-game test; v1.0.5 worked for the rest; not verified in-game yet):
- **Preview models stand still** (no spin), identity rotation facing +z (assumed from the Mixamo rig; if someone shows their back the pivot needs a 180 degree turn).
- **Type label is just the plural name** (`ClothesStore.PluralOf`): Restockers, Cashiers, Customer helpers, Ice cream helpers, Bakers, Janitors, Security guards, Couriers; no "(n/m)" and no "- N model(s)". Apply/Reset messages use the plural ("Applied to Cashiers.", "Reset Cashiers to the original clothes.").
- **Guard torso now recolored.** Cause: the 1.0.5 mask demanded `v from top < 0.64`, which cut off exactly the torso (the two torso pieces are pure black, V = 0, in the bottom third of the texture); only the trousers changed. The guard has no shirt/jacket in the mesh, "his shirt" is that torso. New rule in the classifier table; torso and trousers share one material, so they can't get different multipliers.
- **Guard preview showed only the lower half.** Cause: `FrameCamera` used `Renderer.bounds` of fresh skinned clones, which follows the bones' current pose; the guard prefab's skeleton is not standing until the Animator has run (bone world y -0.98..0.81 vs mesh -0.01..1.79; every other prefab's bones are at about 0..1.7), so his box was ~1 m too low (the logged "1.91 m" was 2 x his `m_AABB` extent). Now `Preview.MeshExtent` frames from `SkinnedMeshRenderer.sharedMesh.bounds` (bind pose) through `localToWorldMatrix` (fallback: assumed 0..1.8 m), half-height factor 0.60, and each Animator gets `Update(0f)` once after instantiate. Pitfall: don't trust `Renderer.bounds` of a fresh skinned clone. `ExtraComputers/src/IconMaker.cs` may have the same bounds problem if it frames from `Renderer.bounds`.
- Logs: `Preview camera: N model(s), mesh y a..b m above the stage floor [(ASSUMED, no usable mesh bounds)], height H m, ortho size X` (guard should read about -0.01..1.79, height 1.80); new `Preview pose check #N: '<model>' mesh y a..b, bones y c..d` ~0.7 s after Show (bones should roughly match the mesh range after the Animator ran; if the guard's bones stay around -1..0.8 his Animator doesn't stand him up).

v1.0.5 (round 2 of the user's in-game test, after v1.0.4; not verified in-game yet):
- **Empty preview, cause: layer 31.** All 32 layers are named (TagManager); the "free layer" search in `Preview.cs` fell back to 31, which is `FPSHand`. The URP renderers (Balanced, Performant, HighFidelity) have opaque/transparent layer masks `0x7FFFFFFF` (HighFidelity also drops layer 11), so layer 31 is never drawn: the preview was just the camera clear color. The stage is now on layer 4 (`Water`, unused, in every mask); the models are 5000 m below the store. Clear color is a mid-light slate blue, `updateWhenOffscreen` is on for the clones. **`ExtraComputers/src/IconMaker.cs` has the same `FreeLayer()` and probably the same bug** (its icon render likely comes out empty and falls back to the drawn icon); not touched. One-shot diagnostics ~0.7 s after each Show (max 4 per run, `VerboseLogging`): `Preview render check #N: ...` ("NOTHING DRAWN" or "something was drawn") and `Preview scene: ...` (mask, layer, camera, model/renderer counts, bounds, first material/shader).
- **Transparent panel:** the tab's cloned `BG` is replaced by an opaque `Image` (cyan taken from the screenshot), panel texts are forced opaque white, and `tab.m_UnlockedUI` (the cards) is hidden while the panel is open and restored in `EndSession` (every end path), only if it was active before. Safety net: `TabUi.EnsureCardsVisible` at the start of the `CustomizationTab.OnEnable` patch restores them (or calls the tab's `UpdateUI`, logging `Customization tab: neither Locked nor Unlocked is active...`) whenever no session is open.
- **Apply to all persists** (since v1.0.7 in the saves file as the global `#applyall` line, `ClothesStore.ApplyToAll`; before that the cfg entry `[Customization] ApplyToAll`). Set on open with `SetSlider` (no event), written when flipped (text-button fallback too). No longer unticks after Apply, Reset or a type switch (supersedes the v1.0.1/v1.0.2 notes). With it on: Apply writes all 8 kinds (hidden ones too, so they have the color once unlocked), Reset resets all 8; the buttons read "Apply to all types" / "Reset all types".
- **Only unlocked types are offered.** A type is shown if (a) the DLC check passes - `HiringTab.RefreshAvailability` lists IceCreamHelper only with the static `DLCTestController.IceCreamDLC` and Baker only with `BakeryDLC` - AND (b) an employee of it is hired (`EmployeeManager.IsEmployeeHired(kind, id)` over `HrHiring.SlotIds(kind)`) or some slot has `HrHiring.MissingCount(kind, id) == 0` (store level, objectives such as ovens/bakery displays for the baker, checkouts for the cashier... from `HrHiring.Gates`; money is not part of it). The reviewer pointed out that the user's "not unlocked yet (e.g. baker)" may mean unmet hiring requirements rather than the DLC, so both are used. Fail open per kind (exception, no `EmployeeManager`, unreadable/empty slots); an empty list falls back to all 8. `TabUi.RefreshKinds` runs at every Open and logs `Employee types in the panel (N/8): <kind>: shown|hidden (<reason>)`. Saved colors of hidden types are kept and still swept; Apply to all still writes all 8. Not verified in-game which rule matches what the user sees.
- **Guard and janitor not recolored, cause: time budget starvation.** `Sweep` started the 25 ms `Recolor` stopwatch before the 8 `FindObjectsByType` scans; those scans ate the budget, and `GetNeutral`/`GetAtlas` postpone builds once it is over, so the types late in the scan order (guard, janitor, courier, baker, ice cream) were postponed on every pass (the log shows only cashier/restocker maps built before the panel's own preview, which has its own budget, built the rest). Nothing in the native dumps touches materials of these two types and their hierarchy is normal. Fix: `Recolor.OverBudget` ignores the stopwatch until at least one build happened in the pass (`_built`, reset in `BeginBudget`).
- **Bake gamma bug:** `Bake` multiplied in linear space with the color left as sRGB (red `#d91f1f` came out orange-ish and washed out). It now writes what the shade-map material shows, `byte = ShadeV * (color * 1.45 * garment)` in sRGB, clamped; the LUTs are gone. Assumes `Material.SetColor` on a Color property converts sRGB to linear (normal Unity behaviour). Baked garments (Restocker_05/06 jeans, baker pants, Low_Dress knit, janitor top, guard suit) look different from 1.0.4 and should now match the shade-map ones.
- **Performance (cause: the same scans).** 8 `FindObjectsByType(Include)` every 0.4 s = 20 scene scans/s, estimated 60-100 ms/s main thread in 25-40 ms hitches. Now event-driven: `EmployeeTracker.cs` has Harmony postfixes on `Start` of Cashier, Clerk, CustomerHelper, SecurityGuard, Janitor, Courier, Baker, IceCreamHelper (all real native methods, not inlined; picked up by the attribute scan, so patches are now 11/11) that register the employee. A pass runs only when a new employee registered, `ClothesStore.Version` changed, a shade map is still postponed (every 0.15 s) or an employee's own verify timer is due (30 s after it registered, so staggered; at most 3 per pass, checked once per second: rescan renderers, re-assert copies; `PruneRegistry` on its own 30 s timer). Passes over 3 ms log `Slow employee pass: <trigger>, N employee(s), X ms` (max once per 10 s). Safety net: one `FindObjectsByType` per colored type every 5 s; a rolling scan of all 8 types once after a scene reset/enable; a type whose hook isn't applied (`Harmony.GetPatchInfo`, owner `flamboyanthopper.employeeclothes`) is scanned every 1 s. `ClothesStore.Tick` every 1 s, `TabUi.Tick` every 0.5 s; `Preview.Update`/`TabUi.PerFrame` return at once when closed. Estimated idle cost 0.1-0.3 ms/s.
- **Diagnostics:** one line per employee after the first apply: `Security guard 'SecurityGuard(Clone)' (found by hook, N employee(s) known): model 'Animated Model', 1 renderer(s), apply complete|still postponed after 8 tries, color #xxxxxx: LOD0[BouncerHeadA_MAT -, BouncerBody_MAT =EC, blinn1 -]` (`-` not clothes, `=EC` our copy is on, `CLOTHES-NOT-SWAPPED` a bug). `start hook isn't applied` Warn if a Start patch is missing.
- Knowledge: `SecurityGuard` has a global `Restocker` script, not a `Clerk` (its `FakeRestocker` field is unused, so a guard is never found as a restocker). `EmployeeManager` has `m_Active*` lists (no bakers; pooled employees aren't in them), unused here.

v1.0.4 (round 1 of the user's first in-game test; not verified in-game yet):
- **Shoes are never recolored.** `NotClothesRx` (`shoe|boot|hair|yaka`) beats `ClothesRx`; the `ShadedGarments` shoe shade is gone (now trousers 0.7 and waistcoat 0.8 only).
- **Hair no longer changes color.** Cause: the classifier matched `tshirt`/`yaka` in the textures of the materials named `Hair_Mat`, which are hair plus the "RESTOCKER" badge (the old note that they were the restocker's T-shirt was wrong). Also `Dress_Mat` on Restocker_03/05 (texture `Low_Dress_Mat`) contains a dark hair blob and the badge next to a blue knit hat: masked by hue. Evidence and the full table: "Employee materials" below.
- **Female restockers' trousers (Restocker_05/06)**: `BritneyBodyMAT` is recolored by a baked texture (jeans islands only, hands/face/hair/ears untouched). Restocker_02..04 had no pants problem (separate `BusinessMan_Pants`/`UrbanGuy_Trousers` materials). Other atlases with non-clothes parts got masks too (Baker pants: boots and belts; guard suit: shoes; Janitor/05/06 top: red emblem). New API `Recolor.GetMaterial(original, slot, color)`, baked textures cached per (material, slot) and re-baked only when the color changes, 512x512 working size, time-budgeted; restore/destroy paths cover the baked textures. Details: "How it works".
- **Card text**: description "Change the color of the employee's uniform" (the description TMP is switched to no-wrap + auto-size so it shrinks to one line in the 200x22 box instead of wrapping), price "$500.00" (`0.00` format, invariant culture; the other cards' `Cost Text` are "250.00"/"700.00"/"750.00" next to a "Cost: $" label). "color" (US) in all user-visible text, docs and log lines.
- Not done on purpose (next round): the panel and the preview UI.

v1.0.3 (the user's final word on charging, plus a placement check; still not verified in-game):
- **Charging is per opening again, as a paid session.** The card click charges `Price` (const 1000 since v1.0.7, was 500) first and opens the panel only if the charge worked (`OnCardClicked`: `HasMoney` check, `Charge`, `Open`; if `Open` throws after the charge it is refunded with `MoneyTransition(+price)`). Inside the session Apply, "Apply to all", Reset, picking and type switching are free. The "Already applied." line stays as plain info (applying the same color again is harmless and free). The Apply button is a plain "Apply" again (no price, no disabling).
- **When a session ends (`TabUi.EndSession`):** Done; switching to another tab or the screen being hidden (a `PanelWatcher` MonoBehaviour on the panel: `OnDisable` fires the moment the panel is deactivated, so there is no window to switch back for free); leaving the computer (Harmony patch `ComputerClosePatch` on `Computer.OpenPC` setter with `false`, plus the `Tick` check of `m_PCCam`/`OpenPC` as a safety net); a scene change (`Forget` destroys the panel); the mod being switched off (`Remove`). `EndSession` deactivates the panel; the only code that activates it is `Open()`, reached only from a charged click, so it can't be re-entered for free. `Patches applied (3/3)` now.
- **Card:** shows the price ("Cost: $500", like the store-name card); `UpdateCard` greys the button while the money is short (the game's feedback in `ChangeNameItem.UpdatePurchaseButton`/`CheckRequirements`), while a session is open and for guests ("Host only").
- **Saving:** the colors go to `flamboyanthopper.employeeclothes.saves.txt` at every Apply and Reset (robust against crashes, no close step). The charge is part of the game's money: quitting without saving the game loses it (money back to the last save) but keeps the colors.
- **Placement check (CustomizationTab hierarchy, `tools/unity_tree.py` + raw component data of `Content`):** `CustomizationTab/Unlocked/Sections Scroll View/Viewport/Content` has a `GridLayoutGroup` (padding l10 r10 t25 b10, upper-centre, start corner upper-left, horizontal axis, **cell 300x120, spacing 30x30, FixedColumnCount = 2**) and a `ContentSizeFitter` (vertical = preferred). Its children are `Store Name UI` (`ChangeNameItem`), `Enterence Position UI` (`DoorPlaceItem`) and `Enterence Apperence UI` (`EntranceChangeItem`, the store front), i.e. cells (1,1), (1,2), (2,1). Content is 305 high = 25 + 120 + 30 + 120 + 10, exactly two rows, and the viewport is wider than the 650 the two columns need, so the free cell (2,2) is inside the visible area (the tab is 367 high): our card, injected as the last child of `Content`, becomes the 4th cell there. It is a copy of `Store Name UI` (same size, background, texts, button), the grid overrides its size and position, and v1.0.3 also forces a layout rebuild (`LayoutRebuilder.ForceRebuildLayoutImmediate(Content)`) after adding it. No layout change is needed. Not seen in-game.
- **Injection timing:** the tab is inactive in the scene; it is activated the first time the player opens the Customization tab, and `CustomizationTab.OnEnable` (patched from plugin load) then injects. The fallback in `Tick` (every 1 s while no card exists, once `IDManager` exists) covers a tab that was enabled before the patch/the scene hook ran, and the case that the `IDManager.Awake` scene reset (`Forget`, which destroys a live card) runs after the card was injected: the next Tick re-injects.
- Docs, config description, `Plugin.cs` comment and test steps rewritten; the v1.0.2 "charge on Apply" model below is superseded.

v1.0.2 (superseded by v1.0.3 for charging; the ToggleSwitch part still holds):
- (Superseded in v1.0.3.) **Charge moved to Apply.** Opening the card/panel is free. Each Apply charges `Price` (const 1000 since v1.0.7, was 500), like the store name where `ChangeNameOverlay.ApproveStoreName` takes the money at approval: `Charge` (kept in v1.0.3, now called at open) = `MoneyManager.MoneyTransition(-price, UPGRADE_COSTS, true)` offline/host, `NetworkMoneyManager.OnMoneyTransition_MasterRequest` if `IsConnectedAndSlave` (mirrors the game; guests can't Apply anyway). Charge first, then apply and save: if the charge throws nothing is applied. If every targeted type already has the picked color, nothing is charged ("Already applied."). "Apply to all" is one Apply, one charge. **Reset is free** (decision: it only restores vanilla; charging to undo would be hostile and there is nothing to exploit). Picking, swatches, type switching are free.
- **Not enough money:** checked in the dumps: `ChangeNameOverlay` has no money check or popup at all, and `InteractionWarningType` has no "not enough money" entry; the game's only feedback is `ChangeNameItem.UpdatePurchaseButton`/`CheckRequirements` disabling the button while `!HasMoney(price)`. So the Apply button is disabled (`RefreshApply`, every tick) and shows "Apply ($500)"; a click that slips through shows "Not enough money" in the info line. The card shows "Per change: $500" (guests: "Host only", button disabled).
- (Superseded in v1.0.3.) The colors were saved in the same Apply as the charge. The game's own save is separate: quitting without saving the game afterwards loses the charge (the game goes back to its saved money) but keeps the colors.
- **"Apply to all" is the game's own switch.** `ToggleSwitch` (global class: `Slider m_toggleSlider`, `Button m_handle`, `Color m_OnColor/m_OffColor`, `bool isOn`, `Action<bool> onValueChanged`) is used by the HR rule rows (`Toggle_Switch` 51x18 on layer 5) and `HrRosterEntry.m_XpToggle` ("Xp Toggle" with a caption), and in the licence item (`LicenceSlider` 164x72). They only exist in prefab assets (sharedassets2; nothing in `level3`), so `MakeToggleTemplate` finds one with `Resources.FindObjectsOfTypeAll<ToggleSwitch>` (prefers width 20..80, i.e. the HR one), clones it under the inactive holder, destroys the `GamepadSwitch` glyph (`GPIcon`) and any TMP caption, sets `onValueChanged = null` (a runtime delegate, not serialised, so the clone has none anyway; the HR card sets its own) and switches off persistent listeners on the handle button. Verified in the ISIL: `OnEnable` adds `OnClick` to `m_handle.onClick`, `OnDisable` removes all; `OnPointerClick` invokes the handle's `onClick`; `OnClick` flips `isOn` and calls `SwitchSlider`, which invokes `onValueChanged(isOn)`, tweens the slider/colors; `SetSlider(bool)` sets `isOn` and the visuals **without** the event (used for the programmatic untick). The copy is scaled 1.25 and placed with a "Apply to all" label. It unticks after Apply, Reset and a type switch. If no `ToggleSwitch` is loaded or cloning throws, the tinted text button from v1.0.1 is used (logged: `"Apply to all" is a text button`). Unverified: that a ToggleSwitch prefab is loaded in memory when the tab is built (it is referenced by HR/licence UI scripts, so it should be), and how the clone looks on this screen.
- Docs/doc comments/config text/test steps updated; v1.0.1's "refund on quit" note is replaced by the above.

v1.0.1 (review round 1, still not verified in-game). Changes after the reviewer's findings (each checked against the code/dumps first):
- **Pooled employees (high):** `LeanPool.Despawn` deactivates fired employees and `FindObjectsOfType` skips inactive objects. The sweeper now uses `FindObjectsByType(type, FindObjectsInactive.Include, FindObjectsSortMode.None)`, and every swap is recorded in a registry in `Recolor` (renderer, slot, original material per index). Restoring (a type losing its color, Reset, disable, new scene, settings change) goes through the registry (`Recolor.RestoreRegistry`), so pooled employees are restored without being found, and it runs before any copy is destroyed (`DestroyVariants`). `ApplyToRenderer` falls back to the registry's original when the renderer's material is a destroyed (`== null`) copy.
- **Sweep cost:** only types with a color are scanned; renderers are those of the top Animator's sub-tree (like the preview), cached per employee (re-scanned every 8 s, materials re-verified every 3 s or when `ClothesStore.Version` changes). Caveat: a box carried in a hand is parented below a hand bone, so it is inside that sub-tree; its materials never match the clothes names, so nothing happens to it.
- **Leaving the computer:** `TabUi.Tick` closes the panel (and so the preview camera) when `Computer.OpenPC` is false or `m_PCCam` is inactive (`set_OpenPC(false)` only deactivates the camera; the screen objects stay active).
- **Stale keys:** every cache is keyed by `GetInstanceID()` (materials, textures, copies, renderers, prefab dedupe), not by interop `.Pointer`.
- **Picker:** the copy gets its own `new Material(colorPickerShader)` as both `image.material` and `generatedMaterial`, so `Initialize(Color)` takes its right-shader branch and draws the initial color. Correction of the v1.0.0 note: `Initialize(Color)` calls `ApplyColor` (and so fires `onColorChanged`) only in the right-shader branch; in the wrong-shader branch it makes the material and returns without drawing. `OnDestroy` uses `DestroyImmediate(generatedMaterial)`, hence the own material.
- **Apply to all:** (superseded in v1.0.5: it now stays on and is saved) unticked after Apply and Reset and on every type switch; a tinted button in v1.0.1 (replaced by the game's switch in v1.0.2, see above).
- **Build failure:** logged once, no retries until the next scene (`_buildFailed`).
- **Charging:** (v1.0.1 charged at open; replaced in v1.0.2, see above.)
- **Streaming mips:** `ClearRequestedMipmapLevel` after the shade map is built.
- **Duplicate card:** `Forget` destroys what is still alive, and `Build` removes an existing `Employee Clothes UI` card / panel in the tab first.
- **Gamepad:** the card's `GamepadUISelectable` events are emptied (their persistent targets belonged to the original card) and Submit opens this card. Whether the card is reachable with a gamepad is unverified; the panel isn't gamepad-aware.

v1.0.0 (TODO Improvement #15): first implementation, written without being able to run the game. **Nothing has been verified in-game.** Every part below marked "unverified" can fail; the log lines in the test steps say which.

## Files

| File | What |
|---|---|
| `Plugin.cs` | Config, patches, the mechanism comment |
| `ClothesStore.cs` | Color per employee type, save-file persistence (plus the global Apply to all line), Photon room property (host publishes, guests read) |
| `Recolor.cs` | Shade maps, material copies, applying a color to a renderer / model |
| `Employees.cs` | Tracks live employees (registered by the `EmployeeTracker` hooks, slow safety-net scans) and applies colors when something changed; the `ClothesDriver` MonoBehaviour |
| `EmployeeTracker.cs` | Harmony postfixes on the employee classes' `Start` that register each employee |
| `Preview.cs` | Camera + RenderTexture + model clones |
| `TabUi.cs` | The Customization card and the panel |
| `Patches.cs` | `CustomizationTab.OnEnable` (inject card), `IDManager.Awake` (new scene: reset caches), `Computer.OpenPC` (leaving the computer ends the session), `SaveManager.Save(string)` / `Clear` / `ApplySaveData` (commit the colors with the game save) |

## Game reference: employees and their clothes

Verified with UnityPy on the shipped assets (`tools/unity_tree.py`, 2026-11-17).
- **Prefabs** (`sharedassets2` plain, `resources.assets` networked copy with `PhotonView`): `Restocker`, `Restocker_02`..`_06` (all `Clerk` + `ClerkAnimationController`), `Cashier`, `Woman_Cashier`, `CustomerHelper`, `SecurityGuard`, `Janitor`, `Courier`, `Baker`, `IceCreamHelper_01`. The HR ScriptableObjects (`IDManager.m_Restockers` ... `m_Couriers`, field `<X>Prefab`, ice cream helper `EmployeePrefab`) point at the plain ones. Each type has several SOs/prefabs (6 restockers, 2 cashiers).
- **Layout:** root (layer 6, scripts, `Highlightable`) > `Animated Model` (Animator with the controller + `LODGroup`; Janitor: `FemaleScientistPFB`, Courier: `Courier`, IceCream: `Ice_Cream_Helper`) > mesh object with `LOD0/1/2` `SkinnedMeshRenderer`s (or a single one) > bones `mixamorig:*`. A child Animator of the same name has no controller. Also `BoostIndicator`, `InfoDisplay` (name tag TextMeshPro, layer 11), SecurityGuard `Hover Info`. The preview clones only the top Animator's GameObject.
- **Materials:** all URP/Lit, `_BaseColor` white (a few 0.8 or 0.74), color comes only from the `_BaseMap` texture (== `_MainTex`). Textures aren't readable (`isReadable` false), 512 to 2048, DXT / crunched; some have streaming mipmaps. No runtime customisation of employees exists (`CharacterCustomize`, `BmanCustomize`, `UrbanGuy_*`, `GiantMan_*`, `BWoman*`, `BigGuy_*` are another character kit used by customers or unused), and `EmployeeTypeColorsSO` is only the HR card accent.
- **Names lie:** see the verified table in "Employee materials" below. Short version: clothes are `BusinessMan_Shirt/Pants/Jacket/Waistcoat/Handkerchief_MAT_V*`, `Female_Shirt/Pants_V*`, `Female_Scientist_TOP/BTM_MAT`, `Dress_Mat` (a hat on Restocker_03/05!), `Bakery_Dres_Mat`, `Bakery_Pants_Mat`, `Ice_Cream_Helper_Dress_Mat`, `URP_UrbanGuy_Trousers_MAT_V*`, `Hat_Mat`, `Helmet_Mat` (vest + helmet), `BouncerBody_MAT` (suit panels + shoes), `BritneyBodyMAT` (body atlas with jeans); `Hair_Mat` is hair + name badge, never a T-shirt. Never recolored: shoes (`*Shoes*`), hair, faces/skin, eyes, glasses, badges (`blinn1`/`Cashi`/`Janitor_Ticket_Mat`), `M_Mop`.
- `Highlightable` doesn't touch materials (checked its dump: no Renderer/Material calls), so swapping `sharedMaterials` isn't undone by hover highlighting. Not verified at runtime.

## Employee materials (verified table)

Made 2026-11-17 with UnityPy (`tools/unity_tree.py --root <prefab>` on `sharedassets2.assets`, 14 prefabs, every renderer of every LOD) plus UV overlays: for each submesh the triangles' UVs were drawn over its base texture, to see which part of an atlas a mesh really uses (the mesh export of UnityPy gives one group per submesh, an empty first group has to be dropped). Columns: material | base texture | recolored by the mod (v1.0.4). LOD1/LOD2 and the `UptownBoy Atlas Material` (TextMeshPro name tag) are left out; LOD renderers repeat the same materials. "YES (rule)" = only the garment pixels, see the rules below the table.

**Restocker**

| Material | Base texture | Recolored |
|---|---|---|
| BusinessMan_Pants_MAT | BusinessMan_Pants_MAT_AlbedoTransparency_V1 | YES |
| Eyes_MAT_V4 | Eyes_MAT_AlbedoTransparency_V4 | no |
| Man_FaceB_MAT | Man_FaceB_MAT_AlbedoTransparency_V1 | no |
| Eyelashes_MAT | EyeLashes_MAT_AlbedoTransparency_V1 | no |
| Man_HairD_MAT_V5 | Man_HairD_MAT_AlbedoTransparency_V5 | no |
| BusinessMan_Pants_MAT_V8 | BusinessMan_Pants_MAT_AlbedoTransparency_V8 | YES |
| BusinessMan_Shirt_MAT_V8 | BusinessMan_Shirt_MAT_AlbedoTransparency_V8 | YES |
| BusinessMan_Shoes_MAT_V2 | BusinessMan_Shoes_MAT_AlbedoTransparency_V2 | no |
| Hair_Mat | Low_Hair_Tshirt_Mat_AlbedoTransparency | no |

**Restocker_02**

| Material | Base texture | Recolored |
|---|---|---|
| BusinessMan_Shirt_MAT_V8 | BusinessMan_Shirt_MAT_AlbedoTransparency_V8 | YES |
| Hair_Mat | Low_Hair_Tshirt_Mat_AlbedoTransparency | no |
| BusinessMan_Pants_MAT | BusinessMan_Pants_MAT_AlbedoTransparency_V1 | YES |
| Eyes_Mat | Eyes_MAT_AlbedoTransparency_V5 | no |
| Skin_Mat | Man_FaceA_MAT_AlbedoTransparency_V1 | no |
| Eyelashes_MAT | EyeLashes_MAT_AlbedoTransparency_V1 | no |
| Pants_Mant | BusinessMan_Pants_MAT_AlbedoTransparency_V10 | YES |
| Shoes_Mat | BusinessMan_Shoes_MAT_AlbedoTransparency_V3 | no |

**Restocker_03**

| Material | Base texture | Recolored |
|---|---|---|
| Dress_Mat | Low_Dress_Mat_AlbedoTransparency 1 | YES (blue knit pixels only) |
| Eyes_MAT_V4 | Eyes_MAT_AlbedoTransparency_V4 | no |
| Man_FaceB_MAT | Man_FaceA_MAT_AlbedoTransparency_V1 | no |
| Eyelashes_MAT | EyeLashes_MAT_AlbedoTransparency_V1 | no |
| URP_UrbanGuy_Trousers_MAT_V2 | UrbanGuy_Trousers_MAT_AlbedoTransparency_V2 | YES |
| URP_UrbanGuy_Trousers_MAT_V3 | UrbanGuy_Trousers_MAT_AlbedoTransparency_V3 | YES |
| BusinessMan_Shirt_MAT_V8 | BusinessMan_Shirt_MAT_AlbedoTransparency_V8 | YES |

**Restocker_04**

| Material | Base texture | Recolored |
|---|---|---|
| Hair_Mat | Low_Hair_Yaka_Mat_AlbedoTransparency | no |
| Man_FaceE_MAT | Man_FaceE_MAT_AlbedoTransparency_V1 | no |
| BusinessMan_Shirt_MAT_V8 | BusinessMan_Shirt_MAT_AlbedoTransparency_V8 | YES |
| BusinessMan_Pants_MAT | BusinessMan_Pants_MAT_AlbedoTransparency_V1 | YES |
| FemaleEyes_MAT_V3 | FemaleEyes_MAT_AlbedoTransparency_V3 | no |
| Eyelashes_MAT | EyeLashes_MAT_AlbedoTransparency_V1 | no |
| Shoes_Mat | BusinessMan_Shoes_MAT_AlbedoTransparency_V3 | no |

**Restocker_05**

| Material | Base texture | Recolored |
|---|---|---|
| HairMAT1 | hairEyelashesCOL | no |
| Female_Eyes_V3 | Female_Eyes_MAT_AlbedoTransparency_V3 | no |
| Female_Scientist_TOP_MAT_V1 | Female_Scientist_TOP_MAT_AlbedoTransparency_V2 | YES (blue pixels only) |
| BritneyBodyMAT | BritneyCOLa | YES (jeans islands only) |
| Hair_Mat | Low_Hair_Yaka_Mat_AlbedoTransparency | no |
| Dress_Mat | Low_Dress_Mat_AlbedoTransparency 1 | YES (blue knit pixels only) |

**Restocker_06**

| Material | Base texture | Recolored |
|---|---|---|
| HairMAT1 | hairEyelashesCOL | no |
| Female_Eyes_V2 | Female_Eyes_MAT_AlbedoTransparency_V2 | no |
| Female_Scientist_TOP_MAT_V1 | Female_Scientist_TOP_MAT_AlbedoTransparency_V2 | YES (blue pixels only) |
| BritneyBodyMAT | Pants | YES (jeans islands only) |
| Hair_Mat | Low_Hair_Yaka_Mat_AlbedoTransparency | no |
| Skin_Mat | BodyCOL01 | no |
| Shoes | Shoes_Albedo | no |
| Hair | Hair_Yellow_Albedo | no |

**Cashier**

| Material | Base texture | Recolored |
|---|---|---|
| Eyes_MAT_V4 | Eyes_MAT_AlbedoTransparency_V4 | no |
| Man_FaceD_MAT | Man_FaceD_MAT_AlbedoTransparency_V1 | no |
| Eyelashes_MAT | EyeLashes_MAT_AlbedoTransparency_V1 | no |
| Man_HairB_MAT_V5 | Man_HairB_MAT_AlbedoTransparency_V5 | no |
| BusinessMan_Pants_MAT_V1 | BusinessMan_Pants_MAT_AlbedoTransparency_V1 | YES |
| BusinessMan_Shirt_MAT_V6 | BusinessMan_Shirt_MAT_AlbedoTransparency_V6 | YES |
| BusinessMan_Shoes_MAT_V1 | BusinessMan_Shoes_MAT_AlbedoTransparency_V1 | no |
| BusinessMan_Waistcoat_MAT_V1 | BusinessMan_Waistcoat_MAT_AlbedoTransparency_V1 | YES |
| blinn1 | loww_standardSurface1_AlbedoTransparency | no |

**Woman_Cashier**

| Material | Base texture | Recolored |
|---|---|---|
| Cashi | loww_standardSurface1_AlbedoTransparency | no |
| Female_Body_V1 | Female_Body_AlbedoTransparency_V1 | no |
| Female_Eyes | Female_Eyes_MAT_AlbedoTransparency_V1 | no |
| Female_HeadE | Female_HeadE_AlbedoTransparency_V5 | no |
| Female_EyeLashes_MAT | Female_EyeLashes_MAT_AlbedoTransparency_V1 | no |
| Female_HairA_Fade_MAT_V1 | Female_HairA_MAT_AlbedoTransparency_V1 | no |
| Female_HairA_Fade_MAT_V5 | Female_HairA_MAT_AlbedoTransparency_V5 | no |
| Female_Pants_V6 | Female_Pants_AlbedoTransparency_V6 | YES |
| Female_Shirt_V5 | Female_Shirt_AlbedoTransparency_V5 | YES |
| Female_Shoes_V5 | Female_Shoes_AlbedoTransparency_V5 | no |
| standardSurface1 | None | no |

**CustomerHelper**

| Material | Base texture | Recolored |
|---|---|---|
| Eyes_MAT_V4 | Eyes_MAT_AlbedoTransparency_V4 | no |
| Man_FaceA_MAT | Man_FaceA_MAT_AlbedoTransparency_V1 | no |
| Eyelashes_MAT | EyeLashes_MAT_AlbedoTransparency_V1 | no |
| Glasses_Frame_MAT | Glasses_Frame_MAT_AlbedoTransparency_V1 | no |
| Glasses_Glass_MAT | Glasses_Glass_MAT_AlbedoTransparency_V1 | no |
| Man_HairI_MAT_V5 | Man_HairI_MAT_AlbedoTransparency_V5 | no |
| BusinessMan_Handkerchief_MAT_V2 | BusinessMan_Handkerchief_MAT_AlbedoTransparency_V2 | YES |
| BusinessMan_Jacket_MAT_V6 | BusinessMan_Jacket_MAT_AlbedoTransparency_V6 | YES |
| BusinessMan_Shirt_MAT_V3 | BusinessMan_Shirt_MAT_AlbedoTransparency_V3 | YES |
| BusinessMan_Pants_MAT_V6 | BusinessMan_Pants_MAT_AlbedoTransparency_V6 | YES |
| BusinessMan_Shoes_MAT_V3 | BusinessMan_Shoes_MAT_AlbedoTransparency_V3 | no |
| blinn1 | loww_standardSurface1_AlbedoTransparency | no |
| Glasses_SunGlass_MAT | None | no |

**SecurityGuard**

| Material | Base texture | Recolored |
|---|---|---|
| BouncerHeadA_MAT | BouncerHeadA_MAT_AlbedoTransparency | no |
| BouncerBody_MAT | BouncerBody_MAT_AlbedoTransparency | YES (torso + trousers; not shoes, soles, belt, buckle) |
| blinn1 | loww_standardSurface1_AlbedoTransparency | no |

**Janitor**

| Material | Base texture | Recolored |
|---|---|---|
| Janitor_Ticket_Mat | loww_standardSurface1_AlbedoTransparency | no |
| Female_Scientist_HairB_MAT_V2_Fade | Female_Scientist_HairB_MAT_AlbedoTransparency_V2 | no |
| Female_Scientist_BTM_MAT_V2 | Female_Scientist_BTM_MAT_AlbedoTransparency_V2 | YES |
| FemaleEyes_MAT_V6 | FemaleEyes_MAT_AlbedoTransparency_V6 | no |
| Female_Scientist_HairB_MAT_V2 | Female_Scientist_HairB_MAT_AlbedoTransparency_V2 | no |
| Female_Scientist_SKIN_B_MAT | Female_Scientist_SKIN_B_MAT_AlbedoTransparency_V1 | no |
| Female_EyeLashes_MAT | Female_EyeLashes_MAT_AlbedoTransparency_V1 | no |
| Female_Scientist_TOP_MAT_V1 | Female_Scientist_TOP_MAT_AlbedoTransparency_V2 | YES (blue pixels only) |
| M_Mop | T_Mop_AlbedoTransparency | no |

**Courier**

| Material | Base texture | Recolored |
|---|---|---|
| Helmet_Mat | Helmet_Mat_AlbedoTransparency | YES |
| BusinessMan_Pants_MAT | BusinessMan_Pants_MAT_AlbedoTransparency_V1 | YES |
| Eyes_MAT_V4 | Eyes_MAT_AlbedoTransparency_V4 | no |
| Man_FaceB_MAT | Man_FaceB_MAT_AlbedoTransparency_V1 | no |
| Eyelashes_MAT | EyeLashes_MAT_AlbedoTransparency_V1 | no |
| Man_HairD_MAT_V5 | Man_HairD_MAT_AlbedoTransparency_V5 | no |
| BusinessMan_Pants_MAT_V8 | BusinessMan_Pants_MAT_AlbedoTransparency_V8 | YES |
| BusinessMan_Shirt_MAT_V8 | BusinessMan_Shirt_MAT_AlbedoTransparency_V8 | YES |
| BusinessMan_Shoes_MAT_V2 | BusinessMan_Shoes_MAT_AlbedoTransparency_V2 | no |
| Eyes_MAT_V4_1 | Eyes_MAT_AlbedoTransparency_V4 | no |

**Baker**

| Material | Base texture | Recolored |
|---|---|---|
| Bakery_Dres_Mat | Bakery_Dres_Base_Color | YES |
| Bakery_Skin_Mat | Man_02_Skin_Dif | no |
| Bakery_Pants_Mat | Man_02_Down_Dress_Diff | YES (blue (denim) pixels only) |
| Glasses_Frame_MAT | Glasses_Frame_MAT_AlbedoTransparency_V1 | no |
| Glasses_Glass_MAT | Glasses_Glass_MAT_AlbedoTransparency_V1 | no |
| Hat_Mat | Hat_Low_Hat_Mat_AlbedoTransparency | YES |

**IceCreamHelper_01**

| Material | Base texture | Recolored |
|---|---|---|
| Ice_Cream_Helper_Dress_Mat | Woman_02_Low_Dress_Mat_AlbedoTransparency | YES |
| Ice_Cream_Helper_Hat_Mat | Hat_Low_Hat_Mat_AlbedoTransparency | YES |
| Woman_02_Eyes_Mat | Woman_02_Eyes_Dif | no |
| Eyelashes_Mat | EyeLashes | no |
| Woman_02_Hair_Mat | Woman_02_Hair_Dif | no |
| Woman_02_Skin_Mat | Woman_02_Skin_Dif | no |
| Woman_02_Shoes_Mat | Woman_02_Shoes_Dif | no |

Facts behind the verdicts (all from overlays, so they are about what the meshes use):
- **`Hair_Mat` is never a T-shirt.** Textures `Low_Hair_Tshirt_Mat` (Restocker, Restocker_02) and `Low_Hair_Yaka_Mat` (Restocker_04/05/06) are hair plus the red "RESTOCKER" name badge. Restocker and Restocker_05/06 use only the 156-face badge patch of them; Restocker_02 uses the beige hair patches, Restocker_04 the grey fur of the whole head (it has no other hair material). The v1.0.0..1.0.3 classifier matched `tshirt`/`yaka` and so recolored these hairs (that was the "hair changes color" bug). The shirts of all restockers are `BusinessMan_Shirt_MAT_V8`.
- **`Dress_Mat` (Restocker_03/05)**, texture `Low_Dress_Mat_AlbedoTransparency 1`: the mesh uses blue knit pieces (a hat), a dark-brown hair-like blob and the red badge. The red cloak area of the texture is not used.
- **`BritneyBodyMAT` (Restocker_05/06)**: one body atlas (jeans legs and waistband strips, hands, face, ears, scalp hair, a grey boot-like piece). The same UV layout in two textures (`BritneyCOLa` for 05, `Pants` 2048 for 06, a re-tinted copy with brighter blue jeans). The red top and the sleeve in the atlas are not covered by the mesh (the separate TOP material is used).
- **`Bakery_Pants_Mat` (Baker)**: jeans, brown boots, brown belts and a grey medallion in one 1024 texture.
- **`BouncerBody_MAT` (guard)**: trousers (4 leg panels, dark grey, V about 0.09), torso (2 pieces, pure black V = 0, bottom third of the texture), a grey sole strip with two brown shoes (middle column), a brown belt with a white buckle at the top of the leg panels. **The guard has no shirt, jacket or tie in the mesh** (SecurityGuard > Animated Model > LOD0, 3 submeshes, no LODGroup); his arms are bare skin (`BouncerHeadA_MAT` = face, ears, forearms, hands, sunglasses arms; `blinn1` = the badge). (v1.0.5's "four dark-grey suit panels" were the trousers.)
- **`Helmet_Mat` (Courier)**: high-visibility vest (green, yellow stripes) plus the helmet halves/strap pieces; no boots (the Courier has `BusinessMan_Shoes`). Fully recolored.
- `Female_Scientist_TOP_MAT` texture has a red patterned emblem (oval) and a pale octagon badge besides the blue top.
- Not visited: LOD1/LOD2 differences (same materials as LOD0 in the table above, checked in the tree dump).

### The classifier (`Recolor.cs`)

`both = "<material name> | <base texture name>"`. Clothes = `ClothesRx` (`shirt|pants|jacket|waistcoat|handkerchief|dres|trouser|_top_|_btm_|bouncerbody|helmet|hat_|_hat_|britney`) and not `NotClothesRx` (`shoe|boot|hair|yaka`, which wins). Then `RuleFor` picks a pixel rule for the mixed atlases:

| Material / texture | Rule (pixels recolored) |
|---|---|
| `BritneyBodyMAT` (both textures) | inside the three jeans polygons (waistband strips, left leg, right leg; 1024-space, `Recolor.JeansPolys`), not black padding (V <= 0.06), not skin-colored piping (hue < 50, S > 0.2, V > 0.35) |
| texture `Man_02_Down_Dress_Diff` (Baker pants) | hue 190..260 and S > 0.30 (denim; boots and belts are brown, the medallion grey) |
| texture starting `Low_Dress_Mat` (Restocker_03/05) | hue 190..260 and S > 0.25 (blue knit; the hair blob is brown, the badge red) |
| texture `Female_Scientist_TOP_MAT_*` (Janitor, Restocker_05/06) | hue 190..260 and S > 0.30 (blue top; the red emblem stays) |
| `BouncerBody_MAT` | S < 0.30 and (V < 0.03, i.e. the pure-black torso, or V < 0.14 outside the shoe/sole box). The box is v-from-top 0.36..0.80 and u 0.43..0.60, widening to u < 0.645 below v 0.53 (trouser panel 3's slanted left edge reaches u 0.61..0.64 above v 0.52). Pure black is always recolored (the torso; the same pixels inside the box are unused background); pure-black pixels take the average shade so torso and trousers match. ~96.5% of the trouser pixels, 100% of the torso, 0 shoe specks (prototype on the real texture; not seen in-game) |

The rules were prototyped in Python on the exported textures at 512x512 and the masks were looked at (purple overlays) before the C# port; the C# runs the same logic on the 512x512 RenderTexture copy. Not seen in-game.

## Game reference: Customization tab and the color picker

- Scene `level3`: `---GAME---/Computer &&/Screen/Management App (695x431, scale 0.001)/Tabs (width 0 x 367)/CustomizationTab` (`WindowTab`, `CustomizationTab`, `GamepadSelectableParent`; all objects are on layer 0). Children: `BG`, `Locked`, `Unlocked` > `Sections Scroll View` > `Viewport` > `Content` (`GridLayoutGroup`, cells 300x120, two columns) with the cards `Store Name UI` (`ChangeNameItem`), `Enterence Position UI` (`DoorPlaceItem`), `Enterence Apperence UI` (`EntranceChangeItem`). Card children: `BG`, `Title` (TMP + `LocalizeStringEvent`, child `Description`), `Cost Info` (`Cost`, `Cost Text`), `Purcase Button` (`Image`, `Button`, `MouseClickSFX`, child `Text (TMP)`). `UpdateUI` toggles `Locked`/`Unlocked` by the store upgrade level (`NetworkSectionManager.GetStoreUpgradeLevel`).
- `ChangeNameItem.Start` finds the `DoorSwitchCamera`, adds the button listener (`SwitchCameraToDoor` -> `DoorSwitchCamera.Initialize(price)`) and keeps the button's `interactable` = `MoneyManager.HasMoney(m_Price)`. The store name UI itself is `NameChangeCanvas` (inactive, `PopUps`, `ChangeNameOverlay` singleton, screen-space) with two color pickers (`Text Color Picker`/`Background Color Picker` > `Color picker` 183x307).
- **Charge pattern** (`ChangeNameOverlay.ApproveStoreName`): offline/host `MoneyManager.MoneyTransition(-price, UPGRADE_COSTS (3), true)`; a guest calls `NetworkMoneyManager.OnMoneyTransition_MasterRequest(-price, 3, true)`. (The enum also has `CUSTOMIZATION`; the game uses 3 here.)
- **`ColorPicker`** (`Image` + `ColorPicker`, shader `UI/ColorPicker`): `Initialize(Color)` takes the Image and RectTransform and sets HSV. If `image.material.shader.name == "UI/ColorPicker"` it calls `ApplyColor` (shader vector + `onColorChanged(Color)` invoke); otherwise it makes `generatedMaterial = new Material(colorPickerShader)` (hideFlags 61), assigns it to the image and **returns without `ApplyColor`**. `OnDestroy` does `DestroyImmediate(generatedMaterial)`, so a copy must not share the source's material (the copy gets its own, v1.0.1). `Update` feeds the rect aspect to the shader. Pointer handling uses `eventData.pressEventCamera`, so it works on a world-space canvas. `get_color` converts HSV.
- Other game uses of room properties: `Money`, `roomStartTime`, `RackCount` (each listener only reads its own key).

## How it works (summary)

- **Recolor:** per source texture a "shade map" (luminance relative to the texture's mean, `shade = 1 + (L - mean) * 1.4` clamped 0.45..1.45, stored as grey / 1.45, alpha kept), made through a `RenderTexture` blit (sRGB) + `ReadPixels`, max 1024, DXT5 compressed. Per (original material, slot) a `new Material(orig)` with the shade map in `_BaseMap`/`_MainTex` and `_BaseColor`/`_Color` = color * 1.45 * garment shade. Slots: 0..7 = `HrEmployeeKind`, 100 = preview. `ApplyToRenderer` swaps `sharedMaterials` entries; null color swaps the original back (`OriginalOf` maps copy -> original). Garment shade (ShadedGarments): pants/trousers/BTM/jeans 0.7, waistcoat 0.8 (shoes are no longer recolored).
- **Masked atlases (v1.0.4):** materials from the rule table get an `Atlas` (a <= 512x512 copy of the texture through a RenderTexture, garment pixel indices from the rule, a per-garment-pixel shade byte using the mean of the garment pixels only) and, per (original material, slot), a `Baked` (own RGBA32 texture with mipmaps + the material copy that shows it). `Bake` copies the original pixels and rewrites the garment pixels as `ShadeV * color * 1.45 * garment` in sRGB, clamped (what the shade-map material shows; v1.0.5, no LUTs), then `SetPixels32` (through a shared scratch `Il2CppStructArray<Color32>` per size) + `Apply(true)`; a bake with the same 8-bit color + garment shade is skipped, and over the time budget it is postponed (`Postponed`, the caller retries; a brand-new copy without pixels is never put on a renderer). `Recolor.GetMaterial(original, slot, color)` is the public entry (null = not clothes or postponed). Cleanup: `DestroyVariants` restores the registry first, then destroys copies and baked textures (`BakedByKey`, `BakedOf`); a copy found destroyed on lookup drops its texture; `DestroyAll` also drops the atlases (managed arrays).
- **Why not a tint:** the textures are already colored (blue shirts, black suits); multiplying `_BaseColor` can only darken. A prototype of the shade formula on the real textures (Python, 2026-11-17) gave clean, evenly colored garments with folds and logos kept.
- **Sweeper (v1.0.5):** event-driven, see the v1.0.5 Status. `ClothesDriver.Update`: `ClothesStore.Tick` every 1 s (load/publish/read), `Employees.Sweep` (a pass only when needed; types that lost their color are restored from the registry), `TabUi.Tick` every 0.5 s (card enabled state, fallback injection, closes the panel if the screen/tab got hidden or the player left the computer). A pass always makes at least one shade-map/atlas/bake build, then stops at the 16-25 ms budget; a texture with streaming mips waits up to 2.5 s for mip 0.
- **Card (placement and layout: see the v1.0.3 Status):** the store-name card is instantiated under an **inactive holder** (so no `Awake` runs), `ChangeNameItem`, `LocalizeStringEvent` and `LocalizeAutoSize` are destroyed, button listeners (runtime and persistent) removed, then re-parented into `Content`. Panel = child of the tab over an opaque `Image` background (v1.0.5; the tab's cards are hidden while it is open); texts/buttons are clones of the card's `Title`/`Purcase Button`; picker cloned from the store-name color picker; 12 swatches (fallback if the picker can't work); `Apply to all` is a clone of the game's `ToggleSwitch` (text button fallback).
- **Preview:** `EmployeeClothes.PreviewStage` at y = -5000 on layer 4 (`Water`; layer 31 is never rendered, see v1.0.5), camera orthographic into a 1024x512 `RenderTexture` shown by a `RawImage`; two point lights (URP ignores light culling masks, so no directional light: it would light the store); models = `Object.Instantiate(<top Animator's GameObject>)` of every prefab of the type, in a row (1.15 m apart), standing still, facing +z, `LODGroup.ForceLOD(0)`, root motion off.
- **Multiplayer:** host/solo only edits. Host: colors -> `flamboyanthopper.employeeclothes.saves.txt` per save file name (`SaveManager.CurrentSaveFilePath`) and the room property `flamboyanthopper.employeeclothes` = `Restocker=ff8800;Cashier=2255aa`. Guests (`PhotonNetwork.IsConnectedAndSlave`): read the room property every 1 s and sweep; never write. The card is disabled for guests ("Host only"). Unmodded guests see original clothes. Money goes through `MoneyManager.MoneyTransition` on the host, which the game already syncs.

## Design decisions the user may want to change

- Charged when the card is opened (paid session; user's final word, v1.0.3): Apply, Apply to all, Reset, picking and type switching are free until Done / another tab / leaving the computer / scene change.
- One color per type; "Apply to all" writes the same color to all 8 types, the types stay individually editable afterwards.
- Colorise (one flat color with the outfit's shading) instead of a hue shift; trousers darker (`ShadedGarments`, now a constant).
- Guests can't change colors (no custom RPC; a guest's choice would also need the host to be charged).
- Persistence in a text file by save file name, written when the game saves (v1.0.10; Apply and Reset only mark the colors dirty), so quitting without saving takes back the $1000 and the colors together; a new game in a used slot starts without colors. The global `#applyall` line is still written at once (free).

## Pitfalls / open questions (unverified)

- The preview (v1.0.5 moved it from layer 31 to 4; still unverified): URP camera rendering to a RenderTexture from a script-made camera (no `UniversalAdditionalCameraData`), lighting with two point lights at 30 (constant `PreviewLight`), whether the Animator controllers show an idle pose or a T-pose, whether `Renderer.bounds` is usable on a fresh skinned clone (otherwise a 1.8 m person is assumed), whether clone `Awake`s misbehave (`RigBuilder`, `jiggleBone`).
- The shade map copy: sRGB RenderTexture round trip (if previews/clothes look too dark or too light, the gamma assumption is wrong), `Il2CppStructArray` data at `+0x20` (verified against the indexer on the first and last element, falls back to the slow indexer; logged as `fast copy`), `Texture2D.Compress`.
- The card: whether the cloned card looks right in the 2-column grid (the 4th slot is empty in vanilla), whether `GamepadUISelectable` on the clone causes trouble with a gamepad, the panel layout in the 695x367 tab (the picker keeps the original 183:307 aspect), whether the picker's shader exists (`UI/ColorPicker`; the swatches are the fallback).
- `Il2CppSystem.Action<Color>` for the picker event: if hooking fails the panel polls `ColorPicker.color` per frame (logged).
- A mod that swaps employee materials itself (or `MoreEmployees` adding new models with other material names) isn't handled; new clothes names need adding to `Recolor.ClothesRx` (hair and shoe names are refused by `NotClothesRx`).
- (Resolved in v1.0.4: Restocker_05/06's jeans are recolored by a baked texture, see "Masked atlases".) Unverified in-game: the masks (edges of the jeans, speckles, the hue/saturation thresholds), the bake cost, and the linear/sRGB handling of the baked texture (it should look like the shade-map garments).
- `Texture2D.SetPixels32` + `Apply(true)` on the baked textures every few frames while the preview picker is dragged: if that stutters, lower `AtlasSize` in `Recolor.cs` or add a minimum interval in `Preview.Update`.
- Employee prefab materials aren't modified, only renderers' `sharedMaterials` on instances; all material copies and shade maps are destroyed on mod disable and on a new scene (`IDManager.Awake` patch).

## First in-game test (send these back)

Log tag `[EmployeeClothes]` in `BepInEx/LogOutput.log` (host, and the guest if it has the mod). Debug lines need `VerboseLogging` turned on in the cfg (default off since v1.0.7).
1. Start: `Employee Clothes 1.0.5 loaded (enabled)` and `Patches applied (11/11)`.
2. Load a save with a few employees, open the computer, Customization tab: the 4th card (second row, right column, next to the store-front card, same look, showing "Cost: $500") "Employee Clothes" (`Card added to the Customization tab (color picker cloned)`). A screenshot of the tab helps (card layout).
3. Press Open: `Charged $500 for opening Employee Clothes`, $500 leaves the money, the panel opens (with less than $500 the card is greyed out and nothing happens). Check: preview shows the models (`Preview: N model(s) for Restocker`, `Preview camera: ...`, `Preview is running ...`; a screenshot please: too dark? T-pose? nothing?), the arrows switch type, the picker works (drag, preview recolors), the swatches work.
4. Apply: employees of that type change color in the store (`Applied #... to Restocker`, `First shade map: ...`, and the per-employee `... =EC` line). Look at shirts, trousers, hats and at the LODs (shoes must NOT change) (walk away). Apply is free (no charge, and you can apply again and again). Switch on Apply to all (the game's HR-style switch, bottom left; it should look like the HR "working" switch), Apply (the switch stays on), switch type (still on), switch it off, apply another color to one type, check that one type differs. Apply the same color again: "Already applied.". Reset: free. Close with Done and press the card again: it charges again. Also end the session by switching to another computer tab and back, and by leaving the computer and returning: the panel must be closed and the card must charge again (log `Employee Clothes session ended (...)` with the reason).
5. Save, quit, reload (also: open the panel, apply a color, quit without saving the game, reload: the $500 is back, the colors stay): the colors are back (`Colors for save '<file>': ...`). Hire a new employee: it comes in with the color.
6. Toggle `Enabled` off in the config manager: original clothes back and the card gone; on again: back.
7. Multiplayer: host applies a color: `Published the colors to the room`. Modded guest: `Colors from the host: ...` and recolored employees; the card shows "Host only"; a late joiner gets the colors. Unmodded guest: original clothes, nothing else different.
8. Fire an employee whose type is colored, hire/rehire one (pooled): the color is right. Reset the type while one is fired, rehire: original clothes. Leave the computer with the panel open: the preview stops (no `Preview` activity), and the panel is closed when you come back (reopening charges again).
9. Gamepad: with a controller, try to select the Employee Clothes card in the Customization tab and press A. It may not be reachable (the card is a copy); if it is, report whether the panel then works with the controller. The panel is not designed for a gamepad.
10. v1.0.4 checks: (a) no employee's hair changes color (Restocker 1..6 incl. the grey fur of #4 and the beige hair of #2, Restocker_03/05's hair, Cashier, Baker, Janitor...), (b) shoes stay original everywhere (also the Baker's boots, the guard's shoes), (c) Restocker_05/06: jeans take the color, hands/face/hair/ears don't (log `Masked copy of 'BritneyCOLa' (Jeans): ...` with the pixel count and ms), (d) Baker jeans take the color but not the boots/belts, (e) the guard's suit changes but not the shoes/belt, (f) the badges ("RESTOCKER") keep their red, (g) dragging the picker in the preview with Restocker_05/06 shown doesn't stutter, (h) the card text/price ("$500.00", one-line description). Report anything half recolored (speckles, outlines) with a screenshot.
11. Anything with `failed`/`Error` in the log, and any performance hitch (the first shade maps take a moment).

## Review notes

Improvement #15 review round 1 (v1.0.10): F1 (free apply by reloading / new game inheriting colors) fixed as described in the v1.0.10 Status; F2, F3, F4, F5, F6, F7, F8 fixed. In-game test for F1: (a) save, open the screen, apply, quit WITHOUT saving, reload: money and colors both back to the last save; (b) apply, save (quick save / day end), reload: colors stay; (c) new game in a slot that had colors: no colors, and the old line is gone from `flamboyanthopper.employeeclothes.saves.txt`; (d) continue/load an existing save after starting the game from the main menu: its colors are intact (not wiped by the main menu's `Clear`). With `VerboseLogging` on: `Game saved: wrote colors for 'slot_N.es3': ...` at each save that changed something.

Round 1 (v1.0.1): all 11 findings applied (see Status). The v1.0.1 decline of cloning a real game toggle was reversed by the user's request in v1.0.2 (done, with the text button as fallback).

Round 2 (v1.0.5): see the review record in TODO.md once the review has run.

## Second in-game test (v1.0.5; send these back)

1. `Employee Clothes 1.0.5 loaded`, `Patches applied (11/11)` (and no `start hook isn't applied` Warn).
2. Open the panel: opaque cyan panel, no cards showing through; the preview shows models on a slate-blue background. Send the lines `Preview render check #1: ...`, `Preview scene: ...`, `Employee types in the panel (...)` and a screenshot.
3. Types you can't use yet are hidden (no DLC, or hiring requirements unmet and nobody hired); the counter reads (1/N) with N the shown types. Send the `Employee types in the panel (...)` line with its per-kind reasons and tell me which types you expected to see.
4. "Apply to all" on, switch type, Apply, Reset: it stays on. Close and reopen the screen: still on, also after restarting the game and in another save slot (line `#applyall<TAB>1` in `flamboyanthopper.employeeclothes.saves.txt`). With `VerboseLogging` on, send: `Saved Apply to all: True in ...` after flipping, and after the restart `Apply to all read from '...': '1' -> True` when opening the screen. A Warn `did not stick` / `Saving the Apply to all switch failed` or `no line (off)` after a restart would show where it breaks. Also send the contents of that file.
5. Security guard and janitor: apply a color, check them in the store. Send the `Security guard '...' ... =EC` and `Janitor '...'` lines and any `CLOTHES-NOT-SWAPPED` / `still postponed`. Baked garments (jeans, guard suit, janitor top) should now look like the shade-map ones.
6. Performance: send any `Slow employee pass` lines (should be rare or absent). Play a few minutes with employees colored; compare the smoothness with 1.0.4. Hire a new employee of a colored type: it should come in colored with no visible flash.
7. Done, another tab and leaving the computer all restore the Customization cards.

## Third in-game test (v1.0.6; send these back)

1. The preview models stand still, face the camera, and the label reads "Cashiers", "Security guards", ... (plural only).
2. Security guard: the preview shows the full body; his torso and trousers take the color, shoes/belt/buckle/skin don't (also in the store). Send `Preview camera: ...`, `Preview pose check #1: ...` and `Masked copy of 'BouncerBody_MAT...' (Guard): ...` (expect about 86-88% garment pixels).
3. Check Restocker (6 models in a row), Cashier (2) and Courier framing, and that the other types still recolor as before.
