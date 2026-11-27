using System;
using UnityEngine;

namespace AutoLights;

/// <summary>Per-frame hook (registered into IL2CPP via BasePlugin.AddComponent).</summary>
public class LightsBehaviour : MonoBehaviour
{
    public LightsBehaviour(IntPtr ptr) : base(ptr) { }

    private void Update()
    {
        try { LightsTimer.Tick(); }
        catch (Exception e) { Plugin.Logger.LogError($"[AutoLights] Tick failed: {e}"); }
    }
}
