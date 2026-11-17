using System;
using Il2CppInterop.Runtime;
using UnityEngine;
using ClerkT = SupermarketSimulator.Clerk.Clerk;
using CourierT = SupermarketSimulator.Courier.Courier;
using JanitorT = __Project__.Scripts.Janitor.Janitor;
using Object = UnityEngine.Object;

namespace EmployeeClothes;

/// <summary>Finds the live employees and makes their clothes match the stored colours.</summary>
internal static class Employees
{
    private static readonly int[] LastCount = new int[8];
    private static int _lastVersion = -1;

    /// <summary>One sweep: for every employee in the scene (host's or network copies) apply the colour of its type.</summary>
    internal static void Sweep()
    {
        if (!ClothesStore.AnySet && !Recolor.NeedsRestore) return;
        Recolor.BeginBudget(25);
        Pass(false);
        _lastVersion = ClothesStore.Version;
        // With no colour left, that pass put the original materials back everywhere: stop sweeping until a colour is set.
        if (!ClothesStore.AnySet) Recolor.NeedsRestore = false;
    }

    /// <summary>Puts the original materials back on every employee (mod switched off, scene change, settings change).</summary>
    internal static void RestoreAll()
    {
        if (!Recolor.NeedsRestore) return;
        Recolor.BeginBudget(1000);
        Pass(true);
        Recolor.NeedsRestore = false;
        Plugin.Debug("Original employee clothes restored");
    }

    private static void Pass(bool restore)
    {
        Visit<Cashier>(HrEmployeeKind.Cashier, restore);
        Visit<ClerkT>(HrEmployeeKind.Restocker, restore);
        Visit<CustomerHelper>(HrEmployeeKind.CustomerHelper, restore);
        Visit<SecurityGuard>(HrEmployeeKind.SecurityGuard, restore);
        Visit<JanitorT>(HrEmployeeKind.Janitor, restore);
        Visit<CourierT>(HrEmployeeKind.Courier, restore);
        Visit<Baker>(HrEmployeeKind.Baker, restore);
        Visit<IceCreamHelper>(HrEmployeeKind.IceCreamHelper, restore);
    }

    private static void Visit<T>(HrEmployeeKind kind, bool restore) where T : Component
    {
        Color? c = restore ? null : ClothesStore.Get(kind);
        if (!c.HasValue && !Recolor.NeedsRestore) return;
        var arr = Object.FindObjectsOfType(Il2CppType.Of<T>());
        int n = 0;
        for (int i = 0; i < arr.Length; i++)
        {
            var comp = arr[i]?.TryCast<Component>();
            if (comp == null) continue;
            n++;
            try { Recolor.ApplyToObject(comp.gameObject, (int)kind, c); }
            catch (Exception e) { Plugin.Debug($"Recolouring a {kind} failed: {e.Message}"); }
        }
        if (!restore && (n != LastCount[(int)kind] || _lastVersion != ClothesStore.Version))
        {
            LastCount[(int)kind] = n;
            if (n > 0 || c.HasValue)
                Plugin.Debug($"{ClothesStore.NameOf(kind)}: {n} employee(s) in the scene, colour {(c.HasValue ? "#" + ClothesStore.Hex(c.Value) : "original")}");
        }
    }
}

/// <summary>Per-frame driver. Registered into IL2CPP by BasePlugin.AddComponent.</summary>
public class ClothesDriver : MonoBehaviour
{
    public ClothesDriver(IntPtr ptr) : base(ptr) { }

    private float _next;
    private float _nextErrorLog;

    private void Update()
    {
        if (!Plugin.Active) return;
        try { Preview.Update(Time.unscaledDeltaTime); }
        catch (Exception e) { LogOnce("Preview update", e); }
        try { TabUi.PerFrame(); }
        catch (Exception e) { LogOnce("Picker poll", e); }

        if (Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + 0.4f;
        try { ClothesStore.Tick(); } catch (Exception e) { LogOnce("Colour sync", e); }
        try { Employees.Sweep(); } catch (Exception e) { LogOnce("Employee sweep", e); }
        try { TabUi.Tick(); } catch (Exception e) { LogOnce("Tab update", e); }
    }

    private void LogOnce(string what, Exception e)
    {
        if (Time.unscaledTime < _nextErrorLog) return;
        _nextErrorLog = Time.unscaledTime + 10f;
        Plugin.Warn($"{what} failed: {e}");
    }
}
