# ClearTags: agent notes

Improvement #11 (`TODO`). Bigger product tags on boxes (config multiplier, kept on the box lid) and a hover panel in the game's UI
style (icon, brand + name; on a box also quantity and box price) when the crosshair is on a box, shelf, rack or vending tag.

## Status

- v0.3.0 (rework after the first in-game test of 0.2.2; **not yet tested in-game**): (1) the 2x tag is shifted (all children by one
  vector, size unchanged) so its bounding box stays inside the box collider's bounds minus 8 mm (`TagScaler.ClampShift`);
  (2) the hover text is a uGUI panel (`HoverPanel`) in the game's hint-window style instead of IMGUI, with the IMGUI text kept as a
  fallback; (3) quantity and box price are only shown for product boxes, shelf/rack/vending tags show icon + name only;
  (4) the name is "brand name"; (5) product icon via `ProductAtlasManager.GetProductIcon`, furniture icon via the sprite.
- v0.2.2 (review round 4): a failing text build is cached as "nothing to show" instead of leaving the previous target's text.

- v0.2.1 (review round 3): hover text for a paint-can box no longer touches `FurnitureBoxData.Furniture` (it logged an error and scanned
  every `FurnitureSO` each frame): the paint-can branch is chosen by `bucketData` (same test as `NetworkFurnitureBox.OnPhotonInstantiate`);
  the text is built only when the target changes and then twice a second at most.
- **Scope, decided by the user (final):** only the tags on product boxes and furniture boxes are enlarged. Shelf, rack and vending label
  graphics are intentionally never changed; those labels (and the box tags) only get the floating hover text. No overlay quad, no
  `ScaleShelfTags` setting, not a gap or a to-do.

- v0.2.0 (review round 2): `FurnitureBox` tags are scaled and have a hover text (see below); the tag hit test is now a ray vs
  quad-plane test (no depth slack).
- v0.1.1 (review round 1): tag hit test per quad in local space; scale on all three axes; the full scan became a 30 s scan that
  only runs while `VerboseLogging` is on (the hooks are what resize boxes; the scan is a diagnostic safety net, so once the test
  confirms `sweep: N of M` never appears, set `VerboseLogging` to false by default); no game access from `Load`; tag renderers
  cached per box; separate error flags.
- v0.1.0: written and builds. **Not tested in-game**; everything below marked "unverified" is untested.
- Open questions for the first test: (1) is `OnGUI` called on the injected behaviour (look for
  `OnGUI is drawing the hover text (first time)`); (2) do the box and furniture-box tags look right at 2x (position, overlap,
  the furniture box's "Color Point"); (3) does the hover work near the tag's edge (the ray must first hit the box's own
  collider); (4) is the shelf Label's collider enabled and under the painted icon.

## Files

| File | What |
|---|---|
| `src/Plugin.cs` | Config, master switch (patch apply/unpatch + resize all live), `Info` log helper. |
| `src/TagScaler.cs` | Box and furniture-box tag scaling + the five Harmony postfixes (`Box.OnEnable`, `Initialize`, `Setup`; `FurnitureBox.OnEnable`, `Initialize`). Doc comment has the mechanism. |
| `src/HoverInfo.cs` | Target detection and the hover content (`HoverData`: title, icon key, rows). Doc comment has the decisions. |
| `src/HoverPanel.cs` | The uGUI panel (canvas, game font/sprite lookup, layout, icon) and the plain-text fallback. Doc comment has how the game styles its UI. |
| `src/ClearTagsBehaviour.cs` | Injected MonoBehaviour: `Update` (hover target to `HoverPanel`, 30 s diagnostic sweep), `OnGUI` (draws the plain fallback only). |
| `src/ClampedFloat.cs` | Clamp-only setting type (typed number, no slider); same as SelfCheckoutSpeed. |

## Game reference: tags

**Box tag** (verified in `resources.assets` with UnityPy, all 18 `CardboardBox_*` prefabs have the same layout):
`Box.m_IconUI` (offset 0x30, GameObject "Icon UI", has a `LODGroup`, position (0,0,0), scale 1) with two child
quads "BG" and "Icon" (`MeshFilter`+`MeshRenderer`; "Icon" is `Box.m_ProductIconImage`, 0x28). The tag's position
is the children's *local position* (for example (-0.092, 0.484, -0.113) on a 20x20x20, same for BG and Icon) and
the size is their local scale (BG about 0.16, Icon about 0.15 x 0.175, z 1). No collider anywhere under "Icon UI".
So scale the **children**, never "Icon UI" (that would scale about the box origin and move the tag). All three axes of the children are multiplied (z of a quad is
  invisible, and it stays correct whichever local plane the mesh lies in).
- Box code only does `m_IconUI.SetActive(product != null)` and `ProductAtlasManager.SetLabelData(productID, m_ProductIconImage)`
  (in `Initialize` and `Setup`); nothing resets the scale.
- **Pose (read with UnityPy 2026-11-17, all 18 `CardboardBox_*` prefabs; furniture boxes too):** "Icon UI" is at the box origin with identity
  rotation and scale 1. BG and Icon are rotated about 90 degrees about x (quaternion (0.707, 0, 0, 0.707); Icon is tilted another ~3 degrees),
  so the quad faces **up** and the tag lies on the **top face**, at y just above the box top (0.485 on a 15x15x15, 0.257 on an 8x8x8, 0.836 on
  a 40x26x26), in the x/z corner area (for example x -0.092, z -0.113 on a 15x15x15 whose lid is 0.45 x 0.449). BG's local x is the box's x, its
  local y is the box's z. Mesh = built-in Quad (bounds extents (0.5, 0.5, 0)). Tag size varies with the box (BG scale 0.12 on 8x8x8, 0.161 on 15x15x15,
  0.2 on 20x20x20, 0.296 on 40x26x26). Every box root has a `BoxCollider` whose size equals the cardboard model's (0.45 x 0.462 x 0.449 on a 15x15x15;
  5 mm bigger on the 8x8x8). The furniture boxes' models are only a skinned "Animated Model" (the 15x15x15 and 40x26x26) or a mesh child (8x8x8), so the collider is
  the common reference.
- **Why 2x floated in the air (user's screenshot, 0.2.2):** scaling about the tag centre pushes the edge over the lid edge (15x15x15: tag x -0.253..0.069, lid
  starts at -0.225; z -0.274..0.048 vs -0.226). `TagScaler.ClampShift` fixes that, see Mechanism.
- Boxes are pooled (`OnSpawn`/`OnDespawn`) and rendered instanced when static (`ToggleInstanced`, the box mesh only;
  the tag objects stay normal renderers).

**Furniture box tag** (verified in `resources.assets`): same "Icon UI" layout as the product box, but no `m_IconUI` field and no
`LODGroup`: `FurnitureBox.m_ProductIconImage` (0x28) is the "Icon" child, its parent is "Icon UI" (`TagScaler.UiOf(FurnitureBox)`). Children:
"BG", "Icon" and "Color Point" (a small solid mesh, scale 0.025, near the frame's corner). Scaling also moves each child's position about
BG's position by the factor, so the Color Point stays in the same spot of the (bigger) frame. `FurnitureBox.Data` (`FurnitureBoxData`):
`Furniture` (`FurnitureSO`: `ID`, `FurnitureName`, `Cost`) or `Bucket` (paint). Hooks: `OnEnable`, `Initialize` (unique RVAs).

**Shelf, rack, vending tags** (`Label` MonoBehaviour, an `IInteractable` with `Type` = 5 `ONCE`, prefab "Label":
`Label`, `Highlightable`, `BoxCollider`, `GenericLODGroup`; children "Visuals/Optimized" (a MeshRenderer, inactive in
the prefab; `Label.m_Renderer`), "UI/ProductTextContainer" and "WeightedProductTextContainer" (TMP count texts)):
- `Label.m_DisplaySlot` (+0x48), `m_RackSlot` (+0x50), `m_VendingSlot` (+0x58) say which slot it belongs to
  (`DisplaySetup`/`RackSetup`/`VendingSetup`).
- The painted icon is **not** a Label object: `DisplaySlot.SetLabel` calls `LabelMaskController.UpdateIcon/UpdateLabelMask`
  (rack: `RackLabelMaskController`, vending: `VendingLabelMaskController`), which write `_AtlasIndexMatrix`, `_IDMatrix`
  and `_LabelMask`/`_LabelMask2` into the shelf material (`Shader Graphs/Furniture_Shader`, material props
  `_LabelMask`, `_LabelMask2`, `_DiscountLabelMask`, `_Atlas`, `_SINGLELABEL`, `_DOUBLEPRECISION`, `_Test`, `_Test2`).
  Icon size/position come from the shelf mesh UVs and the shader. `Label.SetProductIcon` (the old per-label quad
  path, `SetLabelData` on `Label.m_Renderer`) has no callers.
- `Label.set_ProductCount` writes the count into the TMP text of the label (the number you see next to the icon).
- `DisplaySlot.EnableLabelCollider` toggles the Label's collider; no caller found in the dumps (unverified when it is on).

## Mechanism

**Scale** (`TagScaler`): postfix on `Box.OnEnable`, `Initialize`, `Setup` and `FurnitureBox.OnEnable`, `Initialize` -> `Apply(ui)`; plus, on every settings change (and, while `VerboseLogging` is on, a diagnostic sweep every 30 s), (`ResizeAll`, `FindObjectsOfType<Box>` and `<FurnitureBox>` with `includeInactive: true`). State per "Icon UI"
instance id: the children's original `localScale` (captured the first time a box is seen with factor != 1) and the
applied factor; new scale = base * factor on all three axes, new position = centre + (base position - centre) * factor (centre = BG's position); only written when the factor changed, so nothing
compounds; factor 1 restores the base. `LODGroup.RecalculateBounds()` after a change. The sweep logs
`sweep: N of M boxes had not been resized by the hooks`: if N > 0 shows up in tests, a spawn path isn't covered by
the five hooks. Stale ids are pruned during `ResizeAll`.

**Keeping the tag on the lid** (`TagScaler.ClampShift`, since 0.3.0): at state creation the box's `BoxCollider` is turned into an AABB in the "Icon UI"
local space (`FaceMin/FaceMax`), and each child's base rotation and mesh bounds are cached. For factor f != 1 the AABB of all children (positions
scaled about BG's centre, scales * f, from the *base* pose, not from the current transforms) is computed; the thinnest axis is the tag's normal (y in
the prefabs); on the other two axes the tag is moved inside `FaceMin/Max` -/+ 8 mm (`EdgeMargin`), or centred when it is larger than the face. The
same shift is added to every child's position. f == 1 writes the exact base positions (no shift). Hand-checked numbers: 15x15x15 at 2x shifts by
(+0.034, +0.057) and ends at x -0.219..0.103, z -0.217..0.105; 8x8x8 at 2x fills the lid (centred); 40x26x26 only shifts z. The first four non-zero shifts are
logged (`tag kept on its box face ...`). The hover hit test follows the quads, so it needs no change; its old limitation (ray missing the box collider
but hitting a tag that pokes out) no longer applies for the same reason.

**Hover** (`HoverInfo.Resolve`, every frame from `Update`): local player, cursor locked, not paused. Same ray as
`PlayerInteraction.Raycast` (camera position + forward, `m_InteractionDistance`, `m_InteractableLayer`). First the held box
(`BoxInteraction.m_Box`, only while its parent is `PlayerObjectHolder.m_ObjectHolder`, since `m_Box` can be stale), then
`Physics.Raycast`: a hit collider with a `Label` gives the slot info; with a `Box` it is a tag hit only if the same ray
also intersects one of the tag quads: tested per quad in the quad's local space (ray vs the plane of the thinnest mesh-bounds axis, then
the hit point within the other extents * 1.1 + 0.01), so a turned box doesn't enlarge the hit area and there is no depth slack; the renderers/bounds are cached per `m_IconUI` instance id. Racked boxes: vanilla
turns a racked box hit into its RackSlot interactable, but the hit collider is still the Box's, so this works.
Since 0.3.0 the enlarged tag stays within the box's collider bounds (see above), so a ray that misses the collider can no longer hit it (apart from the 8 mm margin).

**Panel** (`HoverPanel`, built lazily from `ClearTagsBehaviour.Update` on the first hover, never from `Load`): one screen-space overlay `Canvas`
(`ClearTags.HoverCanvas`, sorting order 120, `DontDestroyOnLoad`, built under an inactive root so TMP's Awake runs after the font is set) with a panel
(`Image`), a product `RawImage`, a title `TMP_Text`, a 1 px-ish separator `Image` and two `TMP_Text` columns (labels, coloured values). It is re-laid out
only when `HoverData.Signature`, the text size or the screen height changes (text measured with `preferredWidth/Height`, sizes in pixels derived from
`TextSize * Screen.height / 1080`); hidden with `SetActive(false)`. The panel is anchored 40 px (at 1080p) below the screen centre. The icon is
refreshed only when `HoverData.IconKey` changes. Any exception is caught; after 3 errors (or 3 failed builds, 3 s apart) the panel is torn down
and `ClearTagsBehaviour.OnGUI` draws the old IMGUI text from `HoverPanel.FallbackText` (also used when no TMP font is found). IMGUI details of that fallback:
`new GUIStyle(GUIStyle)` isn't exposed by the interop, so the style starts from `new GUIStyle()`; the first draw logs a line.

**How the game styles its UI** (verified in the shipped assets with UnityPy, plus the user's screenshots `box.png` and `ui.png`):
- Key-hint list: `Leftside Layout` (Canvas, `InteractionHintsManager`) > `Interaction Hints Canvas` (inactive when no hint is shown;
  `Image` + `GridLayoutGroup` + `ContentSizeFitter` + `Outline`), which should be `InteractionHintsManager.m_HintsWindow` (a `Transform`, 0x28; unverified that it is this
  object, the panel code also looks it up by name). The `Image` has sprite `Frame_SpeechBubble01` (a rounded frame), colour (0.086, 0.133, 0.149) = **#162226**, and a
  black `Outline`. Items are `InteractionHintItem` prefabs (`m_HintTextPrefab`, 0x40; "Interaction Hint": `IconContainer` with key icon `Image`s + `Text (TMP)`
  with a `LocalizeStringEvent`).
- Font: the TMP font asset **"UptownBoy"** (material "UptownBoy Atlas Material"), white, in both the hint items and the Daily Statistics screen (the
  rounded, heavy display font in the screenshots). Reuse it with `prefab.GetComponentInChildren<TMP_Text>(true).font`.
- Daily Statistics screen (`DailyStatisticsScreen`: TMP fields `m_DayText`, `m_*Text`; colours `m_PositiveTextColor`, `m_NeutralTextColor`, `m_NegativeTextColor`
  and hex strings `m_PositiveColorCode`...): colours sampled from `ui.png`: panel #0B2C39 (square, not the rounded hint frame), section headers
  **#55BCFF** (light blue), "Day N" #B3EAFF, positive numbers **#53FF81**, negative **#FF7153**, labels and neutral numbers white, separators 1-2 px white lines.
  The header blue is not in a code field, so it is a constant in `HoverPanel`; green comes from `m_PositiveTextColor` at runtime when that screen exists.
- The hint panel and the statistics screen are not reusable as tooltip objects (they are bound to hints / the day screen), so the panel is built from scratch with the
  same font, sprite, colour and outline.

## What the panel shows

| Target | Title | Icon | Rows |
|---|---|---|---|
| Box (loose, racked, held) | brand + name (see below) | `ProductAtlasManager.GetProductIcon(id, RawImage)` | `Quantity:` `Box.ProductCount` / `MaxProductCount` (green), `Box price:` `ProductSO.BoxPrice` |
| Shelf tag (`Label.DisplaySlot`), rack tag (`Label.RackSlot`, id from `RackSlot.Data.ProductID`), vending tag | same | same | none (quantity and price are box-only, user decision) |
| Furniture box (loose, held) | `LocalizationManager.LocalizedFurnitureName(id)` (fallback `FurnitureSO.FurnitureName`); paint box: `LocalizedBucketName` | `FurnitureSO.FurnitureIcon` sprite (texture + `textureRect` as `uvRect`); none for a paint box | `Price:` `FurnitureSO.Cost` (the shop price of the furniture inside), none for a paint box |

**Brand and icon (verified in the dumps):**
- `ProductSO.ProductBrand` is a plain string field (0x30), **not localized**. The game's own cards (`SalesItem.get_ProductBrand` returns `[product+0x30]`;
  `SalesUIElement` has separate `m_ProductNameText` and `m_ProductBrandText`) show it next to the localized name (`ProductSO.TempProductName` = `LocalizedProductName(id)`)
  and the product search matches either. So there is no game format "brand name"; `HoverInfo.DisplayName` joins them: `brand + " " + name`, brand left out when empty or when
  the localized name already contains it (case-insensitive).
- `GetProductIcon(id, RawImage)`: `ProductSO` -> `AtlasIndex` (0x40) picks `atlasedMaterials[AtlasIndex]` (list at 0x20 of `ProductAtlasManager`), `RawImage.texture`
  = its `mainTexture`, `uvRect` = (`AtlasPosition.x * 0.125`, `AtlasPosition.y * 0.125`, 0.125, 0.125) (an 8x8 atlas; `AtlasPosition` is a `SerializableVector2Int`, 0x48). So one shared texture
  and a UV rect; no allocation. `SetLabelData`/`SetFurnitureIcon` (MeshRenderer variants) do the same with `_BaseMap` offsets; `FurnitureSO` has `AtlasIndex` 0x38 and
  `AtlasPosition` 0x40 too, but the panel uses `FurnitureSO.FurnitureIcon` (a `Sprite`) instead. Unverified in-game: that `FurnitureIcon` isn't null and that the atlas texture draws
  correctly (with alpha) in a `RawImage`.

`ProductSO.BoxPrice` (virtual, `WeightedProductSO` overrides) = `PriceManager.CurrentCost(id) * GridLayoutInBox.productCount`
(same as `PriceManager.TotalBoxPrice(id)`), i.e. the wholesale price of a **full** box at today's cost. It is shown on boxes only
(also a half-empty one), so "box price" always means what a box of that product costs to buy. Formatted as `$0.00`
(invariant culture); the game's `Extensions.ToMoneyText` returns TMP rich text with size tags, so it isn't used.

## Multiplayer reasoning

Purely local. The scale changes the local `Transform.localScale` of renderer-only quads (no colliders, no Photon components on
them; `PhotonTransformViewOptimized` syncs the box root only). The hover reads game state and draws a local overlay canvas. Nothing is
sent and no game state is written, so host and guests can run different settings or no mod. Box data a guest sees (product,
count) is whatever the game already synced; a guest's racked-box counts can lag behind the host's (dematerialized racked boxes
live in `RackSlotData`, which is what the rack tag reads).

## In-game test

1. Start the game with the mod; `LogOutput.log` has `Clear Tags 0.3.0 loaded` and `Patches applied (5/5)`.
2. Load a save with boxes (storage room, racks). **Tag position:** a 15x15x15, an 8x8x8, a 30x20x20 and a 40x26x26 furniture box, loose, racked and in your hands: the
   tag is twice as big and lies completely on the lid (nothing hangs over an edge). `[ClearTags] tag kept on its box face ...` lines show the shifts (first four only). On the 8x8x8
   the 2x tag fills the whole lid; that is expected. A `[ClearTags] sweep: N of M ...` line (only while `VerboseLogging` is on) would mean a spawn path isn't hooked;
   no sweep line is the good case.
3. Order a delivery / pick up and put down a box / open and close it: the tag stays big and in place.
4. Change `ScaleMultiplier` (3, then 1, then 2): all boxes follow, no drift; at 1 the tag is exactly where vanilla puts it. Toggle `Enabled` off and on.
5. **Panel:** crosshair on the tag of a floor box, a racked box and the box in your hands (look down at it): a dark rounded panel under the crosshair with the product icon
   at the left, the name as a light-blue header with the brand ("Kilemol Cleaner"), a thin white line, then `Quantity:` (green) and `Box price:`. `LogOutput.log` has one line
   `hover panel created: font 'UptownBoy...' from ...; background: hint window Image (sprite Frame_SpeechBubble01 ...)`. Check the icon is the product's, not blank/garbled,
   and the panel looks like the key-hint list on the left (rounded, same font).
6. Crosshair on a shelf label, a rack label and a vending label: panel with icon and name only (no quantity, no price).
7. Furniture box (order furniture): its icon (if the sprite exists), name and `Price:`; a paint-can box: name only, no errors in the logs.
8. `TextSize` 12 / 30 and another resolution/window size: the panel scales; the hover stays off when the crosshair is beside the tag.
9. If the panel never shows but a plain dark text does, send the `[ClearTags]` warnings (`hover panel attempt N failed`, `hover panel failed`).
10. Multiplayer (host with the mod, guest without, and the other way round): nothing different for the other player.

Send back the `[ClearTags]` lines from `BepInEx/LogOutput.log` (the `hover panel created` line, `tag kept on its box face`, `hover (...)` lines, any warnings) and a screenshot of the panel
and of a big tag on a small box.

## Scope notes

- The panel's labels are English only ("Quantity:", "Box price:", "Price:"); product and furniture names are localized, brand names are not.
- The header colour #55BCFF and the panel's fallback colours are measured from screenshots; only the font, sprite, panel colour, outline and the positive colour are read from the game.
- `GridLayoutInStorage.productCount` is no longer used (no shelf quantity), so the ice-cream-slot capacity caveat is gone.

## Shelf, rack, vending label graphics: intentionally unchanged

By the user's decision these are never modified. For reference (measured with UnityPy and the dumps): the painted label is part of
the furniture model (`SM_Shelf_Single` has a 0.1 x 0.057 plate per label and per price tag at its front face) drawn by
`Furniture_Shader`, switched per slot by bits of `_LabelMask`/`_LabelMask2` (bit 2i = price tag, 2i+1 = label); the prefab's `Label`
object is only a collider (about 0.024 behind the plate) with an inactive `Visuals/Optimized` cube. Only the hover text is added there.
