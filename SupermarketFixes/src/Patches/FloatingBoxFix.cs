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
/// Fix: after every player pickup (boxes within 2.5 m, 0.25 s and 1 s later) and every 5 s for the whole scene, on the machine that simulates the box (offline, or the Photon owner), find boxes whose
/// Rigidbody is asleep, not kinematic and has solid colliders, and probe a thin slab just under their
/// collider bounds. Nothing solid there = unsupported: Rigidbody.WakeUp(), and the normal physics lets it
/// fall. A supported box is left alone (its slab hits the box or floor it rests on), so the cost is one
/// overlap query per sleeping loose box; the scene-wide scan runs only every 5 s. Remote clients only see the result through the game's transform sync.
///
/// VerboseLogging: an unsupported box that is NOT asleep or is kinematic is logged once ([FloatBox]) with its
/// state, so a different cause (for example a box stuck kinematic) can be identified from the log.
/// </summary>
internal static class FloatingBoxFix
{
    internal static ConfigEntry<bool> WakeUnsupported;

    /// <summary>Safety net for boxes that leave a pile in ways we don't see (clerks, trash, despawn). Slow on purpose:
    /// FindObjectsOfType walks every box in the scene, racked ones included.</summary>
    private const float SweepInterval = 5f;
    /// <summary>After a pickup: check the boxes within this radius once the box has left its spot.</summary>
    private const float NearRadius = 2.5f;
    private static readonly float[] NearDelays = { 0.25f, 1.0f };
    private const int MaxWakeLogs = 30;
    private const int MaxOtherLogs = 12;

    internal static void Bind(ConfigFile cfg)
    {
        WakeUnsupported = cfg.Bind("FloatingBox", "WakeUnsupportedBoxes", true,
            "Boxes in a pile that stay floating after the box under them was taken are woken up so they fall.");
    }

    internal static void ResetState()
    {
        s_next = 0f;
        s_events.Clear();
        s_woken = 0;
        s_otherLogs = 0;
        s_loggedErrors = false;
        s_seenOther.Clear();
    }

    private static float s_next;
    private static readonly List<(Vector3 pos, float due)> s_events = new();
    private static int s_woken;
    private static int s_otherLogs;
    private static bool s_loggedErrors;
    private static readonly HashSet<IntPtr> s_seenOther = new();

    /// <summary>A player picked this box up: its neighbours may have lost their support.</summary>
    internal static void BoxPickedUp(Vector3 pos)
    {
        if (!WakeUnsupported.Value) return;
        float now = Time.unscaledTime;
        foreach (var d in NearDelays) s_events.Add((pos, now + d));
        if (s_events.Count > 64) s_events.RemoveRange(0, s_events.Count - 64);
    }

    internal static void Tick(float now)
    {
        if (!WakeUnsupported.Value) return;
        if (s_events.Count > 0) RunEvents(now);
        if (now < s_next) return;
        s_next = now + SweepInterval;
        if (!NoktaSingleton<PlayerManager>.HasInstance) return;

        foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<Box>()))
        {
            var box = o.TryCast<Box>();
            if (box != null) SafeCheck(box);
        }
    }

    private static void RunEvents(float now)
    {
        for (int i = s_events.Count - 1; i >= 0; i--)
        {
            var (pos, due) = s_events[i];
            if (now < due) continue;
            s_events.RemoveAt(i);
            // Only colliders near the pickup are looked at, not every box in the scene.
            var near = Physics.OverlapSphere(pos, NearRadius, ~0, QueryTriggerInteraction.Ignore);
            var seen = new HashSet<IntPtr>();
            for (int k = 0; near != null && k < near.Length; k++)
            {
                var c = near[k];
                if (c == null) continue;
                var box = c.GetComponentInParent<Box>();
                if (box != null && seen.Add(box.Pointer)) SafeCheck(box, probe: false);
            }
        }
    }

    private static void SafeCheck(Box box, bool probe = true)
    {
        try { Check(box, probe); }
        catch (Exception e)
        {
            if (!s_loggedErrors)
            {
                s_loggedErrors = true;
                Plugin.Logger.LogError($"[FloatBox] checking a box failed (further errors suppressed): {e}");
            }
        }
    }

    private static bool Simulated(Box box)
    {
        if (!PhotonNetwork.IsConnected) return true;
        var view = box.NetworkBox != null ? box.NetworkBox.PhotonView : null;
        return view == null || view.IsMine;
    }

    private static void Check(Box box, bool probe)
    {
        if (!box.gameObject.activeInHierarchy || box.Racked) return;
        var rb = box.RigidBody;
        if (rb == null) return;
        bool sleeping = rb.IsSleeping();
        if (!sleeping && !rb.isKinematic) return;   // awake and simulated: physics handles it
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

        // Right after a pickup every sleeping neighbour is woken without the support test: a box left half
        // overhanging (one of its two supports taken) counts as "supported" by the slab, yet would fall. PhysX
        // puts stable boxes back to sleep within about half a second, so a needless wake costs nothing.
        if ((probe || rb.isKinematic) && Supported(box, bounds)) return;

        if (rb.isKinematic)
        {
            // Carried boxes (employees, customers) are kinematic and parented: not interesting.
            if (box.transform.parent == null)
                LogOther(box, rb, bounds, "kinematic and nothing under it (not touched)");
            return;
        }

        rb.WakeUp();
        s_woken++;
        if (Plugin.VerboseLogging.Value && s_woken <= MaxWakeLogs)
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
        if (!Plugin.VerboseLogging.Value || s_otherLogs >= MaxOtherLogs || !s_seenOther.Add(box.Pointer)) return;
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

[HarmonyLib.HarmonyPatch(typeof(Box), nameof(Box.PickUp), new Type[0])]
internal static class FloatBox_PickUp_Patch
{
    // Prefix: afterwards the box is parented to the hand, so its position would no longer be the pile's.
    private static void Prefix(Box __instance, out Vector3 __state)
    {
        __state = Vector3.zero;
        try { __state = __instance.transform.position; } catch (Exception) { }
    }

    private static void Postfix(Vector3 __state)
    {
        try { FloatingBoxFix.BoxPickedUp(__state); } catch (Exception) { }
    }
}

[HarmonyLib.HarmonyPatch(typeof(Box), nameof(Box.PickUp), new[] { typeof(PlayerInstance) })]
internal static class FloatBox_PickUpPlayer_Patch
{
    // Prefix: afterwards the box is parented to the hand, so its position would no longer be the pile's.
    private static void Prefix(Box __instance, out Vector3 __state)
    {
        __state = Vector3.zero;
        try { __state = __instance.transform.position; } catch (Exception) { }
    }

    private static void Postfix(Vector3 __state)
    {
        try { FloatingBoxFix.BoxPickedUp(__state); } catch (Exception) { }
    }
}
