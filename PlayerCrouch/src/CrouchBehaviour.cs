using System;
using UnityEngine;

namespace PlayerCrouch;

/// <summary>Drives the crouch logic every frame. Registered into IL2CPP by BasePlugin.AddComponent.</summary>
public class CrouchBehaviour : MonoBehaviour
{
    public CrouchBehaviour(IntPtr ptr) : base(ptr) { }

    private static float _nextErrorLog;

    private void Update()
    {
        if (!Plugin.Active)
        {
            try { Crouch.TickPendingRestore(); }
            catch (Exception e) { LogError("Restore", e); }
            return;
        }
        try { Crouch.Update(); }
        catch (Exception e) { LogError("Update", e); }
    }

    private void LateUpdate()
    {
        if (!Plugin.Active) return;
        try { Crouch.LateUpdate(); }
        catch (Exception e) { LogError("Camera", e); }
        try { CrouchPose.LateUpdate(); }
        catch (Exception e) { LogError("Pose", e); }
    }

    private static void LogError(string where, Exception e)
    {
        // Don't flood the log at 60 fps if something keeps failing.
        if (Time.unscaledTime < _nextErrorLog) return;
        _nextErrorLog = Time.unscaledTime + 5f;
        Plugin.Logger.LogError($"[Crouch] {where} failed: {e}");
    }
}
