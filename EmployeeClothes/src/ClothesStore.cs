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
/// The chosen clothes colour per employee type, where it comes from and where it goes.
///  - Host / solo player: the colours are edited in the UI, saved per save file in a text file next to the other
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
    private static string _key;               // save file the colours belong to (host / solo)
    private static string _lastRemote = "\u0001"; // last room string applied on a guest (sentinel = none yet)
    private static bool _wasGuest;

    /// <summary>Bumped whenever any colour changes (the sweeper re-applies on the next pass anyway).</summary>
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
        catch (Exception e) { Plugin.Warn("Reading the saved colours failed: " + e.Message); }
        return null;
    }

    /// <summary>Writes the current colours under the current save's key (host / solo only).</summary>
    internal static void Save()
    {
        if (IsGuest) return;
        var key = _key ?? SaveKey();
        if (key == null) { Plugin.Warn("No save file known; the colours are applied but won't be remembered after a restart."); return; }
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
            Plugin.Debug($"Saved colours for '{key}': '{s}'");
        }
        catch (Exception e) { Plugin.Warn("Saving the colours failed: " + e.Message); }
    }

    private static void LoadFor(string key)
    {
        _key = key;
        var line = ReadLine(key);
        Deserialize(line ?? "");
        Plugin.Info($"Colours for save '{key}': {(string.IsNullOrEmpty(line) ? "none" : line)}");
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
            string want = Plugin.SyncToGuests.Value ? Serialize() : ""; // sharing off: make sure nothing stays published
            string have = RoomString() ?? "";
            if (want == have) return;
            SetRoomString(want);
            Plugin.Info($"Published the colours to the room: '{want}'");
        }
        catch (Exception e) { Plugin.Debug("Publishing the colours failed: " + e.Message); }
    }

    // ------------------------------------------------------------------ driver hooks

    /// <summary>Called about every 0.4 s while the mod is active.</summary>
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
                Plugin.Info($"Colours from the host: {(remote.Length == 0 ? "none" : remote)}");
            }
            return;
        }

        if (_wasGuest) { _wasGuest = false; _key = null; } // left the room: back to this PC's own save
        string key = SaveKey();
        if (key != null && key != _key) LoadFor(key);
        PublishRoom();
    }

    /// <summary>A new game scene was loaded: forget which save the colours belong to.</summary>
    internal static void OnNewScene()
    {
        _key = null;
        _lastRemote = "\u0001";
    }

    /// <summary>Marks the colours as changed so the next sweep re-applies them.</summary>
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
