/*
 * Improvement #2: make paint cans last longer (default 2x).
 *
 * How paint works in the game (from the native dumps):
 *   - Each can type is a BucketSo ScriptableObject. Its `bucketData.paintAmount` is the amount of paint a
 *     NEW can starts with: DeliveryManager.Delivery(cart) passes BucketSo.bucketData to
 *     BoxGenerator.SpawnBox(BucketData, ...), which copies it into the delivered box. From then on the
 *     amount travels with the can (box data, save file, Photon instantiation data).
 *   - The can itself is a PaintBucket. `paintCount` is the paint left; painting one wall
 *     (PaintBucket.PaintWall) does `paintCount -= 1` and the can is empty at 0.
 *     `m_MaxPaintCount` (serialized on the prefab) is only used for the liquid level shown inside the
 *     can: fill = clamp01(paintCount / m_MaxPaintCount) in Initialize / PaintWall / GetPaintRatio.
 *
 * So "last N times longer" = start new cans with N x paintAmount, and scale m_MaxPaintCount by the
 * same factor so the liquid level keeps the same proportion of a new can's capacity. The vanilla max is
 * always read from the can type's prefab (IDManager.BucketSo(bucketID).BucketPrefab), which is never
 * modified, and the shown max is never below the can's current paint (max(Scale(vanilla), paint)).
 * Vanilla cans hold 8 paint against a prefab max of 10 (80%), so at x2 a new can (16 paint) shows 16/20
 * = 80%, as in vanilla, and a pre-mod can (8 paint) shows 8/20 = 40%. Both drain at the same per-wall
 * rate (1/20 of the display per wall); old cans just start lower.
 *
 * Patches:
 *   - IDManager.BucketSo(id) postfix: scale the returned can type's starting amount (every purchase
 *     looks the type up through this before spawning the box).
 *   - PaintBucket.Initialize prefix: scale the can's m_MaxPaintCount before the fill level is computed.
 * Can type amounts are remembered so changing the multiplier or disabling the mod restores them; can
 * maxima are restored from the prefab.
 *
 * Multiplayer: purchases are spawned by the host (SpawnBox only spawns on the master client) and the
 * BucketData, including the paint amount, is sent to guests, so only the HOST needs the mod for the
 * extra paint. A guest without the mod computes the fill from the prefab max (10): a 16-paint can shows
 * full until the 7th wall (paint drops below 10; display only). A guest-only install changes nothing about painting
 * amounts, but its own Initialize prefix makes vanilla 8-paint cans show 40% instead of 80%.
 *
 * Existing cans are not topped up: their saved amount is kept. If the mod is removed later, cans that
 * hold more than the vanilla capacity just show as full until they drop below it.
 */
using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using __Project__.Scripts.WallPaintSystem;

namespace PaintCanCapacity;

internal static class PaintPatches
{
    // Keyed by native object pointer. `Applied` lets us detect a value the game reset (pooled objects,
    // a fresh instance reusing an address) so we never scale an already scaled number twice.
    private struct Scaled { public int Original; public int Applied; }

    // ScriptableObjects are persistent assets, so their pointers are stable for the whole session.
    private static readonly Dictionary<IntPtr, Scaled> SoAmounts = new();

    private static int Scale(int original)
    {
        if (original <= 0) return original;
        return Math.Max(1, (int)Math.Round(original * Plugin.Multiplier.Value));
    }

    /// <summary>Scale a can type's starting paint. Returns true if the value changed.</summary>
    internal static bool EnsureScaled(BucketSo so)
    {
        if (so == null) return false;
        var data = so.bucketData;
        if (data == null) return false;
        IntPtr key = so.Pointer;
        int current = data.paintAmount;
        if (!SoAmounts.TryGetValue(key, out var s) || current != s.Applied)
            s.Original = current; // first sight, or the game reset it: current is the vanilla value
        s.Applied = Scale(s.Original);
        SoAmounts[key] = s;
        if (current == s.Applied) return false;
        data.paintAmount = s.Applied;
        if (Plugin.VerboseLogging.Value)
            Plugin.Logger.LogInfo($"[PaintCan] type {so.ID} ({SafeName(so)}): new cans start with {s.Applied} paint (vanilla {s.Original})");
        return true;
    }

    /// <summary>The vanilla m_MaxPaintCount for a can type, from its untouched prefab; -1 if unknown.</summary>
    private static int VanillaMax(int productId)
    {
        if (!NoktaSingleton<IDManager>.HasInstance) return -1;
        var so = NoktaSingleton<IDManager>.Instance?.BucketSo(productId);
        var prefab = so != null ? so.BucketPrefab : null;
        return prefab != null ? prefab.m_MaxPaintCount : -1;
    }

    /// <summary>
    /// Vanilla max for this can. The game itself looks the type up by PaintBucket.bucketID (a prefab
    /// field, see BucketInteraction.BoxUpBucket); BucketData.ProductID is the fallback.
    /// </summary>
    private static int VanillaMaxOf(PaintBucket bucket, BucketData data)
    {
        int v = VanillaMax(bucket.bucketID);
        if (v <= 0 && data != null) v = VanillaMax(data.ProductID);
        return v;
    }

    /// <summary>Scale a can's liquid-level maximum. paint = the paint it holds (or will hold).</summary>
    internal static void EnsureScaled(PaintBucket bucket, BucketData data, int paint)
    {
        if (bucket == null) return;
        int vanilla = VanillaMaxOf(bucket, data);
        if (vanilla <= 0) return; // unknown type: leave the game's value alone
        int max = Math.Max(Scale(vanilla), paint);
        if (bucket.m_MaxPaintCount != max) bucket.m_MaxPaintCount = max;
    }

    /// <summary>
    /// Called on live config changes (game running): rescale every loaded can type and can. Inactive cans
    /// (LeanPool-despawned ones) are included, because Initialize never writes m_MaxPaintCount itself and a
    /// reused pooled can would otherwise keep the old maximum. FindObjectsOfType returns scene objects only,
    /// never prefab assets.
    /// </summary>
    internal static void ApplyToAll()
    {
        if (!GameReady()) return;
        // One try/catch per object, so a bad object doesn't skip the rest.
        try
        {
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<BucketSo>()))
            {
                try { EnsureScaled(o.TryCast<BucketSo>()); }
                catch (Exception e) { Plugin.Logger.LogError($"[PaintCan] ApplyToAll (can type) failed: {e}"); }
            }
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<PaintBucket>(), true))
            {
                try
                {
                    var b = o.TryCast<PaintBucket>();
                    if (b == null) continue;
                    EnsureScaled(b, b.bucketData, b.paintCount);
                    if (b.gameObject.activeInHierarchy) RefreshFill(b);
                }
                catch (Exception e) { Plugin.Logger.LogError($"[PaintCan] ApplyToAll (can) failed: {e}"); }
            }
        }
        catch (Exception e) { Plugin.Logger.LogError($"[PaintCan] ApplyToAll failed: {e}"); }
    }

    /// <summary>Put every value we changed back to vanilla (mod disabled).</summary>
    internal static void RestoreAll()
    {
        if (!GameReady()) { SoAmounts.Clear(); return; }
        // One try/catch per object. A can type's record is only dropped once it was restored (or needs no
        // restoring), so a failed one keeps its record and can still be restored by a later call.
        try
        {
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<BucketSo>()))
            {
                try
                {
                    var so = o.TryCast<BucketSo>();
                    if (so?.bucketData == null) continue;
                    if (!SoAmounts.TryGetValue(so.Pointer, out var s)) continue;
                    if (so.bucketData.paintAmount == s.Applied)
                        so.bucketData.paintAmount = s.Original;
                    SoAmounts.Remove(so.Pointer);
                }
                catch (Exception e) { Plugin.Logger.LogError($"[PaintCan] RestoreAll (can type) failed: {e}"); }
            }
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<PaintBucket>(), true))
            {
                try
                {
                    var b = o.TryCast<PaintBucket>();
                    if (b == null) continue;
                    int vanilla = VanillaMaxOf(b, b.bucketData);
                    if (vanilla > 0 && b.m_MaxPaintCount != vanilla)
                    {
                        b.m_MaxPaintCount = vanilla;
                        if (b.gameObject.activeInHierarchy) RefreshFill(b);
                    }
                }
                catch (Exception e) { Plugin.Logger.LogError($"[PaintCan] RestoreAll (can) failed: {e}"); }
            }
        }
        catch (Exception e) { Plugin.Logger.LogError($"[PaintCan] RestoreAll failed: {e}"); }
    }

    private static void RefreshFill(PaintBucket b)
    {
        // Same formula the game uses; TweenMaterial just sets the shader's _Fill value.
        if (b.paintCount <= 0 || b.m_MaxPaintCount <= 0) return;
        try { b.TweenMaterial(Mathf.Clamp01((float)b.paintCount / b.m_MaxPaintCount)); }
        catch (Exception e)
        {
            // renderer not ready (pooled/inactive); Initialize will set the fill on reuse
            if (Plugin.VerboseLogging.Value) Plugin.Logger.LogInfo($"[PaintCan] RefreshFill skipped: {e.Message}");
        }
    }

    // Plugin.Load runs before the game has started; never touch Unity objects from there.
    private static bool GameReady() => Plugin.LoadFinished;

    private static string SafeName(BucketSo so)
    {
        try { return so.name; } catch { return "?"; }
    }

    [HarmonyPatch(typeof(IDManager), nameof(IDManager.BucketSo), new[] { typeof(int) })]
    private static class IDManagerBucketSoPatch
    {
        private static void Postfix(BucketSo __result)
        {
            try
            {
                EnsureScaled(__result);
            }
            catch (Exception e) { Plugin.Logger.LogError($"[PaintCan] BucketSo postfix: {e}"); }
        }
    }

    [HarmonyPatch(typeof(PaintBucket), nameof(PaintBucket.Initialize))]
    private static class PaintBucketInitializePatch
    {
        private static void Prefix(PaintBucket __instance, BucketData data)
        {
            try
            {
                // Initialize copies data.paintAmount into paintCount and computes the fill right after.
                EnsureScaled(__instance, data, data != null ? data.paintAmount : 0);
                if (Plugin.VerboseLogging.Value && data != null)
                    Plugin.Logger.LogInfo($"[PaintCan] can initialized: paint {data.paintAmount}/{__instance.m_MaxPaintCount}");
            }
            catch (Exception e) { Plugin.Logger.LogError($"[PaintCan] Initialize prefix: {e}"); }
        }
    }
}
