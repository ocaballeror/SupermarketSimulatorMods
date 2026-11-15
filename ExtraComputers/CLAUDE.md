# ExtraComputers: notes for agents

Player-facing info is in [README.md](README.md). Shared rules and game internals are in the root [CLAUDE.md](../CLAUDE.md). The root-cause style write-ups are the doc comments at the top of `src/ComputerFurniture.cs` (the furniture) and `src/ComputerSession.cs` (using it).

## Status

v1.2.1 (TODO Improvement #7). First in-game test (host alone, 2026-11-15) failed: `Building the computer furniture failed: no usable furniture prefab to copy the network setup from`. The old template needed a "donor" furniture prefab (plain `Furniture` + `NetworkFurniture` + `PhotonView` + placing mode, no type scripts) and none qualified (which check failed wasn't logged). Consequences seen by the user: the shop showed the drawn placeholder icon (the model render needs the template), and opening the delivered box despawned it and spawned nothing (`Can't spawn a computer`), leaving the player in an empty placing mode with drop/throw/sell/place hints that did nothing.

v1.2.0/1.2.1 fixes:
- The template is now a stripped copy of the store computer's own root, plus added `Furniture` + `NetworkFurniture` (see Design). No donor.
- The icon is rendered from the template, or from the store computer itself if the template is missing; a drawn placeholder is retried up to 3 times. The render now has its own directional light (layer-culled) and is rejected if too dark. Game assets checked with UnityPy (2026-11-15): the furniture icon atlas (`T_IconAtlas_Furnitures`, renders) has no computer; `T_Vending_ComputerIcon` is a vending machine; `icon_device_63` (sharedassets2) is a flat white laptop/monitor UI glyph. Config `Icon = GameSymbol` uses that glyph (blitted through a RenderTexture, tinted dark, centred with a margin); default `Model` keeps the render, which matches the other furniture pictures. Ask the user which they prefer.
- `OpenBoxGate` also refuses to open a computer box while the template can't be built (or, on an online host, can't be registered in `DefaultPool.ResourceCache`), on all three paths (offline `OpenBox`, host `NetworkOpenBox`, guest request), so the player keeps the box instead of getting stuck. Online, `NetworkOpenBox` calls `FurnitureBox.OpenBoxNetwork` before `SpawnFurniture`, so a spawn that fails after the gate still loses the box.
- Without a template the shop item is hidden and `PurchaseGatePatch` refuses a cart with a computer: `MarketShoppingCart.Purchase` dereferences `FurnitureSO.FurniturePrefab` for each cart furniture after `DeliveryManager.Delivery` and would throw (ISIL, review round 4).
- `Presence` publishes "has the mod" as `Active && Template != null`, so a client whose build failed counts as unmodded for the host's guard (covers a guest whose own build failed, and a host whose build failed as seen by a modded guest's request gate).

## Review round 4 (v1.2.1)

- Applied: purchase/shop/presence handling of a failed build (above); box label scale = atlas material tiling × 8 (vanilla only sets the offset `AtlasPosition * 0.125`, so the cell size comes from the tiling or the mesh UVs; × 8 fills the label in both cases; logged as `Box label material: ...`); icon light and brightness check; the template's root scale is forced to 1 (unboxing sets scale 1 anyway; a warning is logged if the store computer isn't at unit scale); `FindRealComputer` skips anything under the template holder; online host gate requires the Photon registration.
- Icon: the reviewer found `icon_device_63`; now a config option (above).
- To check in the multiplayer test: the template keeps PUN's classic `PhotonTransformView` from the store computer (furniture prefabs use the game's `PhotonTransformViewOptimized`); `Synchronization` comes from a furniture prefab. Computers spawned from a save (box view -1) or for a late joiner whose box opener left aren't parented under `FurnitureManager` by `CheckFurnitureType`; vanilla furniture has the same path. Check the `PhotonView: ... sync X` line isn't `Off`, and that a late joiner sees a moved computer in the right place.

Still unverified in-game: everything after the template builds. Expect fixes. Open questions for the next logs:
- `Removed from the computer copy: ...` and `PhotonView: ownership X -> Y (from <prefab>), sync ..., observes [...]`.
- `Placing settings: ...` (tags, renderers, owner colliders, placing box, zone canvas).
- Whether `Computer icon rendered from the model` appears, and how the box label looks.
- Whether placing, moving, boxing up and saving work with the computer's own colliders and placing mode.

## First in-game test (send these back)

Host alone, then with a modded guest, then with an unmodded guest. Log tag `[ExtraComputers]` in `BepInEx/LogOutput.log` (host and guest).
1. Startup/scene load: `Computer furniture registered`, `Computer furniture prefab built from a copy of the store computer`, `Removed from the computer copy`, `PhotonView: ownership ...`, `Placing settings: ...`, `Computer icon rendered from the model`, any `Failed`/`error` lines, and the hierarchy dumps (`Store computer hierarchy`, `Computer furniture prefab`).
2. Buy one ($500) in the furniture shop, take the delivery, check the box icon, unbox, place, use it (shop/pricing/hire), move it, box it up, save and reload. Report what looks wrong (collider too big or blocking, label, screen).
3. Two players, two computers (and one on the store computer): independent screens. Two players on the same extra computer: the second gets "in use by another player" (`Computer view N is in use by another player`).
4. Modded guest: price shown equals the host's (`Computer price is now ...`), computer appears in their shop, they can buy it, the box and computer show for them. Lines `Published player properties (mod=True ...)` on both.
5. Unmodded guest joins (or `Enabled` off on the guest): host log `<name> doesn't have the Extra Computers mod: ... paused`, shop item hidden (`Computer hidden in the shop`), a purchase attempt gives `Purchase refused`, opening a computer box gives `Computer box not opened`. When they leave: `Every player has the Extra Computers mod`. Also send the guest's log if it throws on placed computers (expected for pre-existing ones).
5b. With a modded guest AND an unmodded guest in the room: the modded guest tries to open a computer box; the host log must show `Computer box not opened` (this is the guest-request path). Also with the shop item hidden, check there's no flicker when switching shop tabs.
6. Toggle `Enabled` off/on in the config manager while playing: the shop item hides and returns. Optionally open a computer box while it is off and report what happens.

## Design

**Furniture type (`ComputerFurniture`).**
- A `FurnitureSO` (id **917001**) is copied from an existing one: our price, `IsMainFurniture`, and the most common furniture `BoxSize`.
  - It's added to `IDManager.m_Furnitures` (on `IDManager.Awake`, `CartManager.Initialize` and `FurnituresViewer.SpawnFurnitures`).
  - `IDManager.FurnitureSO(917001)` is answered directly.
  - Name: `LocalizationManager.LocalizedFurnitureName` → "Computer".
  - Box label: `ProductAtlasManager.SetFurnitureIcon` → our own material.
- The prefab template lives under an inactive `DontDestroyOnLoad` holder, so nothing in it ever runs `Awake`. It's built once per game run as a copy of the store computer's root (`m_PlacingMode.m_Furniture`, "Computer &&"):
  - kept: meshes, the root's layer-6 box colliders (what the interaction raycast hits, so `Furniture` on the root is the `IInteractable`), `PhotonView`, `PhotonTransformView(Optimized)`, `NavMeshObstacle`, `Highlightable`, snap points, the "Placing Mode" child (`Rigidbody`, `BoxCollider`, `FurniturePlacingMode`) and the placing mode's `m_InteractionZoneCanvas` subtree untouched;
  - removed: the PC camera (`m_PCCam`), every other canvas subtree (the screen UI), and every other script (`Computer`, `ComputerOperatingSystem`, `MusicPlayerManager`, `NetworkComputer`, `NetworkMarketShoppingCart`, ...). Renderers left under `m_ComputerScreen` get suffix `#ecscreen` (none expected: the screen is all canvases);
  - `FurniturePlacingMode` references were remapped into the copy by `Instantiate`; `m_Furniture` and `m_ParentPhotonView` are re-set explicitly;
  - added: `Furniture` (`m_ID` 917001, `m_PlacingMode`, `m_PhotonView`, `m_NetworkFurniture`) and `NetworkFurniture`. The game has no `Furniture` subclasses; `Furniture.Start` only lazily looks up `InteriorSpotLight`/`Speaker`, and `get_PhotonView`/`OwnNetworkFurniture` fall back to `GetComponent`;
  - `PhotonView`: `sceneViewId` = 0 (else an offline `LeanPool.Spawn` copy would take the store computer's view id in `Awake`), `RpcMonoBehaviours` cleared, `ObservedComponents` filtered to surviving components, `OwnershipTransfer`/`Synchronization` copied from the first furniture prefab with a root `PhotonView` (`NetworkFurniture.OnPhotonInstantiate` transfers ownership to the unboxing player; `Fixed` would ignore that).
  - `Furniture.m_ID` = 917001 is serialized, so it survives `Instantiate`.
  - The session code uses the furniture root itself as the "model" (same layout as the store computer's root).
- Spawning: both `FurnitureGenerator.SpawnFurniture` overloads are replaced for our id.
  - Offline: `LeanPool.Spawn(template)`.
  - Host: `PhotonNetwork.InstantiateRoomObject("Furnitures/ExtraComputers/ExtraComputer", ..., {boxViewId})`. Box view id is `-1` when loading a save, as in vanilla.
  - The template is put in `DefaultPool.ResourceCache` under that id. A `DefaultPool.Instantiate` prefix builds it on demand on guests.

**Using one (`ComputerSession`).**
- `Furniture.InstantInteract` prefix, for our id and not while being placed:
  1. Moves the real `m_ComputerScreen`, `m_PCCam` and `m_PlayerPosition` under scene-root anchors. Each anchor mirrors that part's parent, transposed from the store computer's root to the extra computer's model.
  2. Hides the extra computer's own `#ecscreen` renderers.
  3. Calls the vanilla `Computer.InstantInteract`, with `IsOccupied` forced false for the call and restored afterwards.
  4. Sets `PlayerInteraction.m_CurrentInteractable` to the computer, so `Interact` activates `ComputerInteraction` (type 7). Pointing it at the furniture would throw in `ComputerInteraction.set_Interactable`, which casts to `Computer`.
- `NetworkComputer.InteractComputer_Broadcast` is skipped while the session is active, so other players never see the store computer as occupied.
- End of session:
  - The `Computer.Back` postfix ends it after Back has placed the player at the relocated `m_PlayerPosition`.
  - A `set_OpenPC(false)` outside Back also ends it.
  - The parts are restored to their original parent, local pose and sibling index.
- The anchors follow the model every frame (`Computer.LateUpdate` postfix). They aren't children of the furniture, so boxing up a computer someone is using doesn't destroy the real UI.
- Furniture getters for our id: `InteractionType` → BOTH, `InstantInteractionHint` → `_interact`.

## Multiplayer reasoning

- Each client has exactly one computer UI and one local player, so borrowing it gives every player an independent session. Nothing about the session is networked except the avatar's position, which the vanilla `InstantInteract` and `Back` set.
- Data changes go through the same networked code paths as on the store computer.
- Furniture is host-spawned room objects. Guests need the mod for the prefab id, the `FurnitureSO` (box setup, `IDManager.FurnitureSO`) and the interaction patches. An unmodded guest gets Photon "prefab not found" errors and null `FurnitureSO` exceptions on boxes: `FurnitureBox.Initialize` and `Setup` dereference `IDManager.FurnitureSO(id)` without a null check (ISIL, jump to the NRE thrower).
- **Guard for unmodded players (`Presence.cs`).** Modded clients publish Photon player properties (`oscar.extracomputers` bool, `.price` float from the master, `.use` int PhotonView id of the computer in use). Only `RoomPlayerPropsListener` reads player properties, and only `png`/`sid`, so extra keys are inert for unmodded clients (checked in PlayerCrouch's review). `Presence.Tick` runs about once a second from the `Computer.LateUpdate` postfix (publish, price sync, log when the blocked state changes, show/hide the shop item).
  - While any other player lacks the property (config `ProtectUnmoddedPlayers`, default on), the host: hides the shop entry (`FurnitureSalesItem.Setup` postfix remembers it; `SpawnFurnitures` only runs once per scene, so the item is toggled with `SetActive`), refuses `MarketShoppingCart.Purchase` with a computer in the cart (the host's own click and a guest's `Purchase_Request` -> `Purchase_Response` both end there; guests skip the check because `IsConnectedAndSlave` only sends a request), and refuses `FurnitureBoxInteraction.NetworkOpenBox` for a computer box. Verified in the ISIL: the host's own click goes `OnUse` -> `OpenFurnitureBox_Broadcast` -> `NetworkOpenBox`, a guest's click goes `OpenFurnitureBoxRequest` -> RPC -> `OpenFurnitureBoxRequest_RPC` -> `NetworkOpenBox` (no broadcast method in between), and `NetworkOpenBox` has no other callers. On the host's path nothing else changes state first; on a guest's path only `m_InAnimationTransition` is set and reset by a delayed call.
  - Fails open: errors never block. Not covered, only documented: computers and boxes that exist before the player joins (placed ones, saved boxes, items in the cart, which `CartManager.OnSyncRequest` sends as JSON; what the unmodded guest's cart UI does with an unknown furniture id wasn't checked). Spawning in `ComputerSpawner` only warns, since refusing a save-load spawn would lose the computer.
  - Reading the property: `Presence.GetMod` distinguishes absent / false / true / unreadable. Unreadable (an `Unbox<bool>` failure) counts as modded and warns once, so a read problem can't lock everybody out. With `VerboseLogging`, `Other players: <name>: mod=Yes (raw True); ...` is logged whenever it changes; check it in test step 4.
  - The shop item is re-asserted every frame from a cached state (refreshed once a second), so a shop UI that re-activates its items doesn't flicker.
  - No custom popup text exists in the game (`RaiseInteractionWarning` shows fixed localized texts per `InteractionWarningType`), so refusals are log-only on the host; the shop item being hidden is the visible cue.
- **Price** is a per-client config, but `So.Cost` also drives the cart UI. The host publishes its price; guests with the mod apply it (`Presence.EffectivePrice`). The host charges its own `So.Cost` anyway.
- **In use marker:** a player on an extra computer publishes its PhotonView id as `oscar.extracomputers.use`; `ComputerSession.TryStart` refuses (`IN_USE_BY_OTHER_PLAYER` warning) if another player has the same id. Race between two simultaneous opens is possible and harmless (avatars overlap).
- `NetworkComputer.OnSyncRequest` (late joiners) sends the host's `IsOccupied`. The session restores it, so a host on an extra computer doesn't make the store computer look occupied.

## Review round 1 (v1.1.0)

- Major 1 (unmodded guest): implemented as above. Major 2 (removal/disabled at start): documented in README and the `Enabled` description; no code guard, because a fail-open patch on `FurnitureBox.Initialize/Setup` only exists while the mod is loaded and enabled, and skipping those methods would leave broken boxes.
- Minor 3: when disabled at runtime the `FurnitureSO` stays in `IDManager.m_Furnitures` (existing boxes and computers keep resolving it; removing it would throw on them), `IsMainFurniture` goes false and the shop item is hidden. Round 2 removed a `Furnitures/Furniture/ExtraComputer` alias I had added: `FurnitureGenerator.SpawnFurniture` has typed network spawners only (Rack, Display, Vending, Checkout, CategorySign, Scale, IceCreamStand, InteriorLight, Trash, Speaker, SecurityCamera, Antenna) and a plain `Furniture` is `LeanPool.Spawn`ed locally, so the alias was dead code. With the mod off, an already bought computer box may not unbox correctly (the label also falls back to the donor atlas icon); the README says to turn the mod back on first.
- Minor 4: `ComputerBackPatch` has a Finalizer; `InBack` is also reset in `TryStart` and `End`. Minor 5: `IsOccupied` and `OccupiedPlayer` are restored unconditionally after the vanilla `InstantInteract` (its only readers are inside `InstantInteract` itself, verified).
- Minor 6: the in-use property above. Minor 7: `FurniturePlacingMode.m_PhysicalColliders` is never read by native code (only its own getter, no callers), so adding the root box there changes nothing; `IPlacingMode.OverlapBoxes` does consult `m_OwnerCollider` (+0x38), where the root box already is. The margin was reduced from 2 cm to 1 cm per side. Check in-game that the root box doesn't block the player or placement.
- Minor 8: price sync above.
- Nits: selling is only for unopened furniture boxes (`SellFurnitureBox_Broadcast`); README wording fixed. The donor's Rigidbody is left alone (the donor is a vanilla furniture that stays put) and now logged (`Base furniture keeps a Rigidbody`). The name stays English. `KeyOut` only removes the exact clear colour plus connected magenta-ish edge pixels. The icon `RenderTexture` is explicitly sRGB. `VerboseLogging` stays on until the first in-game confirmation.

## Review round 2 (v1.1.1)

- N1 (guest unboxing bypassed the gate): confirmed in the ISIL, fixed by patching `FurnitureBoxInteraction.NetworkOpenBox` (both paths reach it).
- N2: `GetMod` as above, plus the verbose per-player log.
- N3: no in-game feedback is possible without a suitable `InteractionWarningType` (the existing ones have fixed, unrelated texts, so none was reused); documented in the README that refusals are silent on screen.
- N4: alias removed, README softened.
- N5: shop item refreshed every frame from a cached state; the stale `.use` case is accepted as negligible (Photon drops a leaving or crashed player's entry); the README no longer calls the setting host-only.

## Review round 3 (v1.1.2)

- R3-1: `ComputerFurniture.OnActiveChanged` refreshes the blocked cache only when `ShopItem` exists, so `BasePlugin.Load()` never reaches the Photon statics.
- R3-2: confirmed in the ISIL (`OpenFurnitureBoxRequest` calls `PhotonView.TransferOwnership(MasterClient)` before the RPC). The check now also runs on the requesting client (`OpenBoxRequestGatePatch`, prefix on `NetworkFurnitureBoxInteraction.OpenFurnitureBoxRequest`), sharing `OpenBoxGate.Refuse` with the authoritative host-side gate. A modded guest sees the other players, including the host. Log line: `Computer box not opened (guest side)` / `(host side)`. Test step 5b: expect the guest-side line in the guest's log and the box still following the guest's hand.
- R3-3: `EffectivePrice` reads the master's mod state once.

## Pitfalls / follow-ups

- While a session is active, this client's store computer shows a blank monitor, because its screen is borrowed. That's cosmetic. A fix would need a second screen, i.e. cloning the UI, which isn't possible (singletons).
- If another player moves the store computer during a session, its `StartComputerPlacingMode_RPC` path wasn't checked. Restoring falls back to the computer root if the original parent is gone.
- Template build failures retry at most 3 times per game run (`_buildAttempts`).
