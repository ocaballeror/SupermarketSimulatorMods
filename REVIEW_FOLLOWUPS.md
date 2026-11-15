# Review follow-ups (pending implementer work)

These are findings from the 2026-11-15 review loops (`REVIEW_ORCHESTRATOR.md`) that were **not applied**. Each item passed review, which ends the loop, so these minor findings and nits are still open.

Applying any of them changes the item's code. Set its `STATUS.md` row back to `in-progress` (and the `TODO` prefix to `[IN PROGRESS: ...]`), then run the review loop again.

Line numbers refer to the code as reviewed and may have shifted.

---

## Bug #1: rack move leaves floating, ghost or duplicate boxes

Code: `SupermarketFixes/src/Patches/RackMoveFix.cs`. Reviewed at hash `0a5ff690803c` (v0.7.0 follow-up review; the earlier 6 follow-ups were applied). Approved with only these minor findings left:

1. **Minor:** `RackDiagnostics.cs:122-133` (F8 `DUPLICATE` check) groups boxes by UID alone, but UIDs aren't unique. Two unrelated boxes with the same UID show up as a "duplicated record" in the log. No gameplay effect. Key on `(UID, ProductID)`.
2. **Minor:** `RackMoveFix.cs` `PlacePatch.Postfix` (~835) and `RackDiagnostics.AfterPlace` (41-47): the `DOVirtual.DelayedCall` runs outside any try/catch. Wrap the body.
3. **Minor:** `RackMoveFix.cs:625-634` (guest fallback in `UnrackBeforePickup`): every guest pickup of a parentless networked box calls `IndexLocalCopies(null)` (`FindObjectsOfType<Rack>` plus a slot walk) for the whole session. Small cost; could be limited to the join sweep window or cached.
4. **Minor:** `RackMoveFix.cs:651-663` (`RemoveFromSlot`, shared-UID repair): re-added records are appended, so their order may differ from `m_Boxes` (save order only).
5. **Nit:** `RackInitializePatch.Prefix` (~185) has no try/catch; it is a single assignment.

Not verifiable without the game: whether guest-side adoption removes all floating boxes; whether `DematerializeRackedBoxesOnHost` behaves with an unmodded guest (a single-box slot stays networked; a guest pickup in the instant before the host destroys the box falls back to the game's 3 s timeout).

---

## Bug #5: can't place into a new shelf until the crate is reopened

Code: `SupermarketFixes/src/Patches/BoxPlaceFix.cs`. Reviewed at hash `5066b4084b02` (1 round). Approved with these minor findings left:

1. **Minor: diagnostics blind spot** (`TrackPress`, ~190). It only starts when `m_holdingUse` is already true. If the real cause is that the Use press never reached `m_holdingUse` (`OnUse` returns early while `IsWaitingAvailability`; `OnTake` drops events while paused), nothing is logged. Also track "Use physically pressed but `m_holdingUse` false for more than 1.5 s with an open box and a slot targeted", logging `waiting`, `paused` and `m_PlacingMode`.
2. **Minor: one exception cancels the resync** (`BeforeUpdate`, 150-175). `CheckStuckTake` runs first, and `s_takeAction` is cached per player pointer. A stale cached `InputAction` that throws every frame skips the `ResyncOpenState` block for good. Give each sub-step its own try/catch and drop the cached action on exception.
3. **Minor: README "Who needs it" for Bug #5** should say that a host-only install does nothing for an unmodded guest's controls, and that if the failure only happens on the guest, the guest needs BepInEx with this plugin (macOS support unverified).
4. **Nit:** the "treated as closed" and "Take button was released" lines ignore `Diagnostics`. **Nit:** the "placed nothing for 1.5 s" line also fires on legitimate refusals (empty box, full slot, wrong product).

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
