using System;
using Photon.Pun;
using UnityEngine;
using Hashtable = ExitGames.Client.Photon.Hashtable;
using PhotonPlayer = Photon.Realtime.Player;

namespace SelfCheckoutSpeed;

/// <summary>
/// Keeps every game in a room at the host's speed.
///
/// The host (Photon master client) publishes the multipliers it actually uses as two player custom
/// properties. Guests with the mod read them from the master client and use them instead of their own
/// settings, so a guest scans at exactly the host's speed even when its own config differs (or the mod
/// is switched off there). A host without the mod publishes nothing, so guests fall back to normal
/// speed (1x), which is what that host runs.
///
/// Unmodded clients just store the keys. The values persist for late joiners, and when the master
/// client changes, the new master publishes its own values on its next tick.
/// </summary>
internal static class HostSync
{
    private const string SelfKey = "oscar.scs.self";
    private const string HelpKey = "oscar.scs.help";

    private static float? _sentSelf;
    private static float? _sentHelp;
    private static float _nextCheck;

    /// <summary>Multiplier for a customer scanning and paying on their own.</summary>
    internal static float SelfMultiplier => Effective(SelfKey, LocalSelf());

    /// <summary>Multiplier for an employee (customer helper) scanning for the customer.</summary>
    internal static float HelpMultiplier => Effective(HelpKey, LocalHelp());

    internal static bool IsGuest
    {
        get
        {
            try { return PhotonNetwork.InRoom && !PhotonNetwork.OfflineMode && !PhotonNetwork.IsMasterClient; }
            catch (Exception) { return false; }
        }
    }

    private static float LocalSelf() => Plugin.Enabled.Value ? Plugin.SpeedMultiplier.Value : 1f;
    private static float LocalHelp() =>
        Plugin.Enabled.Value && Plugin.SpeedUpEmployeeHelp.Value ? Plugin.SpeedMultiplier.Value : 1f;

    private static float Effective(string key, float local)
    {
        if (!IsGuest) return local;
        try { return Read(PhotonNetwork.MasterClient, key) ?? 1f; }
        catch (Exception) { return 1f; }
    }

    private static float? Read(PhotonPlayer p, string key)
    {
        var props = p?.CustomProperties;
        if (props == null) return null;
        var k = (Il2CppSystem.Object)key;
        if (!props.ContainsKey(k)) return null;
        var v = props[k];
        if (v == null) return null;
        try
        {
            float f = v.Unbox<float>();
            return float.IsNaN(f) || f < 1f ? 1f : Math.Min(f, Plugin.MaxMultiplier);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Forces a re-publish on the next tick (settings changed).</summary>
    internal static void Invalidate() => _nextCheck = 0f;

    internal static void Reset()
    {
        _sentSelf = _sentHelp = null;
        _nextCheck = 0f;
    }

    /// <summary>Called every frame; publishes the host's values when they change (checked once a second).</summary>
    internal static void Tick()
    {
        if (Time.unscaledTime < _nextCheck) return;
        _nextCheck = Time.unscaledTime + 1f;
        if (!PhotonNetwork.InRoom || PhotonNetwork.OfflineMode || !PhotonNetwork.IsMasterClient)
        {
            _sentSelf = _sentHelp = null;
            return;
        }
        var me = PhotonNetwork.LocalPlayer;
        if (me == null) return;
        float self = LocalSelf(), help = LocalHelp();
        // Compare with what the room holds, not just with what was sent, so this heals itself after
        // joining another room or becoming master.
        if (Read(me, SelfKey) == self && Read(me, HelpKey) == help)
        {
            _sentSelf = self;
            _sentHelp = help;
            return;
        }
        var ht = new Hashtable();
        ht[(Il2CppSystem.Object)SelfKey] = (Il2CppSystem.Object)self;
        ht[(Il2CppSystem.Object)HelpKey] = (Il2CppSystem.Object)help;
        me.SetCustomProperties(ht);
        if (_sentSelf != self || _sentHelp != help)
            Plugin.Logger.LogInfo($"[SelfCheckout] Told guests to use x{self:0.##} (self checkout), x{help:0.##} (employee help)");
        _sentSelf = self;
        _sentHelp = help;
    }
}

/// <summary>Per-frame driver for <see cref="HostSync"/>. Registered into IL2CPP by BasePlugin.AddComponent.</summary>
public class SyncBehaviour : MonoBehaviour
{
    public SyncBehaviour(IntPtr ptr) : base(ptr) { }

    private static float _nextErrorLog;

    private void Update()
    {
        try { HostSync.Tick(); }
        catch (Exception e)
        {
            if (Time.unscaledTime < _nextErrorLog) return;
            _nextErrorLog = Time.unscaledTime + 10f;
            Plugin.Logger.LogWarning($"[SelfCheckout] Publishing the host's speed failed: {e.Message}");
        }
    }
}
