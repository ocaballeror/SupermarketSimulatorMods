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
/// with it: she seems to mop a different patch of floor next to the stain. The user's goal (2026-11-16): the
/// mop is aimed at the stain's centre, and she stands where her body fits. That includes Bug #8 (she pushes
/// herself into walls and shelves to clean a stain; overlapping other characters is fine).
///
/// Root cause (dumps + prefab data, not yet confirmed in-game):
/// - The janitor AI runs only on the master client (Janitor.Update returns on IsConnectedAndSlave). For a
///   stain (Dirt, CleaningType.Dirt) MoveTargetState.Enter calls NavMeshAgent.SetDestination with the stain's
///   own transform position, the centre of its 1 m floor quad.
/// - Arrival is Janitor.RotateAndCheck: when the distance between the agent's transform and agent.destination
///   is at most stoppingDistance, it invokes Janitor.Clean -> DirtCleaningState.Enter. The janitor prefab's
///   NavMeshAgent has stoppingDistance 0.1, radius 0.05, angularSpeed 0 (resources.assets, "Janitor"). So she
///   walks until she is standing on the centre of the stain, with a path planned for a 0.05 m radius.
/// - Her facing is never chosen for the stain: updateRotation is turned off and RotateAndCheck only slerps her
///   towards NavMeshAgent.desiredVelocity while walking. DirtCleaningState.Enter turns on the mop (m_Mop),
///   sets the Rig weight to 1 (hands IK onto the mop grips) and the animator bool "Dirt", but never rotates or
///   moves her. (Dust cleaning, by contrast, DORotates her to the dust's rotation before cleaning.)
/// - The mop (SM_Mop) is not held in a hand bone: it is a child of the character root, placed in the prefab
///   about 0.15 m to her right and 0.55 m in front of her feet; the hands follow it through the rig.
/// So the mop head works the floor about 0.57 m from the stain centre, in whatever direction she happened to
/// arrive from, while she stands on the stain itself. When the stain is next to a wall or shelf, her body
/// (about 0.13 m collider radius plus arms) and the mop end up inside it (Bug #8). The cleaning progress
/// (Dirt.AddProgress from the CleaningForJanitorCoroutine) doesn't depend on distance, so the stain still
/// disappears.
///
/// Fix (host/offline, where the AI runs; guests see it through the janitor's PhotonTransformView, which
/// syncs position and rotation):
/// - MoveTargetState.Enter postfix (stain targets only) picks where she stands and calls SetDestination on it.
///   Candidates lie on rings around the stain (24 directions, distances = mop reach, then reach +-0.1 ...
///   +-0.5 m, then further out to 1.6 m). A candidate must be on the NavMesh (within 0.1 m of the wanted
///   spot) with a clear NavMesh line to the stain, and her body must fit there: a capsule of radius 0.20 m
///   ("room") or at least 0.13 m, her own collider ("tight"), from 0.25 to 1.6 m high, free of walls,
///   furniture, racks and boxes (layers in BodyMask; characters are ignored). The cheapest candidate wins:
///   distance from the mop reach first (the mop head lands on the centre only at exactly that distance),
///   then a tight fit, a blocked mop path (a thin capsule from her hands to where the mop head lands), and
///   the walk round the stain. Rings are searched nearest-to-reach first and the search stops once no
///   further ring can beat the best candidate.
/// - Fallbacks: if no spot fits within 0.5 m of the reach, the stain centre and spots 0.1-0.2 m from it are
///   tried first (the mop lands about 0.4-0.6 m from the centre, still on the stain; among them a roomy fit and
///   a clear mop path win). Only if her body doesn't fit there either, the nearest fitting spot further out is
///   used (she still faces the stain with the mop aimed at its centre, but the mop falls short of it, off the
///   stain). If nothing on the rings fits (a stain in a niche or dead end), the near-centre spots are the last
///   resort: if her body fits there she stands there (the game's own spot; the mop can't reach the centre,
///   but she is inside nothing). Only if her body fits nowhere at all does she stand on the walkable ring spot closest to the
///   reach, and a warning is logged (body overlap, mop still aimed). If no candidate is walkable at all, the
///   vanilla destination is kept. She is
///   never sent away without cleaning: the selected stain has already been taken off the janitors' list
///   (JanitorManager.SelectCleaningObject -> SafeRemove), so skipping it would leave it for the players.
/// - DirtCleaningState.Enter prefix: remember the stain and the planned stand point; the per-frame Tick then
///   slides her the last few centimetres onto the planned point (she stops up to 0.1 m short; via
///   NavMeshAgent.nextPosition, so she stays on the NavMesh) and turns her (yaw only) so that the mop head
///   points at the stain centre. Nothing else moves or rotates her during the dirt-cleaning state.
/// - While she cleans, the Tick measures where the mop head really is relative to her (the animation may
///   swing it) and, if the average is plausible, uses that offset for the next stains. With VerboseLogging on,
///   each cleaning logs the mop head's average distance from the stain centre.
/// - Not covered: the path she walks to the stand point is still planned by the game with its 0.05 m agent
///   radius, so she can brush walls and shelf corners on the way.
/// </summary>
internal static class JanitorCleanAlignFix
{
    internal static ConfigEntry<bool> AlignToStain;

    /// <summary>Mop head position relative to her feet in the prefab (right, forward), in metres.</summary>
    private static readonly Vector2 PrefabMopOffset = new(0.15f, 0.55f);
    private const float TurnSpeedDeg = 540f;
    private const float SlideSpeed = 0.6f;
    /// <summary>Wait this long after cleaning starts before measuring the mop (turn and animation blend-in).</summary>
    private const float MeasureDelay = 0.6f;
    private const int MinSamples = 20;
    private const int MaxLogs = 300;

    /// <summary>Body capsule: comfortable radius (body plus arms) and her own collider radius.</summary>
    private const float RoomRadius = 0.20f, TightRadius = 0.13f;
    private const float BodyBottom = 0.25f, BodyTop = 1.6f;
    /// <summary>Distances beyond reach +- this put the mop head off the stain (half its 1 m quad).</summary>
    private const float OnStainBand = 0.5f;
    private const float MaxStandDist = 1.6f, MinStandDist = 0.25f;
    private const int Directions = 24;

    /// <summary>Layers that block her body: 0 Default, 6 Interactable, 10 Door, 14 Buildings, 15 Obstacle,
    /// 18 WallLayer, 20 RackLayer, 25 Glass (the window dust panes), 29 TrashLayer (trash bins' only solid
    /// collider); indices from the game's TagManager. Player (3) and Customer (28) are left out on purpose:
    /// overlapping other characters is acceptable. Ignore Raycast (2) is left out: floor tiles and the entrance
    /// doors are on it.</summary>
    private static readonly int BodyMask =
        (1 << 0) | (1 << 6) | (1 << 10) | (1 << 14) | (1 << 15) | (1 << 18) | (1 << 20) | (1 << 25) | (1 << 29);

    internal static void Bind(ConfigFile cfg)
    {
        AlignToStain = cfg.Bind("JanitorCleanAlign", "AlignToStain", true,
            "The cleaning lady aims her mop at the centre of the stain she cleans and stands where she fits, " +
            "instead of standing on the stain (or inside a wall or shelf). Only needed on the host (or in single player).");
    }

    internal static void ResetState()
    {
        s_tracks.Clear();
        s_plans.Clear();
        s_offset = PrefabMopOffset;
        s_logs = 0;
        s_loggedError = false;
        s_navRaycastBroken = false;
        s_slideBroken = false;
    }

    private sealed class Track
    {
        public JanitorNpc Janitor;
        public Vector3 Stain;
        public float Start;
        public bool HasStand;
        public Vector3 Stand;      // NavMesh point she should end up on
        public bool Turning;
        public float TargetYaw;
        public float StandDist;    // where she stopped, before the slide
        public Renderer Head;
        public bool HeadSearched;
        public Vector2 OffsetSum;
        public float DistSum;
        public float FeetSum;
        public int Samples;
    }

    private readonly struct Plan
    {
        public readonly Vector3 Stain, Stand;
        public Plan(Vector3 stain, Vector3 stand) { Stain = stain; Stand = stand; }
    }

    private static readonly Dictionary<IntPtr, Track> s_tracks = new();
    private static readonly Dictionary<IntPtr, Plan> s_plans = new();
    private static Vector2 s_offset = PrefabMopOffset;
    private static int s_logs;
    private static bool s_loggedError;
    private static bool s_navRaycastBroken;
    private static bool s_slideBroken;

    private static bool RunsAi => !PhotonNetwork.IsConnected || PhotonNetwork.IsMasterClient;

    private static void Log(string msg)
    {
        if (!Plugin.VerboseLogging.Value || s_logs >= MaxLogs) return;
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
        s_plans.Remove(j.Pointer);
        // Enter falls back to the idle state when the target is gone; only a live stain target is changed.
        if (j.sType != JanitorNpc.StateType.MoveTarget || j.cleaningType != JanitorNpc.CleaningType.Dirt) return;
        var target = j.TargetObject;
        if (target == null || !target.activeInHierarchy) return;
        var agent = j.m_Agent;
        if (agent == null || !agent.enabled || !agent.isOnNavMesh) return;

        Vector3 stain = target.transform.position;
        Vector3 from = j.transform.position;
        if (FindStandPoint(stain, from, out var stand, out var how, out bool warn))
        {
            agent.SetDestination(stand);
            s_plans[j.Pointer] = new Plan(stain, stand);
            string msg = $"Janitor {j.JanitorID} heads for a stain at {Fmt(stain)}: standing at {Fmt(stand)}, " +
                         $"{Flat(stand - stain).magnitude:F2} m from it, mop reach {s_offset.magnitude:F2} m ({how}).";
            if (warn) Plugin.Logger.LogWarning($"[JanitorAlign] {msg}");   // rare, once per stain: always logged
            else Log(msg);
        }
        else
        {
            Log($"Janitor {j.JanitorID} heads for a stain at {Fmt(stain)}: {how}; she goes to the stain itself " +
                "as in the unmodded game.");
        }
    }

    /// <summary>
    /// Picks where she stands. Cost (lower is better): 2 per metre that her distance to the stain differs from
    /// the mop reach (+1 beyond the on-stain band), +0.5 for only a tight fit, +0.6 if the mop path is blocked,
    /// up to +0.3 for walking round to the far side. Spots where her body doesn't fit are only used when
    /// nothing fits anywhere.
    /// </summary>
    private static bool FindStandPoint(Vector3 stain, Vector3 from, out Vector3 stand, out string how, out bool warn)
    {
        stand = default;
        warn = false;
        float reach = Mathf.Max(0.2f, s_offset.magnitude);
        if (!NavMesh.SamplePosition(stain, out var stainHit, 1.0f, NavMesh.AllAreas))
        {
            how = "the stain is not near the NavMesh";
            return false;
        }
        Vector3 stainNav = stainHit.position;
        Vector3 baseDir = Flat(from - stain);
        baseDir = baseDir.sqrMagnitude > 1e-4f ? baseDir.normalized : Vector3.forward;

        // Rings, nearest to the reach first: reach, reach -+0.1 ... -+0.5, then further out.
        var rings = new List<(float dist, float baseCost)>();
        void AddRing(float delta)
        {
            float d = reach + delta;
            if (d < MinStandDist || d > MaxStandDist) return;
            float c = 2f * Mathf.Abs(delta) + (Mathf.Abs(delta) > OnStainBand + 1e-3f ? 1f : 0f);
            rings.Add((d, c));
        }
        AddRing(0f);
        for (int i = 1; i <= 5; i++) { AddRing(-0.1f * i); AddRing(0.1f * i); }
        for (float delta = 0.7f; reach + delta <= MaxStandDist + 1e-3f; delta += 0.2f) AddRing(delta);

        float bestCost = float.MaxValue;
        string bestHow = null;
        float bestOff = 0f;
        bool haveNoFit = false;
        Vector3 noFit = default;
        float noFitCost = float.MaxValue;
        int walkable = 0;

        foreach (var (dist, ringCost) in rings)
        {
            if (ringCost >= bestCost) break;   // rings are in increasing base cost: nothing further can win
            for (int k = 0; k < Directions; k++)
            {
                // Her side of the stain first, then alternately further round both ways.
                int step = (k + 1) / 2;
                float angle = (k % 2 == 1 ? 1f : -1f) * step * (360f / Directions);
                // The last k gives +180; -180 never comes up.
                float angleCost = Mathf.Abs(angle) / 180f * 0.3f;
                if (ringCost + angleCost >= bestCost && ringCost + angleCost >= noFitCost) continue;

                Vector3 dir = Quaternion.Euler(0f, angle, 0f) * baseDir;
                Vector3 want = new(stain.x + dir.x * dist, stainNav.y, stain.z + dir.z * dist);
                if (!NavMesh.SamplePosition(want, out var hit, 0.3f, NavMesh.AllAreas)) continue;
                Vector3 cand = hit.position;
                if (Flat(cand - want).magnitude > 0.1f) continue;    // spot itself not walkable
                if (Blocked(stainNav, cand, stain)) continue;         // a wall or shelf between her and the stain
                walkable++;

                bool room = Fits(cand, RoomRadius);
                bool tight = room || Fits(cand, TightRadius);
                bool mopClear = MopPathClear(cand, stain, reach);
                float cost = ringCost + angleCost + (room ? 0f : 0.5f) + (mopClear ? 0f : 0.6f);
                if (!tight)
                {
                    if (cost < noFitCost) { noFitCost = cost; noFit = cand; haveNoFit = true; }
                    continue;
                }
                if (cost < bestCost)
                {
                    bestCost = cost;
                    stand = cand;
                    float off = Mathf.Abs(dist - reach);
                    bestOff = off;
                    bestHow = (off < 0.05f ? "mop on the stain centre" :
                               off <= OnStainBand + 1e-3f ? $"mop about {off:F1} m off the centre, on the stain" :
                               $"no fitting spot nearer, mop about {off:F1} m short of the centre") +
                              (room ? "" : ", tight fit") + (mopClear ? "" : ", mop path blocked") +
                              $", angle {angle:F0}";
                }
            }
        }
        // A far fallback spot puts the mop 0.7 m or more short of the centre, off the stain. Standing on or right
        // next to the stain keeps the mop on it (about 0.4-0.6 m from the centre), so try that first.
        bool farBest = bestHow != null && bestOff > OnStainBand + 1e-3f;
        if (bestHow != null && !farBest)
        {
            how = bestHow;
            return true;
        }
        // Nothing on the rings fits (or only far away): try the stain itself and spots right next to it before
        // accepting an overlap.
        if (FindNearCentre(stain, stainNav, reach, out var near, out bool nearRoom))
        {
            stand = near;
            how = (farBest ? "no fitting spot within mop reach, and standing on or right next to the stain keeps the mop on it"
                           : "her body fits only on or right next to the stain, so the mop can't reach the centre") +
                  (nearRoom ? "" : ", tight fit");
            return true;
        }
        if (bestHow != null)
        {
            how = bestHow;
            return true;
        }
        if (haveNoFit)
        {
            stand = noFit;
            warn = true;
            how = "her body fits nowhere around the stain (not even on it); using the best walkable spot, she may overlap an obstacle";
            return true;
        }
        how = walkable == 0 ? "no spot around the stain is on the NavMesh" : "every spot around the stain is cut off from it";
        return false;
    }

    /// <summary>The stain's NavMesh point, then 8 directions at 0.1 and 0.2 m; a roomy fit beats a tight one, and
    /// a clear mop path beats a blocked one.</summary>
    private static bool FindNearCentre(Vector3 stain, Vector3 stainNav, float reach, out Vector3 spot, out bool room)
    {
        spot = default;
        room = false;
        bool found = false;
        float bestCost = float.MaxValue;
        for (int r = 0; r <= 2; r++)
        {
            for (int k = 0; k < (r == 0 ? 1 : 8); k++)
            {
                Vector3 want = r == 0 ? stainNav
                    : stainNav + Quaternion.Euler(0f, k * 45f, 0f) * Vector3.forward * (0.1f * r);
                Vector3 cand = stainNav;
                if (r > 0)
                {
                    if (!NavMesh.SamplePosition(want, out var hit, 0.2f, NavMesh.AllAreas)) continue;
                    cand = hit.position;
                    if (Flat(cand - want).magnitude > 0.1f || Blocked(stainNav, cand, stain)) continue;
                }
                bool candRoom = Fits(cand, RoomRadius);
                if (!candRoom && !Fits(cand, TightRadius)) continue;
                // On the stain itself she isn't turned (no direction to aim), so the mop path is unknown: a small
                // penalty makes a spot beside it with a clear, aimed mop path win.
                float cost = (candRoom ? 0f : 0.5f) + (MopPathClear(cand, stain, reach) ? 0f : 0.6f) + (r == 0 ? 0.05f : 0f);
                if (cost < bestCost)   // strict: earlier (nearer the centre) spots win ties
                {
                    bestCost = cost;
                    spot = cand;
                    room = candRoom;
                    found = true;
                }
            }
        }
        return found;
    }

    private static bool Fits(Vector3 feet, float radius) =>
        !Physics.CheckCapsule(feet + Vector3.up * (BodyBottom + radius), feet + Vector3.up * (BodyTop - radius),
                              radius, BodyMask, QueryTriggerInteraction.Ignore);

    /// <summary>Thin capsule from her hands to where the mop head lands when she faces the stain.</summary>
    private static bool MopPathClear(Vector3 feet, Vector3 stain, float reach)
    {
        Vector3 dir = Flat(stain - feet);
        if (dir.sqrMagnitude < 1e-4f) return true;
        dir.Normalize();
        Vector3 hands = feet + dir * 0.3f + Vector3.up * 0.95f;
        Vector3 head = feet + dir * reach + Vector3.up * 0.1f;
        return !Physics.CheckCapsule(hands, head, 0.05f, BodyMask, QueryTriggerInteraction.Ignore);
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
        if (Physics.Linecast(a, b, BodyMask, QueryTriggerInteraction.Ignore)) return true;
        // A clear line doesn't prove she can walk there (another NavMesh island or level), and she has no arrival
        // timeout, so also require a complete path.
        try
        {
            var path = new NavMeshPath();
            return !NavMesh.CalculatePath(stainNav, cand, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete;
        }
        catch { return true; }
    }

    // ---------------------------------------------------------------- facing + measuring (DirtCleaningState)

    internal static void OnDirtCleaningEnter(DirtCleaningState state)
    {
        if (!RunsAi) return;
        var j = state._janitor;
        if (j == null) return;
        bool hadPlan = s_plans.TryGetValue(j.Pointer, out var plan);
        s_plans.Remove(j.Pointer);
        if (!AlignToStain.Value && !Plugin.VerboseLogging.Value) return;
        var target = j.TargetObject;
        if (target == null) return;

        var t = new Track
        {
            Janitor = j,
            Stain = target.transform.position,
            Start = Time.unscaledTime,
        };
        Vector3 feet = j.transform.position;
        t.StandDist = Flat(t.Stain - feet).magnitude;
        // The plan is only used if it was made for this stain and she actually got there (she stops within
        // stoppingDistance of it); otherwise she is aimed from where she stands.
        if (AlignToStain.Value && hadPlan && Flat(plan.Stain - t.Stain).magnitude < 0.05f &&
            Flat(plan.Stand - feet).magnitude < 0.3f)
        {
            t.HasStand = true;
            t.Stand = plan.Stand;
        }
        if (t.HasStand && Plugin.VerboseLogging.Value && !Fits(t.Stand, TightRadius))
            Log($"Janitor {j.JanitorID}: her planned spot {Fmt(t.Stand)} is no longer free (something was placed there after she set off).");
        Vector3 aimFrom = t.HasStand ? t.Stand : feet;
        Vector3 v = Flat(t.Stain - aimFrom);
        float reach = s_offset.magnitude;
        string turn = "not turned";
        // Without a plan, only turn when she stands about one mop-reach away; standing on the stain, no facing helps.
        if (AlignToStain.Value && v.magnitude > 0.05f && (t.HasStand || (v.magnitude >= reach * 0.5f && v.magnitude <= reach * 2f)))
        {
            float yawToStain = Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg;
            float mopAngle = Mathf.Atan2(s_offset.x, s_offset.y) * Mathf.Rad2Deg;
            t.TargetYaw = yawToStain - mopAngle;
            t.Turning = true;
            turn = $"turning {Mathf.Abs(Mathf.DeltaAngle(j.transform.eulerAngles.y, t.TargetYaw)):F0} deg";
        }
        s_tracks[j.Pointer] = t;
        Log($"Janitor {j.JanitorID} starts cleaning a stain {t.StandDist:F2} m from her feet " +
            $"({turn}{(t.HasStand ? $", sliding {Flat(t.Stand - feet).magnitude:F2} m onto her spot" : ", no planned spot")}).");
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
        if (!AlignToStain.Value) { t.Turning = false; t.HasStand = false; }   // switched off mid-way
        bool busy = false;
        if (t.HasStand && !s_slideBroken)
        {
            try
            {
                var agent = j.m_Agent;
                if (agent != null && agent.enabled && agent.isOnNavMesh)
                {
                    Vector3 cur = agent.nextPosition;
                    Vector3 delta = Flat(t.Stand - cur);
                    if (delta.magnitude > 0.01f)
                    {
                        Vector3 move = Vector3.ClampMagnitude(delta, SlideSpeed * Time.deltaTime);
                        agent.nextPosition = cur + move;
                        busy = true;
                    }
                    else t.HasStand = false;
                }
                else t.HasStand = false;
            }
            catch (Exception e)
            {
                s_slideBroken = true;
                t.HasStand = false;
                Plugin.Logger.LogWarning($"[JanitorAlign] Sliding her onto her spot is unavailable: {e.Message}");
            }
        }
        if (t.Turning)
        {
            var goal = Quaternion.Euler(0f, t.TargetYaw, 0f);
            var cur = Quaternion.Euler(0f, tr.eulerAngles.y, 0f);
            var next = Quaternion.RotateTowards(cur, goal, TurnSpeedDeg * Time.deltaTime);
            tr.rotation = next;
            if (Quaternion.Angle(next, goal) < 0.5f) t.Turning = false;
            busy = true;
        }
        if (busy) { t.Start = now; return false; }   // measure only once she has settled
        if (now - t.Start < MeasureDelay) return false;
        if (!TryHeadPoint(t, out var head)) return false;
        Vector3 local = tr.InverseTransformDirection(Flat(head - tr.position));
        t.OffsetSum += new Vector2(local.x, local.z);
        t.DistSum += Flat(head - t.Stain).magnitude;
        t.FeetSum += Flat(tr.position - t.Stain).magnitude;
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
        float feet = t.FeetSum / t.Samples;
        float usedReach = s_offset.magnitude;
        string adopt = "";
        bool plausible = avg.y >= 0.2f && avg.y <= 1.1f && Mathf.Abs(avg.x) <= 0.6f;
        if (plausible && (avg - s_offset).magnitude > 0.03f)
        {
            adopt = $"; next stains use this mop offset instead of right={s_offset.x:F2} forward={s_offset.y:F2}";
            s_offset = avg;
        }
        else if (!plausible) adopt = "; implausible, not used";
        Log($"Janitor {SafeId(t.Janitor)} finished a stain: her mop head was on average {dist:F2} m from the stain centre " +
            $"(her feet {feet:F2} m from it, mop reach {usedReach:F2} m; mop at right={avg.x:F2} forward={avg.y:F2} of her{adopt}).");
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
