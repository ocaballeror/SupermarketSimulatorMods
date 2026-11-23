# ClearTags: agent notes

Improvement #11 (`TODO`). Bigger product tags on boxes (config multiplier, kept on the box lid) and a hover panel in the game's UI
style (icon, brand + name; on a box also quantity and box price) when the crosshair is on a box, shelf, rack or vending tag.

## Status

- v0.3.8 (review round 2 of 0.3.7): the prune only runs again once the remembered tags exceed twice the live count found by the last prune (and 500), so a store with more than 500 live boxes no longer rescans every 120 s.
- v0.3.7 (review round 1 of 0.3.6, approved with minor findings): diagnostic INFO lines (resize count per settings change, `hover panel created`, `OnGUI is drawing`) now go through `Plugin.Info` and only appear with `VerboseLogging`; `TagScaler.States` is pruned by `ResizeAll("prune")` from `ClearTagsBehaviour.Update` every 120 s once it holds more than 500 tags (previously only on settings changes); `Plugin.SetActive(false)` restores the tags even if `UnpatchSelf` throws; `HoverInfo.LastKind` is restored on a cache hit for label and price-tag targets (verbose log only); at an effective factor of 1 `TagScaler.Apply` assigns the captured base scale and position directly (exact, no float round trip). No other behaviour change.
- v0.3.6 (user request): a box **in a storage rack** shows the hover panel when the crosshair is on **any part of the box**, not only on its tag. `HoverInfo.Resolve`: in the `Box` branch the tag-quad test (`TagHit`) is skipped when `box.m_Racked`; loose boxes, the held box and furniture boxes still need the tag. No new patch and no setting: the existing `Physics.Raycast` (same ray and layer mask as the game) already returns the racked box's own collider (vanilla `PlayerInteraction.Raycast` relies on it too: hit collider has a `Box` with `m_Racked` -> takes the `RackSlot` from the parent, else logs "Racked box doesn't have an interactable rack slot as parent"). Vanilla interaction (picking up a racked box, the rack slot hints) is untouched, nothing is patched. Purely local, so it works the same on a guest. **Not yet tested in-game.**
- v0.3.4 (settings reduced, commit "simplify mod settings"): the remaining settings are `Enabled`, `ScaleMultiplier`, `ShowHoverText`, `TextSize` (default now 30, was 22) and `VerboseLogging` (the cfg key `[Diagnostics] VerboseLogging` already existed, so existing cfgs keep their stored value; default now off for new configs; the box sweep diagnostic only runs when it is on). The descriptions were shortened by the user. Nothing was turned into a constant.
- v0.3.3 (review of 0.3.2): fix, no hover on **vending price tags**. `VendingPriceTag` is a subclass of `PriceTag` (stub `class VendingPriceTag : PriceTag`, dump "base: PriceTag"), so `GetComponent<PriceTag>()` also returned vending tags, the vending branch was never reached, and the shelf branch read the inherited `DisplaySlot`, which is null for them (`VendingPriceTag.EnableTag` writes only `m_VendingSlot`, +0x68). `Resolve` now looks up `VendingPriceTag` first, then `PriceTag`. **Not yet tested in-game.**
- v0.3.2 (user's in-game test of 0.3.1: tag size, location and hover fine): `Hover/TextSize` default 18 -> 22 (the whole panel scales with it: padding, gaps, icon, separator and title are all derived from the size; only the 40-unit offset below the crosshair is fixed); the hover also works on the **price tag** of a shelf item (`PriceTag`) and of a vending machine item (`VendingPriceTag`), same content as the slot's Label (`HoverInfo.ProductData(id)` is shared by all three). **Not yet tested in-game.**
- v0.3.1 (review round 1 of 0.3.0): the tag size is now capped per box so it really fits the lid (0.3.0 only shifted/centred it, so the 8x8x8, 20x10x7 and anything at 3x+ still hung over);
  the face is computed from local transforms (zero-scale pooled furniture boxes) and retried; panel canvas has a `CanvasScaler` (1920x1080, match height; sizes in reference units);
  rebuilt panel state reset; icon failure not cached; error counters count consecutive failures; fallback text and Resolve closures no longer allocate per frame;
  null box price skips the row; brand check is whole-word ("Ice" is not found in "Rice"). **Not yet tested in-game.**
- v0.3.0 (rework after the first in-game test of 0.2.2; **not yet tested in-game**): (1) the 2x tag is shifted (all children by one
  vector) so its bounding box stays inside the box collider's bounds minus 8 mm (`TagScaler.ClampShift`, since replaced by `Plan`);
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
  starts at -0.225; z -0.274..0.048 vs -0.226). `TagScaler.Plan` fixes that, see Mechanism.
- Boxes are pooled (`OnSpawn`/`OnDespawn`) and rendered instanced when static (`ToggleInstanced`, the box mesh only;
  the tag objects stay normal renderers).

**Furniture box tag** (verified in `resources.assets`): same "Icon UI" layout as the product box, but no `m_IconUI` field and no
`LODGroup`: `FurnitureBox.m_ProductIconImage` (0x28) is the "Icon" child, its parent is "Icon UI" (`TagScaler.UiOf(FurnitureBox)`). Children:
"BG", "Icon" and "Color Point" (a small built-in Quad like the others (mesh extents (0.5, 0.5, 0)), scale 0.025, near the frame's corner). Scaling also moves each child's position about
BG's position by the factor, so the Color Point stays in the same spot of the (bigger) frame. `FurnitureBox.Data` (`FurnitureBoxData`):
`Furniture` (`FurnitureSO`: `ID`, `FurnitureName`, `Cost`) or `Bucket` (paint). Hooks: `OnEnable`, `Initialize` (unique RVAs).

**Shelf, rack, vending tags** (`Label` MonoBehaviour, an `IInteractable` with `Type` = 5 `ONCE`, prefab "Label":
`Label`, `Highlightable`, `BoxCollider`, `GenericLODGroup`; layer 6 "Interactable"; children "Visuals/Optimized" (a MeshRenderer, inactive in
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
- `DisplaySlot.EnableLabelCollider` / `EnablePriceTagCollider` toggle `Collider.enabled` on `GetComponent<Collider>()` of `m_Label` / `m_PriceTag` (same for `VendingSlot`). Only callers are
  `Display`/`VendingMachine` `set_EnableLabelColliders` / `set_EnablePriceTagColliders`, which `StartPlacingMode` sets to false (the furniture is being carried) and `Place` back to true. So both colliders are on whenever the furniture stands.

**Price tags** (`PriceTag` on shelf slots, `m_DisplaySlot` +0x48, property `DisplaySlot`; `VendingPriceTag` on vending slots, property `VendingSlot`; racks have no price tag class). Verified with UnityPy
(`resources.assets`, e.g. the `Freezer_Double_*` prefabs: "Display Slot (Freezer)" > "Price Tag") and the dumps: the "Price Tag" object has `BoxCollider` + `PriceTag` (or `VendingPriceTag`) + `Highlightable` + `GenericLODGroup`
**on one GameObject, layer 6 "Interactable"** (same as the Label, so it is in `PlayerInteraction.m_InteractableLayer`), children "Visuals/Cube" (inactive) and "UI/Price Text" (TMP). The GameObject is **inactive in the
prefab**: `DisplaySlot.SetPriceTag` (called from the slot's product add/remove paths) calls `PriceTag.EnableTag(slot)`, which stores the slot and `SetActive(true)` on it; `DisableTag` does `SetActive(false)`. So the price tag
(and its collider) can only be hit when the slot has a product, and not while the furniture is carried or in placing mode (collider disabled, see above). The same as the vanilla interaction (the game edits the price through it).

**Pitfall: `VendingPriceTag : PriceTag`.** The vending tag is a subclass, so `GetComponent<PriceTag>()` on a vending tag's GameObject returns it too, and its inherited `DisplaySlot` (+0x48) is null
(`VendingPriceTag.EnableTag` writes only its own `m_VendingSlot`, +0x68). Always test for `VendingPriceTag` before `PriceTag` (0.3.2 did it the other way round, so vending price tags showed no panel).

## Mechanism

**Scale** (`TagScaler`): postfix on `Box.OnEnable`, `Initialize`, `Setup` and `FurnitureBox.OnEnable`, `Initialize` -> `Apply(ui)`; plus, on every settings change (and, while `VerboseLogging` is on, a diagnostic sweep every 30 s; and a silent prune, at most every 120 s, once more than 500 tags (and twice the live count of the last prune) are remembered), (`ResizeAll`, `FindObjectsOfType<Box>` and `<FurnitureBox>` with `includeInactive: true`). State per "Icon UI"
instance id: the children's original `localScale` (captured the first time a box is seen with factor != 1) and the
applied factor; new scale = base * factor on all three axes, new position = centre + (base position - centre) * factor (centre = BG's position); only written when the factor changed, so nothing
compounds; factor 1 restores the base. `LODGroup.RecalculateBounds()` after a change. The sweep logs
`sweep: N of M boxes had not been resized by the hooks`: if N > 0 shows up in tests, a spawn path isn't covered by
the five hooks. Stale ids are pruned during `ResizeAll`, which also runs as the prune above.

**Keeping the tag on the lid** (`TagScaler.Plan`, since 0.3.0, capping since 0.3.1): the box's `BoxCollider` is turned into an AABB in the "Icon UI" local space
(`FaceMin/FaceMax`) from the collider's centre/size and "Icon UI"'s own local TRS (no world transforms: pooled furniture boxes are tweened to scale 0 by
`FurnitureBox.OpenBox` and then despawned, so a world mapping at that moment would be degenerate; `Apply` retries `TryGetFace` until it works and logs once
if no collider is found). Each child's base rotation and mesh bounds are cached. For f > 1 the union AABB of all children's *real* (rotated, scaled) quads at the base
pose is computed; this includes the Icon quad, which is a few degrees rotated and bigger than BG, so the true tag extent is a bit bigger than BG's scale (15x15x15: 0.169 x 0.187, not 0.161).
Scaling about BG's centre scales that AABB about the same point, so its extent at factor f is f * ext1. The thinnest axis is the tag's normal (y in the prefabs). The effective
factor is `fEff = max(1, min(f, min over the two in-plane axes of (face - 2 * 8 mm) / ext1))`: the tag is **shrunk below the requested factor** when it wouldn't fit the lid, never
below vanilla; then all children get one common shift so the AABB lies inside the face. f == 1 (or fEff == 1) writes the exact base positions. `Applied` remembers the requested
f, and a late-found face re-applies. Hand-checked with the prefab numbers (Icon rotation included): 8x8x8 at 2x or 3x -> fEff 1.68 (lid 0.25); 20x10x7 -> 1.67 (lid 0.6 x 0.3);
15x15x15 at 2x -> 2.0 with shift (+0.040, +0.083) ending at x -0.219..0.120, z -0.217..0.157, at 3x -> 2.31; 30x20x20 at 3x -> 2.57; 40x26x26 at 2x -> 2.0 (z shift +0.134). The cap
is logged once per lid size (`tag capped to x... (requested x...)`). The hover hit test follows the quads, so it needs no change; its old limitation (ray missing the box collider but hitting
a tag that pokes out) no longer applies for the same reason.

**Hover** (`HoverInfo.Resolve`, every frame from `Update`): local player, cursor locked, not paused. Same ray as
`PlayerInteraction.Raycast` (camera position + forward, `m_InteractionDistance`, `m_InteractableLayer`). First the held box
(`BoxInteraction.m_Box`, only while its parent is `PlayerObjectHolder.m_ObjectHolder`, since `m_Box` can be stale), then
`Physics.Raycast`: a hit collider with a `Label`, a `VendingPriceTag` or a `PriceTag` (tested in that order, since the vending tag is also a `PriceTag`; each on the collider's own GameObject) gives the slot info (the price tag shows the same as the Label; `LastKind` "shelf price tag" / "vending price tag"); with a `Box` it is a tag hit only if the same ray
also intersects one of the tag quads: tested per quad in the quad's local space (ray vs the plane of the thinnest mesh-bounds axis, then
the hit point within the other extents * 1.1 + 0.01), so a turned box doesn't enlarge the hit area and there is no depth slack; the renderers/bounds are cached per `m_IconUI` instance id. Racked boxes: vanilla
turns a racked box hit into its RackSlot interactable, but the hit collider is still the Box's, so this works; since 0.3.6 a racked box (`Box.m_Racked`) needs no tag hit at all: any point of the box's collider the ray hits shows the panel.
A box that is not racked (loose, held) keeps the tag-only rule. A racked box that isn't materialized (only a record in `RackSlotData`, no `Box` object) has no collider to hit; its rack's tag (`Label`) still works.
Since 0.3.0 the enlarged tag stays within the box's collider bounds (see above), so a ray that misses the collider can no longer hit it (apart from the 8 mm margin).

**Panel** (`HoverPanel`, built lazily from `ClearTagsBehaviour.Update` on the first hover, never from `Load`): one screen-space overlay `Canvas`
(`ClearTags.HoverCanvas`, sorting order 120, `CanvasScaler` 1920x1080 match height, `DontDestroyOnLoad`, built under an inactive root so TMP's Awake runs after the font is set) with a panel
(`Image`), a product `RawImage`, a title `TMP_Text`, a 1 px-ish separator `Image` and two `TMP_Text` columns (labels, coloured values). It is re-laid out
only when `HoverData.Signature` or the text size changes (text measured with `preferredWidth/Height`, sizes in pixels derived from
`TextSize` (1080p reference units; a `CanvasScaler` scales to the screen)); hidden with `SetActive(false)`. The panel is anchored 40 units (px at 1080p) below the screen centre. The icon is
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
| Box (loose or held: on its tag; racked: anywhere on the box) | brand + name (see below) | `ProductAtlasManager.GetProductIcon(id, RawImage)` | `Quantity:` `Box.ProductCount` / `MaxProductCount` (green), `Box price:` `ProductSO.BoxPrice` |
| Shelf tag (`Label.DisplaySlot`), rack tag (`Label.RackSlot`, id from `RackSlot.Data.ProductID`), vending tag (`Label.VendingSlot`), shelf price tag (`PriceTag.DisplaySlot`), vending price tag (`VendingPriceTag.VendingSlot`) | same | same | none (quantity and price are box-only, user decision) |
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

1. Start the game with the mod; `LogOutput.log` has `Clear Tags 0.3.8 loaded` and `Patches applied (5/5)`.
2. Load a save with boxes (storage room, racks). **Tag position:** a 15x15x15, an 8x8x8, a 20x10x7, a 30x20x20 and a 40x26x26 furniture box, loose, racked and in your hands: the
   tag lies completely on the lid (nothing hangs over an edge); on big boxes it is 2x, on the smallest (8x8x8, 20x10x7) about 1.7x. `[ClearTags] tag capped to x... (requested x2.0) ...`
   lines (once per lid size) show where it was reduced. A `[ClearTags] sweep: N of M ...` line (only while `VerboseLogging` is on) would mean a spawn path isn't hooked;
   no sweep line is the good case.
3. Order a delivery / pick up and put down a box / open and close it: the tag stays big and in place.
4. Change `ScaleMultiplier` (3, then 1, then 2): all boxes follow, no drift; at 1 the tag is exactly where vanilla puts it. Toggle `Enabled` off and on.
5. **Panel:** crosshair on the tag of a floor box, a racked box and the box in your hands (look down at it): a dark rounded panel under the crosshair with the product icon
   at the left, the name as a light-blue header with the brand ("Kilemol Cleaner"), a thin white line, then `Quantity:` (green) and `Box price:`. `LogOutput.log` has one line
   `hover panel created: font 'UptownBoy...' from ...; background: hint window Image (sprite Frame_SpeechBubble01 ...)`. Check the icon is the product's, not blank/garbled,
   and the panel looks like the key-hint list on the left (rounded, same font).
6. Crosshair on a shelf label, a rack label and a vending label: panel with icon and name only (no quantity, no price). **New in 0.3.2, vending part fixed in 0.3.3:** the same on the **price tag** of a shelf item and of a vending machine item (log `hover (shelf price tag): ...` / `hover (vending price tag): ...`; check both, the vending one did nothing in 0.3.2); racks have no price tag. If the price tag doesn't react, tell me whether the crosshair shows the vanilla price-edit hint there (then the collider is hit but the mod's component lookup failed).
7. Furniture box (order furniture): its icon (if the sprite exists), name and `Price:`; a paint-can box: name only, no errors in the logs.
8. `TextSize` default is now 30 (was 22 in 0.3.2); try 12 / 30 and another resolution/window size: the panel scales; the hover stays off when the crosshair is beside the tag.
9. If the panel never shows but a plain dark text does, send the `[ClearTags]` warnings (`hover panel attempt N failed`, `hover panel failed`).
10. **New in 0.3.6:** put the crosshair on the **side, front and top (away from the tag)** of boxes standing in a storage rack, at several rack heights: the panel appears everywhere on the box (icon, name, `Quantity:`, `Box price:`), `[ClearTags] hover (racked box): ...` in the log. Also on boxes that were in the save and on boxes you rack during the session. A **loose** box (floor, table) still shows the panel only on its tag, and the box in your hands too. Left-click / E on a racked box and on its rack slot still picks it up / works as before; the vanilla key hints on a rack still show; the panel does not stay when you look past the box. On a guest (with or without the mod on the host) the same; if a racked box on the guest still only reacts on its tag, send the log line (that box's `m_Racked` may not be set there).
11. Multiplayer (host with the mod, guest without, and the other way round): nothing different for the other player.

Send back the `[ClearTags]` lines from `BepInEx/LogOutput.log` (turn on `VerboseLogging` in the cfg first; it's off by default) (the `hover panel created` line, `tag kept on its box face`, `hover (...)` lines, any warnings) and a screenshot of the panel
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
