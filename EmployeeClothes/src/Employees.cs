using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using ClerkT = SupermarketSimulator.Clerk.Clerk;
using CourierT = SupermarketSimulator.Courier.Courier;
using JanitorT = __Project__.Scripts.Janitor.Janitor;
using Object = UnityEngine.Object;

namespace EmployeeClothes;

/// <summary>Finds the employees (active or pooled) and makes their clothes match the stored colors.</summary>
internal static class Employees
{
    private sealed class Cached
    {
        public GameObject Model;      // the top Animator's GameObject: clothes live here, not in carried items elsewhere
        public Renderer[] Rends;
        public int Version = -1;      // ClothesStore.Version the renderers were last made to match
        public float NextScan;        // when to look for new renderers again
        public float NextCheck;       // when to verify the materials again even if nothing changed
        public bool Incomplete;       // a shade map was postponed: try again next pass
    }

    private static readonly Dictionary<int, Cached> Cache = new();
    private static readonly int[] LastCount = new int[8];
    private static int _lastVersion = -1;
    private static float _nextPrune;

    /// <summary>One sweep: for every employee of a colored type apply its color; restore types that lost theirs.</summary>
    internal static void Sweep()
    {
        if (!ClothesStore.AnySet && !Recolor.NeedsRestore) return;
        Recolor.BeginBudget(25);
        Pass();
        _lastVersion = ClothesStore.Version;
        if (Time.unscaledTime >= _nextPrune)
        {
            _nextPrune = Time.unscaledTime + 10f;
            Recolor.PruneRegistry();
            List<int> dead = null;
            foreach (var kv in Cache) if (kv.Value.Model == null) (dead ??= new List<int>()).Add(kv.Key);
            if (dead != null) foreach (var k in dead) Cache.Remove(k);
        }
    }

    /// <summary>
    /// Puts the original materials back on every employee, active or pooled (mod switched off, scene change, settings
    /// change). Goes through the renderer registry, so it doesn't depend on finding the employees.
    /// </summary>
    internal static void RestoreAll()
    {
        Recolor.RestoreRegistry();
        Cache.Clear();
        Plugin.Debug("Original employee clothes restored");
    }

    private static void Pass()
    {
        Visit<Cashier>(HrEmployeeKind.Cashier);
        Visit<ClerkT>(HrEmployeeKind.Restocker);
        Visit<CustomerHelper>(HrEmployeeKind.CustomerHelper);
        Visit<SecurityGuard>(HrEmployeeKind.SecurityGuard);
        Visit<JanitorT>(HrEmployeeKind.Janitor);
        Visit<CourierT>(HrEmployeeKind.Courier);
        Visit<Baker>(HrEmployeeKind.Baker);
        Visit<IceCreamHelper>(HrEmployeeKind.IceCreamHelper);
    }

    private static void Visit<T>(HrEmployeeKind kind) where T : Component
    {
        int slot = (int)kind;
        var c = ClothesStore.Get(kind);
        if (!c.HasValue)
        {
            // No color for this type (any more): everything of this type that we changed goes back, pooled ones too.
            if (Recolor.HasEntries(slot)) Recolor.RestoreRegistry(slot);
            return;
        }
        // Inactive ones are included: fired employees are pooled (LeanPool deactivates them) and come back later.
        var arr = Object.FindObjectsByType(Il2CppType.Of<T>(), FindObjectsInactive.Include, FindObjectsSortMode.None);
        float now = Time.unscaledTime;
        int n = 0;
        for (int i = 0; i < arr.Length; i++)
        {
            var comp = arr[i]?.TryCast<Component>();
            if (comp == null) continue;
            n++;
            try
            {
                var go = comp.gameObject;
                int id = go.GetInstanceID();
                Cache.TryGetValue(id, out var cd);
                if (cd == null || cd.Model == null || now >= cd.NextScan)
                {
                    var anim = go.GetComponentInChildren<Animator>(true);
                    var model = anim != null ? anim.gameObject : go;
                    var list = model.GetComponentsInChildren<Renderer>(true);
                    var rends = new Renderer[list.Length];
                    for (int r = 0; r < rends.Length; r++) rends[r] = list[r];
                    cd = new Cached { Model = model, Rends = rends, NextScan = now + 8f };
                    Cache[id] = cd;
                }
                if (cd.Version == ClothesStore.Version && !cd.Incomplete && now < cd.NextCheck) continue;
                cd.Incomplete = !Recolor.ApplyToRenderers(cd.Rends, slot, c);
                cd.Version = ClothesStore.Version;
                cd.NextCheck = now + 3f;
            }
            catch (Exception e) { Plugin.Debug($"Recoloring a {kind} failed: {e.Message}"); }
        }
        if (n != LastCount[slot] || _lastVersion != ClothesStore.Version)
        {
            LastCount[slot] = n;
            Plugin.Debug($"{ClothesStore.NameOf(kind)}: {n} employee(s) in the scene (incl. pooled), color #{ClothesStore.Hex(c.Value)}");
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
        try { ClothesStore.Tick(); } catch (Exception e) { LogOnce("Color sync", e); }
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
