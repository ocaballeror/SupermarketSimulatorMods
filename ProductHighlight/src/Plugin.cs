using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace ProductHighlight;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "oscar.producthighlight";
    public const string Name = "Product Highlight";
    public const string Version = "0.1.3";

    internal static ManualLogSource Logger;
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<bool> VerboseLogging;

    public override void Load()
    {
        Logger = Log;

        Enabled = Config.Bind("General", "Enabled", true, "Enable the mod.");
        VerboseLogging = Config.Bind("Diagnostics", "VerboseLogging", false,
            "Enable verbose logging for debugging purposes.");

        // The behaviour polls the held item a few times a second and draws/removes the outlines; it re-reads Enabled every tick,
        // so toggling the setting (config manager, Mod Settings Tool) needs no patching or restart.
        Enabled.SettingChanged += (_, _) => HighlightBehaviour.RequestRefresh();

        AddComponent<HighlightBehaviour>();   // nothing touches the game from Load(); the behaviour starts working once the game runs
        Log.LogInfo($"{Name} {Version} loaded ({(Enabled.Value ? "enabled" : "disabled")})");
    }

    internal static void Info(string msg)
    {
        if (VerboseLogging != null && VerboseLogging.Value) Logger.LogInfo("[Highlight] " + msg);
    }

    internal static void Warn(string msg) => Logger.LogWarning("[Highlight] " + msg);

    public override bool Unload()
    {
        try { HighlightBehaviour.Shutdown(); } catch { }
        return true;
    }
}
