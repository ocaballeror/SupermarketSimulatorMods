using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Il2CppInterop.Runtime;
using Photon.Pun;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SupermarketFixes.Patches;

/// <summary>
/// Bug #6: in a pile of boxes (for example a big online order), taking the box directly under another one
/// sometimes leaves the upper box floating in the air until someone picks it up. Taking another box from
/// under it makes it fall.
///
/// Suspected cause (not confirmed in-game): the pile's boxes are ordinary non-kinematic Rigidbodies that have
/// gone to sleep. A sleeping body doesn't notice that its support is gone: a held box turns its colliders
/// into triggers and its Rigidbody kinematic (PlayerObjectHolder.HoldObject), a destroyed or moved box simply
/// vanishes, and PhysX doesn't wake the neighbours that were resting on it. The game only knows this for its
/// delivery street: Box.PickUp(PlayerInstance) calls SortableBoxManager.WakeUpAllBoxesInStreet, on the master
/// client only and for boxes inside the street area. A pile anywhere else, or a box that leaves in any other
/// way (clerk, trash, drop, throw), gets no wake-up.
///
/// Fix: every 0.5 s, on the machine that simulates the box (offline, or the Photon owner), find boxes whose
/// Rigidbody is asleep, not kinematic and has solid colliders, and probe a thin slab just under their
/// collider bounds. Nothing solid there = unsupported: Rigidbody.WakeUp(), and the normal physics lets it
/// fall. A supported box is left alone (its slab hits the box or floor it rests on), so the cost is one
/// overlap query per sleeping loose box. Remote clients only see the result through the game's transform sync.
///
/// Diagnostics: an unsupported box that is NOT asleep or is kinematic is logged once ([FloatBox]) with its
/// state, so a different cause (for example a box stuck kinematic) can be identified from the log.
/// </summary>
internal static class FloatingBoxFix
{
    internal static ConfigEntry<bool> WakeUnsupported;
    internal static ConfigEntry<bool> Diagnostics;

    private const float SweepInterval = 0.5f;
    private const int MaxWakeLogs = 30;
    private const int MaxOtherLogs = 12;

    internal static void Bind(ConfigFile cfg)
    {
        WakeUnsupported = cfg.Bind("FloatingBox", "WakeUnsupportedBoxes", true,
            "Boxes in a pile that stay floating after the box under them was taken are woken up so they fall.");
        Diagnostics = cfg.Bind("FloatingBox", "Diagnostics", true,
            "Log [FloatBox] lines when a floating box is woken, or when one is found that is not simply asleep.");
    }

    internal static void ResetState()
    {
        s_next = 0f;
        s_woken = 0;
        s_otherLogs = 0;
        s_loggedErrors = false;
        s_seenOther.Clear();
    }

    private static float s_next;
    private static int s_woken;
    private static int s_otherLogs;
    private static bool s_loggedErrors;
    private static readonly HashSet<IntPtr> s_seenOther = new();

    internal static void Tick(float now)
    {
        if (!WakeUnsupported.Value) return;
        if (now < s_next) return;
        s_next = now + SweepInterval;
        if (!NoktaSingleton<PlayerManager>.HasInstance) return;

        foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<Box>()))
        {
            var box = o.TryCast<Box>();
            if (box == null) continue;
            try { Check(box); }
            catch (Exception e)
            {
                if (!s_loggedErrors)
                {
                    s_loggedErrors = true;
                    Plugin.Logger.LogError($"[FloatBox] checking a box failed (further errors suppressed): {e}");
                }
            }
        }
    }

    private static bool Simulated(Box box)
    {
        if (!PhotonNetwork.IsConnected) return true;
        var view = box.NetworkBox != null ? box.NetworkBox.PhotonView : null;
        return view == null || view.IsMine;
    }

    private static void Check(Box box)
    {
        if (!box.gameObject.activeInHierarchy || box.Racked) return;
        var rb = box.RigidBody;
        if (rb == null) return;
        if (!Simulated(box)) return;

        // Solid colliders only: a held box has its colliders turned into triggers.
        var cols = box.GetComponentsInChildren<Collider>(false);
        Bounds bounds = default;
        bool any = false;
        for (int i = 0; cols != null && i < cols.Length; i++)
        {
            var c = cols[i];
            if (c == null || c.isTrigger || !c.enabled) continue;
            if (!any) { bounds = c.bounds; any = true; }
            else bounds.Encapsulate(c.bounds);
        }
        if (!any) return;

        bool sleeping = rb.IsSleeping();
        if (!sleeping && !rb.isKinematic) return;   // awake and simulated: physics handles it

        if (Supported(box, bounds)) return;

        if (rb.isKinematic)
        {
            LogOther(box, rb, bounds, "kinematic and nothing under it (not touched)");
            return;
        }

        rb.WakeUp();
        s_woken++;
        if (Diagnostics.Value && s_woken <= MaxWakeLogs)
            Plugin.Logger.LogInfo($"[FloatBox] Woke a sleeping box with nothing under it ({HeldState.Role}): {Describe(box, rb, bounds)}" +
                                  (s_woken == MaxWakeLogs ? " (further wake-ups are not logged)" : ""));
    }

    private static bool Supported(Box box, Bounds b)
    {
        // A thin slab covering [bottom - 3 cm, bottom + 1 cm], a bit narrower than the box.
        var center = new Vector3(b.center.x, b.min.y - 0.01f, b.center.z);
        var half = new Vector3(Mathf.Max(0.01f, b.extents.x * 0.7f), 0.02f, Mathf.Max(0.01f, b.extents.z * 0.7f));
        var hits = Physics.OverlapBox(center, half, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; hits != null && i < hits.Length; i++)
        {
            var h = hits[i];
            if (h == null) continue;
            if (h.transform.IsChildOf(box.transform)) continue;   // its own collider
            return true;
        }
        return false;
    }

    private static void LogOther(Box box, Rigidbody rb, Bounds b, string what)
    {
        if (!Diagnostics.Value || s_otherLogs >= MaxOtherLogs || !s_seenOther.Add(box.Pointer)) return;
        s_otherLogs++;
        Plugin.Logger.LogInfo($"[FloatBox] Box {what} ({HeldState.Role}): {Describe(box, rb, b)}");
    }

    private static string Describe(Box box, Rigidbody rb, Bounds b)
    {
        string uid = "?", product = "?";
        try { var d = box.Data; if (d != null) { uid = d.UID.ToString(); product = d.ProductID.ToString(); } } catch { }
        string view = "local";
        try
        {
            var v = box.NetworkBox != null ? box.NetworkBox.PhotonView : null;
            if (v != null) view = $"{v.ViewID} mine={v.IsMine}";
        }
        catch { }
        var parent = box.transform.parent;
        return $"uid={uid} product={product} pos=({b.center.x:F2},{b.min.y:F2},{b.center.z:F2}) view={view} " +
               $"parent={(parent != null ? parent.name : "none")} kinematic={rb.isKinematic} sleeping={rb.IsSleeping()} " +
               $"gravity={rb.useGravity} layer={LayerMask.LayerToName(box.gameObject.layer)}";
    }
}
