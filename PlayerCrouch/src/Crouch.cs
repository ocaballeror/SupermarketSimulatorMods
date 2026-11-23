/*
 * Improvement #3: crouching with Ctrl, with the character visibly crouching.
 *
 * The game has no crouch: no input action, no animator state, nothing in FirstPersonController.
 * Everything here is added on top of the game's own systems:
 *
 * Local player (this file)
 *  - Input: read directly from the Input System (Keyboard.current[key], optional gamepad button).
 *    Ctrl is already bound to the game's SnapPlace action, which only matters while placing furniture
 *    (FurniturePlacer.m_PlacingMode) or an object (PlayerObjectHolder.m_PlacingMode). While placing, and
 *    while FirstPersonController._movementInteraction is false (menus, computer, checkout, pause), the
 *    crouch input is ignored and the current state is kept. On a skateboard
 *    (PlayerInteraction.InSkateboard) or while driving (VehicleInteraction enabled) the player always stands, and the stand-up there does not wait for headroom.
 *  - Camera: the Cinemachine FPS camera follows FirstPersonController.CinemachineCameraTarget. Native code
 *    only writes that transform's localPosition in PushCameraEffect (a DOLocalMove tween back to
 *    defaultCameraPosition); CameraRotation only sets its rotation. We add a vertical offset in LateUpdate and
 *    re-base when somebody else wrote the position since our last write, so the push tween keeps working.
 *  - Speed: FirstPersonController.Move picks MoveSpeed (0x20) or SprintSpeed (0x24) as the target speed.
 *    PlayerPatches swaps both for the crouch speed for the duration of Move and restores them afterwards.
 *  - Jump: FirstPersonController.Jump (called from the input callback) is skipped while crouched.
 *  - Collider: nothing in the game reads or writes CharacterController.height/center (checked: only
 *    pathfinding/other controllers touch them), so we shrink the capsule while crouched, keeping its bottom
 *    where it was. Standing back up waits until an overlap test of the standing capsule finds nothing.
 *    Switching the mod off while crouched under something keeps the shrunk capsule until there is room.
 *
 * Other players (CrouchPose.cs)
 *  - Each player's crouch state is published as the Photon player custom property "flamboyanthopper.crouch" (bool).
 *    The game already uses player properties ("sid", "png"); unmodded clients just store the extra key.
 *    Properties persist in the room, so late joiners see the right state.
 *  - Every modded client poses every remote player's model procedurally (see CrouchPose); the local model is
 *    never visible, so it is not posed.
 */
using System;
using Il2CppInterop.Runtime;
using Photon.Pun;
using UnityEngine;
using UnityEngine.InputSystem;
using Hashtable = ExitGames.Client.Photon.Hashtable;
using PhotonPlayer = Photon.Realtime.Player;

namespace PlayerCrouch;

internal static class Crouch
{
    internal const string PropKey = "flamboyanthopper.crouch";

    /// <summary>What the player asked for (key held / toggled on).</summary>
    internal static bool Want;
    /// <summary>Crouch amount of the local player, 0 = standing, 1 = fully crouched.</summary>
    internal static float LocalT;
    /// <summary>The FirstPersonController the state belongs to (pointer), for the Harmony patches.</summary>
    internal static IntPtr LocalFpcPtr;

    private static FirstPersonController _fpc;
    private static CharacterController _cc;
    private static float _standHeight;
    private static Vector3 _standCenter;
    private static bool _ccCaptured;
    private static bool _ccModified;

    private static Transform _camTarget;
    private static bool _camWritten;
    private static Vector3 _camLastWritten;
    private static float _camLastOffset;

    private static bool? _lastSent;
    private static float _nextVerify;
    private static float _sendRetryAt;
    private static Il2CppSystem.Object _propKeyBoxed;
    private static bool _loggedBlocked;
    private static bool _suppressUntilRelease;
    private static float _nextCamResolve;

    internal static bool IsCrouching => LocalT > 0.001f;

    // ---------------------------------------------------------------- per-frame

    internal static void Update()
    {
        if (!NoktaSingleton<PlayerManager>.HasInstance) { Release(); return; }
        var player = NoktaSingleton<PlayerManager>.Instance.LocalPlayer;
        if (player == null) { Release(); return; }
        var fpc = player.FirstPersonController;
        if (fpc == null) { Release(); return; }

        if (_fpc == null || fpc.Pointer != LocalFpcPtr) Capture(fpc);

        var interaction = player.PlayerInteraction;
        bool skate = interaction != null && interaction.InSkateboard;
        bool placing = (player.FurniturePlacer != null && player.FurniturePlacer.m_PlacingMode)
                       || (player.PlayerObjectHolder != null && player.PlayerObjectHolder.m_PlacingMode);
        bool canMove = fpc._movementInteraction;
        // Driving: the game enables VehicleInteraction (Interaction.Enable is Behaviour.set_enabled; only the active
        // *Interaction component is enabled). Whether _movementInteraction is false then is unverified, so stand up.
        bool driving = false;
        try { var vi = player.VehicleInteraction; driving = vi != null && vi.enabled; } catch (Exception) { }

        ReadInput(out bool held, out bool pressed);

        bool allowed = canMove && !placing;
        if (!held) _suppressUntilRelease = false;
        else if (!allowed) _suppressUntilRelease = true; // key held while blocked (e.g. Ctrl = snap): needs a fresh press

        if (skate || driving) Want = false;
        else if (ToggleModeOn)
        {
            if (allowed && pressed) Want = !Want;
        }
        else if (allowed) Want = held && (Want || !_suppressUntilRelease);
        else Want &= held; // blocked: can't start a crouch, but letting go of the key still stands you up

        float target = Want ? 1f : 0f;
        // On a skateboard or in a vehicle the player is carried by something else whose own colliders would count as
        // "something above us", so there the stand-up never waits for headroom.
        if (!Want && !(skate || driving) && LocalT > 0f && _ccModified && !HasHeadroom(_cc, _standHeight, _standCenter, player.transform))
        {
            target = LocalT; // something above us: stay down until there's room
            if (!_loggedBlocked) { Plugin.Info("No room to stand up; staying crouched"); _loggedBlocked = true; }
        }
        else _loggedBlocked = false;

        float before = LocalT;
        LocalT = Mathf.MoveTowards(LocalT, target, Time.deltaTime / Mathf.Max(0.01f, Plugin.TransitionTime));
        if ((before == 0f) != (LocalT == 0f) || (before < 1f && LocalT == 1f))
            Plugin.Info(LocalT == 0f ? "Standing" : LocalT == 1f ? "Crouched" : "Crouching");

        ApplyCollider();
        SyncNetwork(target >= 0.5f);
    }

    internal static void LateUpdate()
    {
        if (_fpc == null) return;
        if (!_camWritten && Time.unscaledTime >= _nextCamResolve)
        {
            _nextCamResolve = Time.unscaledTime + 2f;
            var t = ResolveCamTarget(_fpc);
            if (t != null && (_camTarget == null || t.Pointer != _camTarget.Pointer))
            {
                _camTarget = t;
                Plugin.Info($"Camera offset target is now '{t.name}'");
            }
        }
        ApplyCamera();
    }

    private static bool ToggleModeOn => Plugin.ToggleMode.Value;

    private static void ReadInput(out bool held, out bool pressed)
    {
        held = pressed = false;
        var kb = Keyboard.current;
        var key = Plugin.CrouchKey.Value;
        if (kb != null && key != Key.None)
        {
            try
            {
                var k = kb[key];
                if (k != null) { held |= k.isPressed; pressed |= k.wasPressedThisFrame; }
            }
            catch (Exception) { /* key not on this keyboard layout, or an unexpected Input System error: ignore this frame */ }
        }

        var pad = Gamepad.current;
        if (pad == null) return;
        var button = Plugin.PadButton.Value switch
        {
            CrouchPadButton.LeftStickPress => pad.leftStickButton,
            CrouchPadButton.RightStickPress => pad.rightStickButton,
            CrouchPadButton.ButtonNorth => pad.buttonNorth,
            CrouchPadButton.ButtonSouth => pad.buttonSouth,
            CrouchPadButton.ButtonEast => pad.buttonEast,
            CrouchPadButton.ButtonWest => pad.buttonWest,
            CrouchPadButton.LeftShoulder => pad.leftShoulder,
            CrouchPadButton.RightShoulder => pad.rightShoulder,
            CrouchPadButton.DpadUp => pad.dpad.up,
            CrouchPadButton.DpadDown => pad.dpad.down,
            CrouchPadButton.DpadLeft => pad.dpad.left,
            CrouchPadButton.DpadRight => pad.dpad.right,
            _ => null,
        };
        if (button != null) { held |= button.isPressed; pressed |= button.wasPressedThisFrame; }
    }

    /// <summary>Jump while crouched: in toggle mode it stands you up, either way no jump.</summary>
    internal static void OnJumpWhileCrouched()
    {
        if (ToggleModeOn) Want = false;
    }

    // ---------------------------------------------------------------- setup / teardown

    private static void Capture(FirstPersonController fpc)
    {
        Release();
        _fpc = fpc;
        LocalFpcPtr = fpc.Pointer;
        _cc = fpc._controller;
        _ccCaptured = false;
        if (_cc != null && _pendCc != null && _pendCc.Pointer == _cc.Pointer)
        {
            // Re-enabled while the shrunk capsule from a blocked switch-off is still pending: hand it back to the live
            // crouch (fully crouched, standing up once there is headroom) instead of restoring it inside the geometry.
            _standHeight = _pendHeight;
            _standCenter = _pendCenter;
            _ccCaptured = _standHeight > 0.1f;
            _ccModified = _ccCaptured;
            LocalT = _ccCaptured ? 1f : 0f;
            _pendCc = null; _pendRoot = null;
        }
        else
        {
            FinishPendingRestore();
            if (_cc != null)
            {
                _standHeight = _cc.height;
                _standCenter = _cc.center;
                _ccCaptured = _standHeight > 0.1f;
            }
        }
        _camTarget = ResolveCamTarget(fpc);
        _nextCamResolve = Time.unscaledTime + 2f;

        if (Plugin.VerboseLogging.Value)
        {
            string ccInfo = _cc != null
                ? $"height={_standHeight:F2} center={_standCenter} radius={_cc.radius:F2} layer={_cc.gameObject.layer}"
                : "none";
            string camInfo = _camTarget != null
                ? $"'{_camTarget.name}' parent='{(_camTarget.parent != null ? _camTarget.parent.name : "-")}' " +
                  $"local={_camTarget.localPosition} default={fpc.defaultCameraPosition}"
                : "none";
            string follow = "?";
            try
            {
                var vcam = fpc.playerInstance != null ? fpc.playerInstance.m_FPSVCamObject : null;
                if (vcam != null) follow = vcam.Follow != null ? vcam.Follow.name : "-";
            }
            catch (Exception) { }
            Plugin.Info($"Local player set up. Controller: {ccInfo}. Camera target: {camInfo}. VCam follows: '{follow}'. " +
                        $"MoveSpeed={fpc.MoveSpeed:F2} SprintSpeed={fpc.SprintSpeed:F2}");
        }
    }

    /// <summary>
    /// The transform that gets the vertical camera offset: normally CinemachineCameraTarget. If the FPS virtual
    /// camera follows a different transform inside the player (unverified in-game), that one is used instead, because
    /// moving the camera target would otherwise not move the camera. The vcam's Follow may be assigned after the
    /// player appears, so this is re-evaluated every 2 s while no offset is applied.
    /// </summary>
    private static Transform ResolveCamTarget(FirstPersonController fpc)
    {
        var go = fpc.CinemachineCameraTarget;
        var own = go != null ? go.transform : null;
        try
        {
            var pi = fpc.playerInstance;
            var vcam = pi != null ? pi.m_FPSVCamObject : null;
            var follow = vcam != null ? vcam.Follow : null;
            if (follow != null && (own == null || follow.Pointer != own.Pointer))
            {
                var root = pi.transform;
                if (follow.Pointer != root.Pointer && follow.IsChildOf(root)) return follow;
            }
        }
        catch (Exception) { }
        return own;
    }

    /// <summary>Puts collider and camera back for the current local player and forgets it.</summary>
    private static void Release()
    {
        // The local player is gone (respawn, scene change) or the mod is switched off: don't leave other modded
        // players seeing us stuck crouched. Done before the early return below since _fpc may already be null.
        if (_lastSent == true)
        {
            try { if (PhotonNetwork.InRoom && !PhotonNetwork.OfflineMode && PhotonNetwork.LocalPlayer != null) Send(false); }
            catch (Exception) { }
        }
        _lastSent = null;

        if (_fpc == null && !_ccModified && !_camWritten) return;
        try { DeferColliderRestoreIfBlocked(); }
        catch (Exception) { }
        try { RestoreCollider(); } catch (Exception e) { Plugin.Logger.LogWarning($"[Crouch] Restoring collider failed: {e.Message}"); }
        try { RestoreCamera(); } catch (Exception e) { Plugin.Logger.LogWarning($"[Crouch] Restoring camera failed: {e.Message}"); }
        _fpc = null;
        _cc = null;
        _camTarget = null;
        _ccCaptured = false;
        LocalFpcPtr = IntPtr.Zero;
        Want = false;
        LocalT = 0f;
    }

    /// <summary>Called when the mod is enabled/disabled.</summary>
    internal static void Reset()
    {
        Release();
        Want = false;
        LocalT = 0f;
        _suppressUntilRelease = false;
    }

    // ---------------------------------------------------------------- collider

    private static void ApplyCollider()
    {
        if (_cc == null || !_ccCaptured) return;
        if (!Plugin.ShrinkCollider || LocalT <= 0f)
        {
            RestoreCollider();
            return;
        }
        float minH = _cc.radius * 2f + 0.05f;
        float crouchH = Mathf.Max(minH, _standHeight * Plugin.CameraHeight);
        float h = Mathf.Lerp(_standHeight, crouchH, LocalT);
        var c = _standCenter;
        c.y -= (_standHeight - h) * 0.5f;
        if (Mathf.Abs(_cc.height - h) > 0.0005f) _cc.height = h;
        if ((_cc.center - c).sqrMagnitude > 0.0000005f) _cc.center = c;
        _ccModified = true;
    }

    // Mod switched off (or player replaced) while crouched under something: restoring the full-height capsule
    // inside the geometry could push or wedge the player, so keep the shrunk capsule and restore it once there is room
    // (CrouchBehaviour calls TickPendingRestore even while the mod is disabled).
    private static CharacterController _pendCc;
    private static float _pendHeight;
    private static Vector3 _pendCenter;
    private static Transform _pendRoot;

    private static void DeferColliderRestoreIfBlocked()
    {
        if (!_ccModified || _cc == null || !_ccCaptured || _fpc == null) return;
        var root = _fpc.playerInstance != null ? _fpc.playerInstance.transform : _cc.transform;
        if (HasHeadroom(_cc, _standHeight, _standCenter, root)) return;
        _pendCc = _cc; _pendHeight = _standHeight; _pendCenter = _standCenter; _pendRoot = root;
        _ccModified = false; // RestoreCollider (called next) leaves the capsule alone; TickPendingRestore finishes the job
        Plugin.Info("No room to stand up while switching off; collider restored once there is space");
    }

    /// <summary>Runs every frame even while the mod is disabled; cheap when nothing is pending.</summary>
    internal static void TickPendingRestore()
    {
        if (_pendCc == null) { _pendCc = null; return; }
        if (!HasHeadroom(_pendCc, _pendHeight, _pendCenter, _pendRoot)) return;
        FinishPendingRestore();
    }

    private static void FinishPendingRestore()
    {
        if (_pendCc != null)
        {
            try { _pendCc.height = _pendHeight; _pendCc.center = _pendCenter; }
            catch (Exception) { }
        }
        _pendCc = null; _pendRoot = null;
    }

    private static void RestoreCollider()
    {
        if (!_ccModified) return;
        _ccModified = false;
        if (_cc == null || !_ccCaptured) return;
        _cc.height = _standHeight;
        _cc.center = _standCenter;
    }

    private static int CollisionMask(int layer)
    {
        int mask = 0;
        for (int i = 0; i < 32; i++)
            if (!Physics.GetIgnoreLayerCollision(layer, i)) mask |= 1 << i;
        return mask;
    }

    /// <summary>Is the space between the current (crouched) capsule top and the standing capsule top free?</summary>
    private static bool HasHeadroom(CharacterController cc, float standHeight, Vector3 standCenter, Transform root)
    {
        if (cc == null) return true;
        try
        {
            var tr = cc.transform;
            float scaleY = Mathf.Abs(tr.lossyScale.y);
            float scaleXZ = Mathf.Max(Mathf.Abs(tr.lossyScale.x), Mathf.Abs(tr.lossyScale.z));
            float r = cc.radius * scaleXZ * 0.9f;
            float rLocal = cc.radius;
            Vector3 curTop = tr.TransformPoint(cc.center + Vector3.up * Mathf.Max(0f, cc.height * 0.5f - rLocal));
            Vector3 standTop = tr.TransformPoint(standCenter + Vector3.up * Mathf.Max(0f, standHeight * 0.5f - rLocal));
            if (standTop.y - curTop.y < 0.01f * Mathf.Max(scaleY, 0.0001f)) return true;

            var hits = Physics.OverlapCapsule(curTop, standTop, r, CollisionMask(cc.gameObject.layer),
                QueryTriggerInteraction.Ignore);
            if (hits == null) return true;
            for (int i = 0; i < hits.Length; i++)
            {
                var h = hits[i];
                if (h == null || h.isTrigger) continue;
                if (root != null && h.transform.IsChildOf(root)) continue;
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            Plugin.WarnThrottled("headroom", $"Headroom check failed, standing up anyway: {e.Message}");
            return true;
        }
    }

    // ---------------------------------------------------------------- camera

    private static void ApplyCamera()
    {
        if (_camTarget == null) return;
        float standY = _fpc.defaultCameraPosition.y;
        if (standY <= 0.05f) standY = (_camWritten ? _camLastWritten.y - _camLastOffset : _camTarget.localPosition.y);
        float offset = -standY * (1f - Plugin.CameraHeight) * LocalT;

        if (!_camWritten && offset == 0f) return;

        var cur = _camTarget.localPosition;
        // If nobody else touched it since our last write, strip our old offset; otherwise the new value is the base.
        var basePos = _camWritten && cur == _camLastWritten ? cur - new Vector3(0f, _camLastOffset, 0f) : cur;
        var pos = basePos + new Vector3(0f, offset, 0f);
        _camTarget.localPosition = pos;
        _camLastWritten = pos;
        _camLastOffset = offset;
        _camWritten = offset != 0f;
    }

    private static void RestoreCamera()
    {
        if (!_camWritten) return;
        _camWritten = false;
        if (_camTarget == null) return;
        var cur = _camTarget.localPosition;
        if (cur == _camLastWritten) _camTarget.localPosition = cur - new Vector3(0f, _camLastOffset, 0f);
    }

    // ---------------------------------------------------------------- network

    private static void SyncNetwork(bool crouched)
    {
        bool share = Plugin.ShareWithOthers;
        try
        {
            if (!PhotonNetwork.InRoom || PhotonNetwork.OfflineMode) { _lastSent = null; return; }
            if (Time.unscaledTime < _sendRetryAt) return; // back off after a failure
            bool value = share && crouched;
            bool verify = Time.unscaledTime >= _nextVerify;
            // A change from what we last sent is always sent: LocalPlayer.CustomProperties is only updated when the
            // server echoes the change back, so comparing against it right after a quick tap would skip the send.
            bool changed = _lastSent != null && _lastSent != value;
            if (!changed && _lastSent == value && !verify) return;
            if (verify) _nextVerify = Time.unscaledTime + 2f;

            var me = PhotonNetwork.LocalPlayer;
            if (me == null) return;
            if (!changed)
            {
                // First send, or the periodic check (self-healing, e.g. after joining another room): send only if the
                // room's copy differs.
                bool? inRoom = ReadProp(me);
                if (inRoom == value || (inRoom == null && !value)) { _lastSent = value; return; }
            }
            Send(value);
            _lastSent = value;
            _nextVerify = Mathf.Max(_nextVerify, Time.unscaledTime + 1f); // let the echo arrive before the next check
            Plugin.Info($"Sent crouch state {value} to other players");
        }
        catch (Exception e)
        {
            Plugin.WarnThrottled("send", $"Sending crouch state failed: {e.Message}");
            _sendRetryAt = Time.unscaledTime + 5f;
            _nextVerify = _sendRetryAt;
        }
    }

    private static void Send(bool value)
    {
        var ht = new Hashtable();
        ht[(Il2CppSystem.Object)PropKey] = (Il2CppSystem.Object)value;
        PhotonNetwork.LocalPlayer.SetCustomProperties(ht);
    }

    /// <summary>A player's published crouch state; null when they never published one.</summary>
    internal static bool? ReadProp(PhotonPlayer p)
    {
        var props = p?.CustomProperties;
        if (props == null) return null;
        var key = _propKeyBoxed ??= (Il2CppSystem.Object)PropKey;
        if (!props.ContainsKey(key)) return null;
        var v = props[key];
        if (v == null) return null;
        try { return v.Unbox<bool>(); }
        catch (Exception) { return null; }
    }
}
