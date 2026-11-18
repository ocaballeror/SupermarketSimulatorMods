# ProductHighlight: agent notes

Improvement #13 (`TODO`). While the local player holds a product box, outline the shelves/fridges/freezers labeled with its product (whole furniture,
empty or not). While they hold an online-order paper bag, outline the product groups on shelves that the order still needs. Local and visual only.

## Status

- v0.1.0: written and builds. **Not tested in-game**; everything below marked "unverified" is untested. No Harmony patches at all.
- Open questions for the first test (see "In-game test"): (1) does the outline show (an `Outlinable` made with `AddComponent` on a separate child object, targets
  built like the game does); (2) does an outline on a product group show although static shelf products are rendered through the instancing batch
  (`Renderer.forceRenderingOff` is true for them; the game's own hover/open-box highlights target the same renderers, which is the reason to expect it works);
  (3) is `DisplayManager.DisplayedProducts[pid]` really holding every labeled slot that has products (the game's open-box highlight relies on it).

## Files

| File | What |
|---|---|
| `src/Plugin.cs` | Config (`Enabled`, `ShelvesForBox`, `ProductsForOrderBag`, `VerboseLogging`), `[Highlight]` log helper, attaches the behaviour. |
| `src/HighlightBehaviour.cs` | The 0.25 s poll: what is held, which displays/slots qualify, create/destroy outlines. Doc comment has the mechanism. |
| `src/OutlineHandle.cs` | One owned `Outlinable` on a child GameObject (same recipe as the game's `HighlightManager`). |

## Mechanism

- **Poll, no patches.** `HighlightBehaviour.Update` (injected MonoBehaviour via `BasePlugin.AddComponent`) runs `Tick()` every 0.25 s (`Time.unscaledTime`) or at once after a
  setting change. Nothing is hooked, so "put in rack / trash / throw / drop / hand over / pick up" are all handled by the same check: is `PlayerObjectHolder.m_CurrentObject`
  a child of `m_ObjectHolder`? A `Box` gives the shelf set, an `OrderPaperBag` the product-group set, anything else (or nothing) clears everything.
- **Shelves for a box:** `pid = Box.Data.ProductID`. Displays = `slot.m_Display` of (a) `DisplayManager.GetLabeledEmptyDisplaySlots(pid, list)` (native, the game's search used by
  customers/restockers: slots with `ProductID == pid` and no products) and (b) `DisplayManager.DisplayedProducts[pid]` (slots that have products; guarded with `slot.ProductID == pid` since
  `GetDisplaySlots` only prunes that dictionary lazily). Types TRASH, CRATE, ICE_CREAM_STAND are skipped. Carried furniture is removed from both by the game
  (`Display.RemoveFromDisplayManagerWhileCarrying`), so it isn't outlined while moved. Outlined renderers (`BodyRenderers`): enabled mesh/skinned renderers under the display
  that aren't under any `DisplaySlot` (products, price tag, label), aren't the placing-mode hologram (`m_PlacingMode.gameObject`) and aren't under "Sale Signs"; computed once per outline.
- **Product groups for a bag:** order = `bag.m_OrderData` if `bag.IsTagAttached`, else `OnlineOrderInteraction.m_OrderNotebookContainer.CurrentOrder` (the same rule as
  `BulkShelfTransfer/src/BagBulk.cs`, without the courier/other-player checks: it is only a hint). Needed products = `OrderListData.OrderItems` where
  `ProductCount - (products of that id in bag.m_SortedProducts) > 0`. Slots = `DisplayedProducts[pid]` with `slot.ProductID == pid` and `ProductCount > 0`; renderers =
  `slot.m_Highlightable.m_RenderersToHighlight` (all product renderers; `DisplaySlot.AddProduct` appends the new product's `GetComponentsInChildren<Renderer>(true)` to it). The outline
  is rebuilt when a hash of that list changes, which is how stock changes show up.
- **Outline recipe** (`OutlineHandle`): child `GameObject` + `AddComponent<Outlinable>()` (EPOOutline, in `Assembly-CSharp-firstpass`), `outlineParameters.color = HighlightManager.m_OutlineColor`,
  `TryAddTarget(new OutlineTarget(renderer, submesh))` for `submesh < GetSubmeshCount(renderer)`; destroy the GameObject to remove it. Own object so the game's `ClearScrollHighlights`,
  hover highlight and other mods (WholeTruckHighlight uses `Highlightable.AddOrRemoveRenderer` on a vehicle's `SortedBoxPlacementArea`; no overlap) never touch it.
- Cost: a few interop calls per tick while holding something (one native lookup + a short list walk per qualifying product), one `GetComponent` pair when holding something else, nothing
  when the hands are empty. No `FindObjectsOfType`.

## Game reference: the game's highlight system (verified in the dumps, not observed in-game)

- `HighlightManager` (`NoktaSingleton`): `m_OutlineColor` (+0x20, the green), `m_CurrentHighligtedObject` (+0x30, the hover target), `m_ScrollHighlights` (+0x38, `List<Highlightable>`),
  `m_ScrollActive/m_ScrollProductID/m_ScrollTrackPlacement`. `SetHighlight(h, player/alpha)` is the hover outline; `SetScrollHighlights(list, productID, trackPlacement)` /
  `ClearScrollHighlights()` are the persistent ones (callers: `Box.UpdateMatchingDisplaySlotsHighlight`, `CrateInteraction`). `ApplyScrollHighlightToOne(h)`: get an `Outlinable`
  on `h`'s GameObject, `outlineParameters.color = m_OutlineColor`, one `OutlineTarget` per submesh of every renderer in `h.m_RenderersToHighlight`, `h.Highlighted = true`.
  Clearing destroys that component and sets `Highlighted = false`. **No prefab or scene object carries an `Outlinable`** (UnityPy scan of all assets: only the `Outliner` on the main camera), so the component
  is added on demand and removed afterwards. `NotifySlotGainedProduct` (called by `DisplaySlot.AddProduct`) adds a slot that gains the tracked product while a box is held.
- `Highlightable` (+0x20 `m_RenderersToHighlight`, +0x28 `m_Highlighted`, +0x30 `OnHighlightedChanged`); `AddOrRemoveRenderer(renderers, add)` keeps an already highlighted object's outline targets in sync.
  Present on `Display` roots, `DisplaySlot`s (`DisplaySlot.m_Highlightable`, +0x38), `Label`s and `PriceTag`s (prefab data, verified with UnityPy); `Product.Awake` also fetches one.
- `Box.UpdateMatchingDisplaySlotsHighlight` (`Box.m_ActiveHighlightMode` +0xC8: `None 0`, `Display 1`, `Rack 2`): only for a box under the local player's transform. `BoxData.IsOpen` (+0x20) set → `Display`
  (`AddDisplayedProductHighlightables`: for each slot in `DisplayManager.m_DisplayedProducts[pid]`, `DisplaySlot.ActiveHighlightable` = the slot's highlightable if it has products, else its `Label`'s highlightable);
  closed → `Rack` (`AddRackedProductHighlightables`: `RackManager.GetAllRackslotsWithLabel(pid)`, the racked box's `Label.Highlightable` or the slot's own). Then `SetScrollHighlights(list, pid, true)`.
  So the game already highlights shelf *labels* of empty slots for an **open** box; a closed box gets the rack boxes only.
- `DisplayManager` (+0x20 `m_DisplayDatas`, +0x28 `m_Displays`, +0x38 `m_DisplayedProducts` `Dictionary<int, List<DisplaySlot>>`). `AddDisplaySlot(pid, slot)` is called from `DisplaySlot.Initialize/AddProduct/
  SpawnProduct/AddBackToDisplayManagerAfterPlaced`; `GetDisplaySlots(pid, hasProduct, list)` removes slots without products from the dictionary lazily. `GetLabeledEmptyDisplaySlots(pid, list)`: clears `list`, then for every display
  in `m_Displays` adds the slots with `ProductID == pid`, a data object and `ProductCount <= 0`.
- `DisplaySlot`: `m_Highlightable` +0x38, `m_Label` +0x30, `m_Products` +0x50, `m_Display` +0x58, `m_ProductCountData` (`ItemQuantity`) +0x68. `ProductID` (virtual) = `m_ProductCountData.FirstItemID` (the label: the first
  key of the products dictionary; `HasLabel` = `FirstItemID > 0`), `ProductCount` (virtual) = `FirstItemCount`. Ice cream slots override them.
- `Display` (+0x20 `m_Type` `DisplayType`: FREEZER, FRIDGE, CRATE, SHELF, STALL, PEGBOARD, HANGER, OVEN, BAKERY_SHELF, ICE_CREAM_STAND, TRASH; +0x28 `m_PlacingMode`; +0x48 `m_DisplaySlots`).
  Prefab layout (UnityPy, `sharedassets2.assets`): root `Display` + `Highlightable` + `LabelMaskController`; children `Visuals` (the model mesh), `Colliders`, `Displays` (the `DisplaySlot` objects, each with `Price Tag` and `Label`
  children, both inactive until used), `Placing Mode` (hologram mesh), `Sale Signs` (inactive sign meshes), `Interaction Zone Indicator Canvas`, snap points. Fridges/freezers have the same shape.
- Instanced products: static shelf products are drawn through `InstancedRenderer`/`SubRenderer` batches and their own `Renderer`s have `forceRenderingOff = true` (`Product.ToggleInstanced`). EPOOutline draws with
  `CommandBuffer.DrawRenderer`; the game's own highlights rely on the same renderers, so this is expected to work (unverified).
- `EPOOutline` types live in `Assembly-CSharp-firstpass.dll` (stubs: `ilspycmd -p -o <dir> <game>/BepInEx/interop/Assembly-CSharp-firstpass.dll`; not in `decompiled/Assembly-CSharp`). `Outlinable`: `outlineParameters`
  (`OutlineProperties`: `enabled` +0x10, `color` +0x14, `dilateShift`, `blurShift`, `fillPass`), `outlineTargets`, `renderStyle`, `TryAddTarget`, `GetSubmeshCount`.

## Multiplayer reasoning

Purely local: reads the local player's hands and the local `DisplayManager`/slot data (synced by the game on every client), draws outlines on child objects of local furniture. No RPC, no
`PhotonView`, no game state written, no config on other players. Host or guest alike; an unmodded peer is unaffected. A guest's `DisplayManager` has the same displays and labels as the host's
(furniture and labels are synced), so the shelf set should match; stock can lag by an RPC.

## In-game test

1. Start the game with the mod; `LogOutput.log` has `Product Highlight 0.1.0 loaded (enabled)`.
2. Label a shelf (or fridge/freezer) for some product, leave it **empty**; label another shelf for a different product. Pick up a **closed** box of the first product: the whole labeled shelf gets a green outline
   (the other doesn't), plus the game's own outline on racked boxes of that product. Log: `[Highlight] box of product N: 1 shelves, 0 product groups outlined` and `outline created on shelf ...: K target(s) from R renderer(s)`.
   Repeat with a fridge and a freezer, and a shelf that has the product on it (still outlined as a whole).
3. Relabel the shelf (or label a second one) while holding the box: outlines follow within about a quarter second. Open the box (the game's own product-group outlines appear too), close it. Put the box down, throw it, rack it,
   trash it: the outline disappears (`nothing held: 0 shelves, 0 product groups outlined`). Pick up furniture to move it while holding a box: not outlined while carried.
4. Place an online order (several products, some on shelves, one nowhere). Pick up an open paper bag with that order selected in the notebook (or a tagged bag): the **products** on the shelves for the order's items get outlined
   (groups of products, not the shelves); an empty shelf labeled for an ordered product gets nothing; a product not on any shelf: nothing. Log: `paper bag for order #N (M product(s) still needed): 0 shelves, K product groups outlined`.
   Put one ordered product into the bag: when the bag holds the full quantity that product's groups drop out. Take the last product off a shelf group: its outline goes. Put the bag down: all gone.
5. Toggle `Enabled`, `ShelvesForBox`, `ProductsForOrderBag` in the config manager while holding: outlines vanish/return at once.
6. Multiplayer: as host and as guest (guest with the mod, host without, and the other way round): your outlines only on your screen; the other player sees nothing different.
7. With Whole Truck Highlight installed (as on the host): carry a box to the truck, the truck outline still works.

Send back the `[Highlight]` lines from `BepInEx/LogOutput.log` (the `outline created` lines say how many targets were accepted: `0 target(s)` means the renderers were rejected) and a screenshot of a highlighted
shelf and of highlighted product groups. If nothing shows but the log says shelves were outlined, the outline draws nothing for that kind of renderer: tell me which one.
