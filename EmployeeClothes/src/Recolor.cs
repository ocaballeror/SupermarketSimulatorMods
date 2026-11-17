using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using UnityEngine;
using Object = UnityEngine.Object;

namespace EmployeeClothes;

/// <summary>
/// Recolours the clothes of a model without touching its mesh, skeleton or animation. See the header of Plugin.cs
/// for the idea: a shade map per source texture (luminance relative to the texture's mean) plus a material copy per
/// (original material, slot) whose colour is the chosen colour. A slot is an employee type (0..7) or the preview.
/// </summary>
internal static class Recolor
{
    /// <summary>Slot used by the preview models, so dragging the picker never changes the real employees.</summary>
    internal const int PreviewSlot = 100;

    private const float TintScale = 1.45f;   // shade map stores shade / TintScale, the material colour multiplies it back
    private const float Contrast = 1.4f;
    private const float ShadeMin = 0.45f;
    private const int MaxSize = 1024;

    // Names that mean "clothes", looked up in "<material name> | <base texture name>". The game's names lie (the
    // restocker's T-shirt is a material called Hair_Mat), so the texture name counts too.
    private static readonly Regex ClothesRx = new(
        @"shirt|pants|shoes|jacket|waistcoat|handkerchief|dres|trouser|_top_|_btm_|bouncerbody|yaka|helmet|hat_|_hat_|tshirt",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Not recoloured: atlases that hold clothes and skin together, a tint can't tell them apart.
    private static readonly Regex MixedRx = new("britney", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private sealed class Info
    {
        public bool Clothes;
        public float Garment = 1f;   // shade of this garment relative to the chosen colour
        public Texture BaseTex;
        public string BaseProp;
    }

    // All caches are keyed by GetInstanceID (an interop wrapper's Pointer can be reused after the wrapper is collected).
    private static readonly Dictionary<int, Info> InfoCache = new();                        // original material -> classification
    private static readonly Dictionary<int, Texture2D> Neutral = new();                     // source texture -> shade map (null = failed)
    private static readonly Dictionary<(int, int), Material> Variants = new();              // (original material, slot) -> copy
    private static readonly Dictionary<int, Material> OriginalOf = new();                   // copy -> original material
    private static readonly Dictionary<int, float> GarmentOf = new();                       // copy -> garment shade
    private static readonly Dictionary<int, Color> LastTint = new();                        // copy -> colour last set (skip no-op SetColor)
    private static readonly Stopwatch Watch = new();
    private static double _budgetMs = 25;
    private static bool _loggedFirstMap;


    /// <summary>True when creating a shade map was postponed because the time budget ran out; callers retry later.</summary>
    internal static bool Postponed { get; private set; }

    internal static void BeginBudget(double ms)
    {
        _budgetMs = ms;
        Postponed = false;
        Watch.Restart();
    }

    // ------------------------------------------------------------------ classification

    private static Info Classify(Material orig)
    {
        int id = orig.GetInstanceID();
        if (InfoCache.TryGetValue(id, out var info)) return info;
        info = new Info();
        try
        {
            string prop = null;
            Texture tex = null;
            if (orig.HasProperty("_BaseMap")) { prop = "_BaseMap"; tex = orig.GetTexture("_BaseMap"); }
            if (tex == null && orig.HasProperty("_MainTex")) { prop = "_MainTex"; tex = orig.GetTexture("_MainTex"); }
            string name = orig.name ?? "";
            string both = name + " | " + (tex != null ? tex.name : "");
            if (tex != null && ClothesRx.IsMatch(both) && !MixedRx.IsMatch(both))
            {
                info.Clothes = true;
                info.BaseTex = tex;
                info.BaseProp = prop;
                string l = both.ToLowerInvariant();
                if (Plugin.ShadedGarments.Value) info.Garment = GarmentShade(l);
            }
        }
        catch (Exception e) { Plugin.Debug("Classifying a material failed: " + e.Message); }
        InfoCache[id] = info;
        return info;
    }

    private static float GarmentShade(string lowerName)
    {
        if (lowerName.Contains("shoes")) return 0.5f;
        if (lowerName.Contains("pants") || lowerName.Contains("trouser") || lowerName.Contains("_btm_")) return 0.7f;
        if (lowerName.Contains("waistcoat")) return 0.8f;
        return 1f;
    }

    /// <summary>The ShadedGarments setting changed: reclassify and let the next sweep re-tint everything.</summary>
    internal static void OnShadeSettingChanged()
    {
        // Variants keep the garment shade they were made with: put the originals back, drop the copies and let the next
        // sweep rebuild them (cheap: the shade maps stay).
        try
        {
            RestoreRegistry();
            Preview.OnMaterialsReset();
        }
        catch (Exception e) { Plugin.Debug("Resetting after a settings change failed: " + e.Message); }
        DestroyVariants();
        InfoCache.Clear();
        ClothesStore.Touch();
    }

    // ------------------------------------------------------------------ shade maps

    /// <summary>Returns the shade map for a source texture, creating it if the time budget allows.</summary>
    private static Texture2D GetNeutral(Texture src, out bool postponed)
    {
        postponed = false;
        int key = src.GetInstanceID();
        if (Neutral.TryGetValue(key, out var n)) return n;
        if (Watch.IsRunning && Watch.Elapsed.TotalMilliseconds > _budgetMs)
        {
            postponed = true;
            Postponed = true;
            return null;
        }
        if (WaitForStreaming(src, key))
        {
            postponed = true;
            Postponed = true;
            return null;
        }
        try { n = MakeNeutral(src); }
        catch (Exception e)
        {
            Plugin.Warn($"Making a shade map for texture '{src.name}' failed: {e.Message}");
            n = null;
        }
        Neutral[key] = n;
        ClearMipRequest(src);
        return n;
    }

    private static readonly Dictionary<int, float> StreamWaits = new();

    /// <summary>The shade map is built: stop asking the streaming system to keep mip 0 of the source resident.</summary>
    private static void ClearMipRequest(Texture src)
    {
        try
        {
            var t2 = src.TryCast<Texture2D>();
            if (t2 != null && t2.streamingMipmaps) t2.ClearRequestedMipmapLevel();
        }
        catch (Exception) { }
    }

    /// <summary>
    /// A texture with streaming mipmaps may only have a small mip loaded; ask for the full one and give it a few passes
    /// (up to 2.5 s) before copying, otherwise the shade map would be blurry for good.
    /// </summary>
    private static bool WaitForStreaming(Texture src, int key)
    {
        try
        {
            var t2 = src.TryCast<Texture2D>();
            if (t2 == null || !t2.streamingMipmaps) return false;
            t2.requestedMipmapLevel = 0;
            if (t2.loadedMipmapLevel <= 0) return false;
            if (!StreamWaits.TryGetValue(key, out float since)) { since = Time.unscaledTime; StreamWaits[key] = since; }
            return Time.unscaledTime - since < 2.5f;
        }
        catch (Exception) { return false; }
    }

    private static Texture2D MakeNeutral(Texture src)
    {
        int sw = src.width, sh = src.height;
        float scale = Math.Min(1f, MaxSize / (float)Math.Max(sw, sh));
        int tw = Math.Max(4, (int)(sw * scale)), th = Math.Max(4, (int)(sh * scale));
        tw &= ~3; th &= ~3; // multiple of 4 for DXT compression

        var rt = RenderTexture.GetTemporary(tw, th, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        var prev = RenderTexture.active;
        Texture2D readable;
        try
        {
            Graphics.Blit(src, rt);
            RenderTexture.active = rt;
            readable = new Texture2D(tw, th, TextureFormat.RGBA32, false, false);
            readable.ReadPixels(new Rect(0, 0, tw, th), 0, 0);
            readable.Apply(false, false);
        }
        finally
        {
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
        }

        var pixels = readable.GetPixels32();
        int count = pixels.Length;
        var buf = new byte[count * 4];
        bool fast = CopyOut(pixels, buf);

        // Mean luminance of the used pixels (pure black is UV padding).
        double sum = 0; long used = 0;
        for (int i = 0; i < count; i++)
        {
            int o = i * 4;
            float l = (0.299f * buf[o] + 0.587f * buf[o + 1] + 0.114f * buf[o + 2]) / 255f;
            if (l > 0.004f) { sum += l; used++; }
        }
        float mean = used > 0 ? (float)(sum / used) : 0.5f;

        for (int i = 0; i < count; i++)
        {
            int o = i * 4;
            float l = (0.299f * buf[o] + 0.587f * buf[o + 1] + 0.114f * buf[o + 2]) / 255f;
            float shade = Mathf.Clamp(1f + (l - mean) * Contrast, ShadeMin, TintScale);
            byte v = (byte)(shade / TintScale * 255f + 0.5f);
            buf[o] = v; buf[o + 1] = v; buf[o + 2] = v; // alpha (buf[o + 3]) is kept
        }
        CopyIn(buf, pixels, fast);

        var tex = new Texture2D(tw, th, TextureFormat.RGBA32, true, false);
        tex.name = "EC shade " + src.name;
        tex.hideFlags = HideFlags.HideAndDontSave;
        tex.SetPixels32(pixels);
        tex.Apply(true, false);
        try
        {
            tex.Compress(false);   // DXT5: a quarter of the memory
            tex.Apply(false, true);
        }
        catch (Exception e) { Plugin.Debug("Compressing a shade map failed (kept uncompressed): " + e.Message); }
        Object.Destroy(readable);

        if (!_loggedFirstMap)
        {
            _loggedFirstMap = true;
            Plugin.Info($"First shade map: '{src.name}' {sw}x{sh} -> {tw}x{th}, mean luminance {mean:0.000}, fast copy {fast}");
        }
        else Plugin.Debug($"Shade map '{src.name}' {sw}x{sh} -> {tw}x{th}, mean {mean:0.000}");
        return tex;
    }

    // Color32[] <-> byte[] through the IL2CPP array's data pointer (elements start at +0x20). Verified against the
    // indexer on the first element; falls back to the (slow) indexer when that doesn't match.
    private static bool CopyOut(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color32> arr, byte[] buf)
    {
        int n = arr.Length;
        try
        {
            var data = arr.Pointer + 0x20;
            Marshal.Copy(data, buf, 0, n * 4);
            var first = arr[0];
            var lastIdx = n - 1;
            var last = arr[lastIdx];
            if (buf[0] == first.r && buf[1] == first.g && buf[2] == first.b && buf[3] == first.a
                && buf[lastIdx * 4] == last.r && buf[lastIdx * 4 + 1] == last.g && buf[lastIdx * 4 + 2] == last.b && buf[lastIdx * 4 + 3] == last.a)
                return true;
        }
        catch (Exception) { }
        for (int i = 0; i < n; i++)
        {
            var c = arr[i];
            buf[i * 4] = c.r; buf[i * 4 + 1] = c.g; buf[i * 4 + 2] = c.b; buf[i * 4 + 3] = c.a;
        }
        return false;
    }

    private static void CopyIn(byte[] buf, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color32> arr, bool fast)
    {
        int n = arr.Length;
        if (fast)
        {
            Marshal.Copy(buf, 0, arr.Pointer + 0x20, n * 4);
            return;
        }
        for (int i = 0; i < n; i++) arr[i] = new Color32(buf[i * 4], buf[i * 4 + 1], buf[i * 4 + 2], buf[i * 4 + 3]);
    }

    // ------------------------------------------------------------------ material copies

    private static bool TryGetVariant(Material orig, Info info, int slot, out Material variant)
    {
        var key = (orig.GetInstanceID(), slot);
        if (Variants.TryGetValue(key, out variant) && variant != null) return true;
        variant = null;
        var neutral = GetNeutral(info.BaseTex, out _);
        if (neutral == null) return false;   // postponed or failed
        var copy = new Material(orig);
        copy.name = orig.name + " (EC " + slot + ")";
        copy.hideFlags = HideFlags.HideAndDontSave;
        if (copy.HasProperty("_BaseMap")) copy.SetTexture("_BaseMap", neutral);
        if (copy.HasProperty("_MainTex")) copy.SetTexture("_MainTex", neutral);
        Variants[key] = copy;
        int cid = copy.GetInstanceID();
        OriginalOf[cid] = orig;
        GarmentOf[cid] = info.Garment;
        variant = copy;
        return true;
    }

    private static void SetTint(Material m, Color c)
    {
        float k = TintScale * (GarmentOf.TryGetValue(m.GetInstanceID(), out var g) ? g : 1f);
        var tint = new Color(c.r * k, c.g * k, c.b * k, 1f);
        if (LastTint.TryGetValue(m.GetInstanceID(), out var last) && last == tint) return;
        LastTint[m.GetInstanceID()] = tint;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", tint);
        if (m.HasProperty("_Color")) m.SetColor("_Color", tint);
    }

    // ------------------------------------------------------------------ applying

    /// <summary>
    /// Every renderer that has one of our copies on it: the renderer, the slot, and the original material per
    /// material index. Restoring goes through this registry, so it also reaches pooled (inactive) employees and still
    /// works when a copy has been destroyed.
    /// </summary>
    private sealed class Entry
    {
        public Renderer R;
        public int Slot;
        public Material[] Originals;
    }

    private static readonly Dictionary<int, Entry> Registry = new();

    /// <summary>True while some renderer may still show one of our material copies (a restore pass is needed).</summary>
    internal static bool NeedsRestore
    {
        get
        {
            foreach (var kv in Registry) if (kv.Value.Slot != PreviewSlot) return true;
            return false;
        }
    }

    private static bool HasOriginals(Entry e)
    {
        for (int i = 0; i < e.Originals.Length; i++) if (e.Originals[i] != null) return true;
        return false;
    }

    /// <summary>
    /// Makes every clothes material on this renderer match <paramref name="color"/> (null = the original material).
    /// Returns false if some shade map was postponed by the time budget.
    /// </summary>
    internal static bool ApplyToRenderer(Renderer r, int slot, Color? color)
    {
        bool complete = true;
        var mats = r.sharedMaterials;
        if (mats == null) return true;
        int rid = r.GetInstanceID();
        Registry.TryGetValue(rid, out var entry);
        if (entry != null && entry.Originals.Length != mats.Length) entry.Originals = new Material[mats.Length];
        bool changed = false;
        for (int i = 0; i < mats.Length; i++)
        {
            var m = mats[i];
            Material orig;
            bool ours;
            if (m == null)
            {
                // A destroyed copy: fall back to the original recorded for this slot of the renderer.
                orig = entry != null ? entry.Originals[i] : null;
                if (orig == null) continue;
                ours = true;
            }
            else
            {
                ours = OriginalOf.TryGetValue(m.GetInstanceID(), out orig);
                if (!ours) orig = m;
            }
            var info = Classify(orig);
            if (!info.Clothes) continue;
            if (!color.HasValue)
            {
                if (ours) { mats[i] = orig; changed = true; if (entry != null) entry.Originals[i] = null; }
                continue;
            }
            if (!TryGetVariant(orig, info, slot, out var v))
            {
                if (Postponed) complete = false;
                if (m == null) { mats[i] = orig; changed = true; } // don't leave a destroyed material on the renderer
                continue;
            }
            SetTint(v, color.Value);
            if (m == null || v.GetInstanceID() != m.GetInstanceID())
            {
                mats[i] = v;
                changed = true;
                if (entry == null)
                {
                    entry = new Entry { R = r, Slot = slot, Originals = new Material[mats.Length] };
                    Registry[rid] = entry;
                }
                entry.Slot = slot;
                entry.Originals[i] = orig;
            }
        }
        if (changed) r.sharedMaterials = mats;
        if (entry != null && !HasOriginals(entry)) Registry.Remove(rid);
        return complete;
    }

    /// <summary>Applies a colour to the given renderers. Returns false if it should be retried.</summary>
    internal static bool ApplyToRenderers(IList<Renderer> rends, int slot, Color? color)
    {
        bool complete = true;
        for (int i = 0; i < rends.Count; i++)
        {
            var r = rends[i];
            if (r == null) continue;
            try { if (!ApplyToRenderer(r, slot, color)) complete = false; }
            catch (Exception e) { Plugin.Debug($"Recolouring renderer '{r.name}' failed: {e.Message}"); }
        }
        return complete;
    }

    /// <summary>Applies a colour to all renderers under a model. Returns false if it should be retried.</summary>
    internal static bool ApplyToObject(GameObject go, int slot, Color? color) =>
        ApplyToRenderers(go.GetComponentsInChildren<Renderer>(true), slot, color);

    /// <summary>
    /// Puts the original materials back on every renderer that has one of our copies (all slots, or only one), found
    /// through the registry, so inactive pooled employees are included. Must run before any copy is destroyed.
    /// </summary>
    internal static void RestoreRegistry(int onlySlot = -1, bool includePreview = true)
    {
        List<int> done = null;
        foreach (var kv in Registry)
        {
            var e = kv.Value;
            if (onlySlot >= 0 && e.Slot != onlySlot) continue;
            if (!includePreview && e.Slot == PreviewSlot) continue;
            (done ??= new List<int>()).Add(kv.Key);
            if (e.R == null) continue;
            try
            {
                var mats = e.R.sharedMaterials;
                if (mats == null) continue;
                bool changed = false;
                for (int i = 0; i < mats.Length && i < e.Originals.Length; i++)
                {
                    var orig = e.Originals[i];
                    if (orig == null) continue;
                    mats[i] = orig;
                    changed = true;
                }
                if (changed) e.R.sharedMaterials = mats;
            }
            catch (Exception ex) { Plugin.Debug("Restoring a renderer failed: " + ex.Message); }
        }
        if (done != null) foreach (var k in done) Registry.Remove(k);
    }

    internal static bool HasEntries(int slot)
    {
        foreach (var kv in Registry) if (kv.Value.Slot == slot) return true;
        return false;
    }

    /// <summary>Drops registry entries whose renderer was destroyed.</summary>
    internal static void PruneRegistry()
    {
        List<int> dead = null;
        foreach (var kv in Registry) if (kv.Value.R == null) (dead ??= new List<int>()).Add(kv.Key);
        if (dead != null) foreach (var k in dead) Registry.Remove(k);
    }

    // ------------------------------------------------------------------ cleanup

    private static void DestroyVariants()
    {
        RestoreRegistry(); // never destroy a copy that a renderer still shows
        foreach (var kv in Variants) if (kv.Value != null) Object.Destroy(kv.Value);
        Variants.Clear();
        OriginalOf.Clear();
        GarmentOf.Clear();
        LastTint.Clear();
    }

    /// <summary>Destroys every material copy and shade map (callers put the original materials back first).</summary>
    internal static void DestroyAll()
    {
        DestroyVariants();
        foreach (var kv in Neutral) if (kv.Value != null) Object.Destroy(kv.Value);
        Neutral.Clear();
        StreamWaits.Clear();
        Registry.Clear();
        InfoCache.Clear();
    }
}
