# SelfCheckoutSpeed: notes for agents

Player-facing info is in [README.md](README.md). This file holds implementation details and status. The shared rules and game internals are in the root [CLAUDE.md](../CLAUDE.md). Full details are in the doc comment at the top of `src/SelfCheckoutPatches.cs`.

## Status

v1.2.0 (2026-11-19): two user requests. (1) The cashier employee's scanning is sped up too, with the same `SpeedMultiplier` (no new setting; the cfg section/key stay `SelfCheckout`/`SpeedMultiplier` so stored values survive, only the description text changed). (2) The payment step is no longer sped up: the `<<FinishSelfCheckout>g__Payment|26_0>d` patch and the `MinOnlinePaymentWait` floor were removed (the cashier payment coroutine was never patched). Host sync and guest catch-up are unchanged, since the cashier needs neither (see "Cashier" below). Folder/GUID/plugin name were not renamed (open question: the mod now covers more than self checkouts). Not yet tested in-game.

v1.1.2 (2026-11-18): settings reduced (user commit "simplify mod settings"). `SpeedUpEmployeeHelp` is gone (employee help always follows the multiplier; its `Plugin` constant and the off-branch were removed as dead code); remaining settings are `Enabled`, `SpeedMultiplier` and `VerboseLogging` (the cfg key `[Diagnostics] VerboseLogging` already existed, so existing cfgs keep their stored value; the default is now **false** for new configs; only the C# field was renamed from `Verbose`). No behaviour change otherwise.

v1.1.1: v1.1.0 plus review round 1 (see "Review round 1" below). Not yet tested in-game.

v1.1.0 (rework after the 2026-11-15 in-game test):
- The user saw customers leave before their items were scanned on the guest. The guest had switched the mod **off** mid-session (its log: "All patches removed (disabled)"), so it scanned at 1x behind a 2x host.
- The multiplier must be a text field in the in-game mod settings, not a slider.
- Employee-assisted checkouts must be sped up too.

v1.0.x host behaviour (coroutine speed-up) was confirmed in-game.

## How it works

- Four scanning coroutines are postfixed (`MoveNext`); the yielded `WaitForSeconds.m_Seconds` is divided by the multiplier. Unity reads it after `MoveNext` returns. Payment coroutines are deliberately **not** patched (user request, 1.2.0).
  - `SelfCheckout.<<StartSelfCheckout>g__Scanning|23_0>d`: customer scanning, per item `m_CustomerScanningInterval` (host and guests).
  - `<<StartCustomerHelperCheckout>g__Scanning|24_0>d`: employee scanning (host), per item `CustomerHelper.CurrentScanInterval`.
  - `<<StartCustomerHelperCheckoutOrder>g__ScanningOrder|51_0>d`: the guest copy of employee scanning.
  - `AutomatedCheckout.<<StartCashierCheckout>g__Scanning|22_0>d`: cashier employee scanning (host only), per item `Cashier.ScanningInterval` (see "Cashier").
- The classes have obfuscated interop names; they're found at startup by native name (looking in the nested types of `SelfCheckout` and `AutomatedCheckout`). The log reports `n/4` patched. The cashier coroutine gets the postfix only (the guest catch-up prefix is for the `SelfCheckout` ones).
- **Host-authoritative speed (`HostSync.cs`).** The master client publishes the multipliers it uses as player custom properties `flamboyanthopper.scs.self` / `flamboyanthopper.scs.help` (floats), checked once a second by `SyncBehaviour`, re-sent if the room's copy differs. Guests (`InRoom && !IsMasterClient`) read them from `PhotonNetwork.MasterClient`; missing key (unmodded host) means 1x. Local settings only apply when hosting or solo.
- Because of that, **the patches stay installed when `[General] Enabled` is off** (a deliberate exception to the root master-switch convention): a guest that switched the mod off must still follow the host. Enabled only feeds the host/solo multiplier. `Unload()` still unpatches.
- **Guest catch-up.** The host's finish messages (`SelfCheckout.FinishSelfCheckout_Order` via `Checkout.FinishSelfCheckoutOrder`, and `NetworkCheckout.FinishHelperCheckout_RPC`) don't stop the guest's scanning coroutine or clear the belt. On guests the postfix records running coroutines (pointer → `__4__this.m_Checkout` pointer); prefixes on those two methods mark that checkout's running coroutines as rushed, and their remaining waits become 0 (one item per frame). Entries drop when `MoveNext` returns false; the map is cleared above 256 entries in case a coroutine is stopped from outside.
- **Text field instead of slider.** `ClampedFloat` is an `AcceptableValueBase` without `MinValue`/`MaxValue`/`AcceptableValues` properties; Mod Settings Tool picks its control by reflecting on those names (see root CLAUDE.md section 6). BepInEx still clamps through `Clamp`.

## Cashier (game reference, verified in the dumps 2026-11-19)

- A regular checkout with a cashier employee uses `AutomatedCheckout` (field `m_Cashier`, +0x28 `m_Checkout`). `Checkout.StartCheckout` (the code of `AutomatedCheckout.StartCashierCheckout` is inlined there) skips starting the scanning coroutine on a guest (`PhotonNetwork.IsConnectedAndSlave`), so **only the host runs it**.
- `AutomatedCheckout.<<StartCashierCheckout>g__Scanning|22_0>d` loop: `yield new WaitForSeconds(Cashier.ScanningInterval)` (+0x8C), then, while the belt has products, `NetworkAutomatedCheckout.CashierProductScanned_Broadcast`, `Checkout.ProductScanned(next, true)`, `Cashier.ScanAnimation` (SetTrigger "Scan" + `NetworkCashier.ScanAnimation` RPC); it ends when the belt is empty. The first wait comes before the first item.
- `Cashier.ApplySpeeds` sets `ScanningInterval = m_CashierScanIntervals[?] / level scan multiplier / boost` and the animator "work speed" from `m_CashierAnimationSpeeds`; the per-item wait already includes level and boost, we divide on top.
- The last `ProductScanned` -> `Checkout.FinishedScanning` (inlines `AutomatedCheckout.FinishCheckout`) starts `AutomatedCheckout+<>c__DisplayClass23_0+<<FinishCheckout>g__Payment|0>d` (host): wait `m_IntervalAfterScanningAll` (+0x38), `Cashier.TakePaymentAnimation` + wait `m_TakingPaymentInterval` (+0x3C), then (broadcast `CashierTookPayment`, hand money/card) wait `m_FinishingPaymentDuration` (+0x40), then `CashierCompletedCheckout` broadcast, XP. Each wait goes through `BoostScaledDuration` (cashier boost). **Not patched** (payment stays vanilla).
- **Multiplayer:** guests apply `CashierProductScanned_RPC` (takes the next product off their belt and calls `ProductScanned`), `CashierTookPayment_RPC` and `CashierCompletedCheckout_RPC`. They run no cashier coroutine, so they follow the host's pace without the mod and need no `HostSync` key and no catch-up. The host's own multiplier is `HostSync.SelfMultiplier` (local value on the host).
- Not done: the cashier's scan animation speed (animator work speed) isn't scaled, so at high multipliers the "Scan" trigger fires faster than the clip plays (visual only).

## Review round 1 (v1.1.1)

- Employee-help catch-up didn't work: the guest's `ScanningOrder` loop checks `SelfCheckout.IsScanning` (+0x80) before every item, and `NetworkCheckout.FinishHelperCheckout_RPC` only clears that flag (`Checkout+0x80` → SelfCheckout, `+0x80 = 0`). A `MoveNext` prefix now sets `IsScanning = belt.m_Products.Count > 1` (the step scans a product before it tests the flag, so the last one needs no extra lap; review 2026-11-16) for rushed `ScanningOrder` coroutines.
- (Removed in 1.2.0, payment is no longer sped up so vanilla 2 s slack remains) Payment slack: online, the host's payment wait keeps at least 0.5 s (vanilla 2 s), since it's the time a lagging guest has before `FinishSelfCheckout_RPC` makes the customer leave.
- `ClampedFloat.IsValid` accepts any finite float: Mod Settings Tool's `WireInputEndEdit` calls `IsValid` and silently reverts on false, so out-of-range input never reached `Clamp`.
- Stale catch-up records: a `MoveNext` prefix clears records for a coroutine's first step (`<>1__state == 0`, IL2CPP reuses addresses); the postfix drops the rush when an address now belongs to another checkout; `HostSync.Tick` clears everything when not a guest.
- `Unload()` sets `Plugin.Unloaded`, which stops `HostSync.Tick`.
- Docs: decimal comma on comma-locale PCs (Mod Settings Tool parses with the current culture); both sides need ≥ 1.1.0.

## Test plan (v1.2.0)

A. Cashier: hire a cashier, set `SpeedMultiplier = 4`: items on a regular checkout are scanned about 4x faster than before (compare with 1). With `VerboseLogging = true` the host log shows `[SelfCheckout] Cashier wait 0.x s -> 0.x s` per item. After the last item the payment (cashier takes card/money, finishing) lasts as long as at 1x.
B. Self checkout at 4x: scanning is fast, but after the last item the customer waits the vanilla 2 s before leaving (no `Self wait 2s -> ...` line in the log, which v1.1.x printed).
C. Multiplayer: a guest **without the mod** watches a cashier at 4x: items are scanned at the host's pace and the customer leaves after the vanilla payment. A guest with the mod watches a self checkout: still in step (the earlier test plan below).
D. Startup log: `Patches applied (4/4 checkout coroutines)`; `3/4` or a `not every coroutine was found` warning means a name changed after a game update.

## Test plan (v1.1.x, still valid)

1. Mod settings: `SpeedMultiplier` is a text field; typing 1.5 sticks, 50 becomes 10.
1b. Many items (20+) at 10x with a guest: the guest's machine finishes its items before or right as the customer leaves (`catching up with the host` lines with `VerboseLogging`), including when an employee helps.
2. Host 2x, guest with the mod **switched off**: watch a self checkout from the guest. Items should finish before the customer walks away. Host log: `[SelfCheckout] Told guests to use x2 ...` once per session/change.
3. Host changes the multiplier mid-session: guest follows within ~1 s (with `VerboseLogging` on the guest, waits show the host's value).
4. A customer needing help at a self checkout: the employee scans 2x faster on both machines.
5. Send both `LogOutput.log`s with `VerboseLogging = true` if anything is off; look for `catching up with the host` lines on the guest.

## Pitfalls

- An unmodded guest still scans at 1x behind a sped-up host; nothing host-side can change that.
- Custom-property floats come back through `Unbox<float>()`; values are clamped to [1, 10] on read.

- Review 2026-11-16 (v1.1.1): no high/medium findings. Accepted: an unmodded guest can't be matched (it runs its own coroutine; README says so); catch-up also applies when the host is unmodded or at 1x; the employee-help path has no payment-style slack at high multipliers. A typed value in a decimal-comma culture (`1.5` read as 15) clamps to 10 silently.
