# ProductHighlight: agent notes

Improvement #13. While the local player holds a product box, outline the shelves/fridges/freezers labeled with its product (whole furniture,
empty or not). While they hold an **open** online-order paper bag, outline the product groups on shelves that the order still needs. Local and visual only.

## Status

- v0.1.7 (finding: shelf products were outlined for a delivery while the paper bag was still closed): bag mode now requires `OrderPaperBag.m_IsOpen` (+0x80; `IsOpen` getter and `set_IsOpen` read/write the same field; `isOpened` +0x90 is a different, unused flag). `Open()` (left click on a closed bag, after `OnDetachTag`) and `Close()` (close/back/drop paths in `OnlineOrderInteraction`) set it on the holder's own copy and broadcast `NetworkOrderPaperBag.SetOpen_Broadcast`; remote copies get it from `SetOpen_RPC` -> `SetOpenVisual`. So the holder's flag is correct on host and guest alike. A closed or just picked up bag now outlines nothing (log `paper bag (closed): 0 shelves, 0 product groups outlined`); open it and the groups appear within 0.25 s (`paper bag (open) for order #N ...`); close it and they vanish. Box mode untouched. **Not tested in-game.** Test: pick up a bag with an order selected in the notebook (no outline), left-click to open (groups outlined), close it with C (gone), re-open (back), drop (gone). Send the `[Highlight] paper bag (...)` lines (VerboseLogging on) from host and, if possible, a guest.
- v0.1.6 (review round 1 of the second review): the bag-mode signature is now the renderer-list length plus the first and last renderer (was a walk over every renderer each poll); the collection loops (guest scan, labeled-slot and order-group collection) skip a slot or display that throws and warn once, instead of the whole tick failing and clearing every outline. A product taken from an outlined group may stay outlined up to 0.25 s (accepted, cosmetic). **Not tested in-game.**
- v0.1.5 (review round 1, approved with minor findings): the guest scan re-reads the slot labels every tick (only the `FindObjectsOfType` display-list refresh stays at 3 s); "no body renderers" and outline-failure warnings are logged once per display per session; the bag-mode renderer signature is computed without building a list (the list is only built when the signature changed); the first-outline log budget is only spent while `VerboseLogging` is on; destroyed displays/slots are skipped in the sync loops. **Not tested in-game.**
- v0.1.4: GUID renamed to the `flamboyanthopper.` prefix, so BepInEx writes a new cfg file (`flamboyanthopper.producthighlight.cfg`); settings stored under the old GUID are orphaned.
- v0.1.3: settings reduced (commit "simplify mod settings"). Only `Enabled` and `VerboseLogging` (the cfg key `[Diagnostics] VerboseLogging` already existed, so existing cfgs keep their stored value; the default is now false for new configs) remain; the former `ShelvesForBox` and `ProductsForOrderBag` settings are always on (the off-branches and the "(shelf highlight off)" description text were removed as dead code). Their `SettingChanged` refresh hooks are gone; `Enabled` still triggers `RequestRefresh`.
- v0.1.2 (review round 2): `m_Displays` is read first and the native empty-slot lookup is skipped when it is null/empty (guest NRE fix); the guest scan re-checks `IsPlacingMode` per tick; the placing-mode "still held" test also needs the object within 5 m of the holder. **Not tested in-game.**
- v0.1.1 (review round 1): outlines persist while placing (placing mode unparents the held object); the display-type filter also applies in bag mode; failed displays are warned once per hold; submesh loop as the game's; order lines of one product are merged before subtracting the bag; fallback scan for labeled shelves when `DisplayManager.m_Displays` is empty (guest). **Not tested in-game.**
- v0.1.0: written and builds. **Not tested in-game**; everything below marked "unverified" is untested. No Harmony patches at all.
- Open questions for the first test (see "In-game test"): (1) does the outline show (an `Outlinable` made with `AddComponent` on a separate child object, targets
  built like the game does); (2) does an outline on a product group show although static shelf products are rendered through the instancing batch
  (`Renderer.forceRenderingOff` is true for them). Verified in the dump (review): EPOOutline's `Outlinable.UpdateVisibility` treats a renderer with `forceRenderingOff` as visible when `Renderer.enabled`, and the game's own highlights use the same renderers; still pending in-game;
  (3) is `DisplayManager.DisplayedProducts[pid]` really holding every labeled slot that has products (the game's open-box highlight relies on it).

## Files

| File | What |
|---|---|
| `src/Plugin.cs` | Config (`Enabled`, `VerboseLogging`), `[Highlight]` log helper, attaches the behaviour. |
| `src/HighlightBehaviour.cs` | The 0.25 s poll: what is held, which displays/slots qualify, create/destroy outlines. Doc comment has the mechanism. |
| `src/OutlineHandle.cs` | One owned `Outlinable` on a child GameObject (same recipe as the game's `HighlightManager`). |

## Mechanism

- **Poll, no patches.** `HighlightBehaviour.Update` (injected MonoBehaviour via `BasePlugin.AddComponent`) runs `Tick()` every 0.25 s (`Time.unscaledTime`) or at once after a
  setting change. Nothing is hooked, so "put in rack / trash / throw / drop / hand over / pick up" are all handled by the same check: is `PlayerObjectHolder.m_CurrentObject`
  a child of `m_ObjectHolder`? A `Box` gives the shelf set, an open `OrderPaperBag` the product-group set (a closed one gives nothing), anything else (or nothing) clears everything.
- **Held object:** `PlayerObjectHolder.m_CurrentObject` is "held" while it is a child of `m_ObjectHolder`, or in placing mode: `StartPlacingMode` sets `m_PlacingMode` (+0x60) and `PlacingRaycast` unparents the object while the aim hits a surface (`MoveToCarryingPosition` re-parents it). `DropObject` resets `m_PlacingMode`; throw/rack/bin don't (verified in the ISIL), so placing mode alone isn't enough: the object must also be active and have a **kinematic** Rigidbody (`HoldObject` makes it kinematic, drop/throw give physics back).
- **Shelves for a box:** `pid = Box.Data.ProductID`. Displays = `slot.m_Display` of (a) `DisplayManager.GetLabeledEmptyDisplaySlots(pid, list)` (native, the game's search used by
  customers/restockers: slots with `ProductID == pid` and no products) and (b) `DisplayManager.DisplayedProducts[pid]` (slots that have products; guarded with `slot.ProductID == pid` since
  `GetDisplaySlots` only prunes that dictionary lazily). Types TRASH, CRATE, ICE_CREAM_STAND are skipped (in bag mode too); every other type is outlined (shelf, fridge, freezer, pegboard, hanger, stall, bakery shelf, oven, all present in the shipped assets), which the README lists. Orders split over several bags: only the held bag's contents are subtracted, so products already packed in another bag still count as needed (a hint only). **Guest:** `DisplayManager.m_Displays` is only filled by `LoadDisplayData` (host) and `AddDisplay`, which returns at once on `IsConnectedAndSlave` (ISIL, both); `Awake` does not allocate it (the `new List<Display>` is in `LoadSaveProgress`), so on a guest it is **null**, and the native `GetLabeledEmptyDisplaySlots` dereferences it without a null check (NRE). `CollectShelves` therefore reads `m_Displays` first and only calls the native lookup when it is non-empty. `m_DisplayedProducts` is filled on guests too (`AddDisplaySlot` isn't gated; `DisplaySlot.Initialize` and `Display.AddBackToDisplayManagerAfterPlaced` register every slot with a label, empty or not), but `GetDisplaySlots` (restock/customer AI, host only) prunes empty ones lazily. When `m_Displays` is null or empty the mod therefore scans `FindObjectsOfType<Display>` itself (list refreshed every 3 s, slot labels checked every tick, i.e. 0.25 s; log `fallback scan ...`). "Empty" is treated like null on purpose: whether a guest's list is null or an empty non-null list is unverified in-game, and on a host with no displays yet the scan is harmless (finds nothing, one `FindObjectsOfType` per 3 s while a box is held). Carried furniture is removed from both by the game
  (`Display.RemoveFromDisplayManagerWhileCarrying`), so it isn't outlined while moved. Outlined renderers (`BodyRenderers`): enabled mesh/skinned renderers under the display
  that aren't under any `DisplaySlot` (products, price tag, label), aren't the placing-mode hologram (`m_PlacingMode.gameObject`) and aren't under "Sale Signs"; computed once per outline.
- **Product groups for a bag:** order = `bag.m_OrderData` if `bag.IsTagAttached`, else `OnlineOrderInteraction.m_OrderNotebookContainer.CurrentOrder` (the same rule as
  `BulkShelfTransfer/src/BagBulk.cs`, without the courier/other-player checks: it is only a hint). Needed products = `OrderListData.OrderItems` where
  the order's quantity per product (lines of one product merged) minus the products of that id in `bag.m_SortedProducts`, > 0. Slots = `DisplayedProducts[pid]` with `slot.ProductID == pid` and `ProductCount > 0`; renderers =
  `slot.m_Highlightable.m_RenderersToHighlight` (all product renderers; `DisplaySlot.AddProduct` appends the new product's `GetComponentsInChildren<Renderer>(true)` to it). The outline
  is rebuilt when the list's length or its first/last renderer changes (products are appended and removed at the end), which is how stock changes show up.
- **Outline recipe** (`OutlineHandle`): child `GameObject` + `AddComponent<Outlinable>()` (EPOOutline, in `Assembly-CSharp-firstpass`), `outlineParameters.color = HighlightManager.m_OutlineColor`,
  `TryAddTarget(new OutlineTarget(renderer, submesh))` for `submesh < GetSubmeshCount(renderer)`; destroy the GameObject to remove it. Own object so the game's `ClearScrollHighlights`,
  hover highlight and other mods (WholeTruckHighlight uses `Highlightable.AddOrRemoveRenderer` on a vehicle's `SortedBoxPlacementArea`; no overlap) never touch it.
- Cost: a few interop calls per tick while holding something (one native lookup + a short list walk per qualifying product), one `GetComponent` pair when holding something else, nothing
  when the hands are empty. No `FindObjectsOfType` except the guest fallback below (every 3 s, only while a box is held).

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

1. Start the game with the mod; `LogOutput.log` has `Product Highlight <version> loaded (enabled)` (version = `Plugin.Version`).
2. Label a shelf (or fridge/freezer) for some product, leave it **empty**; label another shelf for a different product. Pick up a **closed** box of the first product: the whole labeled shelf gets a green outline
   (the other doesn't), plus the game's own outline on racked boxes of that product. Log: `[Highlight] box of product N: 1 shelves, 0 product groups outlined` and `outline created on shelf ...: K target(s) from R renderer(s)`.
   Repeat with a fridge and a freezer, and a shelf that has the product on it (still outlined as a whole).
2b. **As a guest** (mod on the guest, host any): the same with an EMPTY labeled shelf; it must get the outline. `[Highlight] fallback scan (DisplayManager.m_Displays is null or empty, as on a guest): N display(s) ...` shows the fallback ran (if it never appears on the guest, `m_Displays` isn't empty there after all, which is fine as long as the shelf is outlined).
3. Relabel the shelf (or label a second one) while holding the box: outlines follow within about a quarter second. Open the box (the game's own product-group outlines appear too), close it. Put the box down, throw it, rack it,
   trash it: the outline disappears (`nothing held: 0 shelves, 0 product groups outlined`). Pick up furniture to move it while holding a box: not outlined while carried. Press Drop (placing mode) and move the aim across surfaces: the outlines stay on the whole time (no flicker); cancel placing: they stay; place/drop: they go.
4. Place an online order (several products, some on shelves, one nowhere). Pick up a paper bag with that order selected in the notebook (or a tagged bag): nothing is outlined while it is closed. Left-click to open it: the **products** on the shelves for the order's items get outlined
   (groups of products, not the shelves); an empty shelf labeled for an ordered product gets nothing; a product not on any shelf: nothing. Log: `paper bag (open) for order #N (M product(s) still needed): 0 shelves, K product groups outlined`.
   Put one ordered product into the bag: when the bag holds the full quantity that product's groups drop out. Take the last product off a shelf group: its outline goes. Put the bag down: all gone.
5. Toggle `Enabled` in the config manager while holding: outlines vanish/return at once.
6. Multiplayer: as host and as guest (guest with the mod, host without, and the other way round): your outlines only on your screen; the other player sees nothing different.
7. With Whole Truck Highlight installed (as on the host): carry a box to the truck, the truck outline still works.

Send back the `[Highlight]` lines from `BepInEx/LogOutput.log` (turn on `VerboseLogging` in the cfg first; it's off by default) (the `outline created` lines say how many targets were accepted: `0 target(s)` means the renderers were rejected) and a screenshot of a highlighted
shelf and of highlighted product groups. If nothing shows but the log says shelves were outlined, the outline draws nothing for that kind of renderer: tell me which one.
