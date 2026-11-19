using System;
using HarmonyLib;
using UnityEngine;

namespace PlayerCrouch;

/// <summary>
/// Crouch speed and no jumping while crouched. FirstPersonController.Move reads MoveSpeed (0x20) or, when the
/// sprint input is held, SprintSpeed (0x24) as its target speed; both are swapped for the crouch speed during
/// Move and restored right after, so nothing is changed permanently. See Crouch.cs for the overview.
/// </summary>
[HarmonyPatch(typeof(FirstPersonController), nameof(FirstPersonController.Move))]
internal static class MoveSpeedPatch
{
    internal struct Saved
    {
        public bool Set;
        public float Move, Sprint;
    }

    private static void Prefix(FirstPersonController __instance, out Saved __state)
    {
        __state = default;
        try
        {
            if (!Crouch.IsCrouching || __instance.Pointer != Crouch.LocalFpcPtr) return;
            float move = __instance.MoveSpeed, sprint = __instance.SprintSpeed;
            float crouchSpeed = move * Plugin.SpeedMultiplier;
            float t = Crouch.LocalT;
            __state = new Saved { Set = true, Move = move, Sprint = sprint };
            __instance.MoveSpeed = Mathf.Lerp(move, crouchSpeed, t);
            __instance.SprintSpeed = Mathf.Lerp(sprint, crouchSpeed, t);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Crouch] Move prefix failed: {e.Message}");
        }
    }

    private static void Postfix(FirstPersonController __instance, Saved __state)
    {
        if (!__state.Set) return;
        try
        {
            __instance.MoveSpeed = __state.Move;
            __instance.SprintSpeed = __state.Sprint;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Crouch] Move postfix failed: {e.Message}");
        }
    }
}

[HarmonyPatch(typeof(FirstPersonController), nameof(FirstPersonController.Jump))]
internal static class JumpPatch
{
    private static bool Prefix(FirstPersonController __instance)
    {
        try
        {
            if (__instance.Pointer != Crouch.LocalFpcPtr || (!Crouch.IsCrouching && !Crouch.Want)) return true;
            Crouch.OnJumpWhileCrouched();
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }
}
