# ExtraComputers: notes for agents

Player-facing info is in [README.md](README.md). Shared rules and game internals are in the root [CLAUDE.md](../CLAUDE.md). The root-cause style write-ups are the doc comments at the top of `src/ComputerFurniture.cs` (the furniture) and `src/ComputerSession.cs` (using it).

## Status

v1.0.0 (TODO Improvement #7): written and builds, **not yet run in-game**. Much of it depends on prefab and hierarchy details that the dumps can't show, so the first run is expected to need fixes. `VerboseLogging` dumps the store computer's hierarchy, the chosen base furniture prefab, the built computer prefab and the full furniture list to `LogOutput.log`. Ask for those lines first.

Open questions to settle from the first logs:
- Which base ("donor") furniture gets picked, and which of its components get stripped (`Removed from the base furniture copy: ...`).
- Whether the store computer's `FurniturePlacingMode` was found and copied (`Placing settings copied: ...`). This decides whether placing, overlap checks and the hologram work.
- Whether the icon render works under URP (`Computer icon rendered from the model`), or fell back to the drawn icon.
- Whether the root box collider's layer (the `Computer` object's layer) is clickable, and whether inner model colliders block clicks when standing very close.
- Whether the box label material (a copy of `atlasedMaterials[0]` with our texture, scale 1, offset 0) looks right.

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
  - The root gets a box collider around the model (+4 cm), on the `Computer` object's layer, because `PlayerInteraction.Raycast` needs the `IInteractable` on the hit collider's object.
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
- Furniture is host-spawned room objects. Guests need the mod for the prefab id, the `FurnitureSO` (box setup, `IDManager.FurnitureSO`) and the interaction patches. An unmodded guest gets Photon "prefab not found" errors and null `FurnitureSO` exceptions on boxes.
- `NetworkComputer.OnSyncRequest` (late joiners) sends the host's `IsOccupied`. The session restores it, so a host on an extra computer doesn't make the store computer look occupied.

## Pitfalls / follow-ups

- While a session is active, this client's store computer shows a blank monitor, because its screen is borrowed. That's cosmetic. A fix would need a second screen, i.e. cloning the UI, which isn't possible (singletons).
- If another player moves the store computer during a session, its `StartComputerPlacingMode_RPC` path wasn't checked. Restoring falls back to the computer root if the original parent is gone.
- `SpecialFurnitureTypes` and `IsKeptDonorBehaviour` in `ComputerFurniture` decide the donor. Revisit them with the logged furniture list.
- Template build failures retry at most 3 times per game run (`_buildAttempts`).
