using System;
using UnityEngine;

namespace AutoLights;

/// <summary>Per-frame hook (registered into IL2CPP via BasePlugin.AddComponent).</summary>
public class LightsBehaviour : MonoBehaviour
{
    public LightsBehaviour(IntPtr ptr) : base(ptr) { }

    /// <summary>Set after the first exception: the error is logged once and ticking stops until Enabled is toggled.</summary>
    internal static bool Failed;

    private void Update()
    {
        if (Failed) return;
        try { LightsTimer.Tick(); }
        catch (Exception e)
        {
            Failed = true;
            Plugin.Logger.LogError($"[AutoLights] Tick failed, the mod stops until it is toggled off and on: {e}");
        }
    }
}
