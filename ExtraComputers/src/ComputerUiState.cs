/*
 * ExtraComputers: separate screens per computer.
 *
 * Every computer borrows the one real computer UI (see ComputerSession), so in vanilla terms the "desktop" is shared:
 * whatever app was open when one computer was left is still open on the next one. The store computer behaves the same
 * way in vanilla (Back/set_OpenPC(false) close dropdowns, keyboards and gamepad panels but leave apps and tabs as they
 * are), which is what a player sees as "my last window is still open".
 *
 * What is kept per computer (captured when its session ends, restored just before the next session on it starts):
 *  - which windows are open: the direct children of the screen root (m_ComputerScreen) that are windows (a
 *    BaseWindow/AppWindow component, or a name ending in " App", which is how mods that add an app name their panel),
 *    plus the children of the Desktop Canvas with such a name (Order History puts its panel there), and the OS's
 *    m_CurrentWindow;
 *  - the open tab of every TabManager under the screen that has tabs (Market, Management, Bank, Pricing). The Hiring
 *    tab's nested "Employees" TabManager has NO WindowTab children (m_Tabs is empty) and TabManager.ResetTabs reads
 *    m_Tabs[0] without a length check (IndexOutOfRange, ISIL), so managers without tabs are skipped;
 *  - the Hiring tab's selected employee type, which is not a TabManager tab but HiringTab.m_CurrentIndex (+0xD0):
 *    HiringTab.OnEnable calls OpenTab(m_CurrentIndex) when it is >= 0 and available, else opens the first available.
 * A computer without saved state (never used, or the extra computer was bought after a scene load) starts clean:
 * the game's own ComputerOperatingSystem.CloseAllApp (every app closed, desktop only), the windows named above closed,
 * TabManager.ResetTabs on every manager with tabs (first tab) and HiringTab.m_CurrentIndex = -1 (first available).
 *
 * The shared screen belongs to one computer at a time (_liveKey, the store computer at the start, like vanilla). Apply
 * does nothing when the screen already belongs to the computer being started, so re-entering the same computer behaves
 * exactly like vanilla (nothing is closed or reopened). Every step and every item is fail-safe on its own: one failure
 * never skips the later steps.
 * Windows are opened AFTER the vanilla InstantInteract (Finish), like a click on the desktop icon: AppWindow.Open calls
 * GamePadUIPanel.OpenPanel on gamepad, which belongs after set_OpenPC(true). Remaining gamepad risk: Computer.lastOpenedTabButton
 * is one shared value (only a tab button colour), unverified.
 *
 * Why not everything: data-driven objects (list entries, buttons hidden because something is owned, badges) change
 * with shared game data, so a stale snapshot would show wrong states; scroll positions, search text, dropdown choices
 * and inner popups are not tracked by any game class that could be restored reliably. Those stay as the last
 * computer left them (data-driven ones refresh from the data anyway).
 *
 * The store computer is handled the same way (key 0): Computer.InstantInteract prefix applies its state, the Back
 * postfix captures it. Nothing here touches the network: each player's UI is local.
 */
using System;
using System.Collections.Generic;
using __Project__.Scripts.Computer;
using __Project__.Scripts.Computer.Management.Hiring_Tab;
using UnityEngine;

namespace ExtraComputers;

internal static class ComputerUiState
{
    private sealed class State
    {
        public readonly List<GameObject> Windows = new();
        public GameObject Current;
        public readonly List<KeyValuePair<TabManager, string>> Tabs = new();
        public readonly List<KeyValuePair<HiringTab, int>> Hiring = new();
    }

    internal const int StoreKey = 0;
    private const string WindowSuffix = " App";

    private static readonly Dictionary<int, State> States = new();
    private const int Unknown = int.MinValue;
    /// <summary>Which computer the live (shared) screen currently belongs to.</summary>
    private static int _liveKey = StoreKey;
    private static State _pending;
    private static int _pendingKey = Unknown;

    /// <summary>Scene change or toggling the mod: the saved objects are gone, the screen is the store computer's again.</summary>
    internal static void Clear()
    {
        States.Clear();
        _liveKey = StoreKey;
        _pending = null;
        _pendingKey = Unknown;
    }

    /// <summary>A computer object (possibly a pooled one that is spawned again) starts without any saved state.</summary>
    internal static void Forget(int key)
    {
        if (key == StoreKey) return;
        States.Remove(key);
        if (_liveKey == key) _liveKey = Unknown;
    }

    /// <summary>The computer didn't open: forget the windows that were waiting to be opened.</summary>
    internal static void DropPending() { _pending = null; _pendingKey = Unknown; }

    /// <summary>Keyed on the GameObject (what LeanPool re-uses and what <c>Forget</c> callers hold).</summary>
    internal static int KeyOf(Furniture prop) => prop != null ? prop.gameObject.GetInstanceID() : StoreKey;

    private static string Name(int key) => key == StoreKey ? "store" : key.ToString();

    // ------------------------------------------------------------------ finding things

    private static Transform ScreenRoot(Computer comp) =>
        comp != null && comp.m_ComputerScreen != null ? comp.m_ComputerScreen.transform : null;

    private static bool IsWindow(Transform t)
    {
        if (t == null || t.GetComponent<DesktopCanvas>() != null) return false;
        return t.GetComponent<BaseWindow>() != null || t.name.EndsWith(WindowSuffix, StringComparison.Ordinal);
    }

    /// <summary>Every window-like object of the screen, active or not.</summary>
    private static List<GameObject> FindWindows(Transform screen)
    {
        var list = new List<GameObject>();
        for (int i = 0; i < screen.childCount; i++)
        {
            var child = screen.GetChild(i);
            if (child == null) continue;
            if (child.GetComponent<DesktopCanvas>() != null)
            {
                for (int j = 0; j < child.childCount; j++)
                {
                    var c2 = child.GetChild(j);
                    if (c2 != null && c2.name.EndsWith(WindowSuffix, StringComparison.Ordinal)) list.Add(c2.gameObject);
                }
            }
            else if (IsWindow(child)) list.Add(child.gameObject);
        }
        return list;
    }

    private static ComputerOperatingSystem OsOf(Computer comp) =>
        comp != null ? comp.GetComponent<ComputerOperatingSystem>() : null;

    /// <summary>TabManager.ResetTabs throws on a manager without tabs (the Hiring tab's "Employees" has none).</summary>
    private static bool HasTabs(TabManager m)
    {
        m.EnsureTabsCached();
        var tabs = m.m_Tabs;
        return tabs != null && tabs.Length > 0;
    }

    // ------------------------------------------------------------------ capture / apply

    internal static void Capture(Computer comp, int key)
    {
        var screen = ScreenRoot(comp);
        if (screen == null) return;
        var st = new State();
        var names = new List<string>();
        var tabNames = new List<string>();
        var hiring = new List<string>();

        try
        {
            foreach (var w in FindWindows(screen))
            {
                if (w == null || !w.activeSelf) continue;
                st.Windows.Add(w);
                names.Add(w.name);
            }
            var os = OsOf(comp);
            var cur = os != null ? os.m_CurrentWindow : null;
            if (cur != null && st.Windows.Exists(g => g != null && g.Pointer == cur.gameObject.Pointer)) st.Current = cur.gameObject;
        }
        catch (Exception e) { Plugin.Warn("Saving the open windows failed: " + e.Message); }

        try
        {
            var managers = screen.GetComponentsInChildren<TabManager>(true);
            for (int i = 0; i < managers.Length; i++)
            {
                var m = managers[i];
                if (m == null) continue;
                try
                {
                    var tab = m.currentTab;
                    if (tab == null) continue;
                    st.Tabs.Add(new KeyValuePair<TabManager, string>(m, tab.TabName));
                    tabNames.Add($"{m.gameObject.name}:{tab.TabName}");
                }
                catch (Exception e) { Plugin.Warn($"Saving a tab failed: {e.Message}"); }
            }
        }
        catch (Exception e) { Plugin.Warn("Saving the open tabs failed: " + e.Message); }

        try
        {
            var tabs = screen.GetComponentsInChildren<HiringTab>(true);
            for (int i = 0; i < tabs.Length; i++)
            {
                if (tabs[i] == null) continue;
                st.Hiring.Add(new KeyValuePair<HiringTab, int>(tabs[i], tabs[i].m_CurrentIndex));
                hiring.Add(tabs[i].m_CurrentIndex.ToString());
            }
        }
        catch (Exception e) { Plugin.Warn("Saving the hiring tab failed: " + e.Message); }

        States[key] = st;
        _liveKey = key; // the screen is left the way this computer had it
        Plugin.Debug($"Screen state saved for computer {Name(key)}: windows [{string.Join(", ", names)}], " +
                     $"tabs [{string.Join(", ", tabNames)}], hiring type [{string.Join(", ", hiring)}]");
    }

    /// <summary>
    /// Before the vanilla InstantInteract: puts the shared UI into the clean default and the tab choices of this
    /// computer. Does nothing when the screen already belongs to this computer. Windows are opened by
    /// <see cref="Finish"/>.
    /// </summary>
    internal static void Apply(Computer comp, int key)
    {
        _pending = null;
        _pendingKey = Unknown;
        var screen = ScreenRoot(comp);
        if (screen == null) return;
        if (key == _liveKey)
        {
            Plugin.Debug($"Screen already belongs to computer {Name(key)}; left as it is");
            return;
        }
        States.TryGetValue(key, out var st);
        _liveKey = Unknown; // until Finish: a half-applied screen belongs to nobody

        // 1. Clean default: all apps closed, every tab manager with tabs on its first tab, hiring type reset.
        var os = OsOf(comp);
        try { if (os != null) os.CloseAllApp(); }
        catch (Exception e) { Plugin.Warn("Closing the apps failed: " + e.Message); }
        try
        {
            foreach (var w in FindWindows(screen))
            {
                if (w == null || !w.activeSelf) continue;
                try
                {
                    var win = w.GetComponent<BaseWindow>();
                    if (win != null) win.Close(); else w.SetActive(false);
                }
                catch (Exception e) { Plugin.Warn($"Closing '{w.name}' failed: {e.Message}"); }
            }
        }
        catch (Exception e) { Plugin.Warn("Closing the windows failed: " + e.Message); }
        try
        {
            var managers = screen.GetComponentsInChildren<TabManager>(true);
            for (int i = 0; i < managers.Length; i++)
            {
                var m = managers[i];
                if (m == null) continue;
                try { if (HasTabs(m)) m.ResetTabs(); }
                catch (Exception e) { Plugin.Warn($"Resetting the tabs of '{m.gameObject.name}' failed: {e.Message}"); }
            }
        }
        catch (Exception e) { Plugin.Warn("Resetting the tabs failed: " + e.Message); }
        try
        {
            var tabs = screen.GetComponentsInChildren<HiringTab>(true);
            for (int i = 0; i < tabs.Length; i++)
                if (tabs[i] != null) tabs[i].m_CurrentIndex = -1;
        }
        catch (Exception e) { Plugin.Warn("Resetting the hiring type failed: " + e.Message); }

        if (st == null)
        {
            Plugin.Debug($"Screen reset to the desktop for computer {Name(key)} (nothing saved)");
            _pending = new State(); // nothing to open, but Finish marks the screen as this computer's
            _pendingKey = key;
            return;
        }

        // 2. Tabs and hiring type while the apps are still closed: TabManager/HiringTab.OnEnable re-open them with the app.
        int restoredTabs = 0;
        foreach (var pair in st.Tabs)
        {
            try
            {
                var m = pair.Key;
                if (m == null || pair.Value == null) continue;
                var cur = m.currentTab;
                if (cur != null && cur.TabName == pair.Value) continue;
                if (!HasTabs(m)) continue;
                m.OpenTab(pair.Value);
                restoredTabs++;
            }
            catch (Exception e) { Plugin.Warn($"Restoring tab '{pair.Value}' failed: {e.Message}"); }
        }
        foreach (var pair in st.Hiring)
        {
            try { if (pair.Key != null) pair.Key.m_CurrentIndex = pair.Value; }
            catch (Exception e) { Plugin.Warn($"Restoring the hiring type failed: {e.Message}"); }
        }
        Plugin.Debug($"Screen prepared for computer {Name(key)}: {restoredTabs} tab(s) changed");
        _pending = st;
        _pendingKey = key;
    }

    /// <summary>After the vanilla InstantInteract opened the PC: opens the windows this computer had open.</summary>
    internal static void Finish(Computer comp)
    {
        var st = _pending;
        int key = _pendingKey;
        _pending = null;
        _pendingKey = Unknown;
        if (st == null) return;
        var opened = new List<string>();
        foreach (var g in st.Windows)
        {
            try
            {
                if (g == null) continue;
                var app = g.GetComponent<AppWindow>();
                if (app != null) app.Open();
                else
                {
                    var win = g.GetComponent<BaseWindow>();
                    if (win != null) win.Open(); else g.SetActive(true);
                }
                opened.Add(g.name);
            }
            catch (Exception e) { Plugin.Warn($"Reopening '{g?.name}' failed: {e.Message}"); }
        }
        try
        {
            var os = OsOf(comp);
            if (os != null && st.Current != null)
            {
                var app = st.Current.GetComponent<AppWindow>();
                if (app != null) os.m_CurrentWindow = app;
            }
        }
        catch (Exception e) { Plugin.Warn("Restoring the current window failed: " + e.Message); }
        _liveKey = key;
        Plugin.Debug($"Screen state restored for computer {Name(key)}: windows [{string.Join(", ", opened)}]");
    }
}
