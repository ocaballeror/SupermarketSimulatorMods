/*
 * Procedural crouch pose for every player model (local and remote).
 *
 * Player models are driven by PlayerAnimation.m_Animator (a humanoid Animator plus Animation Rigging rigs
 * for look-at and hand IK). There is no crouch clip, so after the Animator has written the pose (we run in
 * LateUpdate, which Unity runs after the animation update) we bend it:
 *   1. remember the animated feet (world position + rotation) and head rotation,
 *   2. lower the hips by BodyDepth * leg length (and shift them back a little, for balance),
 *   3. lean the spine forward by LeanAngle, and give the head its old world rotation back so the gaze stays,
 *   4. solve each leg (upper leg / lower leg / foot) with an analytic two-bone IK so the feet land back on their
 *      animated positions, knees bending towards the character's forward, and restore the foot rotations.
 * The walk cycle keeps playing, so crouch-walking still moves the feet.
 *
 * Accumulation guard: the bend is applied on top of whatever pose the bones have. If the Animator didn't
 * write this frame (culled or disabled), the bones still hold last frame's bent pose and bending again would
 * accumulate. We remember the hips localPosition and left upper-leg localRotation we wrote; if they are
 * unchanged at the next LateUpdate, the Animator didn't run and we leave the pose alone.
 *
 * Leg IK: PlayerIKHandler has two-bone IK constraints for the legs (m_LeftLegRig / m_RightLegRig), and
 * NetworkVehicle.SeatPlayer/UnseatPlayer switch the "Vehicle" rig layer on and off with SetWeight. Nothing in the
 * game calls SetLeftLeg/SetRightLeg, so outside vehicles the leg constraints have no target and weight 0. While
 * either leg constraint (and its Rig) has a weight above 0.01 (seated in a vehicle) the legs belong to that IK,
 * so we don't pose that model, to keep the two systems from fighting. Seated players are not crouching anyway.
 *
 * Crouch amount per player: the local player uses Crouch.LocalT; remote players follow the "oscar.crouch"
 * Photon player property of the PlayerInstance's PhotonView owner, smoothed over TransitionSeconds.
 */
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations.Rigging;

namespace PlayerCrouch;

internal static class CrouchPose
{
    private sealed class State
    {
        public PlayerInstance Instance;
        public Animator Animator;
        public bool WarnedNotHuman;
        public PlayerIKHandler IkHandler;
        public bool SkippedForIk;
        public float T;
        public bool LastRemote;
        public bool Applied;
        public Vector3 LastHipsLocal;
        public Quaternion LastThighLocal;
        public bool Seen;
    }

    private static readonly Dictionary<IntPtr, State> States = new();
    private static readonly List<IntPtr> Stale = new();
    private static float _nextAnimatorRefresh;

    internal static void Reset()
    {
        States.Clear();
    }

    internal static void LateUpdate()
    {
        if (!NoktaSingleton<PlayerManager>.HasInstance) { States.Clear(); return; }
        var pm = NoktaSingleton<PlayerManager>.Instance;
        var local = pm.LocalPlayer;
        IntPtr localPtr = local != null ? local.Pointer : IntPtr.Zero;

        foreach (var s in States.Values) s.Seen = false;

        bool refresh = Time.unscaledTime >= _nextAnimatorRefresh;
        if (refresh) _nextAnimatorRefresh = Time.unscaledTime + 2f;

        var list = pm.PlayerInstances;
        bool localDone = false;
        if (list != null)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var inst = list[i];
                if (inst == null) continue;
                bool isLocal = inst.Pointer == localPtr;
                localDone |= isLocal;
                Tick(inst, isLocal, refresh);
            }
        }
        if (!localDone && local != null) Tick(local, true, refresh);

        Stale.Clear();
        foreach (var kv in States) if (!kv.Value.Seen) Stale.Add(kv.Key);
        foreach (var k in Stale) States.Remove(k);
    }

    private static void Tick(PlayerInstance inst, bool isLocal, bool refresh)
    {
        if (!States.TryGetValue(inst.Pointer, out var s))
        {
            s = new State { Instance = inst };
            States[inst.Pointer] = s;
        }
        s.Seen = true;

        if (isLocal) s.T = Crouch.LocalT;
        else
        {
            bool remote = false;
            if (Plugin.AnimateOthers.Value)
            {
                try
                {
                    var owner = inst.m_PhotonView != null ? inst.m_PhotonView.Owner : null;
                    remote = owner != null && Crouch.ReadProp(owner) == true;
                    if (remote != s.LastRemote)
                        Plugin.Info($"Player '{(owner != null ? owner.NickName : "?")}' {(remote ? "crouched" : "stood up")}");
                }
                catch (Exception) { remote = false; }
            }
            s.LastRemote = remote;
            float target = remote ? 1f : 0f;
            s.T = Mathf.MoveTowards(s.T, target, Time.deltaTime / Mathf.Max(0.01f, Plugin.TransitionTime.Value));
        }

        if (s.T <= 0.001f) { s.Applied = false; return; }

        if (s.Animator == null || refresh)
        {
            var anim = inst.GetComponentInChildren<PlayerAnimation>(true);
            var a = anim != null ? anim.m_Animator : null;
            if (a != null && (s.Animator == null || a.Pointer != s.Animator.Pointer))
            {
                s.Animator = a;
                s.Applied = false;
                s.IkHandler = inst.GetComponentInChildren<PlayerIKHandler>(true);
                Plugin.Info($"Model for player {(isLocal ? "(local)" : "(remote)")}: animator '{a.name}' human={a.isHuman} " +
                            $"onRoot={a.transform == inst.transform} {DescribeLegIk(s.IkHandler)}");
            }
        }
        if (s.Animator == null || !s.Animator.isActiveAndEnabled) { s.Applied = false; return; }
        if (!s.Animator.isHuman)
        {
            if (!s.WarnedNotHuman) { Plugin.Logger.LogWarning("[Crouch] Player model isn't a humanoid rig; can't pose it"); s.WarnedNotHuman = true; }
            return;
        }

        bool ik = LegIkActive(s.IkHandler);
        if (ik != s.SkippedForIk)
        {
            s.SkippedForIk = ik;
            Plugin.Info(ik ? $"Leg IK is active on a {(isLocal ? "local" : "remote")} model; not posing it ({DescribeLegIk(s.IkHandler)})"
                           : "Leg IK no longer active; posing resumed");
        }
        if (ik) { s.Applied = false; return; }

        try { Apply(s); }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[Crouch] Posing a player model failed: {e.Message}");
            s.Applied = false;
        }
    }

    private static bool LegIkActive(PlayerIKHandler h)
    {
        if (h == null) return false;
        try { return Driven(h.m_LeftLegRig) || Driven(h.m_RightLegRig); }
        catch (Exception) { return false; }
    }

    private static bool Driven(TwoBoneIKConstraint c)
    {
        if (c == null || c.weight <= 0.01f) return false;
        var rig = c.GetComponentInParent<Rig>(true);
        return rig == null || rig.weight > 0.01f;
    }

    private static string DescribeLegIk(PlayerIKHandler h)
    {
        if (h == null) return "legIK=no handler";
        try
        {
            string One(TwoBoneIKConstraint c)
            {
                if (c == null) return "-";
                var rig = c.GetComponentInParent<Rig>(true);
                return $"{c.weight:F2}/{(rig != null ? rig.weight.ToString("F2") : "?")}";
            }
            return $"legIK(L={One(h.m_LeftLegRig)} R={One(h.m_RightLegRig)})";
        }
        catch (Exception) { return "legIK=?"; }
    }

    private static void Apply(State s)
    {
        var an = s.Animator;
        var hips = an.GetBoneTransform(HumanBodyBones.Hips);
        var lUp = an.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
        var lLo = an.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
        var lFoot = an.GetBoneTransform(HumanBodyBones.LeftFoot);
        var rUp = an.GetBoneTransform(HumanBodyBones.RightUpperLeg);
        var rLo = an.GetBoneTransform(HumanBodyBones.RightLowerLeg);
        var rFoot = an.GetBoneTransform(HumanBodyBones.RightFoot);
        if (hips == null || lUp == null || lLo == null || lFoot == null || rUp == null || rLo == null || rFoot == null)
        {
            s.Applied = false;
            return;
        }

        // Animator didn't overwrite our pose since last frame: it's still bent, don't bend again.
        if (s.Applied && hips.localPosition == s.LastHipsLocal && lUp.localRotation == s.LastThighLocal) return;

        var spine = an.GetBoneTransform(HumanBodyBones.Spine);
        var head = an.GetBoneTransform(HumanBodyBones.Head);
        var root = an.transform;
        Vector3 fwd = root.forward, right = root.right;
        float t = s.T;

        float legLen = (lUp.position - lLo.position).magnitude + (lLo.position - lFoot.position).magnitude;
        float drop = legLen * Plugin.BodyDepth.Value * t;

        Vector3 lFootPos = lFoot.position, rFootPos = rFoot.position;
        Quaternion lFootRot = lFoot.rotation, rFootRot = rFoot.rotation;
        Quaternion headRot = head != null ? head.rotation : Quaternion.identity;

        hips.position = hips.position - Vector3.up * drop - fwd * (drop * 0.25f);

        float lean = Plugin.LeanAngle.Value * t;
        if (spine != null && lean > 0.01f)
        {
            spine.rotation = Quaternion.AngleAxis(lean, right) * spine.rotation;
            if (head != null) head.rotation = headRot;
        }

        SolveLeg(lUp, lLo, lFoot, lFootPos, fwd);
        SolveLeg(rUp, rLo, rFoot, rFootPos, fwd);
        lFoot.rotation = lFootRot;
        rFoot.rotation = rFootRot;

        s.Applied = true;
        s.LastHipsLocal = hips.localPosition;
        s.LastThighLocal = lUp.localRotation;
    }

    /// <summary>Analytic two-bone IK: rotate upper and lower so the end bone reaches target, bending towards pole.</summary>
    private static void SolveLeg(Transform upper, Transform lower, Transform end, Vector3 target, Vector3 pole)
    {
        Vector3 a = upper.position, b = lower.position, c = end.position;
        float lab = (b - a).magnitude, lbc = (c - b).magnitude;
        if (lab < 1e-4f || lbc < 1e-4f) return;

        Vector3 toT = target - a;
        Vector3 dir = toT.sqrMagnitude > 1e-8f ? toT.normalized : (c - a).normalized;
        float d = Mathf.Clamp(toT.magnitude, Mathf.Abs(lab - lbc) + 1e-3f, lab + lbc - 1e-3f);

        float cosA = Mathf.Clamp((lab * lab + d * d - lbc * lbc) / (2f * lab * d), -1f, 1f);
        float sinA = Mathf.Sqrt(Mathf.Max(0f, 1f - cosA * cosA));

        Vector3 bend = pole - dir * Vector3.Dot(pole, dir);
        if (bend.sqrMagnitude < 1e-6f)
        {
            Vector3 knee = b - a;
            bend = knee - dir * Vector3.Dot(knee, dir);
            if (bend.sqrMagnitude < 1e-6f) return;
        }
        bend.Normalize();

        Vector3 kneeTarget = a + dir * (lab * cosA) + bend * (lab * sinA);
        upper.rotation = Quaternion.FromToRotation(b - a, kneeTarget - a) * upper.rotation;

        Vector3 b2 = lower.position, c2 = end.position;
        lower.rotation = Quaternion.FromToRotation(c2 - b2, a + dir * d - b2) * lower.rotation;
    }
}
