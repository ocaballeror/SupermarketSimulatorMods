using System;
using System.Collections.Generic;
using DG.Tweening;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Photon.Pun;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BulkShelfTransfer;

internal static class Bulk
{
    /// <summary>True while the configured modifier is held (always true when it is set to None).</summary>
    internal static bool ModifierHeld()
    {
        var key = Plugin.ModifierKey.Value;
        if (key == Key.None) return true;
        var kb = Keyboard.current;
        if (kb == null) return false;
        try
        {
            var k = kb[key];
            return k != null && k.isPressed;
        }
        catch (Exception) { return false; } // key not on this layout
    }

    /// <summary>Connected to a room and not the host: our product moves go through the host and land later.</summary>
    internal static bool IsGuest => PhotonNetwork.IsConnected && !PhotonNetwork.IsMasterClient;

    internal static bool IsLocal(PlayerInstance p)
    {
        try
        {
            if (p == null || !p.IsLocalPlayerInstance) return false;
            // The game's crate/bag input handlers also require the player's PhotonView to be ours online.
            if (PhotonNetwork.IsConnected)
            {
                var pv = p.m_PhotonView;
                if (pv == null || !pv.IsMine) return false;
            }
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Time between steps. Online at least 0.02 s, so a high frame rate can't flood the room with RPCs.</summary>
    internal static float Interval => Math.Max(PhotonNetwork.IsConnected ? 0.02f : 0f, Plugin.SecondsBetweenItems.Value);

    internal static void Info(string msg)
    {
        if (Plugin.Verbose.Value) Plugin.Logger.LogInfo("[Bulk] " + msg);
    }

    private static readonly HashSet<string> LoggedErrors = new();

    /// <summary>Log an exception once per site, so a per-frame failure doesn't flood the log.</summary>
    internal static void Error(string site, Exception e)
    {
        if (LoggedErrors.Add(site)) Plugin.Logger.LogError($"[Bulk] {site} failed (further failures here are not logged): {e}");
    }

    internal static bool Same(Il2CppObjectBase a, Il2CppObjectBase b) =>
        a != null && b != null && a.Pointer == b.Pointer;

    internal static int ProductCapacityInBox(int productId)
    {
        var so = ProductSO(productId);
        return so?.GridLayoutInBox?.productCount ?? 0;
    }

    internal static int ProductCapacityOnShelf(int productId)
    {
        var so = ProductSO(productId);
        return so?.GridLayoutInStorage?.productCount ?? 0;
    }

    internal static ProductSO ProductSO(int id)
    {
        if (id <= 0 || !NoktaSingleton<IDManager>.HasInstance) return null;
        var so = NoktaSingleton<IDManager>.Instance.ProductSO(id);
        return so == null ? null : so;
    }
}

/// <summary>
/// Plays the game's own product fly animations faster during a bulk transfer.
/// The animations are DOTween tweens created inside the game's per-product methods (Box.AddProduct,
/// DisplaySlot.AddProduct, OrderPaperBagAnimation.PlayProductMove, the crate loops). We snapshot the
/// playing tweens before such a call and raise <c>timeScale</c> on the ones that appeared during it.
/// A Sequence's timeScale also applies to the tweens nested inside it.
/// </summary>
internal static class TweenBoost
{
    /// <summary>Tweens sped up since the last reset (diagnostics only).</summary>
    internal static int Boosted;

    private static int _depth;
    private static int _frame = -1;
    private static readonly HashSet<IntPtr> Before = new();
    private static Il2CppSystem.Collections.Generic.List<Tween> _buf;

    // Containers/slots whose incoming products should animate fast (pointer -> expiry Time.time).
    // Needed for guests: their products arrive later through the host's RPC, outside our own call.
    private static readonly Dictionary<IntPtr, float> Marks = new();

    internal static void Reset()
    {
        _depth = 0;
        Before.Clear();
        Marks.Clear();
        _buf = null;
    }

    internal static void Mark(Il2CppObjectBase o, float seconds)
    {
        if (o == null) return;
        Marks[o.Pointer] = Time.time + seconds;
    }

    internal static bool IsMarked(Il2CppObjectBase o)
    {
        if (o == null || Marks.Count == 0) return false;
        if (!Marks.TryGetValue(o.Pointer, out float until)) return false;
        if (Time.time <= until) return true;
        Marks.Remove(o.Pointer);
        return false;
    }

    /// <summary>Start capturing tweens. Returns true if this call opened the scope (pass it to <see cref="End"/>).</summary>
    internal static bool Begin()
    {
        if (Plugin.AnimationSpeed.Value <= 1.001f) return false;
        if (_depth > 0 && _frame != Time.frameCount) _depth = 0; // a scope leaked (exception in the game method)
        if (_depth++ > 0) return true;
        _frame = Time.frameCount;
        Before.Clear();
        try
        {
            var list = Playing();
            if (list != null)
                for (int i = 0; i < list.Count; i++) Before.Add(list[i].Pointer);
        }
        catch (Exception e) { Bulk.Error("TweenBoost.Begin", e); }
        return true;
    }

    internal static void End(bool begun)
    {
        if (!begun || _depth <= 0) return;
        if (--_depth > 0) return;
        try
        {
            float speed = Plugin.AnimationSpeed.Value;
            var list = Playing();
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                var t = list[i];
                if (t == null || Before.Contains(t.Pointer)) continue;
                if (!IsProductTween(t)) continue;
                t.timeScale = speed;
                Boosted++;
            }
        }
        catch (Exception e) { Bulk.Error("TweenBoost.End", e); }
        finally { Before.Clear(); }
    }

    /// <summary>
    /// Only product movement: tweens whose target is (part of) a Product, plus target-less Sequences (the paper
    /// bag's PlayProductMove). Warning popups, sale signs, crate highlights etc. keep their normal speed.
    /// </summary>
    private static bool IsProductTween(Tween t)
    {
        var target = t.target;
        if (target == null) return true;
        var comp = target.TryCast<Component>();
        if (comp == null) return false;
        return comp.GetComponentInParent(Il2CppType.Of<Product>(), true) != null;
    }

    private static Il2CppSystem.Collections.Generic.List<Tween> Playing()
    {
        // DOTween adds to the list without clearing it, and returns null when nothing is playing.
        _buf ??= new Il2CppSystem.Collections.Generic.List<Tween>();
        _buf.Clear();
        return DOTween.PlayingTweens(_buf);
    }
}
