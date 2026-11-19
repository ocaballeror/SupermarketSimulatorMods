/*
 * Procedural crouch pose for every REMOTE player model. The local player's own model is skipped: its renderers are
 * always disabled (PlayerSkin.Toggle(!isLocal)), so posing it would be invisible work; the local player only
 * sees the camera drop (Crouch.cs).
 *
 * Player models are driven by a humanoid Animator plus Animation Rigging rigs for look-at and hand IK. Each
 * selectable character (skin) has its own; the one shown is PlayerInstance.SkinController.ActiveSkin (see
 * FindAnimator). There is no crouch clip, so after the Animator has written the pose (we run in
 * LateUpdate, which Unity runs after the animation update) we bend it:
 *   1. remember the animated feet (world position + rotation) and head rotation,
 *   2. lower the hips so the head ends up at the crouched eye height (the same CameraHeight fraction the camera
 *      and collider use, limited to MaxBodyDepth * leg length because the legs can't fold further), and shift them
 *      back a little, for balance,
 *   3. lean the spine forward by LeanAngle, and give the head its old world rotation back so the gaze stays,
 *   4. solve each leg (upper leg / lower leg / foot) with an analytic two-bone IK so the feet land back on their
 *      animated positions, knees bending towards the character's forward, and restore the foot rotations.
 * The walk cycle keeps playing, so crouch-walking still moves the feet.
 *
 * Held items: the carried box hangs under the player's "Camera Pivot" (FirstPersonController.CinemachineCameraTarget
 * -> "Object Holder" child), and the hands are IK'd onto it before our LateUpdate. Only rotation is networked for
 * the pivot (PhotonTransformView SyncPosition=0), so its position is ours to move on a remote copy: we shift it by
 * exactly the displacement of the model's head (hips drop + lean), which keeps the box with the hands and body.
 * Restored when the player stands, on errors, when the mod is switched off.
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
 * so we don't pose that model, to keep the two systems from fighting. (A seated remote player who published
 * crouch=true would only be posed if the leg IK weights are 0; the local side forces standing while driving.)
 *
 * Crouch amount per remote player: the "flamboyanthopper.crouch" Photon player property of the PlayerInstance's
 * PhotonView owner, smoothed over TransitionTime.
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
        public bool WarnedNoAnimator;
        public PlayerIKHandler IkHandler;
        public bool SkippedForIk;
        public float T;
        public bool LastRemote;
        public bool Applied;
        public Vector3 LastHipsLocal;
        public Quaternion LastThighLocal;
        public bool Seen;
        public float NextFind;
        public Transform Pivot;
        public float NextPivotFind;
        public bool PivotWritten;
        public Vector3 PivotLastPos;
        public Vector3 PivotLastOffset;
        public bool LoggedDims;
    }

    private static readonly Dictionary<IntPtr, State> States = new();
    private static readonly List<IntPtr> Stale = new();
    private static float _nextAnimatorRefresh;

    internal static void Reset()
    {
        foreach (var s in States.Values) RestorePivot(s);
        States.Clear();
    }

    internal static void LateUpdate()
    {
        if (!NoktaSingleton<PlayerManager>.HasInstance) { Reset(); return; }
        var pm = NoktaSingleton<PlayerManager>.Instance;
        var local = pm.LocalPlayer;
        IntPtr localPtr = local != null ? local.Pointer : IntPtr.Zero;

        foreach (var s in States.Values) s.Seen = false;

        bool refresh = Time.unscaledTime >= _nextAnimatorRefresh;
        if (refresh) _nextAnimatorRefresh = Time.unscaledTime + 2f;

        var list = pm.PlayerInstances;
        if (list != null)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var inst = list[i];
                if (inst == null || inst.Pointer == localPtr) continue; // own model is never visible
                Tick(inst, refresh);
            }
        }

        Stale.Clear();
        foreach (var kv in States) if (!kv.Value.Seen) Stale.Add(kv.Key);
        foreach (var k in Stale)
        {
            RestorePivot(States[k]); // player left or was replaced; harmless if the object is already destroyed
            States.Remove(k);
        }
    }

    private static void Tick(PlayerInstance inst, bool refresh)
    {
        if (!States.TryGetValue(inst.Pointer, out var s))
        {
            s = new State { Instance = inst };
            States[inst.Pointer] = s;
        }
        s.Seen = true;

        {
            bool remote = false;
            if (Plugin.AnimateOthers)
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
            s.T = Mathf.MoveTowards(s.T, target, Time.deltaTime / Mathf.Max(0.01f, Plugin.TransitionTime));
        }

        if (s.T <= 0.001f) { s.Applied = false; RestorePivot(s); return; }

        if (refresh || ((s.Animator == null || !s.Animator.isActiveAndEnabled) && Time.unscaledTime >= s.NextFind))
        {
            s.NextFind = Time.unscaledTime + 0.5f;
            var a = FindAnimator(inst, out var ikHandler, out string from);
            if (a != null && (s.Animator == null || a.Pointer != s.Animator.Pointer))
            {
                s.Animator = a;
                s.Applied = false;
                s.IkHandler = ikHandler;
                Plugin.Info($"Model for player (remote): animator '{a.name}' ({from}) human={a.isHuman} " +
                            $"onRoot={a.transform == inst.transform} {DescribeLegIk(s.IkHandler)}");
            }
        }
        if (s.Animator == null || !s.Animator.isActiveAndEnabled)
        {
            if (!s.WarnedNoAnimator)
            {
                s.WarnedNoAnimator = true;
                Plugin.Logger.LogWarning("[Crouch] No active character model found for a remote player; can't pose it");
            }
            s.Applied = false;
            RestorePivot(s);
            return;
        }
        s.WarnedNoAnimator = false;
        if (!s.Animator.isHuman)
        {
            if (!s.WarnedNotHuman) { Plugin.Logger.LogWarning("[Crouch] Player model isn't a humanoid rig; can't pose it"); s.WarnedNotHuman = true; }
            RestorePivot(s);
            return;
        }

        bool ik = LegIkActive(s.IkHandler);
        if (ik != s.SkippedForIk)
        {
            s.SkippedForIk = ik;
            Plugin.Info(ik ? $"Leg IK is active on a remote model; not posing it ({DescribeLegIk(s.IkHandler)})"
                           : "Leg IK no longer active; posing resumed");
        }
        if (ik) { s.Applied = false; RestorePivot(s); return; }

        try { Apply(s); }
        catch (Exception e)
        {
            Plugin.WarnThrottled("pose", $"Posing a player model failed: {e.Message}");
            s.Applied = false;
            RestorePivot(s);
        }
    }

    /// <summary>
    /// The Animator of the character the player is actually shown as. Every skin under the player's "Skins"
    /// object (Man_01, Man_02, Woman_01, Woman_02 and the "Man_01_Rig_Test Variant") has its own Animator,
    /// PlayerAnimation, rigs and IK handler; the chosen one is shown (PlayerSkinController.SetSkinIndex activates it
    /// and hides the others' renderers; it may not deactivate them, so use ActiveSkin, not "the active one").
    /// The first PlayerAnimation in the hierarchy belongs to the rig-test variant, which is normally
    /// inactive, so posing it did nothing (in-game 2026-11-15).
    /// </summary>
    private static Animator FindAnimator(PlayerInstance inst, out PlayerIKHandler ik, out string from)
    {
        ik = null;
        from = "?";
        try
        {
            var sc = inst.SkinController;
            var skin = sc != null ? sc.ActiveSkin : null;
            if (skin != null && skin.gameObject.activeInHierarchy)
            {
                var a = skin.m_Animator;
                if (a == null) a = skin.GetComponentInChildren<Animator>(false);
                if (a != null && a.isActiveAndEnabled)
                {
                    ik = skin.GetComponentInChildren<PlayerIKHandler>(true) ?? skin.GetComponentInParent<PlayerIKHandler>(true);
                    from = $"active skin '{skin.name}'";
                    return a;
                }
            }
        }
        catch (Exception) { }

        // Fallback: the first active humanoid Animator under the player, preferring its own PlayerAnimation's.
        var anims = inst.GetComponentsInChildren<PlayerAnimation>(false);
        for (int i = 0; anims != null && i < anims.Length; i++)
        {
            var a = anims[i] != null ? anims[i].m_Animator : null;
            if (a != null && a.isActiveAndEnabled && a.isHuman)
            {
                ik = anims[i].GetComponentInParent<PlayerIKHandler>(true) ?? inst.GetComponentInChildren<PlayerIKHandler>(false);
                from = $"active PlayerAnimation on '{anims[i].name}'";
                return a;
            }
        }
        var all = inst.GetComponentsInChildren<Animator>(false);
        for (int i = 0; all != null && i < all.Length; i++)
        {
            var a = all[i];
            if (a != null && a.isActiveAndEnabled && a.isHuman)
            {
                ik = inst.GetComponentInChildren<PlayerIKHandler>(false);
                from = "first active humanoid Animator";
                return a;
            }
        }
        return null;
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
            RestorePivot(s);
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

        // How far the hips must drop so the head ends up at the crouched eye height: the same fraction of the
        // standing head height that the camera and the collider drop by, minus what the forward lean already lowers
        // the head, and limited by how far the legs can fold.
        Vector3 headBefore = head != null ? head.position : hips.position + Vector3.up * (legLen * 0.9f);
        float headHeight = Mathf.Clamp(headBefore.y - root.position.y, legLen, legLen * 3f);
        float wantDrop = headHeight * (1f - Plugin.CameraHeight);
        float spineLen = head != null && spine != null ? (head.position - spine.position).magnitude : legLen * 0.7f;
        float leanDrop = spineLen * (1f - Mathf.Cos(Plugin.LeanAngle * Mathf.Deg2Rad));
        float fullDrop = Mathf.Clamp(wantDrop - leanDrop, 0f, legLen * Plugin.MaxBodyDepth);
        float drop = fullDrop * t;
        if (!s.LoggedDims)
        {
            s.LoggedDims = true;
            Plugin.Info($"Pose dims: legLen={legLen:F2} headHeight={headHeight:F2} wantHeadDrop={wantDrop:F2} hipsDrop={fullDrop:F2}");
        }

        Vector3 lFootPos = lFoot.position, rFootPos = rFoot.position;
        Quaternion lFootRot = lFoot.rotation, rFootRot = rFoot.rotation;
        Quaternion headRot = head != null ? head.rotation : Quaternion.identity;

        hips.position = hips.position - Vector3.up * drop - fwd * (drop * 0.25f);

        float lean = Plugin.LeanAngle * t;
        if (spine != null && lean > 0.01f)
        {
            spine.rotation = Quaternion.AngleAxis(lean, right) * spine.rotation;
            if (head != null) head.rotation = headRot;
        }

        if (head != null) MovePivot(s, head.position - headBefore);

        SolveLeg(lUp, lLo, lFoot, lFootPos, fwd);
        SolveLeg(rUp, rLo, rFoot, rFootPos, fwd);
        lFoot.rotation = lFootRot;
        rFoot.rotation = rFootRot;

        s.Applied = true;
        s.LastHipsLocal = hips.localPosition;
        s.LastThighLocal = lUp.localRotation;
    }

    /// <summary>
    /// Moves the remote player's Camera Pivot (parent of the Object Holder, so the carried box) by a world-space
    /// displacement. Its position isn't networked, so nothing overwrites it; the same re-base pattern as the local
    /// camera keeps it correct if something ever does.
    /// </summary>
    private static void MovePivot(State s, Vector3 worldDelta)
    {
        if (s.Pivot == null && Time.unscaledTime >= s.NextPivotFind)
        {
            s.NextPivotFind = Time.unscaledTime + 2f;
            s.Pivot = FindPivot(s.Instance);
        }
        var p = s.Pivot;
        if (p == null) return;
        Vector3 local = p.parent != null ? p.parent.InverseTransformVector(worldDelta) : worldDelta;
        var cur = p.localPosition;
        var basePos = s.PivotWritten && cur == s.PivotLastPos ? cur - s.PivotLastOffset : cur;
        var pos = basePos + local;
        p.localPosition = pos;
        s.PivotLastPos = pos;
        s.PivotLastOffset = local;
        s.PivotWritten = true;
    }

    private static void RestorePivot(State s)
    {
        if (!s.PivotWritten) return;
        s.PivotWritten = false;
        try
        {
            var p = s.Pivot;
            if (p == null) return;
            var cur = p.localPosition;
            if (cur == s.PivotLastPos) p.localPosition = cur - s.PivotLastOffset;
        }
        catch (Exception) { /* object already destroyed */ }
    }

    private static Transform FindPivot(PlayerInstance inst)
    {
        Transform t = null;
        string how = "FirstPersonController.CinemachineCameraTarget";
        try
        {
            var fpc = inst.FirstPersonController;
            var go = fpc != null ? fpc.CinemachineCameraTarget : null;
            if (go != null) t = go.transform;
        }
        catch (Exception) { }
        if (t == null)
        {
            how = "name search";
            var all = inst.GetComponentsInChildren<Transform>(true);
            for (int i = 0; all != null && i < all.Length; i++)
                if (all[i] != null && all[i].name == "Camera Pivot") { t = all[i]; break; }
        }
        if (t != null && Plugin.VerboseLogging.Value)
        {
            bool holderBelow = false;
            try
            {
                var holder = inst.PlayerObjectHolder != null ? inst.PlayerObjectHolder.m_ObjectHolder : null;
                holderBelow = holder != null && holder.IsChildOf(t);
            }
            catch (Exception) { }
            Plugin.Info($"Remote camera pivot '{t.name}' via {how}; Object Holder is below it: {holderBelow}");
        }
        else if (t == null) Plugin.Info("Remote camera pivot not found; held items won't follow the crouch");
        return t;
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
