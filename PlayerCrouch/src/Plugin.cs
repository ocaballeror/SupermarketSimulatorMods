using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine.InputSystem;

namespace PlayerCrouch;

/// <summary>Gamepad buttons that can be used for crouching (None = keyboard only).</summary>
public enum CrouchPadButton
{
    None,
    LeftStickPress,
    RightStickPress,
    ButtonNorth,
    ButtonSouth,
    ButtonEast,
    ButtonWest,
    LeftShoulder,
    RightShoulder,
    DpadUp,
    DpadDown,
    DpadLeft,
    DpadRight,
}

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "oscar.playercrouch";
    public const string Name = "Player Crouch";
    public const string Version = "1.0.3";

    internal static ManualLogSource Logger;
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<Key> CrouchKey;
    internal static ConfigEntry<CrouchPadButton> PadButton;
    internal static ConfigEntry<bool> ToggleMode;
    internal static ConfigEntry<float> SpeedMultiplier;
    internal static ConfigEntry<float> CameraHeight;
    internal static ConfigEntry<float> TransitionTime;
    internal static ConfigEntry<bool> ShrinkCollider;
    internal static ConfigEntry<float> BodyDepth;
    internal static ConfigEntry<float> LeanAngle;
    internal static ConfigEntry<bool> ShareWithOthers;
    internal static ConfigEntry<bool> AnimateOthers;
    internal static ConfigEntry<bool> Verbose;

    internal static bool Active { get; private set; }

    private Harmony _harmony;

    public override void Load()
    {
        Logger = Log;
        _harmony = new Harmony(Guid);

        Enabled = Config.Bind("General", "Enabled", true,
            "Master switch. Turning it off stands you up and removes the feature right away (no restart needed).");

        CrouchKey = Config.Bind("Controls", "Key", Key.LeftCtrl,
            "Keyboard key used to crouch. Note: Ctrl is also the game's snap key while placing furniture; " +
            "crouching ignores the key while you are placing something.");
        PadButton = Config.Bind("Controls", "GamepadButton", CrouchPadButton.None,
            "Gamepad button used to crouch. None = no gamepad button. Pick one the game doesn't already use " +
            "(left stick press is sprint, right stick press is ping).");
        ToggleMode = Config.Bind("Controls", "ToggleMode", false,
            "false = hold the key to stay crouched. true = press once to crouch, again to stand up " +
            "(jump also stands you up).");

        SpeedMultiplier = Config.Bind("Crouch", "SpeedMultiplier", 0.5f,
            new ConfigDescription("Walking speed while crouched, as a fraction of normal walking speed. " +
                                  "You can't sprint while crouched.",
                new AcceptableValueRange<float>(0.1f, 1f)));
        CameraHeight = Config.Bind("Crouch", "CameraHeight", 0.65f,
            new ConfigDescription("Eye height while crouched, as a fraction of the standing eye height.",
                new AcceptableValueRange<float>(0.3f, 0.95f)));
        TransitionTime = Config.Bind("Crouch", "TransitionSeconds", 0.2f,
            new ConfigDescription("How long it takes to crouch down or stand up.",
                new AcceptableValueRange<float>(0.05f, 1f)));
        ShrinkCollider = Config.Bind("Crouch", "ShrinkCollider", true,
            "Make your collision shape shorter while crouched, so you fit under low things. " +
            "You only stand back up when there is room above you.");

        BodyDepth = Config.Bind("Animation", "BodyDepth", 0.4f,
            new ConfigDescription("How far the character's hips drop when crouched, as a fraction of leg length. " +
                                  "Higher = deeper squat.",
                new AcceptableValueRange<float>(0.1f, 0.6f)));
        LeanAngle = Config.Bind("Animation", "LeanAngle", 15f,
            new ConfigDescription("Forward lean of the upper body when crouched, in degrees.",
                new AcceptableValueRange<float>(0f, 40f)));
        ShareWithOthers = Config.Bind("Multiplayer", "ShareCrouchState", true,
            "Tell other players when you crouch, so their game can show your character crouching " +
            "(they need this mod too). Players without the mod simply ignore it.");
        AnimateOthers = Config.Bind("Multiplayer", "AnimateOtherPlayers", true,
            "Show other players' characters crouching when they crouch (they need this mod too).");

        Verbose = Config.Bind("Diagnostics", "VerboseLogging", false,
            "Write [Crouch] lines to BepInEx/LogOutput.log (setup details, crouch state changes, " +
            "other players' crouch state).");

        Enabled.SettingChanged += (_, _) => SetActive(Enabled.Value);
        AddComponent<CrouchBehaviour>();

        SetActive(Enabled.Value);
        Log.LogInfo($"{Name} {Version} loaded ({(Active ? "enabled" : "disabled")})");
    }

    private void SetActive(bool on)
    {
        if (on == Active) return;
        if (on) ApplyPatches();
        else
        {
            _harmony.UnpatchSelf();
            Log.LogInfo("All patches removed (disabled)");
        }
        Active = on;
        Crouch.Reset();
        CrouchPose.Reset();
    }

    private void ApplyPatches()
    {
        foreach (var type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
        {
            if (type.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
            try
            {
                _harmony.CreateClassProcessor(type).Patch();
                Log.LogDebug($"Applied {type.FullName}");
            }
            catch (Exception e)
            {
                Log.LogError($"Failed to apply {type.FullName}: {e}");
            }
        }
        Log.LogInfo("Patches applied");
    }

    internal static void Info(string msg)
    {
        if (Verbose.Value) Logger.LogInfo("[Crouch] " + msg);
    }

    public override bool Unload()
    {
        SetActive(false);
        return true;
    }
}
