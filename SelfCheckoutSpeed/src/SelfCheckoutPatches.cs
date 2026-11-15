/*
 * Improvement #4: customers using self checkout machines are N times faster (default 2x, max 10x).
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
 * Fix: postfix both coroutines' MoveNext and divide the yielded WaitForSeconds.m_Seconds by the
 * multiplier. Unity reads the wait time after MoveNext returns, and every yield allocates a new
 * WaitForSeconds, so this shortens each wait exactly once without touching prefab values.
 * Customer-helper assisted scanning (<StartCustomerHelperCheckout>...) is employee speed, so it is
 * left alone.
 *
 * Multiplayer: the host drives the checkout (FinishedScanning and the finish broadcast only run on
 * the master client), so the HOST needs this mod. A guest runs its own copy of the Scanning
 * coroutine (StartSelfCheckout_RPC) at its own speed. The host's FinishSelfCheckout_RPC
 * (-> FinishSelfCheckout_Order -> CashierCompletedCheckout) neither stops that coroutine nor clears
 * CheckoutBelt.m_Products. In vanilla the guest always finishes first thanks to the 2 s payment wait;
 * with a faster host, an unmodded guest lags by about items x interval x (1 - 1/N) and keeps
 * "scanning" (tweens, screen entries, sounds) after the customer has gone, possibly overlapping the
 * next customer at that machine. Likely visual only, but untested; guests should install the mod.
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
    /// <summary>Native names of the SelfCheckout coroutine classes to speed up.</summary>
    internal static readonly string[] Targets =
    {
        "<<StartSelfCheckout>g__Scanning|23_0>d",
        "<<FinishSelfCheckout>g__Payment|26_0>d",
    };

    // interop coroutine type -> its "<>2__current" property
    private static readonly Dictionary<Type, PropertyInfo> CurrentProp = new();

    // The postfix runs on every coroutine step; if it keeps failing (e.g. after a game update) only the
    // first failure is logged so LogOutput.log isn't flooded.
    private static bool _postfixErrorLogged;

    internal static int Apply(Harmony harmony)
    {
        int applied = 0;
        CurrentProp.Clear(); // reset static state on every (re-)apply, e.g. after the master switch was toggled
        _postfixErrorLogged = false;
        var postfix = new HarmonyMethod(typeof(SelfCheckoutPatches), nameof(MoveNextPostfix));
        foreach (var nested in typeof(SelfCheckout).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            string native = NativeName(nested);
            if (native == null || Array.IndexOf(Targets, native) < 0) continue;
            try
            {
                var moveNext = AccessTools.Method(nested, "MoveNext");
                var current = AccessTools.Property(nested, "__2__current");
                if (moveNext == null || current == null)
                {
                    Plugin.Logger.LogError($"[SelfCheckout] {native}: MoveNext or <>2__current not found");
                    continue;
                }
                CurrentProp[nested] = current;
                harmony.Patch(moveNext, postfix: postfix);
                applied++;
                Plugin.Logger.LogDebug($"[SelfCheckout] patched {native} ({nested.Name})");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[SelfCheckout] failed to patch {native}: {e}");
            }
        }
        if (applied < Targets.Length)
            Plugin.Logger.LogWarning("[SelfCheckout] not every coroutine was found; the game may have been updated. " +
                                     "Self checkouts are only partly sped up.");
        return applied;
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
        if (!__result) return; // coroutine finished, nothing yielded
        try
        {
            float mult = Plugin.SpeedMultiplier.Value;
            if (mult <= 1.0001f) return;
            if (!CurrentProp.TryGetValue(__instance.GetType(), out var prop)) return;
            var wait = (prop.GetValue(__instance) as Il2CppSystem.Object)?.TryCast<WaitForSeconds>();
            if (wait == null) return;
            float before = wait.m_Seconds;
            wait.m_Seconds = before / mult;
            if (Plugin.Verbose.Value)
                Plugin.Logger.LogInfo($"[SelfCheckout] wait {before:0.###}s -> {wait.m_Seconds:0.###}s");
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
}
