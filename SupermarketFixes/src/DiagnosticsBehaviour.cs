using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SupermarketFixes;

/// <summary>Per-frame hook for watchdogs and debug hotkeys (registered into IL2CPP via BasePlugin.AddComponent).</summary>
public class DiagnosticsBehaviour : MonoBehaviour
{
    public DiagnosticsBehaviour(IntPtr ptr) : base(ptr) { }

    private void Update()
    {
        if (!Plugin.Active) return;

        float now = Time.unscaledTime;
        try { Patches.StuckBoxFix.Tick(now); }
        catch (Exception e) { Plugin.Logger.LogError($"[StuckBox] Tick failed: {e}"); }
        try { Patches.GhostHeldItemFix.Tick(now); }
        catch (Exception e) { Plugin.Logger.LogError($"[GhostHeld] Tick failed: {e}"); }
        try { Patches.RackMoveFix.Tick(now); }
        catch (Exception e) { Plugin.Logger.LogError($"[RackMove] Tick failed: {e}"); }

        var kb = Keyboard.current;
        if (kb == null) return;
        if (kb.f8Key.wasPressedThisFrame)
            Patches.RackDiagnostics.ScanStore("F8");
        if (kb.f9Key.wasPressedThisFrame)
        {
            // "My hands/controls are stuck": log the state, then fix whatever is found right away.
            Patches.HeldState.Dump("F9");
            Patches.StuckBoxFix.ForceCheck();
            Patches.GhostHeldItemFix.ForceCheck();
        }
    }
}
