using System;
using Photon.Pun;
using UnityEngine;

namespace SupermarketFixes.Patches;

/// <summary>Shared helpers for Bug #2 / Bug #3: the local player and what it is holding.</summary>
internal static class HeldState
{
    /// <summary>The player this machine controls, or null outside a running store.</summary>
    internal static PlayerInstance LocalPlayer()
    {
        if (!NoktaSingleton<PlayerManager>.HasInstance) return null;
        var pm = NoktaSingleton<PlayerManager>.Instance;
        var p = pm != null ? pm.LocalPlayer : null;
        return p != null ? p : null;
    }

    internal static bool Online => PhotonNetwork.IsConnected;

    internal static string Role => !PhotonNetwork.IsConnected ? "offline" : PhotonNetwork.IsMasterClient ? "host" : "guest";

    internal static bool SameObject(UnityEngine.Object a, UnityEngine.Object b) =>
        a != null && b != null && a.Pointer == b.Pointer;

    internal static string Path(Transform t)
    {
        if (t == null) return "<none>";
        var s = t.name;
        for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s;
        return s;
    }

    internal static string Describe(Box box)
    {
        if (box == null) return "box=null";
        try
        {
            var t = box.transform;
            var pos = t.position;
            var data = box.Data;
            var nb = box.NetworkBox;
            var view = nb != null ? nb.PhotonView : null;
            var rb = box.m_RigidBody;
            return $"box[uid={(data != null ? data.UID : -1)} product={(data != null ? data.ProductID : -1)} " +
                   $"view={(view != null ? view.ViewID.ToString() : "local")} mine={(view == null || view.IsMine)} " +
                   $"netOccupied={(nb != null && nb.IsNetworkOccupied)} occupied={box.m_IsBoxOccupied}:" +
                   $"{(box.m_OccupyOwner != null ? box.m_OccupyOwner.name : "none")} " +
                   $"pos=({pos.x:F2},{pos.y:F2},{pos.z:F2}) parent={Path(t.parent)} " +
                   $"layer={LayerMask.LayerToName(box.gameObject.layer)} active={box.gameObject.activeInHierarchy} " +
                   $"kinematic={(rb != null ? rb.isKinematic.ToString() : "-")}]";
        }
        catch (Exception e) { return $"box[describe failed: {e.Message}]"; }
    }

    internal static string DescribePlayer(PlayerInstance p)
    {
        if (p == null) return "player=null";
        try
        {
            var pi = p.PlayerInteraction;
            var bi = p.BoxInteraction;
            var holder = p.PlayerObjectHolder;
            var cur = holder != null ? holder.m_CurrentObject : null;
            return $"player[{Role} inInteraction={(pi != null && pi.m_InInteraction)} " +
                   $"current={(p.CurrentInteraction != null ? p.CurrentInteraction.GetIl2CppType().Name : "none")} " +
                   $"boxInt.enabled={(bi != null && bi.Enable)} boxInt.waiting={(bi != null && bi.IsWaitingAvailability)} " +
                   $"boxInt.placing={(bi != null && bi.m_PlacingMode)} " +
                   $"holder.current={(cur != null ? Path(cur.transform) : "none")} " +
                   $"holder.placing={(holder != null && holder.m_PlacingMode)}]";
        }
        catch (Exception e) { return $"player[describe failed: {e.Message}]"; }
    }

    /// <summary>Everything this machine's player has under its own hierarchy, for bug reports (F9).</summary>
    internal static void Dump(string trigger)
    {
        var p = LocalPlayer();
        if (p == null) { Plugin.Logger.LogInfo($"[HeldState] {trigger}: no local player"); return; }
        Plugin.Logger.LogInfo($"[HeldState] {trigger}: {DescribePlayer(p)}");
        var bi = p.BoxInteraction;
        if (bi != null && bi.m_Box != null)
            Plugin.Logger.LogInfo($"[HeldState]   BoxInteraction.m_Box: {Describe(bi.m_Box)}");
        var holder = p.PlayerObjectHolder;
        var hold = holder != null ? holder.m_ObjectHolder : null;
        if (hold != null)
            for (int i = 0; i < hold.childCount; i++)
                Plugin.Logger.LogInfo($"[HeldState]   in hands: {Path(hold.GetChild(i))}");
        var boxes = p.GetComponentsInChildren<Box>(true);
        for (int i = 0; i < boxes.Length; i++)
            Plugin.Logger.LogInfo($"[HeldState]   box under player: {Describe(boxes[i])}");
        if (hold != null && !hold.IsChildOf(p.transform))
        {
            Plugin.Logger.LogInfo($"[HeldState]   hold point is outside the player: {Path(hold)}");
            var handBoxes = hold.GetComponentsInChildren<Box>(true);
            for (int i = 0; i < handBoxes.Length; i++)
                Plugin.Logger.LogInfo($"[HeldState]   box in hands: {Describe(handBoxes[i])}");
        }
    }
}
