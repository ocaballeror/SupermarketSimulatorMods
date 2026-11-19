using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using Photon.Pun;
using UnityEngine;
using Hashtable = ExitGames.Client.Photon.Hashtable;

namespace EmployeeClothes;

/// <summary>
/// The chosen clothes color per employee type, where it comes from and where it goes.
///  - Host / solo player: the colors are edited in the UI, saved per save file in a text file next to the other
///    BepInEx configs and published as a Photon room property for guests.
///  - Guest: read-only, taken from the room property the host published (polled; unmodded hosts publish nothing).
/// </summary>
internal static class ClothesStore
{
    internal const string RoomKey = "oscar.employeeclothes";

    /// <summary>All employee types in the order the UI shows them.</summary>
    internal static readonly HrEmployeeKind[] Kinds =
    {
        HrEmployeeKind.Restocker, HrEmployeeKind.Cashier, HrEmployeeKind.CustomerHelper, HrEmployeeKind.IceCreamHelper,
        HrEmployeeKind.Baker, HrEmployeeKind.Janitor, HrEmployeeKind.SecurityGuard, HrEmployeeKind.Courier,
    };

    private static readonly Color?[] Colors = new Color?[8]; // indexed by (int)HrEmployeeKind
    private static string _key;               // save file the colors belong to (host / solo)
    private static string _lastRemote = "\u0001"; // last room string applied on a guest (sentinel = none yet)
    private static bool _wasGuest;

    /// <summary>Bumped whenever any color changes (the sweeper re-applies on the next pass anyway).</summary>
    internal static int Version { get; private set; }

    internal static bool AnySet
    {
        get
        {
            for (int i = 0; i < Colors.Length; i++) if (Colors[i].HasValue) return true;
            return false;
        }
    }

    internal static string NameOf(HrEmployeeKind k) => k switch
    {
        HrEmployeeKind.Restocker => "Restocker",
        HrEmployeeKind.CustomerHelper => "Customer helper",
        HrEmployeeKind.Cashier => "Cashier",
        HrEmployeeKind.IceCreamHelper => "Ice cream helper",
        HrEmployeeKind.Janitor => "Janitor",
        HrEmployeeKind.Baker => "Baker",
        HrEmployeeKind.SecurityGuard => "Security guard",
        HrEmployeeKind.Courier => "Courier",
        _ => k.ToString(),
    };

    /// <summary>Plural display name for the panel ("Cashiers"); the log keeps the singular <see cref="NameOf"/>.</summary>
    internal static string PluralOf(HrEmployeeKind k) => k switch
    {
        HrEmployeeKind.Restocker => "Restockers",
        HrEmployeeKind.CustomerHelper => "Customer helpers",
        HrEmployeeKind.Cashier => "Cashiers",
        HrEmployeeKind.IceCreamHelper => "Ice cream helpers",
        HrEmployeeKind.Janitor => "Janitors",
        HrEmployeeKind.Baker => "Bakers",
        HrEmployeeKind.SecurityGuard => "Security guards",
        HrEmployeeKind.Courier => "Couriers",
        _ => k + "s",
    };

    // ------------------------------------------------------------------ which types are unlocked

    /// <summary>
    /// "Unlocked" = the DLC check passes AND the player has a use for the type: at least one employee of it is already
    /// hired, or at least one of its hire slots currently has no unmet requirement. Verified in the ISIL (2026-11-18):
    ///  - DLC: HiringTab.RefreshAvailability lists the ice cream helper tab only if DLCTestController.IceCreamDLC and the
    ///    baker tab only if DLCTestController.BakeryDLC (all other kinds always).
    ///  - HrHiring.SlotIds(kind) = the employee ids (ScriptableObjects) of that kind; HrHiring.MissingCount(kind, id) =
    ///    the number of unmet entries (HrRequirement.Met == false) that HrHiring.Gates(kind, id) collects: the SO's store
    ///    level for every kind plus per-kind objectives (restocker: racks; customer helper: self checkouts and scales;
    ///    cashier: checkouts; baker: ovens and bakery displays; courier: its park area). It does not look at money
    ///    (HrHiring.CanHire = enough money AND MissingCount == 0), so this is "could be hired if the player had the money".
    ///  - EmployeeManager.IsEmployeeHired(kind, id) is the game's own "is hired" (a List&lt;int&gt;.Contains on the hired ids).
    /// Fails open: an exception, a missing singleton or a kind without any slot counts as unlocked (detail says why).
    /// </summary>
    internal static bool IsUnlocked(HrEmployeeKind k, out string detail)
    {
        detail = null;
        try
        {
            if (k == HrEmployeeKind.IceCreamHelper && !DLCTestController.IceCreamDLC) { detail = "no Ice Cream DLC (IceCreamDLC=False)"; return false; }
            if (k == HrEmployeeKind.Baker && !DLCTestController.BakeryDLC) { detail = "no Bakery DLC (BakeryDLC=False)"; return false; }
        }
        catch (Exception e)
        {
            detail = "DLC check failed: " + e.Message;
            Plugin.Warn($"Checking the DLC of {NameOf(k)} failed (treated as unlocked): {e.Message}");
            return true;
        }
        try
        {
            if (!NoktaSingleton<EmployeeManager>.HasInstance) { detail = "no EmployeeManager yet, shown"; return true; }
            var ro = HrHiring.SlotIds(k);
            // The interop interface only exposes the indexer; the object behind it is a List<int> (SlotIds fills one).
            var ids = ro != null ? ro.TryCast<Il2CppSystem.Collections.Generic.List<int>>() : null;
            if (ids == null) { detail = "slot list unreadable, shown"; return true; }
            int n = ids.Count;
            if (n == 0) { detail = "no hire slots, shown"; return true; }
            var slots = new int[n];                  // SlotIds returns a shared list: copy it first
            for (int i = 0; i < n; i++) slots[i] = ids[i];
            var em = NoktaSingleton<EmployeeManager>.Instance;
            int minMissing = int.MaxValue;
            for (int i = 0; i < n; i++)
            {
                if (em.IsEmployeeHired(k, slots[i])) { detail = "an employee is hired"; return true; }
                int miss = HrHiring.MissingCount(k, slots[i]);
                if (miss == 0) { detail = "can be hired (requirements met)"; return true; }
                minMissing = Math.Min(minMissing, miss);
            }
            detail = $"hiring requirements unmet in all {n} slot(s) (fewest unmet: {minMissing}), none hired";
            return false;
        }
        catch (Exception e)
        {
            detail = "requirement check failed: " + e.Message;
            Plugin.Warn($"Checking the hiring requirements of {NameOf(k)} failed (treated as unlocked): {e.Message}");
            return true;
        }
    }

    /// <summary>The employee types the panel offers (in UI order). Never empty: if no type passes, all are offered.</summary>
    internal static HrEmployeeKind[] UnlockedKinds(out string report)
    {
        var list = new List<HrEmployeeKind>();
        var sb = new StringBuilder();
        foreach (var k in Kinds)
        {
            bool ok = IsUnlocked(k, out var detail);
            if (ok) list.Add(k);
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(NameOf(k)).Append(ok ? ": shown" : ": hidden");
            if (detail != null) sb.Append(" (").Append(detail).Append(')');
        }
        report = sb.ToString();
        if (list.Count == 0)
        {
            Plugin.Warn("No employee type passed the unlock check; showing all of them.");
            return (HrEmployeeKind[])Kinds.Clone();
        }
        return list.ToArray();
    }

    internal static Color? Get(HrEmployeeKind k)
    {
        int i = (int)k;
        return i >= 0 && i < Colors.Length ? Colors[i] : null;
    }

    internal static void Set(HrEmployeeKind k, Color? c)
    {
        int i = (int)k;
        if (i < 0 || i >= Colors.Length) return;
        if (c.HasValue) c = new Color(Mathf.Clamp01(c.Value.r), Mathf.Clamp01(c.Value.g), Mathf.Clamp01(c.Value.b), 1f);
        Colors[i] = c;
        Version++;
    }

    // ------------------------------------------------------------------ network state

    internal static bool IsGuest
    {
        get
        {
            try { return PhotonNetwork.IsConnectedAndSlave; }
            catch (Exception) { return false; }
        }
    }

    private static bool Online
    {
        get
        {
            try { return PhotonNetwork.InRoom && !PhotonNetwork.OfflineMode; }
            catch (Exception) { return false; }
        }
    }

    // ------------------------------------------------------------------ text form ("Restocker=ff8800;Cashier=2255aa")

    internal static string Serialize()
    {
        var sb = new StringBuilder();
        foreach (var k in Kinds)
        {
            var c = Colors[(int)k];
            if (!c.HasValue) continue;
            if (sb.Length > 0) sb.Append(';');
            sb.Append(k.ToString()).Append('=').Append(Hex(c.Value));
        }
        return sb.ToString();
    }

    internal static void Deserialize(string s)
    {
        for (int i = 0; i < Colors.Length; i++) Colors[i] = null;
        if (!string.IsNullOrWhiteSpace(s))
        {
            foreach (var part in s.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                if (!Enum.TryParse<HrEmployeeKind>(part.Substring(0, eq).Trim(), out var kind)) continue;
                if (!TryParseHex(part.Substring(eq + 1).Trim(), out var c)) continue;
                int idx = (int)kind;
                if (idx >= 0 && idx < Colors.Length) Colors[idx] = c;
            }
        }
        Version++;
    }

    internal static string Hex(Color c) =>
        $"{Mathf.RoundToInt(Mathf.Clamp01(c.r) * 255f):x2}{Mathf.RoundToInt(Mathf.Clamp01(c.g) * 255f):x2}{Mathf.RoundToInt(Mathf.Clamp01(c.b) * 255f):x2}";

    private static bool TryParseHex(string s, out Color c)
    {
        c = Color.white;
        if (s.StartsWith("#")) s = s.Substring(1);
        if (s.Length != 6) return false;
        if (!int.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int v)) return false;
        c = new Color(((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f, 1f);
        return true;
    }

    // ------------------------------------------------------------------ persistence (host / solo)

    private static string FilePath => Path.Combine(Paths.ConfigPath, "oscar.employeeclothes.saves.txt");

    private static string SaveKey()
    {
        try
        {
            if (!NoktaSingleton<SaveManager>.HasInstance) return null;
            var p = NoktaSingleton<SaveManager>.Instance.CurrentSaveFilePath;
            return string.IsNullOrEmpty(p) ? null : Path.GetFileName(p);
        }
        catch (Exception) { return null; }
    }

    private static string ReadLine(string key)
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            foreach (var line in File.ReadAllLines(FilePath))
            {
                int tab = line.IndexOf('\t');
                if (tab > 0 && line.Substring(0, tab) == key) return line.Substring(tab + 1);
            }
        }
        catch (Exception e) { Plugin.Warn("Reading the saved colors failed: " + e.Message); }
        return null;
    }

    // The "Apply to all" switch is one global value, kept as a line "#applyall<TAB>1" in the same file. The key starts
    // with '#', so it can't clash with a save file name, and the per-save lines are left alone when it is rewritten.
    // Old files without the line mean false; older versions ignore the line (they only look for their save's key).
    private const string ApplyAllKey = "#applyall";
    private static bool _applyAll;
    private static bool _applyAllLoaded;

    /// <summary>The "Apply to all" switch of the screen, remembered across game restarts (read once, written on change).</summary>
    internal static bool ApplyToAll
    {
        get
        {
            if (!_applyAllLoaded)
            {
                _applyAllLoaded = true;
                _applyAll = ReadLine(ApplyAllKey)?.Trim() == "1";
            }
            return _applyAll;
        }
        set
        {
            _applyAllLoaded = true;
            if (_applyAll == value) return;
            _applyAll = value;
            if (IsGuest) return; // guests can't open the screen; never write from a guest
            try
            {
                var lines = new List<string>();
                if (File.Exists(FilePath))
                    foreach (var line in File.ReadAllLines(FilePath))
                    {
                        int tab = line.IndexOf('\t');
                        if (tab > 0 && line.Substring(0, tab) == ApplyAllKey) continue;
                        if (!string.IsNullOrWhiteSpace(line)) lines.Add(line);
                    }
                if (value) lines.Add(ApplyAllKey + "\t1");
                File.WriteAllLines(FilePath, lines);
                Plugin.Debug($"Saved Apply to all: {value}");
            }
            catch (Exception e) { Plugin.Warn("Saving the Apply to all switch failed: " + e.Message); }
        }
    }

    /// <summary>Writes the current colors under the current save's key (host / solo only).</summary>
    internal static void Save()
    {
        if (IsGuest) return;
        var key = _key ?? SaveKey();
        if (key == null) { Plugin.Warn("No save file known; the colors are applied but won't be remembered after a restart."); return; }
        _key = key;
        try
        {
            var lines = new List<string>();
            if (File.Exists(FilePath))
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int tab = line.IndexOf('\t');
                    if (tab > 0 && line.Substring(0, tab) == key) continue;
                    if (!string.IsNullOrWhiteSpace(line)) lines.Add(line);
                }
            var s = Serialize();
            if (s.Length > 0) lines.Add(key + "\t" + s);
            File.WriteAllLines(FilePath, lines);
            Plugin.Debug($"Saved colors for '{key}': '{s}'");
        }
        catch (Exception e) { Plugin.Warn("Saving the colors failed: " + e.Message); }
    }

    private static void LoadFor(string key)
    {
        _key = key;
        var line = ReadLine(key);
        Deserialize(line ?? "");
        Plugin.Info($"Colors for save '{key}': {(string.IsNullOrEmpty(line) ? "none" : line)}");
    }

    // ------------------------------------------------------------------ room property

    private static string RoomString()
    {
        try
        {
            var room = PhotonNetwork.CurrentRoom;
            var props = room?.CustomProperties;
            if (props == null) return null;
            var k = (Il2CppSystem.Object)RoomKey;
            if (!props.ContainsKey(k)) return null;
            return props[k]?.ToString();
        }
        catch (Exception) { return null; }
    }

    private static void SetRoomString(string value)
    {
        var ht = new Hashtable();
        ht[(Il2CppSystem.Object)RoomKey] = (Il2CppSystem.Object)value;
        PhotonNetwork.CurrentRoom.SetCustomProperties(ht, null, null);
    }

    private static void PublishRoom()
    {
        if (!Online) return;
        try
        {
            if (!PhotonNetwork.IsMasterClient || PhotonNetwork.CurrentRoom == null) return;
            string want = Plugin.SyncToGuests ? Serialize() : ""; // sharing off: make sure nothing stays published
            string have = RoomString() ?? "";
            if (want == have) return;
            SetRoomString(want);
            Plugin.Info($"Published the colors to the room: '{want}'");
        }
        catch (Exception e) { Plugin.Debug("Publishing the colors failed: " + e.Message); }
    }

    // ------------------------------------------------------------------ driver hooks

    /// <summary>Called by the driver once a second while the mod is active.</summary>
    internal static void Tick()
    {
        if (IsGuest)
        {
            if (!_wasGuest) { _wasGuest = true; _lastRemote = "\u0001"; }
            string remote = RoomString() ?? "";
            if (remote != _lastRemote)
            {
                _lastRemote = remote;
                Deserialize(remote);
                Plugin.Info($"Colors from the host: {(remote.Length == 0 ? "none" : remote)}");
            }
            return;
        }

        if (_wasGuest) { _wasGuest = false; _key = null; } // left the room: back to this PC's own save
        string key = SaveKey();
        if (key != null && key != _key) LoadFor(key);
        PublishRoom();
    }

    /// <summary>A new game scene was loaded: forget which save the colors belong to.</summary>
    internal static void OnNewScene()
    {
        _key = null;
        _lastRemote = "\u0001";
    }

    /// <summary>Marks the colors as changed so the next sweep re-applies them.</summary>
    internal static void Touch() => Version++;

    internal static void OnEnabled()
    {
        _key = null;
        _lastRemote = "\u0001";
        Version++;
    }

    /// <summary>The mod was switched off: put the original clothes back and clear what we published.</summary>
    internal static void OnDisabled()
    {
        try
        {
            if (Online && PhotonNetwork.IsMasterClient && PhotonNetwork.CurrentRoom != null
                && !string.IsNullOrEmpty(RoomString()))
                SetRoomString("");
        }
        catch (Exception e) { Plugin.Debug("Clearing the room property failed: " + e.Message); }
        Employees.RestoreAll();
        TabUi.Remove();
        Preview.Destroy();
        Recolor.DestroyAll();
    }
}
