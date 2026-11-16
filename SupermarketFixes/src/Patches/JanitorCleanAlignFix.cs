using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Photon.Pun;
using UnityEngine;
using UnityEngine.AI;
using __Project__.Scripts.Janitor.States;
using JanitorNpc = __Project__.Scripts.Janitor.Janitor;

namespace SupermarketFixes.Patches;

/// <summary>
/// Bug #7: the cleaning lady (janitor employee) cleans a floor stain successfully, but she isn't lined up
/// with it: she seems to mop a different patch of floor next to the stain.
///
/// Root cause (dumps + prefab data, not yet confirmed in-game):
/// - The janitor AI runs only on the master client (Janitor.Update returns on IsConnectedAndSlave). For a
///   stain (Dirt, CleaningType.Dirt) MoveTargetState.Enter calls NavMeshAgent.SetDestination with the stain's
///   own transform position, the centre of its 1 m floor quad.
/// - Arrival is Janitor.RotateAndCheck: when the distance between the agent's transform and agent.destination
///   is at most stoppingDistance, it invokes Janitor.Clean -> DirtCleaningState.Enter. The janitor prefab's
///   NavMeshAgent has stoppingDistance 0.1, radius 0.05, angularSpeed 0 (resources.assets, "Janitor"). So she
///   walks until she is standing on the centre of the stain.
/// - Her facing is never chosen for the stain: updateRotation is turned off and RotateAndCheck only slerps her
///   towards NavMeshAgent.desiredVelocity while walking. DirtCleaningState.Enter turns on the mop (m_Mop),
///   sets the Rig weight to 1 (hands IK onto the mop grips) and the animator bool "Dirt", but never rotates or
///   moves her. (Dust cleaning, by contrast, DORotates her to the dust's rotation before cleaning.)
/// - The mop (SM_Mop) is not held in a hand bone: it is a child of the character root, placed in the prefab
///   about 0.15 m to her right and 0.55 m in front of her feet; the hands follow it through the rig.
/// So the mop head works the floor about 0.57 m from the stain centre, in whatever direction she happened to
/// arrive from, while she stands on the stain itself. The cleaning progress (Dirt.AddProgress from the
/// CleaningForJanitorCoroutine) doesn't depend on distance, so the stain still disappears.
///
/// Fix (host/offline, where the AI runs; guests see it through the janitor's PhotonTransformView, which
/// syncs position and rotation):
/// - MoveTargetState.Enter postfix (stain targets only): instead of the stain centre, send her to a stand
///   point one mop-reach away from the stain, minus half the agent's stoppingDistance (she stops up to about
///   0.09 m before her destination, usually on her own side of it). Candidates are tried around the stain, starting on her side of
///   it; a candidate must be on the NavMesh close to the wanted spot, have a clear NavMesh line to the stain,
///   and preferably room for her body (capsule check against walls, furniture and racks). If none is
///   walkable, vanilla's destination is kept.
/// - DirtCleaningState.Enter prefix: remember the stain; the per-frame Tick then turns her (yaw only) so the
///   mop head, not her feet, lands on the stain. Nothing else rotates her during the dirt-cleaning state.
/// - While she cleans, the Tick measures where the mop head really is relative to her (the animation may
///   swing it) and, if the average is plausible, uses that offset for the next stains. With Diagnostics on,
///   each cleaning logs the mop head's average distance from the stain centre.
///
/// Related: Bug #8 (she walks into walls and shelves to clean a stain) shares part of this mechanism: she
/// always heads for the stain centre itself with a 0.05 m agent radius. This fix's stand-point choice avoids
/// spots without room for her body when another one exists, but it doesn't change how she walks there.
/// </summary>
internal static class JanitorCleanAlignFix
{
    internal static ConfigEntry<bool> AlignToStain;
    internal static ConfigEntry<bool> Diagnostics;

    /// <summary>Mop head position relative to her feet in the prefab (right, forward), in metres.</summary>
    private static readonly Vector2 PrefabMopOffset = new(0.15f, 0.55f);
    private const float TurnSpeedDeg = 540f;
    /// <summary>Wait this long after cleaning starts before measuring the mop (turn and animation blend-in).</summary>
    private const float MeasureDelay = 0.6f;
    private const int MinSamples = 20;
    private const int MaxLogs = 80;

    /// <summary>Layers that block her body: 0 Default, 6 Interactable, 10 Door, 14 Buildings, 15 Obstacle,
    /// 18 WallLayer, 20 RackLayer (indices from the game's TagManager).</summary>
    private static readonly int BodyMask =
        (1 << 0) | (1 << 6) | (1 << 10) | (1 << 14) | (1 << 15) | (1 << 18) | (1 << 20);

    internal static void Bind(ConfigFile cfg)
    {
        AlignToStain = cfg.Bind("JanitorCleanAlign", "AlignToStain", true,
            "The cleaning lady stands so that her mop, not her feet, is on the stain she cleans, and faces it. " +
            "Only needed on the host (or in single player).");
        Diagnostics = cfg.Bind("JanitorCleanAlign", "Diagnostics", true,
            "Log [JanitorAlign] lines: where she stands for each stain and how far her mop was from it.");
    }

    internal static void ResetState()
    {
        s_tracks.Clear();
        s_offset = PrefabMopOffset;
        s_logs = 0;
        s_loggedError = false;
        s_navRaycastBroken = false;
    }

    private sealed class Track
    {
        public JanitorNpc Janitor;
        public Vector3 Stain;
        public float Start;
        public bool Turning;
        public float TargetYaw;
        public float StandDist;
        public Renderer Head;
        public bool HeadSearched;
        public Vector2 OffsetSum;
        public float DistSum;
        public int Samples;
    }

    private static readonly Dictionary<IntPtr, Track> s_tracks = new();
    private static Vector2 s_offset = PrefabMopOffset;
    private static int s_logs;
    private static bool s_loggedError;
    private static bool s_navRaycastBroken;

    private static bool RunsAi => !PhotonNetwork.IsConnected || PhotonNetwork.IsMasterClient;

    private static void Log(string msg)
    {
        if (!Diagnostics.Value || s_logs >= MaxLogs) return;
        s_logs++;
        Plugin.Logger.LogInfo($"[JanitorAlign] {msg}" + (s_logs == MaxLogs ? " (further lines are not logged)" : ""));
    }

    internal static void LogError(string where, Exception e)
    {
        if (s_loggedError) return;
        s_loggedError = true;
        Plugin.Logger.LogError($"[JanitorAlign] {where} failed (further errors suppressed): {e}");
    }

    private static Vector3 Flat(Vector3 v) => new(v.x, 0f, v.z);

    // ---------------------------------------------------------------- stand point (MoveTargetState.Enter)

    internal static void OnMoveTargetEnter(MoveTargetState state)
    {
        if (!AlignToStain.Value || !RunsAi) return;
        var j = state._janitor;
        if (j == null) return;
        // Enter falls back to the idle state when the target is gone; only a live stain target is changed.
        if (j.sType != JanitorNpc.StateType.MoveTarget || j.cleaningType != JanitorNpc.CleaningType.Dirt) return;
        var target = j.TargetObject;
        if (target == null || !target.activeInHierarchy) return;
        var agent = j.m_Agent;
        if (agent == null || !agent.enabled || !agent.isOnNavMesh) return;

        Vector3 stain = target.transform.position;
        Vector3 from = j.transform.position;
        // She stops as soon as she is within stoppingDistance (3D, including the -0.04 base offset) of the
        // destination, usually on her own side of it, so aim a little inside the mop reach.
        float inset = Mathf.Clamp(agent.stoppingDistance * 0.5f, 0f, 0.1f);
        if (FindStandPoint(stain, from, inset, out var stand, out var how))
        {
            agent.SetDestination(stand);
            Log($"Janitor {j.JanitorID} heads for a stain at {Fmt(stain)}: standing at {Fmt(stand)}, " +
                $"{Flat(stand - stain).magnitude:F2} m from it ({how}; mop offset right={s_offset.x:F2} forward={s_offset.y:F2}).");
        }
        else
        {
            Log($"Janitor {j.JanitorID} heads for a stain at {Fmt(stain)}: no walkable spot one mop-reach away " +
                $"({how}), she goes to the stain itself as in the unmodded game.");
        }
    }

    private static bool FindStandPoint(Vector3 stain, Vector3 from, float inset, out Vector3 stand, out string how)
    {
        stand = default;
        float reach = Mathf.Max(0.1f, s_offset.magnitude - inset);
        if (!NavMesh.SamplePosition(stain, out var stainHit, 1.0f, NavMesh.AllAreas))
        {
            how = "stain is not near the NavMesh";
            return false;
        }
        Vector3 stainNav = stainHit.position;

        Vector3 baseDir = Flat(from - stain);
        baseDir = baseDir.sqrMagnitude > 1e-4f ? baseDir.normalized : Vector3.forward;

        bool haveFallback = false;
        Vector3 fallback = default;
        int fallbackAngle = 0;
        int walkable = 0;
        // Her side of the stain first (shortest walk), then alternately further round both ways.
        for (int k = 0; k < 12; k++)
        {
            int angle = k == 0 ? 0 : (k % 2 == 1 ? 1 : -1) * 30 * ((k + 1) / 2);
            // k = 11 gives +180; -180 never comes up.
            Vector3 dir = Quaternion.Euler(0f, angle, 0f) * baseDir;
            Vector3 want = new(stain.x + dir.x * reach, stainNav.y, stain.z + dir.z * reach);
            if (!NavMesh.SamplePosition(want, out var hit, 0.3f, NavMesh.AllAreas)) continue;
            Vector3 cand = hit.position;
            if (Flat(cand - want).magnitude > 0.15f) continue;   // spot itself not walkable
            if (Blocked(stainNav, cand, stain)) continue;          // a wall or shelf between her and the stain
            walkable++;
            bool room = !Physics.CheckCapsule(cand + Vector3.up * 0.35f, cand + Vector3.up * 1.45f, 0.15f,
                                              BodyMask, QueryTriggerInteraction.Ignore);
            if (room)
            {
                stand = cand;
                how = $"angle {angle}";
                return true;
            }
            if (!haveFallback) { haveFallback = true; fallback = cand; fallbackAngle = angle; }
        }
        if (haveFallback)
        {
            stand = fallback;
            how = $"angle {fallbackAngle}, no spot with room for her body";
            return true;
        }
        how = walkable == 0 ? "no candidate on the NavMesh" : "all candidates blocked";
        return false;
    }

    private static bool Blocked(Vector3 stainNav, Vector3 cand, Vector3 stain)
    {
        if (!s_navRaycastBroken)
        {
            try { return NavMesh.Raycast(stainNav, cand, out _, NavMesh.AllAreas); }
            catch (Exception e)
            {
                s_navRaycastBroken = true;
                Plugin.Logger.LogWarning($"[JanitorAlign] NavMesh.Raycast unavailable, using a physics ray instead: {e.Message}");
            }
        }
        Vector3 a = new(stain.x, stainNav.y + 0.3f, stain.z);
        Vector3 b = new(cand.x, cand.y + 0.3f, cand.z);
        return Physics.Linecast(a, b, BodyMask, QueryTriggerInteraction.Ignore);
    }

    // ---------------------------------------------------------------- facing + measuring (DirtCleaningState)

    internal static void OnDirtCleaningEnter(DirtCleaningState state)
    {
        if (!RunsAi) return;
        if (!AlignToStain.Value && !Diagnostics.Value) return;
        var j = state._janitor;
        if (j == null) return;
        var target = j.TargetObject;
        if (target == null) return;

        var t = new Track
        {
            Janitor = j,
            Stain = target.transform.position,
            Start = Time.unscaledTime,
        };
        Vector3 v = Flat(t.Stain - j.transform.position);
        t.StandDist = v.magnitude;
        float reach = s_offset.magnitude;
        string turn = "not turned";
        // Only turn when she stands about one mop-reach away; standing on the stain, no facing helps.
        if (AlignToStain.Value && t.StandDist >= reach * 0.5f && t.StandDist <= reach * 2f)
        {
            float yawToStain = Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg;
            float mopAngle = Mathf.Atan2(s_offset.x, s_offset.y) * Mathf.Rad2Deg;
            t.TargetYaw = yawToStain - mopAngle;
            t.Turning = true;
            turn = $"turning {Mathf.Abs(Mathf.DeltaAngle(j.transform.eulerAngles.y, t.TargetYaw)):F0} deg";
        }
        s_tracks[j.Pointer] = t;
        Log($"Janitor {j.JanitorID} starts cleaning a stain {t.StandDist:F2} m from her feet ({turn}).");
    }

    internal static void Tick(float now)
    {
        if (s_tracks.Count == 0) return;
        List<IntPtr> done = null;
        foreach (var kv in s_tracks)
        {
            var t = kv.Value;
            bool finished;
            try { finished = Step(t, now); }
            catch (Exception e) { LogError("Tick", e); finished = true; }
            if (finished) (done ??= new()).Add(kv.Key);
        }
        if (done != null) foreach (var p in done) s_tracks.Remove(p);
    }

    /// <summary>Returns true when the track is over.</summary>
    private static bool Step(Track t, float now)
    {
        var j = t.Janitor;
        if (j == null || !j.gameObject.activeInHierarchy) return true;
        if (j.sType != JanitorNpc.StateType.DirtCleaning)
        {
            Finish(t);
            return true;
        }
        var tr = j.transform;
        if (t.Turning && !AlignToStain.Value) t.Turning = false;   // switched off mid-turn
        if (t.Turning)
        {
            var goal = Quaternion.Euler(0f, t.TargetYaw, 0f);
            var cur = Quaternion.Euler(0f, tr.eulerAngles.y, 0f);
            var next = Quaternion.RotateTowards(cur, goal, TurnSpeedDeg * Time.deltaTime);
            tr.rotation = next;
            if (Quaternion.Angle(next, goal) < 0.5f) t.Turning = false;
            return false;
        }
        if (now - t.Start < MeasureDelay) return false;
        if (!TryHeadPoint(t, out var head)) return false;
        Vector3 local = tr.InverseTransformDirection(Flat(head - tr.position));
        t.OffsetSum += new Vector2(local.x, local.z);
        t.DistSum += Flat(head - t.Stain).magnitude;
        t.Samples++;
        return false;
    }

    private static bool TryHeadPoint(Track t, out Vector3 head)
    {
        head = default;
        var mop = t.Janitor.m_Mop;
        if (mop == null || !mop.activeInHierarchy) return false;
        if (!t.HeadSearched)
        {
            // The mop head is the part nearest the floor (SM_Mop_1; the handle SM_Mop_2 reaches up to her hands).
            t.HeadSearched = true;
            var rs = mop.GetComponentsInChildren<Renderer>(false);
            float best = float.MaxValue;
            for (int i = 0; rs != null && i < rs.Length; i++)
            {
                var r = rs[i];
                if (r == null || !r.enabled) continue;
                float y = r.bounds.center.y;
                if (y < best) { best = y; t.Head = r; }
            }
        }
        head = t.Head != null ? t.Head.bounds.center : mop.transform.position;
        return true;
    }

    private static void Finish(Track t)
    {
        if (t.Samples < MinSamples)
        {
            Log($"Janitor {SafeId(t.Janitor)} stopped cleaning before the mop could be measured ({t.Samples} samples).");
            return;
        }
        var avg = t.OffsetSum / t.Samples;
        float dist = t.DistSum / t.Samples;
        string adopt = "";
        bool plausible = avg.y >= 0.2f && avg.y <= 1.1f && Mathf.Abs(avg.x) <= 0.6f;
        if (plausible && (avg - s_offset).magnitude > 0.03f)
        {
            adopt = $"; next stains use this mop offset instead of right={s_offset.x:F2} forward={s_offset.y:F2}";
            s_offset = avg;
        }
        else if (!plausible) adopt = "; implausible, not used";
        Log($"Janitor {SafeId(t.Janitor)} finished a stain: her mop head was on average {dist:F2} m from the stain centre " +
            $"(she stood {t.StandDist:F2} m from it, mop reach {s_offset.magnitude:F2} m; mop at right={avg.x:F2} forward={avg.y:F2} of her{adopt}).");
    }

    private static string SafeId(JanitorNpc j)
    {
        try { return j != null ? j.JanitorID.ToString() : "?"; } catch { return "?"; }
    }

    private static string Fmt(Vector3 p) => $"({p.x:F2},{p.y:F2},{p.z:F2})";
}

[HarmonyLib.HarmonyPatch(typeof(MoveTargetState), nameof(MoveTargetState.Enter))]
internal static class JanitorAlign_MoveTargetEnter_Patch
{
    private static void Postfix(MoveTargetState __instance)
    {
        try { JanitorCleanAlignFix.OnMoveTargetEnter(__instance); }
        catch (Exception e) { JanitorCleanAlignFix.LogError("MoveTargetState.Enter postfix", e); }
    }
}

[HarmonyLib.HarmonyPatch(typeof(DirtCleaningState), nameof(DirtCleaningState.Enter))]
internal static class JanitorAlign_DirtCleaningEnter_Patch
{
    private static void Prefix(DirtCleaningState __instance)
    {
        try { JanitorCleanAlignFix.OnDirtCleaningEnter(__instance); }
        catch (Exception e) { JanitorCleanAlignFix.LogError("DirtCleaningState.Enter prefix", e); }
    }
}
