# SelfCheckoutSpeed: notes for agents

Player-facing info is in [README.md](README.md). This file holds implementation details and status. The shared rules and game internals are in the root [CLAUDE.md](../CLAUDE.md). Full details are in the doc comment at the top of `src/SelfCheckoutPatches.cs`.

## Status

v1.0.0: done, confirmed in-game.

## How it works

- A customer's whole stay at a self checkout is spent in two coroutines on `SelfCheckout`:
  - `<<StartSelfCheckout>g__Scanning|23_0>d` waits `m_CustomerScanningInterval` per item. The last scan starts payment immediately; the 1 s wait it yields on an empty belt delays nothing.
  - `<<FinishSelfCheckout>g__Payment|26_0>d` waits 2 s.
- The mod postfixes both coroutines' `MoveNext` and divides the yielded `WaitForSeconds.m_Seconds` by the multiplier. Unity reads the wait time after `MoveNext` returns, so this shortens every wait.
- The coroutine classes have obfuscated names in the interop assembly. They're found at startup by their native IL2CPP names instead, so if the game renames them the log reports `n/2` patched.
- The host drives the checkout: `Checkout.FinishedScanning` and the finish broadcast only run on the master client. That's why the host needs the mod.
- Guests run their own copy of the scanning coroutine (`StartSelfCheckout_RPC`). The host's finish message (`FinishSelfCheckout_RPC` → `CashierCompletedCheckout`) neither stops it nor clears `CheckoutBelt.m_Products`. That's why an unmodded guest keeps scanning after a sped-up checkout has finished.
- Possible follow-up if that turns out to cause problems: on modded guests, stop the leftover scanning coroutine in a `SelfCheckout.FinishSelfCheckout_Order` prefix.
