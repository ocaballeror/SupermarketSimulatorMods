/*
 * Improvement #8: grid mode is on by default when placing furniture of any kind.
 *
 * How grid mode works in the game (native dumps of GridManager, FurniturePlacer):
 *   - GridManager (NoktaSingleton, one per scene) draws a grid on the floor and quantises the position
 *     of the furniture being placed while its field m_GridEnabled (+0xB0, property IsGridEnabled) is true.
 *     FurniturePlacer.PlacingRaycast asks GridManager.ModifyTargetPosition for the final position every
 *     frame (it returns the raw target unchanged when m_GridEnabled is false), and also
 *     OverrideSmoothness / ShouldSnapRotationAngle.
 *   - It is toggled by the "GridToggle" input action (X / gamepad North, NOT Ctrl) ->
 *     PlayerInteraction.onGridToggle -> GridManager.OnGridTogglePressed. That flips m_GridEnabled and
 *     copies the result to m_GridEnabledPreference (+0xB1) ("Preference saved" in its log line). The
 *     preference is the ONLY thing OnGridTogglePressed writes besides m_GridEnabled, and nothing
 *     initialises it, so it is false at the start of every session and is not written to disk.
 *   - GridManager.OnFurnitureStartPlacingMode(GameObject) is subscribed (OnLocalPlayerUpdated) to the
 *     LOCAL player's FurniturePlacer.OnFurnitureStartPlacingMode, which FurniturePlacer.StartPlacingMode
 *     invokes for every piece of furniture, picked up or just unpacked from a box. It rebuilds the
 *     active surface areas from the piece's placing tags and, only if there are some, does
 *     "if (m_GridEnabledPreference) { m_GridEnabled = true; DrawGrid(); UpdateGridSizeHint(); }".
 *     OnFurniturePlacedOrBoxed (placed, boxed up) sets m_GridEnabled = false and hides the grid, but
 *     leaves the preference alone.
 *   - Ctrl is the separate "SnapPlace" action (GridManager.m_SnapPlace): snapping to other furniture's
 *     snap points via TrySnapPlacing. It is independent of the grid and is not touched here.
 *
 * Fix: set GridManager.m_GridEnabledPreference = true before OnFurnitureStartPlacingMode runs. The game
 * then enables and draws the grid for every placement, for exactly those furniture types and surfaces
 * where the grid is supported at all (types without a matching area stay free, as in vanilla). The X key
 * still toggles it, and ModifyTargetPosition / the hints / the cell size keys all work as in vanilla.
 *   - The preference is forced once per GridManager instance (first placement after the instance was
 *     created; GridManager.Awake resets the marker), so a player who then presses X keeps their choice
 *     for the rest of the session, as in vanilla. With RememberToggle = false it is forced on every
 *     placement, so each piece starts with the grid on.
 *   - When the mod is disabled live, the original preference is restored.
 *   - No transpiler needed: the field is written from a prefix before the original reads it.
 *     OnFurnitureStartPlacingMode is only reached through a delegate (callers.sh finds no direct callers),
 *     so inlining is not a concern.
 *
 * Multiplayer: purely local. GridManager reacts only to the local player's placer and input, and only
 * changes where the local player's held piece is aimed. The final position goes through the normal
 * placement code and the usual placement RPCs / transform sync, so other players (modded or not) just
 * see a furniture piece at a grid-aligned position. No networked state is touched, so neither the host
 * nor a guest needs anything from the other.
 */
using System;
using HarmonyLib;
using UnityEngine;

namespace GridModeDefault;

[HarmonyPatch(typeof(GridManager), nameof(GridManager.OnFurnitureStartPlacingMode))]
internal static class GridDefaultPatch
{
    // Pointer of the GridManager whose preference we already forced, and what it was before.
    private static IntPtr _seeded;
    private static bool _original;

    internal static void Reset() { _seeded = IntPtr.Zero; }

    /// <summary>Puts the preference back when the mod is switched off live.</summary>
    internal static void Restore()
    {
        try
        {
            if (_seeded == IntPtr.Zero || !NoktaSingleton<GridManager>.HasInstance) return;
            var gm = NoktaSingleton<GridManager>.Instance;
            if (gm == null || gm.Pointer != _seeded) return;
            gm.m_GridEnabledPreference = _original;
            Log($"restored grid preference to {_original}");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[GridMode] restore failed: {e.Message}");
        }
    }

    [HarmonyPrefix]
    private static void Prefix(GridManager __instance)
    {
        try
        {
            if (!Plugin.Active) return;
            bool first = __instance.Pointer != _seeded;
            if (first)
            {
                _seeded = __instance.Pointer;
                _original = __instance.m_GridEnabledPreference;
            }
            else if (Plugin.RememberToggle.Value) return;

            bool before = __instance.m_GridEnabledPreference;
            __instance.m_GridEnabledPreference = true;
            Log($"placement started: grid preference {before} -> true ({(first ? "first placement" : "re-forced")})");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[GridMode] prefix failed (vanilla runs): {e.Message}");
        }
    }

    internal static void Log(string msg)
    {
        if (Plugin.Verbose != null && Plugin.Verbose.Value) Plugin.Logger.LogInfo($"[GridMode] {msg}");
    }
}

/// <summary>A new GridManager (scene reload) must be forced again, even if the allocation address is reused.</summary>
[HarmonyPatch(typeof(GridManager), "Awake")]
internal static class GridManagerAwakePatch
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        try { GridDefaultPatch.Reset(); }
        catch { /* nothing to do */ }
    }
}
