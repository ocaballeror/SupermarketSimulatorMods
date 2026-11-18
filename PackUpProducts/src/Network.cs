using System;
using System.Collections.Generic;
using Photon.Pun;
using UnityEngine;
using Hashtable = ExitGames.Client.Photon.Hashtable;
using PhotonPlayer = Photon.Realtime.Player;

namespace PackUpProducts;

/// <summary>
/// The two small things packing needs from the network, both carried by Photon player custom properties
/// (unmodded clients just store them):
///
///  - <c>oscar.packup.ver</c>: every modded client publishes the mod version on its own player. A guest
///    only packs when the master client has published it (see PackUp.cs, "Guest").
///  - <c>oscar.packup.reg</c>: a modded guest that spawned a box writes "viewId:sequence" on its own player.
///    The modded host sees the change and adds that box's data to its InventoryManager.Boxes (the list the
///    game saves loose boxes from). Vanilla does the same for wholesale boxes through Setup_RPC(newBox:true),
///    which would also reset the count to a full box, so it can't be used here.
/// </summary>
internal static class Network
{
    internal const string VerKey = "oscar.packup.ver";
    private const string RegKey = "oscar.packup.reg";

    private static float _nextPresence;
    private static float _nextServe;
    private static int _seq;

    // host side: what we already handled / are waiting for, per guest actor number
    private static readonly Dictionary<int, string> Handled = new();
    private static readonly Dictionary<int, float> FirstSeen = new();

    internal static bool InRoom
    {
        get
        {
            try { return PhotonNetwork.InRoom && !PhotonNetwork.OfflineMode; }
            catch (Exception) { return false; }
        }
    }

    internal static bool IsGuest
    {
        get
        {
            try { return InRoom && !PhotonNetwork.IsMasterClient; }
            catch (Exception) { return false; }
        }
    }

    internal static void Reset()
    {
        Handled.Clear();
        FirstSeen.Clear();
        _nextPresence = _nextServe = 0f;
    }

    private static string ReadString(PhotonPlayer p, string key)
    {
        var props = p?.CustomProperties;
        if (props == null) return null;
        var k = (Il2CppSystem.Object)key;
        if (!props.ContainsKey(k)) return null;
        var v = props[k];
        return v?.ToString();
    }

    /// <summary>True when the master client runs this mod (a guest needs that, see PackUp.cs).</summary>
    internal static bool HostHasMod()
    {
        try
        {
            var s = ReadString(PhotonNetwork.MasterClient, VerKey);
            return !string.IsNullOrEmpty(s);
        }
        catch (Exception) { return false; }
    }

    /// <summary>Called every frame: publishes the version (once per 2 s check) and serves guests when we are the host.</summary>
    internal static void Tick()
    {
        if (!InRoom)
        {
            Handled.Clear();
            FirstSeen.Clear();
            return;
        }
        float now = Time.unscaledTime;
        if (now >= _nextPresence)
        {
            _nextPresence = now + 2f;
            Publish();
        }
        if (now >= _nextServe)
        {
            _nextServe = now + 0.25f;
            if (PhotonNetwork.IsMasterClient && Plugin.Enabled.Value) Serve(now);
        }
    }

    private static void Publish()
    {
        var me = PhotonNetwork.LocalPlayer;
        if (me == null) return;
        string want = Plugin.Enabled.Value ? Plugin.Version : "";
        string have = ReadString(me, VerKey) ?? "";
        if (have == want) return;
        var ht = new Hashtable();
        ht[(Il2CppSystem.Object)VerKey] = (Il2CppSystem.Object)want;
        me.SetCustomProperties(ht);
        PackUp.Log($"Published mod presence \"{want}\" (this machine is {(PhotonNetwork.IsMasterClient ? "the host" : "a guest")})");
    }

    /// <summary>Guest: tell the host to save the box with this PhotonView id.</summary>
    internal static void SendRegistration(int viewId)
    {
        var me = PhotonNetwork.LocalPlayer;
        if (me == null) return;
        _seq++;
        var ht = new Hashtable();
        ht[(Il2CppSystem.Object)RegKey] = (Il2CppSystem.Object)$"{viewId}:{_seq}";
        me.SetCustomProperties(ht);
    }

    /// <summary>Host: add the boxes modded guests announced to the saved inventory.</summary>
    private static void Serve(float now)
    {
        var others = PhotonNetwork.PlayerListOthers;
        if (others == null) return;
        for (int i = 0; i < others.Length; i++)
        {
            var p = others[i];
            if (p == null) continue;
            string val = ReadString(p, RegKey);
            if (string.IsNullOrEmpty(val)) continue;
            int actor = p.ActorNumber;
            if (Handled.TryGetValue(actor, out var done) && done == val) continue;
            if (!FirstSeen.ContainsKey(actor)) FirstSeen[actor] = now;

            int colon = val.IndexOf(':');
            if (colon <= 0 || !int.TryParse(val.Substring(0, colon), out int viewId))
            {
                Handled[actor] = val;
                continue;
            }
            bool ok = TryRegister(viewId, p.NickName ?? ("#" + actor), out string why);
            if (ok || now - FirstSeen[actor] > 5f)
            {
                if (!ok) PackUp.Warn($"Gave up saving the box (view {viewId}) a guest packed: {why}");
                Handled[actor] = val;
                FirstSeen.Remove(actor);
            }
        }
    }

    private static bool TryRegister(int viewId, string who, out string why)
    {
        why = null;
        try
        {
            var pv = PhotonView.Find(viewId);
            if (pv == null) { why = "no PhotonView with that id (yet)"; return false; }
            var nb = pv.GetComponent<NetworkBox>();
            var box = nb != null ? nb.Box : null;
            if (box == null) { why = "the view isn't a box"; return false; }
            if (box.Racked) { PackUp.Log($"Box of {who} (view {viewId}) is racked now; not adding it"); return true; }
            var data = box.Data;
            if (data == null) { why = "the box has no data yet"; return false; }
            var inv = NoktaSingleton<InventoryManager>.Instance;
            if (inv == null) { why = "no InventoryManager"; return false; }
            if (PackUp.InInventory(inv, data))
            {
                PackUp.Log($"Box of {who} (view {viewId}) was already in the saved inventory");
                return true;
            }
            inv.AddBox(data);
            PackUp.Log($"Saving the box {who} packed: view {viewId}, product {data.ProductID}, count {data.ProductCount} (added to the inventory)");
            return true;
        }
        catch (Exception e)
        {
            why = e.Message;
            return false;
        }
    }
}
