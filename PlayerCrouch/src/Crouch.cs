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
 *    (PlayerInteraction.InSkateboard) the player always stands.
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
 *
 * Other players (CrouchPose.cs)
 *  - Each player's crouch state is published as the Photon player custom property "oscar.crouch" (bool).
 *    The game already uses player properties ("sid", "png"); unmodded clients just store the extra key.
 *    Properties persist in the room, so late joiners see the right state.
 *  - Every modded client poses every player's model procedurally (see CrouchPose).
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
    internal const string PropKey = "oscar.crouch";

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
    private static bool _loggedBlocked;

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

        ReadInput(out bool held, out bool pressed);

        if (skate) Want = false;
        else if (canMove && !placing)
        {
            if (ToggleModeOn) { if (pressed) Want = !Want; }
            else Want = held;
        }

        float target = Want ? 1f : 0f;
        if (!Want && LocalT > 0f && _ccModified && !HasHeadroom(player))
        {
            target = LocalT; // something above us: stay down until there's room
            if (!_loggedBlocked) { Plugin.Info("No room to stand up; staying crouched"); _loggedBlocked = true; }
        }
        else _loggedBlocked = false;

        float before = LocalT;
        LocalT = Mathf.MoveTowards(LocalT, target, Time.deltaTime / Mathf.Max(0.01f, Plugin.TransitionTime.Value));
        if ((before == 0f) != (LocalT == 0f) || (before < 1f && LocalT == 1f))
            Plugin.Info(LocalT == 0f ? "Standing" : LocalT == 1f ? "Crouched" : "Crouching");

        ApplyCollider();
        SyncNetwork(target >= 0.5f);
    }

    internal static void LateUpdate()
    {
        if (_fpc == null) return;
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
            catch (ArgumentException) { /* key not on this keyboard layout */ }
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
        if (_cc != null)
        {
            _standHeight = _cc.height;
            _standCenter = _cc.center;
            _ccCaptured = _standHeight > 0.1f;
        }
        var camGo = fpc.CinemachineCameraTarget;
        _camTarget = camGo != null ? camGo.transform : null;

        if (Plugin.Verbose.Value)
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

    /// <summary>Puts collider and camera back for the current local player and forgets it.</summary>
    private static void Release()
    {
        if (_fpc == null && !_ccModified && !_camWritten) return;
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
        try
        {
            if (_lastSent == true && PhotonNetwork.InRoom && !PhotonNetwork.OfflineMode) Send(false);
        }
        catch (Exception) { }
        _lastSent = null;
    }

    // ---------------------------------------------------------------- collider

    private static void ApplyCollider()
    {
        if (_cc == null || !_ccCaptured) return;
        if (!Plugin.ShrinkCollider.Value || LocalT <= 0f)
        {
            RestoreCollider();
            return;
        }
        float minH = _cc.radius * 2f + 0.05f;
        float crouchH = Mathf.Max(minH, _standHeight * Plugin.CameraHeight.Value);
        float h = Mathf.Lerp(_standHeight, crouchH, LocalT);
        var c = _standCenter;
        c.y -= (_standHeight - h) * 0.5f;
        if (Mathf.Abs(_cc.height - h) > 0.0005f) _cc.height = h;
        if ((_cc.center - c).sqrMagnitude > 0.0000005f) _cc.center = c;
        _ccModified = true;
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
    private static bool HasHeadroom(PlayerInstance player)
    {
        if (_cc == null) return true;
        try
        {
            var tr = _cc.transform;
            float scaleY = Mathf.Abs(tr.lossyScale.y);
            float scaleXZ = Mathf.Max(Mathf.Abs(tr.lossyScale.x), Mathf.Abs(tr.lossyScale.z));
            float r = _cc.radius * scaleXZ * 0.9f;
            float rLocal = _cc.radius;
            Vector3 curTop = tr.TransformPoint(_cc.center + Vector3.up * Mathf.Max(0f, _cc.height * 0.5f - rLocal));
            Vector3 standTop = tr.TransformPoint(_standCenter + Vector3.up * Mathf.Max(0f, _standHeight * 0.5f - rLocal));
            if (standTop.y - curTop.y < 0.01f * Mathf.Max(scaleY, 0.0001f)) return true;

            var hits = Physics.OverlapCapsule(curTop, standTop, r, CollisionMask(_cc.gameObject.layer),
                QueryTriggerInteraction.Ignore);
            if (hits == null) return true;
            var root = player.transform;
            for (int i = 0; i < hits.Length; i++)
            {
                var h = hits[i];
                if (h == null || h.isTrigger) continue;
                if (h.transform.IsChildOf(root)) continue;
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[Crouch] Headroom check failed, standing up anyway: {e.Message}");
            return true;
        }
    }

    // ---------------------------------------------------------------- camera

    private static void ApplyCamera()
    {
        if (_camTarget == null) return;
        float standY = _fpc.defaultCameraPosition.y;
        if (standY <= 0.05f) standY = (_camWritten ? _camLastWritten.y - _camLastOffset : _camTarget.localPosition.y);
        float offset = -standY * (1f - Plugin.CameraHeight.Value) * LocalT;

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
        bool share = Plugin.ShareWithOthers.Value;
        try
        {
            if (!PhotonNetwork.InRoom || PhotonNetwork.OfflineMode) { _lastSent = null; return; }
            bool value = share && crouched;
            bool verify = Time.unscaledTime >= _nextVerify;
            if (_lastSent == value && !verify) return;
            if (verify) _nextVerify = Time.unscaledTime + 2f;

            // Self-healing: re-send if the room's copy differs (e.g. after joining another room).
            var me = PhotonNetwork.LocalPlayer;
            if (me == null) return;
            bool? inRoom = ReadProp(me);
            if (inRoom == value || (inRoom == null && !value)) { _lastSent = value; return; }
            Send(value);
            _lastSent = value;
            Plugin.Info($"Sent crouch state {value} to other players");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[Crouch] Sending crouch state failed: {e.Message}");
            _nextVerify = Time.unscaledTime + 5f;
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
        var key = (Il2CppSystem.Object)PropKey;
        if (!props.ContainsKey(key)) return null;
        var v = props[key];
        if (v == null) return null;
        try { return v.Unbox<bool>(); }
        catch (Exception) { return null; }
    }
}
