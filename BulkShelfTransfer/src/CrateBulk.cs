/*
 * Improvement #9, the multi-product crate: modifier + Take fills the crate from the shelf slot,
 * modifier + Use empties the crate's matching products onto it.
 *
 * How the game does it (native dumps of CrateInteraction and its coroutines). The field and coroutine
 * names are swapped relative to the buttons:
 *   - OnTake(down) (right click) sets m_IsOnUse = down and starts <OnUseLoop>d__54: take from the shelf
 *     slot (or vending slot / baker / guard crate) into the crate.
 *   - OnUse(down) (left click) sets m_IsOnTake = down and starts <OnTakeLoop>d__62: put crate products
 *     onto the slot.
 *   - Release (down=false) clears the flag and StopCoroutine's the loop. Each loop iteration checks its
 *     flag, moves one product (with all of the game's checks and its multiplayer handling), then yields
 *     new WaitForSeconds(TotalProductInteractionInterval). On any refusal (crate empty/full, slot full,
 *     no matching product, ...) it shows the game's warning and ends (MoveNext returns false).
 *
 * What the mod does: modifier + click lets the game start its loop as usual, but marks a burst for it.
 * While the burst is active the matching release is swallowed (so the loop keeps going without
 * holding the button), each yielded WaitForSeconds is shortened to SecondsBetweenItems, and each step
 * runs inside TweenBoost so the products fly faster. The burst ends when the loop ends by itself, when
 * the player presses the button again without the modifier, or as a safety net if the loop doesn't run
 * for a second or runs longer than 30 s; ending it clears the loop's flag, which stops the loop at its
 * next step exactly like a release.
 * Multiplayer is the game's own: we only run its loop faster, on the acting client.
 *
 * Review round 1: the loop re-reads its targets every step (display slot, vending slot, floor product
 * m_CurrentInteractable, m_CurrentCrate). Because the release is swallowed, turning away mid-burst would
 * move products elsewhere, so the targets at the start are pinned and every step checks them (MoveNext
 * prefix) and ends the burst if they changed. Ending outside the loop also StopCoroutine's it, as a
 * release does, so a re-press can't leave two loops running.
 */
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace BulkShelfTransfer;

internal static class CrateBulk
{
    private sealed class Burst
    {
        public CrateInteraction Ci;
        public bool Take;      // true: right click / OnUseLoop (shelf -> crate)
        public float Started;
        public float LastStep;
        public int Steps;
        public DisplaySlot Slot;          // the loop re-reads its targets every step; we pin them
        public IntPtr Interactable;       // m_CurrentInteractable at start (floor products etc.)
        public IntPtr CurrentCrate;       // m_CurrentCrate at start (another crate being looked at)
    }

    private static Burst _active;

    // interop coroutine type -> (is the take loop, <>4__this, <>2__current)
    private static readonly Dictionary<Type, (bool take, PropertyInfo self, PropertyInfo current)> Loops = new();

    /// <summary>Ends a running burst like a release (used when the mod is switched off).</summary>
    internal static void Reset() => Finish("mod reset", stopLoop: true);

    internal static void PatchCoroutines(Harmony harmony)
    {
        Loops.Clear();
        var prefix = new HarmonyMethod(typeof(CrateBulk), nameof(MoveNextPrefix));
        var postfix = new HarmonyMethod(typeof(CrateBulk), nameof(MoveNextPostfix));
        foreach (var nested in typeof(CrateInteraction).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            string native = NativeName(nested);
            if (native == null) continue;
            bool take;
            if (native.StartsWith("<OnUseLoop>d__", StringComparison.Ordinal)) take = true;
            else if (native.StartsWith("<OnTakeLoop>d__", StringComparison.Ordinal)) take = false;
            else continue;

            var moveNext = AccessTools.Method(nested, "MoveNext");
            var self = AccessTools.Property(nested, "__4__this");
            var current = AccessTools.Property(nested, "__2__current");
            if (moveNext == null || self == null || current == null)
            {
                Plugin.Logger.LogError($"[Bulk] crate coroutine {native}: MoveNext/<>4__this/<>2__current not found");
                continue;
            }
            Loops[nested] = (take, self, current);
            harmony.Patch(moveNext, prefix: prefix, postfix: postfix);
            Plugin.Logger.LogDebug($"[Bulk] patched crate coroutine {native} ({nested.Name})");
        }
        if (Loops.Count < 2)
            Plugin.Logger.LogWarning("[Bulk] crate loops not found (game updated?); bulk transfer with the crate is off.");
    }

    private static string NativeName(Type t)
    {
        try
        {
            IntPtr klass = Il2CppClassPointerStore.GetNativeClassPointer(t);
            if (klass == IntPtr.Zero) return null;
            return Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass));
        }
        catch { return null; }
    }

    /// <summary>Prefix logic for OnTake (take=true) / OnUse (take=false). Returns false to skip the original.</summary>
    internal static bool OnButton(CrateInteraction ci, bool take, bool down)
    {
        var b = _active;
        bool mine = b != null && Bulk.Same(b.Ci, ci);
        if (!down)
            return !(mine && b.Take == take); // swallow the release of the burst's own button

        if (mine) Finish("button pressed again", stopLoop: true);
        if (Loops.Count < 2) return true;
        if (!Plugin.Crates.Value || !(take ? Plugin.BulkTake.Value : Plugin.BulkPlace.Value)) return true;
        if (!Bulk.ModifierHeld() || !Bulk.IsLocal(ci.PlayerInstance)) return true;
        var slot = ci.m_CurrentDisplaySlot;
        if (slot == null || ci.m_CurrentVendingSlot != null) return true; // only shelves; vending/baker/guard stay vanilla
        var crate = ci.m_Crate;
        if (crate == null || crate.IsGuardCrate || crate.IsBakerCrate) return true;

        TweenBoost.Boosted = 0;
        _active = new Burst
        {
            Ci = ci, Take = take, Started = Time.time, LastStep = Time.time, Slot = slot,
            Interactable = Ptr(ci.m_CurrentInteractable), CurrentCrate = Ptr(ci.m_CurrentCrate),
        };
        Bulk.Info($"crate {(take ? "take" : "place")}: start");
        return true; // the game starts its loop as usual
    }

    internal static void Tick(CrateInteraction ci)
    {
        var b = _active;
        if (b == null || !Bulk.Same(b.Ci, ci)) return;
        float now = Time.time;
        if (now - b.LastStep > 1f) Finish("loop not running", stopLoop: true);
        else if (now - b.Started > 30f) Finish("timeout", stopLoop: true);
    }

    private static IntPtr Ptr(Il2CppObjectBase o) => o == null ? IntPtr.Zero : o.Pointer;

    /// <summary>Why the loop must stop before this step, or null. Checked at the start of every loop step.</summary>
    private static string TargetChanged(Burst b)
    {
        var ci = b.Ci;
        if (ci == null || !ci.isActiveAndEnabled) return "crate interaction disabled";
        if (Time.time - b.Started > 30f) return "timeout";
        if (!Bulk.Same(ci.m_CurrentDisplaySlot, b.Slot)) return "looked away from the slot";
        if (ci.m_CurrentVendingSlot != null) return "looking at a vending machine";
        if (Ptr(ci.m_CurrentInteractable) != b.Interactable) return "looking at something else";
        if (Ptr(ci.m_CurrentCrate) != b.CurrentCrate) return "looking at another crate";
        return null;
    }

    /// <summary>
    /// Ends the burst like a release: clear the loop's flag (the loop exits at its next flag check) and, when
    /// not called from inside the loop itself, StopCoroutine it as OnTake/OnUse(false) do.
    /// </summary>
    private static void Finish(string why, bool stopLoop = false)
    {
        var b = _active;
        _active = null;
        if (b == null) return;
        try
        {
            var ci = b.Ci;
            if (ci != null)
            {
                if (b.Take) ci.m_IsOnUse = false;
                else ci.m_IsOnTake = false;
                if (stopLoop)
                {
                    var routine = b.Take ? ci.m_OnUseCoroutine : ci.m_OnTakeCoroutine;
                    if (routine != null) ci.StopCoroutine(routine);
                }
            }
        }
        catch (Exception e) { Bulk.Error("crate finish", e); }
        Bulk.Info($"crate {(b.Take ? "take" : "place")}: stopped ({why}) after {b.Steps} step(s), {TweenBoost.Boosted} animation(s) sped up");
    }

    private static Burst Match(Il2CppObjectBase routine)
    {
        var b = _active;
        if (b == null) return null;
        if (!Loops.TryGetValue(routine.GetType(), out var info) || info.take != b.Take) return null;
        var self = info.self.GetValue(routine) as Il2CppObjectBase;
        return Bulk.Same(self, b.Ci) ? b : null;
    }

    private static void MoveNextPrefix(Il2CppObjectBase __instance, out bool __state)
    {
        __state = false;
        try
        {
            var b = Match(__instance);
            if (b == null) return;
            string stop = TargetChanged(b);
            // Inside the loop: only clear the flag; this very step then exits at the loop's flag check.
            if (stop != null) { Finish(stop); return; }
            b.LastStep = Time.time;
            __state = TweenBoost.Begin();
        }
        catch (Exception e) { Bulk.Error("crate MoveNext prefix", e); }
    }

    private static void MoveNextPostfix(Il2CppObjectBase __instance, bool __result, bool __state)
    {
        TweenBoost.End(__state);
        try
        {
            var b = Match(__instance);
            if (b == null) return;
            if (!__result) { Finish("loop ended"); return; }
            b.Steps++;
            var prop = Loops[__instance.GetType()].current;
            var wait = (prop.GetValue(__instance) as Il2CppSystem.Object)?.TryCast<WaitForSeconds>();
            if (wait != null) wait.m_Seconds = Math.Min(wait.m_Seconds, Bulk.Interval);
        }
        catch (Exception e) { Bulk.Error("crate MoveNext postfix", e); }
    }
}

[HarmonyPatch(typeof(CrateInteraction), nameof(CrateInteraction.OnTake))]
internal static class CrateOnTakePatch
{
    private static bool Prefix(CrateInteraction __instance, bool down)
    {
        try { return CrateBulk.OnButton(__instance, take: true, down); }
        catch (Exception e) { Bulk.Error("CrateInteraction.OnTake", e); return true; }
    }
}

[HarmonyPatch(typeof(CrateInteraction), nameof(CrateInteraction.OnUse))]
internal static class CrateOnUsePatch
{
    private static bool Prefix(CrateInteraction __instance, bool down)
    {
        try { return CrateBulk.OnButton(__instance, take: false, down); }
        catch (Exception e) { Bulk.Error("CrateInteraction.OnUse", e); return true; }
    }
}

[HarmonyPatch(typeof(CrateInteraction), nameof(CrateInteraction.Update))]
internal static class CrateUpdatePatch
{
    private static void Prefix(CrateInteraction __instance)
    {
        try { CrateBulk.Tick(__instance); }
        catch (Exception e) { Bulk.Error("CrateInteraction.Update", e); }
    }
}
