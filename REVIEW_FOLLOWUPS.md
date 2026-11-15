# Review follow-ups (pending implementer work)

These are findings from the 2026-11-15 review loops (`REVIEW_ORCHESTRATOR.md`) that were **not applied**. Each item passed review, which ends the loop, so these minor findings and nits are still open.

Applying any of them changes the item's code. Set its `STATUS.md` row back to `in-progress` (and the `TODO` prefix to `[IN PROGRESS: ...]`), then run the review loop again.

Line numbers refer to the code as reviewed and may have shifted.

---

## Bug #1: rack move leaves floating, ghost or duplicate boxes

Code: `SupermarketFixes/src/Patches/RackMoveFix.cs`. Reviewed at hash `9f3292ae76c2` (1 round).

**1. Minor: guests without the mod are still affected by causes A and C.** This is about the design and its coverage: the A and C sections of `RackMoveFix.cs` and the README's "Who needs it".
- **Failure scenario:** the Mac guest, usually unmodded, joins mid-game or gets a rack rebuild from the host's clerk AI. It still ends up with loose networked boxes that stay floating when the rack moves. The host's copy of the mod only fixes D.
- **Why this is minor:** the README says clearly that the guest needs the mod, which the stated intent allows. But the guest who reported the problem is the one most likely to be unmodded, and the root CLAUDE.md says to prefer host-side fixes.
- **Suggested direction (follow-up):** the game has a complete host-driven path that every client handles without a mod: `NetworkRack.DematerializeBox_Broadcast(slotIdx, uid)` (RpcTarget.All) → `RackSlot.DematerializeNetworkedBox`. It does `RemoveBox`, then the master calls `PhotonNetwork.Destroy`, then every client runs `SpawnLocalRackedBox`. The game's own `ENABLE_DEMATERIALIZE_ON_PLACE` (AddBox `b__2`) calls it.
  - A host-only option that dematerializes networked racked boxes, either once placement settles or when a player joins, would leave no networked boxes on racks. That would cover A and C for unmodded guests.
  - The developers disabled this path for an unknown reason, so it needs care: clerk `IBoxTarget` references to destroyed boxes, and whether destroying an orphaned instanced box unregisters its snapshot.
  - At minimum, say plainly in the README that an unmodded guest still gets floating boxes in those two cases.

**2. Minor: when two boxes in a slot share a UID, the fallback also deletes the other box's save record.** See `RackMoveFix.cs:498-503` together with `UnrackBeforePickup` at line 549.
- **Cause:** `RackSlot.RemoveBox` calls `RackedBoxDatas.RemoveAll(r => r.m_UID == box.UID)` (the `DisplayClass76_0.b__1` predicate compares BoxData+0x10, which is `m_UID`). It removes every record with that UID.
- **Failure scenario:** the host skips the broadcast because `CountUid != 1`, then runs `RemoveBox` locally. That also deletes the record of the other live box with the same UID. Records are not rebuilt from `m_Boxes` before saving (`UpdateRackedBoxDatas` is only called from the wholesale path), so after a reload that box is missing from the rack.
- **How likely:** rare. The slot must hold two same-product boxes with the same UID, which can happen because UIDs restart every session. Without the mod you would get a duplicate on reload; with it you lose a box instead. Vanilla `TakeBoxFromRack` has the same flaw.
- **Suggested direction:** in the shared-UID case, call `slot.UpdateRackedBoxDatas()` after `RemoveBox` to rebuild the records from `m_Boxes`, or skip unracking in that case.

**3. Minor: a released networked box stays parented to the slot and is never looked at again.** See `Release`, lines 312-317.
- **What happens:** `Release` makes the box's mesh visible but leaves it a child of the slot and not in `m_Boxes`. The rebuild prefix only captures boxes that are in `m_Boxes`, and the join sweep only considers boxes with no parent.
- **Failure scenario:** a rebuild arrives early, before the host's data includes the box. The box is released. A later rebuild that does include it then spawns a local copy beside it, giving a lasting visible duplicate (mesh plus instanced copy) that moves with the rack.
- **Suggested direction:** either unparent unclaimed boxes in `Release` (`SetParent(null, true)`) so the sweep can adopt them later, or have the `RackSlot.Initialize` prefix also capture networked `Box` children of the slot that are not in `m_Boxes`.

**4. Minor: a failed match in the join sweep is not logged.** See `AdoptOrphans`, line 446: `if (found == 0) continue;`.
- **Why it matters:** if a joining guest receives a box's position late, or the host's rack moved, a copy with the same UID and product can exist more than 0.3 m away. Nothing is logged then, so an in-game test can't tell a failed match from an ordinary loose box.
- **Suggested direction:** when diagnostics are on, log once per box when a copy with the same key exists but is further than 0.3 m, including the distance.

**5. Nit: network transform sync stays off after a box is grabbed straight off a rack.** `UnrackBeforePickup` could call `box.NetworkBox.DisableNetworkTransformSync(true)` after unracking a networked box, as vanilla `TakeBoxFromRack_RPC` does. That would remove the "dropped box hangs for others" lead that the doc comment already notes for Bug #6. It only works when both sides have the mod. This is optional and out of scope.

**6. Nit: `Tick` keeps searching for racks in stores that have none** (lines 347-350). Until racks exist, it calls `FindObjectsOfType<Rack>` every 2 s. In a store with no racks that runs for the whole session. The cost is negligible.

(The reviewer's finding 7, about a version mismatch, was a snapshot taken during a parallel edit. Versions are now in sync at 0.6.1, so it's resolved.)

---

## Bug #2: ghost of an item left in the player's hands

Code: `SupermarketFixes/src/Patches/GhostHeldItemFix.cs`. Reviewed at hash `50bfe90a93e8` (2 rounds, v0.6.1).

**A. Minor: the README's limitation text covers boxes but not other orphaned items** (README.md line 23; `GhostHeldItemFix.cs` lines 414-419).
- Furniture boxes have the same host-side lock as boxes. `NetworkFurnitureBox.AvailabilityCallback_RPC` rejects while `IsNetworkOccupied` (+0x40) is set, and `PickUPFurnitureBox_Broadcast` sets it.
- After the mod drops an orphaned furniture box from a guest's hands, the host still considers it held. `RegrantToSameHolder` only covers `NetworkBox`, so nobody, the guest included, can pick that furniture box up until a reload, even with a modded host.
- When the affected player is the host itself, `DropOrphan` clears `IsNetworkOccupied` only for a `Box`, never for a `NetworkFurnitureBox`.
- This isn't worse than vanilla, where the item is stuck in the hands anyway, but the README only says this for boxes.
- **Suggested direction:** either clear `NetworkFurnitureBox.IsNetworkOccupied` too when on the host or offline, or extend the README sentence to "if it's a box or furniture box, …" and say that the regrant only helps for boxes.

**B. Nit: non-box `current` objects in a no-interaction state are still left alone** (line 227). The implementer flagged this. The analysis says case (b) can only produce a box, so this is acceptable as it is. Revisit only if in-game logs show a non-box case.

---

## Bug #3: stuck controlling a box after buying it in a market

Code: `SupermarketFixes/src/Patches/StuckBoxFix.cs`. Reviewed at hash `13bfc34905b6` (1 round).

**1. Minor: the per-box recovery cap and the retry guard never reset, and they're keyed by object pointer.** See `StuckBoxFix.cs` lines 64, 147-160 (`s_recoveredBox`/`s_recoveries`) and 205-215 (`s_retried`).
- **Why the cap protects nothing:** there is no automatic loop for it to stop. The retry already happens only once per box, and any later stuck state needs the player to click the box again.
- **Failure scenario:** a box gets stuck in three separate incidents. From then on, the player is left stuck again with exactly the reported symptom, unless they know about F9.
- **Also:** after one retry, that box object never gets a retry again for the whole session. Offline, LeanPool reuses Box objects for new purchases, so a later purchase can be denied its retry.
- **README gap:** the README promises "after 2 seconds... your controls are freed" and doesn't mention the cap.
- **Suggested direction:** drop the cap, or reset both counters once the box is properly held or a different box is bound. If you keep the cap, document it in the README.

**2. Minor: a guest's recovery without a modded host leaves host-side state behind** (lines 186-193).
- The host keeps `IsNetworkOccupied` set, with the guest as occupant.
- If the host's pickup grant did run on the other clients, they still see the box in the guest avatar's hands.
- Nobody can pick that box up until the session reloads. This isn't worse than vanilla, where the player is stuck anyway.
- **Suggested direction:** the README's "Who needs it" section should say this explicitly: without the mod on the host, the stuck box may stay un-pickable and may look carried to the other players.

**3. Minor: the waiting-branch recovery changes what a late host answer does** (line 181).
- `EndAvailabilityWait()` also resets `m_AvailabilityTimedOut`.
- If `PickUP_RPC` arrives after that, vanilla's late-grant rebinding path (`SetCurrentInteraction`) is skipped, but `Box.PickUp` still puts the box in the hands. The box ends up held with no interaction, the Bug #2 state, which is left to GhostHeldItemFix to drop.
- This only happens when the game's own 3 s timeout coroutine never ran, so it's rare.
- **Suggested direction:** accept it and note it in the mod's CLAUDE.md, or skip `EndAvailabilityWait` and the retry in the waiting case.

**4. Minor: F9 during a normal pickup wait cancels a valid pending pickup** (`ForceCheck`, line 136, `force: true`).
- The retry then asks the host again.
- With an unmodded host, the first grant puts the box in the hands, and the second request is refused, which ends the interaction. The result is a box in the hands with no interaction.
- **Suggested direction:** in force mode, still require the wait to have exceeded the game's 3 s timeout, or don't retry when the box was in the waiting state.

**5. Nit: two statements in the doc comment are inaccurate.**
- Path (a) (NetworkBox Unity-null on a guest) can't happen on the purchase path while online. `Purchase` dereferences `box.NetworkBox` for `Setup_Broadcast` before it calls `Interact`, so a null NetworkBox would throw first. The path only applies to other clicks.
- "m_CurrentObject is only ever written by HoldObject" isn't strictly true: `SetNullCurrentObject` and `set_CurrentObject` exist, though no native callers were found.

**6. Nit: the docs give the wrong timeout.** The mod's CLAUDE.md and the doc comment say a host answer that never arrives is handled "within 6 seconds". It is actually 6 s plus `StuckSeconds`, so 8 s by default.

**7. Nit: the `Diagnostics` entry gates almost nothing.** It controls a single log line ("Retried pickup"). All the other `[StuckBox]` warnings and info lines are logged regardless, which contradicts the config and README descriptions. Either gate them or reword the descriptions.
