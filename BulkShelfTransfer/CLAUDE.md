# BulkShelfTransfer: agent notes

Improvement #9. Modifier (LeftShift) + Take/Use moves every product between the looked-at
`DisplaySlot` and the held container in one click, with the game's fly animation sped up.

## Status

- v0.2.5 (review fix of 0.2.4): 0.2.4 treated "order wants the product and the bag already has all of it" like "product not in the order" and filled the bag far past the order. Now: product not in the order (or no active order) -> fill like the crate; order wants it and still needs n -> take n; order wants it and the bag has it all -> nothing is taken, `bag take: not started (order #N already has all ...)` and the game's FULL_PAPERBAG popup (`WarningSystem.RaiseInteractionWarning`; the enum has no closer "order is complete" warning). A bag click while a bag burst of the same interaction runs is ignored (`ignored (a transfer is running)`), so a restart can't change the limit. The wanted quantity is summed over all `OrderItems` entries of the product (whether an order can list a product twice is unconfirmed).
- v0.2.4 (user finding after the 2026-11-27 session, verbose logging was off so the log has no `[Bulk]` lines): "shift-click to/from the paper bag does nothing". Cause found for the take direction: with an order in the notebook (`nb.CurrentOrder` is also set when the player only has the notebook open), a product not in that order, or already complete, got `limit == 0` and the click was swallowed silently (`bag take: nothing to do`). Now only a product the order still needs is capped (to what it needs); anything else fills the bag like the crate (no limit; `bag take: start ... filling the bag`). For the place direction (no order logic at all) no static defect was found in `OnlineOrderInteraction.OnUse`/`PlaceProductToDisplay`/`Refusal` (traced in the native dump); if it still fails, the `bag place: not started (<reason>)` line says why (needs VerboseLogging).
- Review round 2 (docs only): README now states that an order another player views counts as "worked on" (game's own `IsBeingWorkedOn` signal), so co-op players on the same order lose the cap; no stronger signal checked.
- v0.2.3 (review round 1 of Improvement #9): a bag place burst now ends quietly when the bag has no product left for the slot (`!bag.IsTargetProductExist(slot)` in the quiet end checks), so a mixed bag no longer ends with the game's "no matching product" warning; a box or bag burst is finished as stale when its interaction's `Update` hasn't run for 1 s (container thrown / put away mid-burst, then picked up again); `Plugin.AnimationSpeed` is `static readonly` (CS0162 warning); doc fixes (0.02 s interval, second-click behaviour).
- v0.2.2: only the `SecondsBetweenItems` value change back to 0.02 (see below).
- v0.2.1 (settings reduced, commit "simplify mod settings"): only `Enabled`, `ModifierKey` and `VerboseLogging` (the cfg key `[Diagnostics] VerboseLogging` already existed, so existing cfgs keep their stored value; the default is now false for new configs) remain settings. `BulkTake`, `BulkPlace`, `Boxes`, `PaperBags`, `Crates` (all true), `SecondsBetweenItems` (**0.02**; the user's value, commit `0ca098e` set it back from 0.2, do not change it) and `AnimationSpeed` (5) are `const`s in `Plugin.cs`.
- v0.2.0 (rework after the 2026-11-15 session, not yet tested in-game): the user reported that Shift-click didn't work with online-order paper bags (no `[Bulk] bag` line in either log, so `BagBulk.TryStart` never started a burst; the failing condition is unknown). Now every refusal while the modifier is held is logged (`bag take: not started (<reason>)`). New: a take burst for a bag packed for an active order is capped at what the order still needs (`BagBulk.ActiveOrder`: `OrderNotebookContainer.CurrentOrder`; opening a bag runs `OrderPaperBag.OnDetachTag`, so the bag's tag is normally gone by then; excluded when `CourierManager.TryGetClaimStatus` or online `NetworkOrderManager.IsBeingWorkedOn(id, ownUserId)` says someone else handles it, the same checks as the notebook's "worked on by" label). Order without that product / already enough → the click is swallowed (`bag take: nothing to do`). Review 2026-11-16: the earlier bag burst never started because `Refusal` used `WeaponWheelController.IsWheelInteraction`, whose last link is `OnlineOrderInteraction.enabled` (true while a bag is held); only `IsOpen` is checked now. Don't use `IsWheelInteraction` in code that runs with a bag in hand. No active order → fill the bag as before.
- v0.1.1 written and builds; review round 1 applied (see below). **Not tested in-game.** Nothing about the runtime behaviour is confirmed yet.
- Open questions to settle from the first test logs (see "In-game test"):
  1. Does the tween speed-up catch anything? (`N animation(s) sped up` in the `[Bulk] ... stopped` lines.
     0 means `DOTween.PlayingTweens` doesn't list freshly created tweens as playing; then switch to
     `DOTween.TweensByTarget(product.transform)` in the AddProduct postfixes, and find another hook for the
     bag's target-less Sequence.)
  2. Host: a box burst that stops with `no progress` after 1 product means `PhotonView.RPC(..., RpcTarget.All)`
     is not executing locally inside the call (assumed from PUN2 docs, not verified in the dumps).
  3. Crate: does the loop really end on its own when the crate/slot runs out (`stopped (loop ended)`), or
     only through the 1 s / 30 s safety nets?

## Files

| File | What |
|---|---|
| `src/Plugin.cs` | Config (3 settings) and tuning constants, live master switch, patch application (attribute patches + `CrateBulk.PatchCoroutines`). |
| `src/Common.cs` | `Bulk` helpers (modifier key, guest check, capacities from `ProductSO.GridLayoutInBox/InStorage.productCount`), `TweenBoost`. |
| `src/BoxBulk.cs` | Product boxes. Doc comment at the top has the full game flow. |
| `src/BagBulk.cs` | Online-order paper bags. |
| `src/CrateBulk.cs` | The multi-product crate (coroutine-driven). |

## Mechanism (summary; details in each file's doc comment)

- **Trigger:** prefixes on the interaction's `OnTake(bool down)` / `OnUse(bool down)` (input delegates; no
  direct callers, unique RVAs). Modifier held + target is a display slot + the same preconditions as the
  game's own hold path -> start a burst. Box/bag: return false so the hold flag is never latched. Crate: let
  the game start its loop, swallow the matching release.
- **Box steps** (`BoxInteraction.Update` prefix, runs before the game's per-product code in the click frame):
  the game's own `CheckTakeProductFromSlot` + `TryTakeProductFromSlot` / `CheckPlaceProductToSlot` +
  `TryPlaceProductToDisplay(Vector3)`, one per `SecondsBetweenItems` (const 0.02 s). Capped up front at
  min(available, room) from local state.
- **Bag order cap:** `Burst.Limit` (only when the order still needs the product; otherwise -1 = fill) = order quantity of the slot's product (`OrderListData.GetOrderDataOfId(pid).ProductCount`) minus that product's count in `bag.m_SortedProducts`; -1 without an active order.
- **Bag steps** (`OnlineOrderInteraction.Update` prefix): `OnlineOrderService.TryTakeProductIntoBag(slot, bag,
  m_MaxBagItemCount, pos, m_TakeProductBroadcast)` until status != Success / `PlaceProductToDisplay()` until the
  bag count stops changing.
- **Crate:** `<OnUseLoop>d__54` (right click, shelf->crate) and `<OnTakeLoop>d__62` (left click, crate->shelf);
  the field/coroutine names are swapped relative to the buttons. MoveNext prefix/postfix (found by native name
  prefix) wraps each step in `TweenBoost` and shortens the yielded `WaitForSeconds`. Ending a burst clears the
  loop's flag (`m_IsOnUse` for take, `m_IsOnTake` for place), which is what a release does.
- **TweenBoost:** snapshot `DOTween.PlayingTweens` before a game call, set `timeScale = AnimationSpeed` on the
  tweens that appeared during it (a Sequence's timeScale covers its nested tweens). Used around bag steps, crate
  MoveNext, and `Box.AddProduct` / `DisplaySlot.AddProduct` when that box/slot is marked by a box burst (marks
  last 3 s after the last step so a guest's late RPC-driven moves are covered too).

## Multiplayer reasoning

- Box take on a guest: `AskTakeProduct` -> host `AskTakeProduct_RPC` relays `TakeProduct_RPC` to All **without
  any check**, and `Box.AddProduct` has no capacity check. Over-asking would overfill the box on every client. The
  up-front cap from local state prevents that; after a guest burst, a new burst on the same box is refused until
  the box shows the expected count (2 s timeout). Vanilla hold-to-take has the same race in principle.
- Box place on a guest: the host re-checks (`TryPlaceProductToDisplay(slot, pos)` on its copy), so over-asking
  would only be refused there. Still capped.
- Bag and crate moves are local-authoritative + broadcast in the game itself; we only call them faster.
- The speed-up is local only; other players see normal-speed animations in quick succession.
- Only the acting player needs the mod.

## Review round 1 (v0.1.1)

Applied:
- Guest box guard (`BoxBulk.Guarded`): while a guest burst runs or its answers are pending (box count !=
  expected, 2 s timeout), OnTake/OnUse presses (all OnUse branches, incl. rack/trash/placement area), OnDrop and
  OnThrow are swallowed. Reason: `TakeProduct_RPC`/`PlaceProduct_RPC` are dropped by a client whose
  `BoxInteraction.m_Box` isn't the message's box ("Boxes are not same"), and DropBox clears m_Box at once.
- TryStart consumes the click if a burst on the same interaction is running (no stacked bursts / overfill);
  a stale burst for a different box is finished first.
- Placing mode: TryStart refuses, Tick stops (OnDrop sets m_PlacingMode but leaves m_CurrentDisplaySlot).
- Box place also refuses when `m_TargetPlacementArea` is set.
- Crate: start targets pinned (slot, vending slot, m_CurrentInteractable, m_CurrentCrate) and checked in the
  MoveNext prefix every step; a change clears the flag so the step exits at the loop's flag check. Finish outside
  the loop also StopCoroutine's `m_OnUseCoroutine`/`m_OnTakeCoroutine` like a release; Reset (mod disabled)
  finishes the burst; the 30 s timeout and an `isActiveAndEnabled` check also run in the MoveNext prefix.
- Bag take refuses when the bag is already full, so the game's "bag full" warning shows; bag bursts don't
  start while the weapon wheel is open (`WeaponWheelController.IsOpen`; `IsWheelInteraction` is not used, see v0.2.0).
- `Bulk.IsLocal` also requires `PlayerInstance.m_PhotonView.IsMine` online (the game's crate/bag input guard).
- TweenBoost only speeds tweens whose target is (inside) a `Product`, plus target-less Sequences (the bag's
  PlayProductMove). Warning popups, sale signs, highlights keep their speed.
- Online the step interval is at least 0.02 s.

## Not handled (on purpose)

- Vending slots (`m_CurrentVendingSlot`), baker crate, guard crate: left vanilla.
- Ice cream slots (`IceCreamConeDisplaySlot`/`IceCreamFlavourDisplaySlot`) override `AddProduct` natively, so the `DisplaySlot.AddProduct` boost probably doesn't reach them: box bursts onto them (host/offline) likely play at normal animation speed (cosmetic, unverified, left as is).
- Ice cream slots with a box on a guest: vanilla (their
  capacity rules aren't the plain `GridLayoutInStorage`).
- No key hint added.

## In-game test

1. Offline: open box, look at a stocked shelf slot of the same product, Shift+right click -> box fills fast.
   Shift+left click on an empty or same-product slot -> slot fills until full/box empty. Try a wrong product
   (warning, nothing moves) and a full box (game's warning).
2. Same with a paper bag (online order) and with the crate.
2b. Paper bag with an order selected in the notebook (or a tagged bag) that needs, say, 3 of a product: Shift + right click on a slot with 10 takes exactly 3 (`... order #N ... needs 3 more`, `stopped (order has what it needs)`). Again on the same slot: nothing is taken, the game's "paper bag is full" popup shows (`not started (order #N already has all ...)`). A product not in the order: the bag is filled like with the crate (`... doesn't contain product N: filling the bag`). Shift + left click on a slot of a product the bag holds: puts them all back (`bag place: ...`). An order being packed by the other player or claimed by a courier: fills the bag (`no active order (order #N is being packed by ...)`). If nothing happens at all, send the `bag ...: not started (...)` lines.
3. Multiplayer, guest with the mod, host without: box take/place, check counts on both screens (no overfilled box).
   Right after a guest burst, try to drop/throw the box and to click again: should be ignored briefly
   (`waiting for the host ... ignored` lines), then work.
4. Host with the mod: same, check the guest sees the products.
5. Toggle `Enabled` off in the config manager: plain clicks only.

Send back the `[Bulk]` lines from `BepInEx/LogOutput.log` (turn on `VerboseLogging` in the cfg first; it's off by default) (start / stopped lines include counts and the number of
sped-up animations).
