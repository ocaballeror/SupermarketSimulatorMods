# PlayerDoubleCarry: agent notes

Improvement #10: a player can pick up a second box while holding a closed one; with two carried neither can be opened; when the top one leaves the hands (drop, throw, rack, trash, placement area) the other one becomes the normal held box.

Full root-cause / mechanism write-up: doc comment at the top of `src/DoubleCarry.cs`. This file summarises it.

## Status

- v0.2.0 (rework after the 2026-11-15 session, not tested in-game): buying a second box at the wholesale store. Holding one closed box, holding Use on a `PurchasableProduct` for its `Duration` runs the game's `PurchasableProduct.HoldingInteract()` with `s_inPurchase` set. Inside it, `Purchase` would switch the player to the new box (`PlayerInteraction.SetCurrentInteraction(PRODUCT)`, `SetCurrentInteractable(box)`, `InteractionHintsManager.Clear`, `PlayerInteraction.Interact`); those four are skipped for the local player during the call (unique RVAs, checked). The box from `BoxGenerator.SpawnBoxLocalMarket` (postfix; online `SpawnNetworkBoxClient` = buyer-spawned networked box, returned synchronously) is then requested like a loose box (`RequestSecondBox` → `InstantInteract`). No hold-progress UI. Release (`OnUse(false)`) or looking away cancels. Config `BuySecondBoxAtWholesale`. Unconfirmed: that skipping those calls leaves nothing half-done in `Purchase` (the rest is onboarding steps), and that the buyer-spawned box is granted by the host like any loose box.
- v0.1.1 written and built, **not tested in-game**. Nothing below is confirmed in-game.
- v0.1.1 applied the first independent review (open second box, player leaving, late grant after timeout, disable during a pending request, highlight, rack/animation condition, bottom-box colliders, docs).
- Open questions to settle with the first test logs:
  - Is `PlayerObjectHolder.m_ObjectHolder`'s parent something that follows the camera? The bottom box is reparented there. If it isn't, the bottom box won't pitch with the view (cosmetic).
  - Does `PhotonView.RPC("PickUP_RPC", RpcTarget.All, userId)` sent by a guest on a box view it owns get executed on an unmodded host and other guests? (PUN doesn't restrict RPC senders; untested here.)
  - Does a guest's second pickup look right on an unmodded host (both boxes at the same offset) and nothing worse?
  - Does `PlayerInstance.OnPlayerLeftRoom` run before PUN destroys the leaving player's avatar? (Realtime raises the callback before PUN's own event handler cleans up, as far as known; the vanilla handler relies on the same order.)

## Game mechanism (verified in the dumps)

- Pickup is a Use click: `PlayerInteraction.Interact` → `Box.InstantInteract`. Offline → `Box.PickUp()` (does **not** bind BoxInteraction; `Interact` → `SetCurrentInteraction(BOX)` does). Host → `NetworkBox.AvailabilityCallback_RPC(own id)` directly. Guest → `NetworkBox.AskAvailability` (`BeginAvailabilityWait`: `BoxInteraction.IsWaitingAvailability`, 3 s timeout coroutine).
- Host decision: reject if `IsNetworkOccupied` or claimed by an NPC → `AvailabilityRejected_Response` → `EndAvailabilityWait` → `BoxInteraction.CancelInteracitonForOnlyMultiplayer` (= `PlayerInteraction.InteractionEnd` + clear hints). Timeout → `m_AvailabilityTimedOut = true` + the same cancel. Grant → `PickUp_Broadcast`: `IsNetworkOccupied = true`, `PickUP_RPC(userId)` to **All**, `TransferOwnership` to the player.
- `PickUP_RPC` on every client: `EndAvailabilityWait` on that player's copy (if it returns "timed out" and it's the local player not in an interaction, it binds the interaction itself), then `Box.PickUp(player)`: `BoxInteraction.set_Interactable(box)` (m_Box, m_IsBoxOpen, hints), occupy, `HoldObject(box, BoxSO.PositionOffset, Vector3.zero)`; then transform sync off.
- `HoldObject` overwrites `m_CurrentObject` without releasing the previous object (CLAUDE.md §8). All releases act on the current object / `m_Box` only and end with `InteractionEnd` (BoxInteraction disabled).
- `BoxInteraction.OnUse(true)` first sets `m_holdingUse`, then: placement-area target / placing mode / rack slot (only if the box is **not** in its 0.7 s open-close animation, `m_InAnimationTransition`) / trash bin / placement area each take their own branch; otherwise an open box returns (and `Update` places products while `m_holdingUse`), a closed box opens. `OnTake` only sets `m_holdingTake` (taking products back needs an open box).
- Pickup does **not** close an open box: the `CloseBox` in `InstantInteract` / `Box.PickUp` only runs for a racked box, and `m_Racked` is cleared right before. Nothing closes a box when it is put down. `set_Interactable` copies `IsOpen` into `m_IsBoxOpen`. `Box.CloseBox(false)` online goes through `NetworkBox.CloseBox_Broadcast`; `IsOpen` changes after a delayed call.
- Timeout bookkeeping: the timeout coroutine sets `NetworkBoxInteraction.m_AvailabilityTimedOut` and `IsWaitingAvailability = false`, then cancels. `EndAvailabilityWait` (any later reply) clears the wait flag and returns-and-clears `m_AvailabilityTimedOut`; a late refusal therefore skips the cancel, a late grant (`PickUP_RPC`) still calls `Box.PickUp`.
- `UpdateOpeningBoxHint` re-adds the USE "_open" hint for a closed box; `RackHints` etc. call it and then add their own USE hint after it.
- `PhotonNetwork.RPC` with `RpcTarget.All` raises the event to others, then `ExecuteRpc` locally right away (native-pun dump), so the local effect of `PickUP_RPC` is synchronous.
- `PlayerInstance.OnPlayerLeftRoom` (master, matches `otherPlayer.UserId` against its view's owner) only unparents and `ThrowBoxInstant_Broadcast`s the leaving player's `BoxInteraction.m_Box`; anything else under their hold point is destroyed with the avatar on every client. `Box.OnDestroy` doesn't remove the box from `InventoryManager`, so the host's save keeps it (it comes back on reload).

## What the mod does

| Patch | Why |
|---|---|
| `BoxInteraction.OnUse` prefix (local) | Two carried: swallow `OnUse(true)` whenever nothing (rack / trash / placement area / placing mode) is targeted, open box or not, so no opening and no product placing. One closed box, Enabled, "would open" branch: raycast like `PlayerInteraction.Raycast` (camera forward, `m_InteractionDistance`, `m_InteractableLayer`, `Box` on the hit collider's own GameObject, not racked); if a box is hit: refuse if it is open or another request is still pending, else call its `InstantInteract()` (vanilla pickup path, host decides). Click swallowed either way. |
| `BoxInteraction.OnUse` prefix, wholesale (local) | One closed box, "would open" branch, no loose box hit, but a `PurchasableProduct` on the hit collider's GameObject: start a buy hold (`StartBuy`), swallow. `OnUse(false)` during a hold cancels it. `TickBuy` completes it after `Duration` (see Status v0.2.0). |
| `BoxGenerator.SpawnBoxLocalMarket` postfix | Catch the box our purchase spawned (`s_inPurchase` only). |
| `PlayerInteraction.SetCurrentInteraction` / `SetCurrentInteractable` / `Interact`, `InteractionHintsManager.Clear` prefixes | Skipped for the local player while `s_inPurchase` (the purchase's simulated click on the new box). |
| `BoxInteraction.OnTake` prefix (local) | Two carried: swallow `OnTake(true)` (no taking products). |
| `PlayerObjectHolder.HoldObject` prefix+postfix (all players) | Prefix (local): if the requested box arrives while the first box is in placing mode (possible after the freeze ends), cancel placing (`BoxInteraction.OnBack`, else `CancelPlacingMode`) so the first box is back under the hold point. Postfix, local: when the requested box replaces the held one, stack: the old box ("bottom") goes to `m_ObjectHolder.parent`, is kept just under the new box ("top") and its colliders are disabled (restored before it comes back); offline, also bind BoxInteraction to the top box; close either box if open. Remote players: only tween the displaced box under the new one (cosmetic, `ShowOtherPlayersStack`). |
| `BoxInteraction.CancelInteracitonForOnlyMultiplayer` prefix | A refusal/timeout for the second box would end the interaction with the first box still in the hands. Skipped while a request is pending and the first box is held normally. Refusal clears the request. Timeout re-sets `IsWaitingAvailability` (freeze, like the game's own wait) until 5 s after the request, so a late grant finds the first box held normally; the request is then remembered until 15 s (a late grant still stacks, new second-box requests are refused). A late refusal clears the wait without a cancel; Tick sees that and forgets the request. |
| `PlayerInstance.OnPlayerLeftRoom` prefix (every modded client) | For the leaving player (`OwnerActorNr` or `LastPlayerUserId`): release every Box that is a direct child of their hold point other than `BoxInteraction.m_Box`: unparent, layer Interactable, colliders solid, `SetOccupy(false)`, `IsNetworkOccupied = false`, transform sync on; physics on the master only. |
| `PlayerInteraction.Interact` prefix | Blocks new interactions in the 0.25 s between the top box leaving and the bottom box coming back. |
| `BoxInteraction.UpdateOpeningBoxHint` postfix | Removes the "open" hint while two are carried. |
| Tick (`CarryBehaviour.Update`) | Keeps the bottom box posed under the hold point; detects the top box leaving (`!bi.Enable` or `bi.m_Box != top`); then, once not in an interaction / placing mode, hands the bottom box back: online `PickUP_RPC(own userId)` on its view to All, offline `Box.PickUp(player)`, then `SetCurrentInteractable(null)` (drops any crosshair highlight from the gap), `m_CurrentInteractable = box; SetCurrentInteraction(BOX); m_CurrentInteractable = null` (what `Interact` / the timed-out `PickUP_RPC` branch do). |

Why the bottom box isn't left under `m_ObjectHolder`: SupermarketFixes' GhostHeldItemFix drops any non-current box that is a direct child of the local hold point after 2 s. The mods must not depend on each other, so the bottom box lives one level up.

## Multiplayer reasoning

- Everything network-visible goes through the game's own messages (`InstantInteract` for the second box, `PickUP_RPC` to give the first one back), so an unmodded host and unmodded guests stay consistent: on their side the bottom box simply stays under the carrier's hold point (orphaned, same offset as the top box) until `PickUP_RPC` makes it current again. The host keeps `IsNetworkOccupied` on it the whole time; ownership stays with the carrier from the first pickup.
- Ordering: the top box's release message and our `PickUP_RPC` come from the same sender, or the release came from the host and reached the Photon server before we could react to it; the server relays reliable events in the order it receives them, so other clients see them in order too. The 0.25 s delay is mostly cosmetic.
- A late joiner only gets `SyncBoxState` for `m_Box` (the top box); its copy of the bottom box sits wherever the join-time sync puts it (probably not in the carrier's hands). The host refuses pickups of it (occupied), and our `PickUP_RPC` puts it in the carrier's hands later.
- Player leaving with two boxes: the `OnPlayerLeftRoom` prefix saves the bottom box on every client that runs the mod. A modded host keeps it in the game (enough for two players); unmodded other guests lose their copy until reload.

## Pitfalls / follow-ups

- Disabling in the config while carrying two, or while a second-box request is pending, keeps the patches until that is over (`DoubleCarry.HasWork`, `Plugin.s_disableWhenIdle`).
- A late grant beyond 15 s after the request (or for a request replaced meanwhile) falls to vanilla `HoldObject` and orphans the first box, exactly like the game's own late-grant ghost (Bug #2). Freeze/linger make this very unlikely.
- If the first box is waiting for a guest rack placement answer when a late grant arrives, the host's rack reply would act on the new top box. Not possible during the freeze; very unlikely afterwards.
- `Box.PickUp()` (no-arg, offline) doesn't bind BoxInteraction; `Box.PickUp(PlayerInstance)` does. Use the latter when calling it ourselves.
- If the top box is released in a way that keeps `BoxInteraction` enabled and bound to it (none known), the bottom box stays put.

## In-game test steps

1. Host alone (offline or hosting): hold a closed box, look at another box, left-click. Expect `[PlayerCarry] Picking up a second box ... (host)` / `(offline)` and `Carrying two boxes: ...`. The new box is in the usual spot, the first one just below it.
2. Left-click with nothing targeted: nothing opens, log `Can't open or use a box while carrying two`. Right-click: `Can't take products ...`. The "open" key hint is gone. Try clicking an open box as the second box: refused (`... is open; close it first`).
3. Drop (G), throw (R), rack, trash: each time expect `Top box left your hands`, then `Second box back in your hands: ... (held=True)`, and the box opens / restocks normally.
4. Guest with the mod, host unmodded: same as 1–3; expect `(guest, asking the host)`. On the host, the guest's two boxes overlap. Try picking a box another player holds: `The host refused ...`, still holding the first box.
5. Host with the mod, guest unmodded: the guest sees the host's two boxes overlapping, then the right box after each drop. Then guest with the mod watching the host: `... carries two boxes` and a visible stack.
6. Toggle `Enabled` off while carrying two: the second box still comes back; then `All patches removed`.
6b. Wholesale store (offline, host, and guest): hold a closed box, hold left click on a product: `Buying a second box ...`, then after the hold time `Picking up a second box ...`, `Carrying two boxes ...`; money goes down by one box. Release early / look away: `Purchase cancelled (...)`. Not enough money: `The game refused the purchase ...`. With two boxes: no purchase.
7. Host with the mod, guest with the mod carrying two boxes quits: on the host, `[PlayerCarry] <name> left while carrying box[...]; put it down` and the box falls to the floor and can be picked up.

Send back the `[PlayerCarry]` lines from `BepInEx/LogOutput.log` (both machines when online), plus any `[GhostHeld]` / `[StuckBox]` lines if SupermarketFixes is installed.
