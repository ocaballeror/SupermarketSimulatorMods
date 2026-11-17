# ClearTags: agent notes

Improvement #11 (`TODO`). Bigger product tags on boxes (config multiplier) and a hover text (product name,
quantity, box price) when the crosshair is on a box, shelf, rack or vending tag.

## Status

- v0.1.0: written and builds. **Not tested in-game**; everything below marked "unverified" is untested.
- Not done on purpose: scaling the **shelf/rack/vending** tags (see "Shelf and rack tags"). The request's third
  bullet ("this works also for the tags in shelves and racks") is implemented for the hover text only.
- Open questions for the first test: (1) is `OnGUI` called on the injected behaviour (look for
  `OnGUI is drawing the hover text (first time)`); (2) do the box tags look right at 2x (position, overlap);
  (3) does the box-tag hover work at the top edge/corner (the tag is tested with a padded bounds box, but the ray
  must first hit the box's own collider); (4) is the shelf Label's collider enabled and under the painted icon.

## Files

| File | What |
|---|---|
| `src/Plugin.cs` | Config, master switch (patch apply/unpatch + resize all live), `Info` log helper. |
| `src/TagScaler.cs` | Box tag scaling + the three Harmony postfixes (`Box.OnEnable`, `Initialize`, `Setup`). Doc comment has the mechanism. |
| `src/HoverInfo.cs` | Target detection and hover text. Doc comment has the decisions. |
| `src/ClearTagsBehaviour.cs` | Injected MonoBehaviour: `Update` (hover target, 3 s sweep), `OnGUI` (draws the text). |
| `src/ClampedFloat.cs` | Clamp-only setting type (typed number, no slider); same as SelfCheckoutSpeed. |

## Game reference: tags

**Box tag** (verified in `resources.assets` with UnityPy, all 18 `CardboardBox_*` prefabs have the same layout):
`Box.m_IconUI` (offset 0x30, GameObject "Icon UI", has a `LODGroup`, position (0,0,0), scale 1) with two child
quads "BG" and "Icon" (`MeshFilter`+`MeshRenderer`; "Icon" is `Box.m_ProductIconImage`, 0x28). The tag's position
is the children's *local position* (for example (-0.092, 0.484, -0.113) on a 20x20x20, same for BG and Icon) and
the size is their local scale (BG about 0.16, Icon about 0.15 x 0.175, z 1). No collider anywhere under "Icon UI".
So scale the **children**, never "Icon UI" (that would scale about the box origin and move the tag).
- Box code only does `m_IconUI.SetActive(product != null)` and `ProductAtlasManager.SetLabelData(productID, m_ProductIconImage)`
  (in `Initialize` and `Setup`); nothing resets the scale.
- The quad rotation was not read (UnityPy dump only printed local position and scale); the tag is somewhere near the
  top of the box (local y about 0.48 on a box whose model is about 0.5 high), and unverified which way it faces.
- Boxes are pooled (`OnSpawn`/`OnDespawn`) and rendered instanced when static (`ToggleInstanced`, the box mesh only;
  the tag objects stay normal renderers).

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

**Scale** (`TagScaler`): postfix on `Box.OnEnable`, `Initialize`, `Setup` -> `Apply(box)`; plus a sweep every 3 s and
on every settings change (`ResizeAll`, `FindObjectsOfType<Box>(includeInactive: true)`). State per "Icon UI"
instance id: the children's original `localScale` (captured the first time a box is seen with factor != 1) and the
applied factor; new scale = base * factor on x and y (z unchanged); only written when the factor changed, so nothing
compounds; factor 1 restores the base. `LODGroup.RecalculateBounds()` after a change. The sweep logs
`sweep: N of M boxes had not been resized by the hooks`: if N > 0 shows up in tests, a spawn path isn't covered by
the three hooks. Stale ids are pruned during `ResizeAll`.

**Hover** (`HoverInfo.Resolve`, every frame from `Update`): local player, cursor locked, not paused. Same ray as
`PlayerInteraction.Raycast` (camera position + forward, `m_InteractionDistance`, `m_InteractableLayer`). First the held box
(`BoxInteraction.m_Box`, only while its parent is `PlayerObjectHolder.m_ObjectHolder`, since `m_Box` can be stale), then
`Physics.Raycast`: a hit collider with a `Label` gives the slot info; with a `Box` it is a tag hit only if the same ray
also intersects the padded (0.03) union of the world bounds of the `MeshRenderer`s under `m_IconUI`. Racked boxes: vanilla
turns a racked box hit into its RackSlot interactable, but the hit collider is still the Box's, so this works.
Not handled: a ray that misses the box collider but would hit the (scaled) tag poking out beyond the box edge.

**Text** (`ClearTagsBehaviour.OnGUI`): IMGUI. No reusable tooltip in the game: `InteractionHintsManager` is the key-hint list
(one hint per `InputActionID`, localized keys), not free text. Dark rect (`GUI.DrawTexture` with `Texture2D.whiteTexture`) and
a rich-text label under the screen centre; font size scales with `Screen.height / 1080`. `new GUIStyle(GUIStyle)` isn't exposed
by the interop, so the style starts from `new GUIStyle()`. Unverified: that Unity calls `OnGUI` on an injected class (the first
draw logs a line).

## What the text shows

| Target | Name | Quantity | Price |
|---|---|---|---|
| Box (loose, racked, held) | `LocalizationManager.LocalizedProductName(id)` (fallback `ProductSO.ProductName`) | `Box.ProductCount` / `MaxProductCount` | `ProductSO.BoxPrice` |
| Shelf tag (`Label.DisplaySlot`) | same | `DisplaySlot.ProductCount` / `GridLayoutInStorage.productCount` | `BoxPrice` |
| Rack tag (`Label.RackSlot`) | same, id from `RackSlot.Data.ProductID` | `RackSlot.ProductCount` (all boxes) and `Data.BoxCount` boxes | `BoxPrice` |
| Vending tag | same | `VendingSlot.ProductCount` | `BoxPrice` |

`ProductSO.BoxPrice` (virtual, `WeightedProductSO` overrides) = `PriceManager.CurrentCost(id) * GridLayoutInBox.productCount`
(same as `PriceManager.TotalBoxPrice(id)`), i.e. the wholesale price of a **full** box at today's cost. It is shown for every
target, also for a half-empty box, so "box price" always means what a box of that product costs to buy. Formatted as `$0.00`
(invariant culture); the game's `Extensions.ToMoneyText` returns TMP rich text with size tags, so it isn't used.
`GridLayoutInStorage.productCount` for ice cream slots may not be the real capacity (vanilla overrides `Full` for them); the
quantity there could be misleading (unverified, not special-cased).

## Multiplayer reasoning

Purely local. The scale changes the local `Transform.localScale` of renderer-only quads (no colliders, no Photon components on
them; `PhotonTransformViewOptimized` syncs the box root only). The hover reads game state and draws with IMGUI. Nothing is
sent and no game state is written, so host and guests can run different settings or no mod. Box data a guest sees (product,
count) is whatever the game already synced; a guest's racked-box counts can lag behind the host's (dematerialized racked boxes
live in `RackSlotData`, which is what the rack tag reads).

## In-game test

1. Start the game with the mod; `LogOutput.log` has `Clear Tags 0.1.0 loaded` and `Patches applied (3/3)`.
2. Load a save with boxes (storage room, racks): expect `[ClearTags] ... boxes resized (x2)` after about 2 s (the first sweep)
   or no line if the hooks already handled them (that is the good case). Look at a box: the tag is twice as big and centred on
   the same spot. Check a small (8x8x8), a big (30x20x20) and a racked box.
3. Order a delivery / pick up and put down a box / open and close it: the tag stays big, never grows further.
4. Change `ScaleMultiplier` in the config manager (for example 3, then 1, then 2): all boxes follow at once, no compounding.
   Toggle `Enabled` off and on: boxes return to normal and back.
5. Crosshair on the tag of a box on the floor, on a rack, and the box in your hands (look down at it): the hover text appears
   below the crosshair with name, quantity and box price. Crosshair on the box body away from the tag: no text.
6. Crosshair on a shelf slot's icon label and on a rack slot's label: hover text with the quantity as in the table above.
7. Multiplayer (host with the mod, guest without, and the other way round): nothing different for the other player.

Send back the `[ClearTags]` lines from `BepInEx/LogOutput.log` (resize counts, `hover (...)` lines, any `OnGUI`/`hover failed`
errors), and say whether the tag looks right and whether the text is readable. If the shelf/rack hover never appears, say
whether the label collider is hit at all (the game's own highlight/hint appears when it is).

## Ideas, not done

- Scale shelf/rack tags: would need an overlay quad per slot with the product icon (`ProductAtlasManager.SetLabelData` works on
  any `MeshRenderer`; `Label.m_Renderer`/"Visuals/Optimized" is an unused quad of unknown size and orientation), or a replaced
  shelf shader. Not attempted without being able to see the result.
- Hover on a tag the ray reaches only outside the box collider (scaled tag poking out): needs a candidate list of nearby boxes.
