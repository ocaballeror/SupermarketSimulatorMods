using System;
using UnityEngine;

namespace PlayerDoubleCarry;

/// <summary>Per-frame hook (registered into IL2CPP via BasePlugin.AddComponent).</summary>
public class CarryBehaviour : MonoBehaviour
{
    public CarryBehaviour(IntPtr ptr) : base(ptr) { }

    private void Update()
    {
        try { Plugin.Tick(); }
        catch (Exception e) { Plugin.Logger.LogError($"[PlayerCarry] Tick failed: {e}"); }
    }
}
