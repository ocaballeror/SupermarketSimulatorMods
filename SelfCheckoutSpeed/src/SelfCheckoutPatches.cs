/*
 * Improvement #4: customers using self checkout machines are N times faster (default 2x, max 10x),
 * including when an employee (customer helper) scans for them.
 *
 * How a self checkout runs (native dumps of SelfCheckout and its coroutines):
 *   Checkout.StartCheckout puts the customer's products on the belt instantly, then
 *   Checkout.StartSelfCheckout -> SelfCheckout.StartSelfCheckout starts the coroutine
 *   <StartSelfCheckout>g__Scanning|23_0, which loops:
 *       customer scan animation, Checkout.ProductScanned(next product),
 *       yield new WaitForSeconds(m_CustomerScanningInterval)        // per item
 *   and when the belt is empty: yield new WaitForSeconds(1f), after which the coroutine just ends
 *   (that wait delays nothing).
 *   The last ProductScanned -> Checkout.FinishedScanning (host only) -> customer pays immediately ->
 *   SelfCheckout.FinishSelfCheckout starts <FinishSelfCheckout>g__Payment|26_0:
 *       yield new WaitForSeconds(2f); hand card, complete checkout, broadcast finish to guests.
 *   The customer's time at the machine is the per-item waits plus the payment wait.
 *
 *   A customer who needs help is served by a customer helper employee instead:
 *   <StartCustomerHelperCheckout>g__Scanning|24_0 (host) scans each item and yields
 *   WaitForSeconds(CustomerHelper.CurrentScanInterval), then FinishHelperCheckout_Broadcast and a final
 *   1 s wait. Guests run <StartCustomerHelperCheckoutOrder>g__ScanningOrder|51_0 with the same per-item
 *   wait.
 *
 * Fix: postfix all four coroutines' MoveNext and divide the yielded WaitForSeconds.m_Seconds by the
 * multiplier. Unity reads the wait time after MoveNext returns, and every yield allocates a new
 * WaitForSeconds, so this shortens each wait exactly once without touching prefab values.
 *
 * Multiplayer: the host drives the checkout (FinishedScanning and the finish broadcasts only run on
 * the master client). Each guest runs its own copy of the scanning coroutine at its own speed, and the
 * host's finish messages (SelfCheckout.FinishSelfCheckout_Order, NetworkCheckout.FinishHelperCheckout_RPC)
 * neither stop that coroutine nor clear CheckoutBelt.m_Products. Confirmed in-game (2026-11-15): with
 * the mod switched off on the guest only, the guest saw customers leave before their items were
 * scanned. Two measures:
 *   1. HostSync: the host publishes the multipliers it uses; modded guests always use those, whatever
 *      their own settings say. Patches therefore stay installed even when [General] Enabled is off,
 *      and Enabled only decides the speed when you host or play solo.
 *   2. Catch-up: when the host's finish message reaches a guest while that guest's scanning
 *      coroutine for the same checkout is still running (network lag, or a host without the mod after
 *      a guest-side difference), every remaining wait of that coroutine becomes 0, so the remaining
 *      items are scanned on the next frames.
 * An unmodded guest still scans at normal speed; nothing on the host can change that.
 */
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace SelfCheckoutSpeed;

internal static class SelfCheckoutPatches
{
    private enum Kind { Self, Help }

    /// <summary>Native names of the SelfCheckout coroutine classes to speed up, and whose speed applies.</summary>
    private static readonly (string Name, Kind Kind)[] TargetList =
    {
        ("<<StartSelfCheckout>g__Scanning|23_0>d", Kind.Self),
        ("<<FinishSelfCheckout>g__Payment|26_0>d", Kind.Self),
        ("<<StartCustomerHelperCheckout>g__Scanning|24_0>d", Kind.Help),
        ("<<StartCustomerHelperCheckoutOrder>g__ScanningOrder|51_0>d", Kind.Help),
    };

    internal static int TargetCount => TargetList.Length;

    private sealed class Coro
    {
        public Kind Kind;
        public PropertyInfo Current;   // <>2__current
        public PropertyInfo This;      // <>4__this (the SelfCheckout)
    }

    private static readonly Dictionary<Type, Coro> Coros = new();

    // Guest catch-up. Running coroutine (pointer) -> its Checkout (pointer), and the coroutines whose
    // remaining waits must be 0 because the host already finished that checkout.
    private static readonly Dictionary<IntPtr, IntPtr> Running = new();
    private static readonly HashSet<IntPtr> Rushed = new();

    // The postfix runs on every coroutine step; if it keeps failing (e.g. after a game update) only the
    // first failure is logged so LogOutput.log isn't flooded.
    private static bool _postfixErrorLogged;

    internal static int Apply(Harmony harmony)
    {
        int applied = 0;
        Coros.Clear();
        Running.Clear();
        Rushed.Clear();
        _postfixErrorLogged = false;
        var postfix = new HarmonyMethod(typeof(SelfCheckoutPatches), nameof(MoveNextPostfix));
        foreach (var nested in typeof(SelfCheckout).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            string native = NativeName(nested);
            if (native == null) continue;
            int idx = Array.FindIndex(TargetList, t => t.Name == native);
            if (idx < 0) continue;
            try
            {
                var moveNext = AccessTools.Method(nested, "MoveNext");
                var current = AccessTools.Property(nested, "__2__current");
                var self = AccessTools.Property(nested, "__4__this");
                if (moveNext == null || current == null)
                {
                    Plugin.Logger.LogError($"[SelfCheckout] {native}: MoveNext or <>2__current not found");
                    continue;
                }
                Coros[nested] = new Coro { Kind = TargetList[idx].Kind, Current = current, This = self };
                harmony.Patch(moveNext, postfix: postfix);
                applied++;
                Plugin.Logger.LogDebug($"[SelfCheckout] patched {native} ({nested.Name})");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[SelfCheckout] failed to patch {native}: {e}");
            }
        }
        if (applied < TargetList.Length)
            Plugin.Logger.LogWarning("[SelfCheckout] not every coroutine was found; the game may have been updated. " +
                                     "Self checkouts are only partly sped up.");

        TryPatch(harmony, AccessTools.Method(typeof(SelfCheckout), nameof(SelfCheckout.FinishSelfCheckout_Order)),
            nameof(FinishSelfCheckoutOrderPrefix), "SelfCheckout.FinishSelfCheckout_Order");
        TryPatch(harmony, AccessTools.Method(typeof(NetworkCheckout), nameof(NetworkCheckout.FinishHelperCheckout_RPC)),
            nameof(FinishHelperCheckoutRpcPrefix), "NetworkCheckout.FinishHelperCheckout_RPC");
        return applied;
    }

    private static void TryPatch(Harmony harmony, MethodBase target, string prefix, string label)
    {
        try
        {
            if (target == null) { Plugin.Logger.LogError($"[SelfCheckout] {label} not found; guest catch-up is off"); return; }
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(SelfCheckoutPatches), prefix));
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[SelfCheckout] failed to patch {label}: {e}");
        }
    }

    private static string NativeName(Type t)
    {
        try
        {
            IntPtr klass = Il2CppClassPointerStore.GetNativeClassPointer(t);
            if (klass == IntPtr.Zero) return null;
            return Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass));
        }
        catch { return null; }
    }

    private static void MoveNextPostfix(Il2CppObjectBase __instance, bool __result)
    {
        try
        {
            IntPtr me = __instance.Pointer;
            if (!__result)
            {
                // Coroutine finished: forget it.
                Running.Remove(me);
                Rushed.Remove(me);
                return;
            }
            if (!Coros.TryGetValue(__instance.GetType(), out var coro)) return;

            bool guest = HostSync.IsGuest;
            if (guest && coro.This != null && !Running.ContainsKey(me))
            {
                var sc = coro.This.GetValue(__instance) as SelfCheckout;
                var checkout = sc != null ? sc.m_Checkout : null;
                if (checkout != null)
                {
                    if (Running.Count > 256) { Running.Clear(); Rushed.Clear(); } // stopped coroutines never report false
                    Running[me] = checkout.Pointer;
                }
            }

            bool rush = guest && Rushed.Contains(me);
            float mult = coro.Kind == Kind.Help ? HostSync.HelpMultiplier : HostSync.SelfMultiplier;
            if (!rush && mult <= 1.0001f) return;

            var wait = (coro.Current.GetValue(__instance) as Il2CppSystem.Object)?.TryCast<WaitForSeconds>();
            if (wait == null) return;
            float before = wait.m_Seconds;
            wait.m_Seconds = rush ? 0f : before / mult;
            if (Plugin.Verbose.Value)
                Plugin.Logger.LogInfo($"[SelfCheckout] {coro.Kind} wait {before:0.###}s -> {wait.m_Seconds:0.###}s" +
                                      (rush ? " (catching up with the host)" : ""));
        }
        catch (Exception e)
        {
            if (!_postfixErrorLogged)
            {
                _postfixErrorLogged = true;
                Plugin.Logger.LogError($"[SelfCheckout] MoveNext postfix failed (further failures are not logged): {e}");
            }
        }
    }

    // Host's "customer finished paying" reaching a guest.
    private static void FinishSelfCheckoutOrderPrefix(SelfCheckout __instance)
    {
        try
        {
            var checkout = __instance != null ? __instance.m_Checkout : null;
            if (checkout != null) Rush(checkout.Pointer, "self checkout");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[SelfCheckout] catch-up failed: {e.Message}"); }
    }

    // Host's "employee finished helping" reaching a guest.
    private static void FinishHelperCheckoutRpcPrefix(NetworkCheckout __instance)
    {
        try
        {
            var checkout = __instance != null ? __instance.Checkout : null;
            if (checkout != null) Rush(checkout.Pointer, "employee-assisted checkout");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[SelfCheckout] catch-up failed: {e.Message}"); }
    }

    private static void Rush(IntPtr checkout, string what)
    {
        if (!HostSync.IsGuest) return;
        int n = 0;
        foreach (var kv in Running)
            if (kv.Value == checkout && Rushed.Add(kv.Key)) n++;
        if (n > 0 && Plugin.Verbose.Value)
            Plugin.Logger.LogInfo($"[SelfCheckout] host finished a {what} before this game did; scanning the rest at once");
    }
}
