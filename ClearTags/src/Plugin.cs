using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace ClearTags;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "oscar.cleartags";
    public const string Name = "Clear Tags";
    public const string Version = "0.2.2";

    internal static ManualLogSource Logger;
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<float> TagScale;
    internal static ConfigEntry<bool> HoverText;
    internal static ConfigEntry<float> HoverTextSize;
    internal static ConfigEntry<bool> Verbose;

    /// <summary>True while the box patches are applied (the master switch is on).</summary>
    internal static bool Active { get; private set; }

    private static Plugin s_instance;
    private Harmony _harmony;

    public override void Load()
    {
        s_instance = this;
        Logger = Log;
        _harmony = new Harmony(Guid);

        Enabled = Config.Bind("General", "Enabled", true,
            "Master switch. Turning it off takes effect immediately: tags go back to their normal size and the hover text disappears.");
        // ClampedFloat instead of AcceptableValueRange: config managers draw a text field, not a slider.
        TagScale = Config.Bind("Tags", "ScaleMultiplier", 2f,
            new ConfigDescription(
                "How much bigger the product icon tag on every box is drawn. Type a number: 1 = normal size, " +
                "2 = twice as big, 1.5 = one and a half times, from 0.5 to 5. Applies at once, also to boxes already in the store. " +
                "Only affects what you see.",
                new ClampedFloat(0.5f, 5f)));
        HoverText = Config.Bind("Hover", "ShowHoverText", true,
            "Show the product name, the quantity and the price of the box while your crosshair is on a box tag, " +
            "a shelf tag or a rack tag (also on the box you are holding).");
        HoverTextSize = Config.Bind("Hover", "TextSize", 18f,
            new ConfigDescription("Font size of the hover text (at 1080p; scales with the screen height). Type a number from 8 to 48.",
                new ClampedFloat(8f, 48f)));
        Verbose = Config.Bind("Diagnostics", "VerboseLogging", true,
            "Log what the mod does (tag [ClearTags] in BepInEx/LogOutput.log): boxes it resized, and each new hover target.");

        Enabled.SettingChanged += (_, _) => SetActive(Enabled.Value);
        TagScale.SettingChanged += (_, _) => OnScaleChanged();

        AddComponent<ClearTagsBehaviour>();
        SetActive(Enabled.Value, initial: true);
        Log.LogInfo($"{Name} {Version} loaded ({(Active ? "enabled" : "disabled")}, tag scale x{TagScale.Value})");
    }

    private void SetActive(bool on, bool initial = false)
    {
        if (on == Active) return;
        if (on)
        {
            ApplyPatches();
            Active = true;
            if (!initial) TagScaler.ResizeAll("enabled");   // Load() must not touch the game; the hooks handle boxes spawned later
        }
        else
        {
            Active = false;               // TagScaler.Factor is 1 from here on
            _harmony.UnpatchSelf();
            TagScaler.ResizeAll("disabled");
            Log.LogInfo("All patches removed (disabled)");
        }
    }

    private static void OnScaleChanged()
    {
        if (!Active) return;
        TagScaler.ResizeAll("scale changed");
    }

    private void ApplyPatches()
    {
        // Each [HarmonyPatch] class is applied on its own, so one broken patch (e.g. after a game update)
        // is logged without disabling the others.
        int ok = 0, total = 0;
        foreach (var type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
        {
            if (type.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
            total++;
            try
            {
                _harmony.CreateClassProcessor(type).Patch();
                ok++;
            }
            catch (Exception e)
            {
                Log.LogError($"Failed to apply {type.FullName}: {e}");
            }
        }
        Log.LogInfo($"Patches applied ({ok}/{total})");
    }

    internal static void Info(string msg)
    {
        if (Verbose != null && Verbose.Value) Logger.LogInfo("[ClearTags] " + msg);
    }

    public override bool Unload()
    {
        Active = false;
        try { TagScaler.ResizeAll("unload"); } catch { }
        _harmony?.UnpatchSelf();
        return true;
    }
}
