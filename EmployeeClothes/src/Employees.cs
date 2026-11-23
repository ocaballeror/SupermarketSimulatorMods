using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using ClerkT = SupermarketSimulator.Clerk.Clerk;
using CourierT = SupermarketSimulator.Courier.Courier;
using JanitorT = __Project__.Scripts.Janitor.Janitor;
using Object = UnityEngine.Object;

namespace EmployeeClothes;

/// <summary>
/// Knows the employees (active or pooled) and makes their clothes match the stored colors.
///
/// Discovery is event-driven: a Harmony postfix on every employee class's Start (EmployeeTracker.cs) registers the new
/// employee, so a hire is recolored within a frame or two and nothing has to search the scene. A slow safety net
/// (one FindObjectsByType per few seconds, only for types that have a color) catches whatever the hooks missed, and a
/// rolling scan of every type runs once after a scene start or when the mod is switched on. Before 1.0.5 every 0.4 s
/// eight FindObjectsByType calls (all objects incl. inactive) ran, and their time even ate the shade-map budget, which
/// starved the last types (guard, janitor, courier) of any recoloring.
///
/// Known employees cost nothing while nothing changed: a pass only runs when a new employee was registered, the colors
/// changed (ClothesStore.Version), a shade map was postponed (retry), or an employee's own 30 s verification is due (re-find the renderers,
/// re-assert our materials; staggered, at most 3 employees per pass).
/// </summary>
internal static class Employees
{
    private sealed class Tracked
    {
        public int Id;                 // Go.GetInstanceID()
        public GameObject Go;          // the employee's root
        public HrEmployeeKind Kind;
        public string Via;             // "hook" or "scan", for the log
        public GameObject Model;       // the top Animator's GameObject: clothes live here, not in carried items elsewhere
        public Renderer[] Rends;
        public int AppliedVersion = -1;// ClothesStore.Version the renderers were last made to match
        public bool Incomplete;        // a shade map was postponed: try again next pass
        public int Tries;
        public float NextVerify;       // when to re-find the renderers and re-assert our materials (30 s, staggered)
        public bool Logged;            // the one-off diagnostic line was written
        public bool LoggedPostponed;   // ... while some shade map was still postponed (log the end of it too)
    }

    private static readonly Dictionary<int, Tracked> ById = new();
    private static readonly List<Tracked> All = new();
    private static int _lastVersion = -1;
    private static bool _dirty;            // an employee was registered: run a pass at once
    private static bool _incomplete;       // some employee still waits for a postponed shade map
    private static float _nextVerifyCheck; // when to look for employees whose 30 s verification is due
    private static float _nextPrune;
    private static readonly Stopwatch PassWatch = new();
    private static float _nextSlowLog;
    private const int VerifyPerPass = 3;   // verifications are spread over passes, this many employees each
    private static float _nextScan;
    private static float _nextFastScan;
    private static int _initialPos;        // rolling first scan of all 8 types (after a scene start / enabling)
    private static int _rotation;          // steady-state safety net: which type next
    private static int _fastRotation;
    private static readonly bool?[] Hooked = new bool?[8];

    private static readonly HrEmployeeKind[] Kinds =
    {
        HrEmployeeKind.Cashier, HrEmployeeKind.Restocker, HrEmployeeKind.CustomerHelper, HrEmployeeKind.SecurityGuard,
        HrEmployeeKind.Janitor, HrEmployeeKind.Courier, HrEmployeeKind.Baker, HrEmployeeKind.IceCreamHelper,
    };

    /// <summary>True when the driver should run a pass right away instead of waiting for its next tick.</summary>
    internal static bool Wants => _dirty;

    /// <summary>True while postponed work is retried (the driver ticks faster).</summary>
    internal static bool Busy => _incomplete;

    // ------------------------------------------------------------------ registration

    /// <summary>An employee component started (Harmony postfix) or was found by a scan.</summary>
    internal static void Register(HrEmployeeKind kind, Component comp, string via)
    {
        if (!Plugin.Active || comp == null) return;
        var go = comp.gameObject;
        int id = go.GetInstanceID();
        if (ById.TryGetValue(id, out var t))
        {
            // Same object seen under another type (should not happen): the more specific type wins over Restocker.
            if (t.Kind != kind && t.Kind == HrEmployeeKind.Restocker) { t.Kind = kind; t.AppliedVersion = -1; _dirty = true; }
            return;
        }
        t = new Tracked { Id = id, Go = go, Kind = kind, Via = via, NextVerify = Time.unscaledTime + 30f };
        ById[id] = t;
        All.Add(t);
        _dirty = true;
    }

    /// <summary>Start of an employee component: register it (called from the Harmony postfixes; never throws).</summary>
    internal static void OnStarted(HrEmployeeKind kind, Component comp)
    {
        try { Register(kind, comp, "hook"); }
        catch (Exception e) { LogOnce("Employee hook", e); }
    }

    // ------------------------------------------------------------------ the pass

    /// <summary>
    /// One sweep (called by the driver every ~0.25 s, and by the UI after Apply/Reset): applies the colors to the
    /// employees that need it, restores types that lost theirs, and steps the safety-net scan. Near free when idle.
    /// </summary>
    internal static void Sweep()
    {
        float now = Time.unscaledTime;
        bool vchg = ClothesStore.Version != _lastVersion;
        bool newcomers = _dirty;
        bool retry = _incomplete;
        bool verify = false;
        if (now >= _nextVerifyCheck && ClothesStore.AnySet)
        {
            _nextVerifyCheck = now + 1f;
            for (int i = 0; i < All.Count; i++) if (now >= All[i].NextVerify) { verify = true; break; }
        }
        if (vchg || newcomers || retry || verify)
        {
            _dirty = false;
            PassWatch.Restart();
            Process(now, vchg, verify);
            double ms = PassWatch.Elapsed.TotalMilliseconds;
            if (ms > 3.0)
                SlowLog($"{(vchg ? "colors changed" : newcomers ? "new employee" : retry ? "postponed retry" : "verify")}" +
                        $"{(vchg && (newcomers || verify) ? " (+" + (newcomers ? "new employee" : "verify") + ")" : "")}, {All.Count} employee(s)", ms);
        }
        ScanStep(now);
    }

    /// <summary>Performance check for the tests: a pass over 3 ms is logged, at most once per 10 s.</summary>
    private static void SlowLog(string what, double ms)
    {
        float now = Time.unscaledTime;
        if (now < _nextSlowLog) return;
        _nextSlowLog = now + 10f;
        Plugin.Debug($"Slow employee pass: {what}, {ms:0.0} ms");
    }

    private static void Process(float now, bool vchg, bool verify)
    {
        // Also when no color is set: preview clones register renderers (slot 100) that get destroyed on every type switch.
        if (now >= _nextPrune) { _nextPrune = now + 30f; Recolor.PruneRegistry(); }
        if (!ClothesStore.AnySet)
        {
            // No color anywhere: put back whatever of ours is still on a renderer (pooled employees included).
            if (vchg || Recolor.NeedsRestore) RestoreUncolored();
            _lastVersion = ClothesStore.Version;
            _incomplete = false;
            return;
        }
        if (vchg) RestoreUncolored();
        Recolor.BeginBudget(16);
        bool anyIncomplete = false;
        int version = ClothesStore.Version;
        int verified = 0;
        for (int i = All.Count - 1; i >= 0; i--)
        {
            var t = All[i];
            try
            {
                if (t.Go == null) { Remove(i, t); continue; }
                var c = ClothesStore.Get(t.Kind);
                if (!c.HasValue) { if (now >= t.NextVerify) t.NextVerify = now + 30f; continue; }
                bool due = verify && now >= t.NextVerify && verified < VerifyPerPass;
                bool need = vchg || due || t.Incomplete || t.AppliedVersion != version || t.Rends == null;
                if (!need) continue;
                if (due) { verified++; t.NextVerify = now + 30f; }
                if (t.Rends == null || t.Model == null || due) Rescan(t);
                bool ok = Recolor.ApplyToRenderers(t.Rends, (int)t.Kind, c);
                t.Incomplete = !ok;
                t.AppliedVersion = version;
                t.Tries++;
                if (!ok) anyIncomplete = true;
                if (!t.Logged && (ok || t.Tries >= 8)) LogEmployee(t, ok);
                else if (ok && t.LoggedPostponed) { t.LoggedPostponed = false; Plugin.Debug($"{ClothesStore.NameOf(t.Kind)} '{t.Go.name}': apply complete after {t.Tries} tries: {Recolor.Describe(t.Rends)}"); }
            }
            catch (Exception e) { Plugin.Debug($"Recoloring a {t.Kind} failed: {e.Message}"); }
        }
        _incomplete = anyIncomplete;
        _lastVersion = version;
    }

    private static void Rescan(Tracked t)
    {
        var anim = t.Go.GetComponentInChildren<Animator>(true);
        var model = anim != null ? anim.gameObject : t.Go;
        var list = model.GetComponentsInChildren<Renderer>(true);
        var rends = new Renderer[list.Length];
        for (int r = 0; r < rends.Length; r++) rends[r] = list[r];
        t.Model = model;
        t.Rends = rends;
    }

    private static void Remove(int index, Tracked t)
    {
        All.RemoveAt(index);
        ById.Remove(t.Id);
    }

    private static void RestoreUncolored()
    {
        foreach (var k in Kinds)
            if (!ClothesStore.Get(k).HasValue && Recolor.HasEntries((int)k)) Recolor.RestoreRegistry((int)k);
    }

    /// <summary>Once per employee: what the renderers hold after the first apply (the root cause of "not colored" next time).</summary>
    private static void LogEmployee(Tracked t, bool ok)
    {
        t.Logged = true;
        t.LoggedPostponed = !ok;
        try
        {
            Plugin.Debug($"{ClothesStore.NameOf(t.Kind)} '{t.Go.name}' (found by {t.Via}, {All.Count} employee(s) known): model '{t.Model.name}', " +
                         $"{t.Rends.Length} renderer(s), apply {(ok ? "complete" : "still postponed after " + t.Tries + " tries")}, " +
                         $"color #{ClothesStore.Hex(ClothesStore.Get(t.Kind).Value)}: {Recolor.Describe(t.Rends)}");
        }
        catch (Exception e) { Plugin.Debug("Employee diagnostics failed: " + e.Message); }
    }

    /// <summary>
    /// Puts the original materials back on every employee, active or pooled (mod switched off, scene change, settings
    /// change). Goes through the renderer registry, so it doesn't depend on finding the employees. Also forgets the
    /// known employees and starts the rolling full scan again.
    /// </summary>
    internal static void RestoreAll()
    {
        Recolor.RestoreRegistry();
        ById.Clear();
        All.Clear();
        _initialPos = 0;
        _rotation = 0;
        _fastRotation = 0;
        _incomplete = false;
        _dirty = false;
        _lastVersion = -1;
        _nextVerifyCheck = 0f;
        _nextScan = Time.unscaledTime + 0.75f;
        _nextFastScan = 0f;
        for (int i = 0; i < Hooked.Length; i++) Hooked[i] = null;
        Plugin.Debug("Original employee clothes restored");
    }

    // ------------------------------------------------------------------ safety net

    private static void ScanStep(float now)
    {
        if (now >= _nextScan)
        {
            if (_initialPos < Kinds.Length)
            {
                // First look after a scene start / enabling: every type, one per step.
                _nextScan = now + 0.25f;
                ScanKind(Kinds[_initialPos++]);
                return;
            }
            _nextScan = now + 5f;
            if (ClothesStore.AnySet)
            {
                // Steady state: one hooked, colored type per step (a full round takes 5 s x the colored types).
                for (int n = 0; n < Kinds.Length; n++)
                {
                    var k = Kinds[_rotation++ % Kinds.Length];
                    if (!ClothesStore.Get(k).HasValue || !IsHooked(k)) continue;
                    ScanKind(k);
                    break;
                }
            }
        }
        // Types whose hook didn't apply can't register hires: look for them more often.
        if (now >= _nextFastScan && _initialPos >= Kinds.Length)
        {
            _nextFastScan = now + 1f;
            if (ClothesStore.AnySet)
            {
                for (int n = 0; n < Kinds.Length; n++)
                {
                    var k = Kinds[_fastRotation++ % Kinds.Length];
                    if (!ClothesStore.Get(k).HasValue || IsHooked(k)) continue;
                    ScanKind(k);
                    break;
                }
            }
        }
    }

    private static Type TypeOf(HrEmployeeKind k) => k switch
    {
        HrEmployeeKind.Cashier => typeof(Cashier),
        HrEmployeeKind.Restocker => typeof(ClerkT),
        HrEmployeeKind.CustomerHelper => typeof(CustomerHelper),
        HrEmployeeKind.SecurityGuard => typeof(SecurityGuard),
        HrEmployeeKind.Janitor => typeof(JanitorT),
        HrEmployeeKind.Courier => typeof(CourierT),
        HrEmployeeKind.Baker => typeof(Baker),
        HrEmployeeKind.IceCreamHelper => typeof(IceCreamHelper),
        _ => null,
    };

    /// <summary>Whether our Start postfix is applied for this type (checked once, through Harmony's patch info).</summary>
    private static bool IsHooked(HrEmployeeKind k)
    {
        int i = (int)k;
        if (i < 0 || i >= Hooked.Length) return false;
        if (Hooked[i].HasValue) return Hooked[i].Value;
        bool ok = false;
        try
        {
            var type = TypeOf(k);
            var m = type != null ? AccessTools.Method(type, "Start") : null;
            var info = m != null ? Harmony.GetPatchInfo(m) : null;
            ok = info != null && info.Postfixes.Any(p => p.owner == Plugin.Guid);
        }
        catch (Exception e) { Plugin.Debug($"Checking the {k} hook failed: {e.Message}"); }
        Hooked[i] = ok;
        if (!ok) Plugin.Warn($"The {ClothesStore.NameOf(k)} start hook isn't applied; looking for those employees by scanning (slower)");
        return ok;
    }

    private static void ScanKind(HrEmployeeKind k)
    {
        PassWatch.Restart();
        ScanKindInner(k);
        double ms = PassWatch.Elapsed.TotalMilliseconds;
        if (ms > 3.0) SlowLog($"scan for {k}", ms);
    }

    private static void ScanKindInner(HrEmployeeKind k)
    {
        switch (k)
        {
            case HrEmployeeKind.Cashier: Scan<Cashier>(k); break;
            case HrEmployeeKind.Restocker: Scan<ClerkT>(k); break;
            case HrEmployeeKind.CustomerHelper: Scan<CustomerHelper>(k); break;
            case HrEmployeeKind.SecurityGuard: Scan<SecurityGuard>(k); break;
            case HrEmployeeKind.Janitor: Scan<JanitorT>(k); break;
            case HrEmployeeKind.Courier: Scan<CourierT>(k); break;
            case HrEmployeeKind.Baker: Scan<Baker>(k); break;
            case HrEmployeeKind.IceCreamHelper: Scan<IceCreamHelper>(k); break;
        }
    }

    private static void Scan<T>(HrEmployeeKind kind) where T : Component
    {
        try
        {
            // Inactive ones are included: fired employees are pooled (LeanPool deactivates them) and come back later.
            var arr = Object.FindObjectsByType(Il2CppType.Of<T>(), FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < arr.Length; i++)
            {
                var comp = arr[i]?.TryCast<Component>();
                if (comp != null) Register(kind, comp, "scan");
            }
        }
        catch (Exception e) { Plugin.Debug($"Scanning for {kind} failed: {e.Message}"); }
    }

    // ------------------------------------------------------------------ errors

    private static float _nextErrorLog;

    /// <summary>Warn about an exception at most every 10 s (the driver runs these paths all the time).</summary>
    internal static void LogOnce(string what, Exception e)
    {
        if (Time.unscaledTime < _nextErrorLog) return;
        _nextErrorLog = Time.unscaledTime + 10f;
        Plugin.Warn($"{what} failed: {e}");
    }
}

/// <summary>Per-frame driver. Registered into IL2CPP by BasePlugin.AddComponent.</summary>
public class ClothesDriver : MonoBehaviour
{
    public ClothesDriver(IntPtr ptr) : base(ptr) { }

    private float _nextEmployees;
    private float _nextStore;
    private float _nextTab;

    private void Update()
    {
        if (!Plugin.Active) return;
        try { Preview.Update(); }
        catch (Exception e) { Employees.LogOnce("Preview update", e); }
        try { TabUi.PerFrame(); }
        catch (Exception e) { Employees.LogOnce("Picker poll", e); }

        float now = Time.unscaledTime;
        if (now >= _nextStore)
        {
            _nextStore = now + 1f;
            try { ClothesStore.Tick(); } catch (Exception e) { Employees.LogOnce("Color sync", e); }
        }
        if (Employees.Wants || now >= _nextEmployees)
        {
            _nextEmployees = now + (Employees.Busy ? 0.15f : 0.25f);
            try { Employees.Sweep(); } catch (Exception e) { Employees.LogOnce("Employee sweep", e); }
        }
        if (now >= _nextTab)
        {
            _nextTab = now + 0.5f;
            try { TabUi.Tick(); } catch (Exception e) { Employees.LogOnce("Tab update", e); }
        }
    }
}
