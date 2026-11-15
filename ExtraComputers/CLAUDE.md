# ExtraComputers: notes for agents

Player-facing info is in [README.md](README.md). Shared rules and game internals are in the root [CLAUDE.md](../CLAUDE.md). The root-cause style write-ups are the doc comments at the top of `src/ComputerFurniture.cs` (the furniture) and `src/ComputerSession.cs` (using it).

## Status

v1.1.2 (TODO Improvement #7): written, review rounds 1 to 3 applied (below), builds, **not yet run in-game**. Much of it depends on prefab and hierarchy details that the dumps can't show, so the first run is expected to need fixes. `VerboseLogging` dumps the store computer's hierarchy, the chosen base furniture prefab, the built computer prefab and the full furniture list to `LogOutput.log`. Ask for those lines first.

Open questions to settle from the first logs:
- Which base ("donor") furniture gets picked, and which of its components get stripped (`Removed from the base furniture copy: ...`).
- Whether the store computer's `FurniturePlacingMode` was found and copied (`Placing settings copied: ...`). This decides whether placing, overlap checks and the hologram work.
- Whether the icon render works under URP (`Computer icon rendered from the model`), or fell back to the drawn icon.
- Whether the root box collider's layer (the `Computer` object's layer) is clickable, and whether inner model colliders block clicks when standing very close.
- Whether the box label material (a copy of `atlasedMaterials[0]` with our texture, scale 1, offset 0) looks right.

## First in-game test (send these back)

Host alone, then with a modded guest, then with an unmodded guest. Log tag `[ExtraComputers]` in `BepInEx/LogOutput.log` (host and guest).
1. Startup/scene load: `Computer furniture registered`, `Using furniture id ... as the base`, `Computer furniture prefab built`, `Placing settings copied`, `Computer icon rendered from the model`, any `Failed`/`error` lines, and the hierarchy dumps (`Store computer hierarchy`, `Base furniture prefab`, `Computer furniture prefab`, `Base furniture keeps a Rigidbody ...`).
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
- The prefab template lives under an inactive `DontDestroyOnLoad` holder, so nothing in it ever runs `Awake`. It's built once per game run, from:
  - a donor furniture prefab: plain `Furniture` + `NetworkFurniture` + `PhotonView`, with no Rack, Display, ... scripts. The donor's visuals, colliders, navmesh data and non-Photon scripts are removed;
  - a stripped clone of the real computer, as child `ExtraComputerModel`: canvases and PC camera removed, every script except `FurniturePlacingMode`, `NavmeshBuildSourceObject` and `NavMeshObstacle` removed. Renderers left over from `m_ComputerScreen` are renamed with suffix `#ecscreen`.
  - The clone's `FurniturePlacingMode` settings are copied onto the donor's component, keeping only references inside the template. Then the clone's component is removed.
  - The root gets a box collider around the model (+1 cm per side), on the `Computer` object's layer, because `PlayerInteraction.Raycast` needs the `IInteractable` on the hit collider's object.
  - `Furniture.m_ID` = 917001 is serialized, so it survives `Instantiate`.
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
- `SpecialFurnitureTypes` and `IsKeptDonorBehaviour` in `ComputerFurniture` decide the donor. Revisit them with the logged furniture list.
- Template build failures retry at most 3 times per game run (`_buildAttempts`).
