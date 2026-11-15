# SelfCheckoutSpeed: notes for agents

Player-facing info is in [README.md](README.md). This file holds implementation details and status. The shared rules and game internals are in the root [CLAUDE.md](../CLAUDE.md). Full details are in the doc comment at the top of `src/SelfCheckoutPatches.cs`.

## Status

v1.0.1: host behaviour (coroutine speed-up) confirmed in-game with v1.0.0. 1.0.1 is a retrospective review cleanup (clear `CurrentProp` on re-apply, log a persistent postfix failure only once, doc tidy); no behaviour change.

Open: the unmodded-guest case is still untested (see "Unmodded guest test" below).

## How it works

- A customer's whole stay at a self checkout is spent in two coroutines on `SelfCheckout`:
  - `<<StartSelfCheckout>g__Scanning|23_0>d` waits `m_CustomerScanningInterval` per item. The last scan starts payment immediately; the 1 s wait it yields on an empty belt delays nothing.
  - `<<FinishSelfCheckout>g__Payment|26_0>d` waits 2 s.
- The mod postfixes both coroutines' `MoveNext` and divides the yielded `WaitForSeconds.m_Seconds` by the multiplier. Unity reads the wait time after `MoveNext` returns, so this shortens every wait.
- The coroutine classes have obfuscated names in the interop assembly. They're found at startup by their native IL2CPP names instead, so if the game renames them the log reports `n/2` patched.
- The host drives the checkout: `Checkout.FinishedScanning` and the finish broadcast only run on the master client. That's why the host needs the mod.
- Guests run their own copy of the scanning coroutine (`StartSelfCheckout_RPC`). The host's finish message (`FinishSelfCheckout_RPC` → `CashierCompletedCheckout`) neither stops it nor clears `CheckoutBelt.m_Products`. That's why an unmodded guest keeps scanning after a sped-up checkout has finished.
- Possible follow-up if that turns out to cause problems: on modded guests, stop the leftover scanning coroutine in a `SelfCheckout.FinishSelfCheckout_Order` prefix.

## Unmodded guest test (still open)

Dumps confirm the risk: `SelfCheckout.FinishSelfCheckout_Order` (what the host's finish RPC runs on the guest) only does `HandCard`, despawns the card, calls `Checkout.CashierCompletedCheckout`, hides an object and clears two flags. It doesn't stop the guest's scanning coroutine or clear `CheckoutBelt.m_Products`. The host-side mod can't change the guest's coroutine speed, so there's no host-side fix. Only a modded guest could stop the leftover scanner.

Test: host with the mod, guest **without** it, a self checkout in use by a customer with several items (6+).
1. Run at the default 2x, then at 10x. Watch the machine from the guest.
2. Look for: scan beeps or items still being scanned after the customer paid and left; the checkout screen total growing or showing stale entries after the customer is gone; two scans overlapping when the next customer starts at the same machine; belt items left over.
3. Report: which multiplier, how many items, what was seen or heard and for how long, plus the guest's `Player.log` if a stale state persisted. A host `LogOutput.log` with `VerboseLogging = true` shows the shortened waits (`[SelfCheckout] wait ...`) for timing correlation.

If it shows up, implement the follow-up: on modded guests, stop the leftover scanner (and clear the belt) in a `SelfCheckout.FinishSelfCheckout_Order` prefix or postfix. For unmodded guests the only mitigation is a lower multiplier (the lag is about items x interval x (1 - 1/N)).
