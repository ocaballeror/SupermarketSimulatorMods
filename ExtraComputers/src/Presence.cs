/*
 * ExtraComputers: who in the room has the mod, and who is on which computer.
 *
 * Why: the computer is a furniture type that only exists where this mod runs. A guest WITHOUT the mod
 * can't load its Photon prefab, and a computer furniture box (id 917001) makes FurnitureBox.Initialize /
 * Setup dereference a null FurnitureSO (verified in the ISIL: no null guard, jumps to the NRE thrower).
 * The host can't fix the guest, but it can avoid creating computer boxes and computers while somebody in
 * the room lacks the mod.
 *
 * How: every modded client publishes Photon player custom properties (same technique as PlayerCrouch; the
 * game's only player-property listener, RoomPlayerPropsListener -> RoomSettingsManager, looks at "png" and
 * "sid" only, so extra keys are ignored by unmodded clients):
 *   oscar.extracomputers        bool   "this client runs Extra Computers (enabled)"
 *   oscar.extracomputers.price  float  the host's price; modded guests show/charge that value
 *   oscar.extracomputers.use    int    PhotonView id of the extra computer this player is using (0 = none)
 * A player who never published the first key, or published false, counts as "without the mod".
 *
 * What the host does while somebody lacks the mod (config Multiplayer/ProtectUnmoddedPlayers, default on):
 *   - hides the computer in the furniture shop,
 *   - refuses MarketShoppingCart.Purchase when the cart holds a computer (guest purchases arrive there too),
 *   - refuses to open a computer box (NetworkFurnitureBoxInteraction.OpenFurnitureBox_Broadcast).
 * Not covered (can't be): computers and computer boxes that already exist when the player joins (placed in the
 * save, boxes bought earlier, a computer already in the cart): their Photon instantiation / box data reaches the
 * newcomer regardless. The README tells hosts to remove those first.
 *
 * Everything here fails open: on an unexpected error the gate lets the vanilla/mod behaviour through.
 */
using System;
using System.Collections.Generic;
using Photon.Pun;
using UnityEngine;
using Hashtable = ExitGames.Client.Photon.Hashtable;
using PhotonPlayer = Photon.Realtime.Player;

namespace ExtraComputers;

internal static class Presence
{
    internal const string KeyMod = "oscar.extracomputers";
    internal const string KeyPrice = "oscar.extracomputers.price";
    internal const string KeyUse = "oscar.extracomputers.use";

    private static float _nextTick;
    private static bool _lastBlocked;
    private static bool _loggedError;
    private static int _useViewId;

    internal static bool Online
    {
        get
        {
            try { return PhotonNetwork.InRoom && !PhotonNetwork.OfflineMode; }
            catch (Exception) { return false; }
        }
    }

    // ---------------------------------------------------------------- reading other players' properties

    private static Il2CppSystem.Object Get(PhotonPlayer p, string key)
    {
        var props = p?.CustomProperties;
        if (props == null) return null;
        var k = (Il2CppSystem.Object)key;
        return props.ContainsKey(k) ? props[k] : null;
    }

    private static bool? ReadBool(PhotonPlayer p, string key)
    {
        var v = Get(p, key);
        if (v == null) return null;
        try { return v.Unbox<bool>(); } catch (Exception) { return null; }
    }

    private static int? ReadInt(PhotonPlayer p, string key)
    {
        var v = Get(p, key);
        if (v == null) return null;
        try { return v.Unbox<int>(); } catch (Exception) { return null; }
    }

    private static float? ReadFloat(PhotonPlayer p, string key)
    {
        var v = Get(p, key);
        if (v == null) return null;
        try { return v.Unbox<float>(); } catch (Exception) { return null; }
    }

    // ---------------------------------------------------------------- the gate

    /// <summary>Names of the other players that don't have the mod (empty when the guard is off or offline).</summary>
    internal static List<string> PlayersWithoutMod()
    {
        var list = new List<string>();
        if (!Plugin.ProtectUnmodded.Value || !Online) return list;
        try
        {
            var others = PhotonNetwork.PlayerListOthers;
            if (others == null) return list;
            for (int i = 0; i < others.Length; i++)
            {
                var p = others[i];
                if (p == null) continue;
                if (ReadBool(p, KeyMod) != true)
                    list.Add(string.IsNullOrEmpty(p.NickName) ? "player " + p.ActorNumber : p.NickName);
            }
        }
        catch (Exception e)
        {
            if (!_loggedError) { Plugin.Warn("Checking the other players failed (computers stay allowed): " + e.Message); _loggedError = true; }
            list.Clear();
        }
        return list;
    }

    /// <summary>True while at least one other player lacks the mod (and the guard is on).</summary>
    internal static bool Blocked(out string who)
    {
        var names = PlayersWithoutMod();
        who = string.Join(", ", names);
        return names.Count > 0;
    }

    internal static bool Blocked() => Blocked(out _);

    // ---------------------------------------------------------------- publishing our own state

    /// <summary>Called about once a second from the store computer's LateUpdate while the mod is active.</summary>
    internal static void Tick()
    {
        if (Time.unscaledTime < _nextTick) return;
        _nextTick = Time.unscaledTime + 1f;
        try
        {
            if (Online) Publish(Plugin.Active);
            ComputerFurniture.ApplyPrice();

            bool blocked = Blocked(out var who);
            if (blocked != _lastBlocked)
            {
                _lastBlocked = blocked;
                if (blocked)
                    Plugin.Warn($"{who} doesn't have the Extra Computers mod: computer purchases and unboxing are paused. " +
                                "Computers or computer boxes that already exist will cause errors for that player.");
                else
                    Plugin.Info("Every player has the Extra Computers mod; computers are available again");
            }
            ComputerFurniture.RefreshShopItem();
        }
        catch (Exception e) { Plugin.Debug("Presence tick failed: " + e.Message); }
    }

    private static void Publish(bool modOn)
    {
        var me = PhotonNetwork.LocalPlayer;
        if (me == null) return;
        Hashtable ht = null;
        void Set(string key, Il2CppSystem.Object value)
        {
            ht ??= new Hashtable();
            ht[(Il2CppSystem.Object)key] = value;
        }

        if (ReadBool(me, KeyMod) != modOn) Set(KeyMod, (Il2CppSystem.Object)modOn);
        if (modOn && PhotonNetwork.IsMasterClient)
        {
            float price = Plugin.Price.Value;
            var cur = ReadFloat(me, KeyPrice);
            if (cur == null || Mathf.Abs(cur.Value - price) > 0.0001f) Set(KeyPrice, (Il2CppSystem.Object)price);
        }
        if ((ReadInt(me, KeyUse) ?? 0) != _useViewId) Set(KeyUse, (Il2CppSystem.Object)_useViewId);
        if (ht == null) return;
        me.SetCustomProperties(ht);
        Plugin.Debug($"Published player properties (mod={modOn}, using computer view {_useViewId})");
    }

    /// <summary>The price to use: the host's when this client is a guest and the host published one.</summary>
    internal static float EffectivePrice()
    {
        try
        {
            if (Online && !PhotonNetwork.IsMasterClient)
            {
                var master = PhotonNetwork.MasterClient;
                if (master != null && ReadBool(master, KeyMod) == true)
                {
                    var p = ReadFloat(master, KeyPrice);
                    if (p != null) return p.Value;
                }
            }
        }
        catch (Exception) { }
        return Plugin.Price.Value;
    }

    /// <summary>The mod is being switched off: tell everyone, so a host's guard counts this client as unmodded.</summary>
    internal static void OnDisabled()
    {
        _useViewId = 0;
        try
        {
            if (Online) Publish(false);
        }
        catch (Exception e) { Plugin.Debug("Clearing player properties failed: " + e.Message); }
        _lastBlocked = false;
    }

    // ---------------------------------------------------------------- who is on which computer

    /// <summary>Is another player currently using the extra computer with this PhotonView id?</summary>
    internal static bool IsInUseByOther(int viewId)
    {
        if (viewId == 0 || !Online) return false;
        try
        {
            var others = PhotonNetwork.PlayerListOthers;
            if (others == null) return false;
            for (int i = 0; i < others.Length; i++)
                if (others[i] != null && ReadInt(others[i], KeyUse) == viewId) return true;
        }
        catch (Exception) { }
        return false;
    }

    /// <summary>Marks the local player as using (or no longer using) an extra computer.</summary>
    internal static void SetUse(int viewId)
    {
        if (_useViewId == viewId) return;
        _useViewId = viewId;
        try
        {
            if (Online && Plugin.Active) Publish(true);
        }
        catch (Exception e) { Plugin.Debug("Publishing the computer in use failed: " + e.Message); }
    }
}
