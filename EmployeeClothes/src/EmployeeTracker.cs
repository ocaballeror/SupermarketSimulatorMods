using HarmonyLib;
using ClerkT = SupermarketSimulator.Clerk.Clerk;
using CourierT = SupermarketSimulator.Courier.Courier;
using JanitorT = __Project__.Scripts.Janitor.Janitor;

namespace EmployeeClothes;

// One postfix per employee class on its Start (a real, non-trivial native method on all eight classes, called only by
// Unity: CallerCount 0). Start runs once when a hired or loaded employee first becomes active, after all its child
// objects exist, so the renderers are there. Pooled employees (LeanPool) keep their object and stay known to
// Employees; they are never re-registered, they only get re-applied when the colors change. Each postfix only adds the
// object to Employees' list (cheap, never throws); the recoloring itself happens in the next Employees pass.
// Plugin.ApplyPatches picks these classes up through their [HarmonyPatch] attribute, one class at a time.

[HarmonyPatch(typeof(Cashier), nameof(Cashier.Start))]
internal static class CashierStartPatch
{
    private static void Postfix(Cashier __instance) => Employees.OnStarted(HrEmployeeKind.Cashier, __instance);
}

[HarmonyPatch(typeof(ClerkT), nameof(ClerkT.Start))]
internal static class ClerkStartPatch
{
    private static void Postfix(ClerkT __instance) => Employees.OnStarted(HrEmployeeKind.Restocker, __instance);
}

[HarmonyPatch(typeof(CustomerHelper), nameof(CustomerHelper.Start))]
internal static class CustomerHelperStartPatch
{
    private static void Postfix(CustomerHelper __instance) => Employees.OnStarted(HrEmployeeKind.CustomerHelper, __instance);
}

[HarmonyPatch(typeof(SecurityGuard), nameof(SecurityGuard.Start))]
internal static class SecurityGuardStartPatch
{
    private static void Postfix(SecurityGuard __instance) => Employees.OnStarted(HrEmployeeKind.SecurityGuard, __instance);
}

[HarmonyPatch(typeof(JanitorT), nameof(JanitorT.Start))]
internal static class JanitorStartPatch
{
    private static void Postfix(JanitorT __instance) => Employees.OnStarted(HrEmployeeKind.Janitor, __instance);
}

[HarmonyPatch(typeof(CourierT), nameof(CourierT.Start))]
internal static class CourierStartPatch
{
    private static void Postfix(CourierT __instance) => Employees.OnStarted(HrEmployeeKind.Courier, __instance);
}

[HarmonyPatch(typeof(Baker), nameof(Baker.Start))]
internal static class BakerStartPatch
{
    private static void Postfix(Baker __instance) => Employees.OnStarted(HrEmployeeKind.Baker, __instance);
}

[HarmonyPatch(typeof(IceCreamHelper), nameof(IceCreamHelper.Start))]
internal static class IceCreamHelperStartPatch
{
    private static void Postfix(IceCreamHelper __instance) => Employees.OnStarted(HrEmployeeKind.IceCreamHelper, __instance);
}
