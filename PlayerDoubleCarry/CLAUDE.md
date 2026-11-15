# PlayerDoubleCarry: agent notes

Improvement #10: a player can pick up a second box while holding a closed one; with two carried neither can be opened; when the top one leaves the hands (drop, throw, rack, trash, placement area) the other one becomes the normal held box.

Full root-cause / mechanism write-up: doc comment at the top of `src/DoubleCarry.cs`. This file summarises it.

## Status

- v0.1.0 written and built, **not tested in-game**. Nothing below is confirmed in-game.
- Open questions to settle with the first test logs:
  - Is `PlayerObjectHolder.m_ObjectHolder`'s parent something that follows the camera? The bottom box is reparented there. If it isn't, the bottom box won't pitch with the view (cosmetic).
  - Does `PhotonView.RPC("PickUP_RPC", RpcTarget.All, userId)` sent by a guest on a box view it owns get executed on an unmodded host and other guests? (PUN doesn't restrict RPC senders; untested here.)
  - Does a guest's second pickup look right on an unmodded host (both boxes at the same offset) and nothing worse?

## Game mechanism (verified in the dumps)

- Pickup is a Use click: `PlayerInteraction.Interact` → `Box.InstantInteract`. Offline → `Box.PickUp()` (does **not** bind BoxInteraction; `Interact` → `SetCurrentInteraction(BOX)` does). Host → `NetworkBox.AvailabilityCallback_RPC(own id)` directly. Guest → `NetworkBox.AskAvailability` (`BeginAvailabilityWait`: `BoxInteraction.IsWaitingAvailability`, 3 s timeout coroutine).
- Host decision: reject if `IsNetworkOccupied` or claimed by an NPC → `AvailabilityRejected_Response` → `EndAvailabilityWait` → `BoxInteraction.CancelInteracitonForOnlyMultiplayer` (= `PlayerInteraction.InteractionEnd` + clear hints). Timeout → `m_AvailabilityTimedOut = true` + the same cancel. Grant → `PickUp_Broadcast`: `IsNetworkOccupied = true`, `PickUP_RPC(userId)` to **All**, `TransferOwnership` to the player.
- `PickUP_RPC` on every client: `EndAvailabilityWait` on that player's copy (if it returns "timed out" and it's the local player not in an interaction, it binds the interaction itself), then `Box.PickUp(player)`: `BoxInteraction.set_Interactable(box)` (m_Box, m_IsBoxOpen, hints), occupy, `HoldObject(box, BoxSO.PositionOffset, Vector3.zero)`; then transform sync off.
- `HoldObject` overwrites `m_CurrentObject` without releasing the previous object (CLAUDE.md §8). All releases act on the current object / `m_Box` only and end with `InteractionEnd` (BoxInteraction disabled).
- `BoxInteraction.OnUse(true)` opens the box only when: `m_Box` set, not waiting, no `m_TargetPlacementArea`, not `m_PlacingMode`, no `m_CurrentRackSlot`, no `m_CurrentTrashBin`, no `m_CurrentPlacementArea`, box closed. Placing products (`Update`) and taking products (`CheckForTakingItem`) both need the box open, so blocking that branch is enough to stop all product work.
- `UpdateOpeningBoxHint` re-adds the USE "_open" hint for a closed box; `RackHints` etc. call it and then add their own USE hint after it.
- `PhotonNetwork.RPC` with `RpcTarget.All` raises the event to others, then `ExecuteRpc` locally right away (native-pun dump), so the local effect of `PickUP_RPC` is synchronous.
- `NetworkManager.OnPlayerLeftRoom` / `PlayerInstance.OnPlayerLeftRoom` (master) only throws the leaving player's `BoxInteraction.m_Box` out of the hands; anything else under their hold point goes down with the player object.

## What the mod does

| Patch | Why |
|---|---|
| `BoxInteraction.OnUse` prefix (local) | In the "would open" branch: with two carried, swallow the click. With one closed box and Enabled, raycast like `PlayerInteraction.Raycast` (camera forward, `m_InteractionDistance`, `m_InteractableLayer`, `Box` on the hit collider's own GameObject, not racked); if a box is hit call its `InstantInteract()` (vanilla pickup path, host decides) and swallow the click. |
| `PlayerObjectHolder.HoldObject` prefix+postfix (all players) | Local: when the requested box replaces the held one, stack: the old box ("bottom") goes to `m_ObjectHolder.parent` and is kept just under the new box ("top"); offline, also bind BoxInteraction to the top box. Remote players: only tween the displaced box under the new one (cosmetic, `ShowOtherPlayersStack`). |
| `BoxInteraction.CancelInteracitonForOnlyMultiplayer` prefix | A refusal/timeout for the second box would end the interaction with the first box still in the hands. Skipped while a request is pending and the first box is held normally. Refusal clears the request; timeout keeps it (a late grant still stacks). |
| `PlayerInteraction.Interact` prefix | Blocks new interactions in the 0.25 s between the top box leaving and the bottom box coming back. |
| `BoxInteraction.UpdateOpeningBoxHint` postfix | Removes the "open" hint while two are carried. |
| Tick (`CarryBehaviour.Update`) | Keeps the bottom box posed under the hold point; detects the top box leaving (`!bi.Enable` or `bi.m_Box != top`); then, once not in an interaction / placing mode, hands the bottom box back: online `PickUP_RPC(own userId)` on its view to All, offline `Box.PickUp(player)`, then `m_CurrentInteractable = box; SetCurrentInteraction(BOX); m_CurrentInteractable = null` (what `Interact` / the timed-out `PickUP_RPC` branch do). |

Why the bottom box isn't left under `m_ObjectHolder`: SupermarketFixes' GhostHeldItemFix drops any non-current box that is a direct child of the local hold point after 2 s. The mods must not depend on each other, so the bottom box lives one level up.

## Multiplayer reasoning

- Everything network-visible goes through the game's own messages (`InstantInteract` for the second box, `PickUP_RPC` to give the first one back), so an unmodded host and unmodded guests stay consistent: on their side the bottom box simply stays under the carrier's hold point (orphaned, same offset as the top box) until `PickUP_RPC` makes it current again. The host keeps `IsNetworkOccupied` on it the whole time; ownership stays with the carrier from the first pickup.
- Ordering: the top box's release message and our `PickUP_RPC` come from the same sender (or the host answered us before we send), so with two players they arrive in order. With 3+ players a host-sent release (guest rack placement) and our `PickUP_RPC` can reach a third client in either order; the 0.25 s delay makes that unlikely but not impossible (that client would apply the rack placement to the wrong box).
- A late joiner only gets `SyncBoxState` for `m_Box` (the top box).
- Player leaving with two boxes: see the last mechanism bullet; the bottom box is likely lost on the other clients. Not handled.

## Pitfalls / follow-ups

- Disabling in the config while carrying two keeps the patches until the bottom box is back (`Plugin.s_disableWhenIdle`).
- `Box.PickUp()` (no-arg, offline) doesn't bind BoxInteraction; `Box.PickUp(PlayerInstance)` does. Use the latter when calling it ourselves.
- If the top box is released in a way that keeps `BoxInteraction` enabled and bound to it (none known), the bottom box stays put.

## In-game test steps

1. Host alone (offline or hosting): hold a closed box, look at another box, left-click. Expect `[PlayerCarry] Picking up a second box ... (host)` / `(offline)` and `Carrying two boxes: ...`. The new box is in the usual spot, the first one just below it.
2. Left-click with nothing targeted: nothing opens, log `Can't open a box while carrying two`. The "open" key hint is gone.
3. Drop (G), throw (R), rack, trash: each time expect `Top box left your hands`, then `Second box back in your hands: ... (held=True)`, and the box opens / restocks normally.
4. Guest with the mod, host unmodded: same as 1–3; expect `(guest, asking the host)`. On the host, the guest's two boxes overlap. Try picking a box another player holds: `The host refused ...`, still holding the first box.
5. Host with the mod, guest unmodded: the guest sees the host's two boxes overlapping, then the right box after each drop. Then guest with the mod watching the host: `... carries two boxes` and a visible stack.
6. Toggle `Enabled` off while carrying two: the second box still comes back; then `All patches removed`.

Send back the `[PlayerCarry]` lines from `BepInEx/LogOutput.log` (both machines when online), plus any `[GhostHeld]` / `[StuckBox]` lines if SupermarketFixes is installed.
