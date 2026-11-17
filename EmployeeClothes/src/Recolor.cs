using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using UnityEngine;
using Object = UnityEngine.Object;

namespace EmployeeClothes;

/// <summary>
/// Recolors the clothes of a model without touching its mesh, skeleton or animation. See the header of Plugin.cs
/// for the idea: a shade map per source texture (luminance relative to the texture's mean) plus a material copy per
/// (original material, slot) whose color is the chosen color. A slot is an employee type (0..7) or the preview.
/// </summary>
internal static class Recolor
{
    /// <summary>Slot used by the preview models, so dragging the picker never changes the real employees.</summary>
    internal const int PreviewSlot = 100;

    private const float TintScale = 1.45f;   // shade map stores shade / TintScale, the material color multiplies it back
    private const float Contrast = 1.4f;
    private const float ShadeMin = 0.45f;
    private const int MaxSize = 1024;

    // Names that mean "clothes", looked up in "<material name> | <base texture name>". The game's names lie, so the
    // texture name counts too. See "Employee materials" in CLAUDE.md for the verified table of every renderer.
    private static readonly Regex ClothesRx = new(
        @"shirt|pants|jacket|waistcoat|handkerchief|dres|trouser|_top_|_btm_|bouncerbody|helmet|hat_|_hat_|britney",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Never recolored, even when a clothes word also matches: shoes and boots, and everything hair. The materials called
    // Hair_Mat (textures Low_Hair_Tshirt_Mat / Low_Hair_Yaka_Mat) are hair plus the "RESTOCKER" name badge, not a T-shirt
    // (they contain "shirt" in the texture name, which is why this list wins over ClothesRx).
    private static readonly Regex NotClothesRx = new(@"shoe|boot|hair|yaka", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Some clothes materials are atlases that hold other things too (hands, face, hair, badge, boots, belt). For those
    /// only the garment pixels are recolored, chosen by this rule, and the result is a baked texture per color.
    /// </summary>
    private enum RuleKind
    {
        None,
        Blue,     // pixels with a blue hue (denim, a blue knit hat, a blue top): hair, skin, badges and boots are not blue
        Jeans,    // the BritneyBodyMAT atlas of Restocker_05/06: jeans islands (fixed UV layout), skin-colored outline kept
        Guard,    // BouncerBody_MAT: the dark suit panels, not the grey/brown strip with the shoes, the belt or the buckle
    }

    private sealed class Rule
    {
        public RuleKind Kind;
        public float SatMin;
    }

    // BritneyBodyMAT jeans islands (waistband strips, left leg, right leg) in a 1024x1024 top-down layout; the same UV
    // layout is used by both textures (BritneyCOLa of Restocker_05 and Pants of Restocker_06).
    private static readonly float[][] JeansPolys =
    {
        new float[] { 18, 20, 255, 20, 255, 112, 18, 112 },
        new float[] { 0, 100, 350, 100, 350, 240, 305, 690, 145, 695, 110, 560, 55, 420, 0, 270 },
        new float[] { 360, 125, 530, 125, 528, 200, 545, 300, 585, 400, 640, 490, 668, 545, 668, 562, 325, 720, 318, 570, 335, 330 },
    };

    private sealed class Info
    {
        public bool Clothes;
        public float Garment = 1f;   // shade of this garment relative to the chosen color
        public Texture BaseTex;
        public string BaseProp;
        public Rule Rule;            // null = the whole material is clothes (shade map); else baked per color
    }

    // All caches are keyed by GetInstanceID (an interop wrapper's Pointer can be reused after the wrapper is collected).
    private static readonly Dictionary<int, Info> InfoCache = new();                        // original material -> classification
    private static readonly Dictionary<int, Texture2D> Neutral = new();                     // source texture -> shade map (null = failed)
    private static readonly Dictionary<(int, int), Material> Variants = new();              // (original material, slot) -> copy
    private static readonly Dictionary<int, Material> OriginalOf = new();                   // copy -> original material
    private static readonly Dictionary<int, float> GarmentOf = new();                       // copy -> garment shade
    private static readonly Dictionary<int, Atlas> Atlases = new();                         // source texture -> masked pixels (null = failed)
    private static readonly Dictionary<(int, int), Baked> BakedByKey = new();               // (original material, slot) -> its baked texture
    private static readonly Dictionary<int, Baked> BakedOf = new();                         // copy -> baked texture
    private static readonly Dictionary<int, Color> LastTint = new();                        // copy -> color last set (skip no-op SetColor)
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
            if (tex != null && ClothesRx.IsMatch(both) && !NotClothesRx.IsMatch(both))
            {
                info.Clothes = true;
                info.BaseTex = tex;
                info.BaseProp = prop;
                string l = both.ToLowerInvariant();
                info.Rule = RuleFor(l, tex.name ?? "");
                if (Plugin.ShadedGarments.Value) info.Garment = GarmentShade(l);
            }
        }
        catch (Exception e) { Plugin.Debug("Classifying a material failed: " + e.Message); }
        InfoCache[id] = info;
        return info;
    }

    private static Rule RuleFor(string lowerBoth, string texName)
    {
        if (lowerBoth.Contains("britney")) return new Rule { Kind = RuleKind.Jeans };
        if (lowerBoth.Contains("man_02_down_dress")) return new Rule { Kind = RuleKind.Blue, SatMin = 0.30f };   // Baker: jeans + boots + belts
        if (texName.StartsWith("Low_Dress_Mat", StringComparison.OrdinalIgnoreCase)) return new Rule { Kind = RuleKind.Blue, SatMin = 0.25f }; // Restocker_03/05: blue knit hat + hair blob + badge
        if (lowerBoth.Contains("scientist_top")) return new Rule { Kind = RuleKind.Blue, SatMin = 0.30f };       // Janitor, Restocker_05/06: blue top + a red emblem
        if (lowerBoth.Contains("bouncerbody")) return new Rule { Kind = RuleKind.Guard };
        return null;
    }

    private static float GarmentShade(string lowerName)
    {
        if (lowerName.Contains("pants") || lowerName.Contains("trouser") || lowerName.Contains("_btm_") || lowerName.Contains("britney")) return 0.7f;
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

    // ------------------------------------------------------------------ atlases (masked, baked per color)

    private const int AtlasSize = 512;   // working size of an atlas copy (the textures are 1024 or 2048)

    /// <summary>The garment pixels of a source texture plus a copy of all its pixels, at most 512x512.</summary>
    private sealed class Atlas
    {
        public int W, H;
        public byte[] Src;       // RGBA32 sRGB bytes, row 0 = bottom (Texture2D order)
        public int[] Idx;        // pixel indices (in Src order) that are garment
        public byte[] ShadeV;    // per Idx entry: shade / TintScale as a byte (the same value the shade map would hold)
        public bool Fast;        // the Il2Cpp array copy was verified (see CopyOut)
    }

    /// <summary>One baked texture + the material copy that shows it (a slot of an atlas material).</summary>
    private sealed class Baked
    {
        public Atlas A;
        public Texture2D Tex;
        public byte[] Buf;
        public uint Key = uint.MaxValue;   // color (8 bit per channel) and garment shade last baked; skip identical bakes
    }

    private static readonly float[] LinLut = MakeLinLut();
    private const int SrgbLutN = 4096;
    private static readonly byte[] SrgbLut = MakeSrgbLut();
    private static readonly Dictionary<int, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color32>> Scratch = new();

    private static float[] MakeLinLut()
    {
        var t = new float[256];
        for (int i = 0; i < 256; i++)
        {
            float c = i / 255f;
            t[i] = c <= 0.04045f ? c / 12.92f : (float)Math.Pow((c + 0.055f) / 1.055f, 2.4);
        }
        return t;
    }

    private static byte[] MakeSrgbLut()
    {
        var t = new byte[SrgbLutN];
        for (int i = 0; i < SrgbLutN; i++)
        {
            float l = i / (float)(SrgbLutN - 1);
            float c = l <= 0.0031308f ? l * 12.92f : 1.055f * (float)Math.Pow(l, 1.0 / 2.4) - 0.055f;
            t[i] = (byte)Math.Min(255, (int)(c * 255f + 0.5f));
        }
        return t;
    }

    private static void Hsv(byte br, byte bg, byte bb, out float h, out float s, out float v)
    {
        float r = br / 255f, g = bg / 255f, b = bb / 255f;
        float mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b)), d = mx - mn;
        v = mx;
        s = mx > 0f ? d / mx : 0f;
        if (d < 1e-6f) { h = 0f; return; }
        if (mx == r) h = ((g - b) / d) % 6f;
        else if (mx == g) h = (b - r) / d + 2f;
        else h = (r - g) / d + 4f;
        h *= 60f;
        if (h < 0f) h += 360f;
    }

    /// <summary>Marks the pixels inside the polygons (coordinates in a 1024x1024 top-down layout). Even-odd scanline fill.</summary>
    private static bool[] FillPolys(int w, int h, float[][] polys)
    {
        var inside = new bool[w * h];   // top-down: index = yTop * w + x
        var xs = new List<float>(16);
        foreach (var poly in polys)
        {
            int n = poly.Length / 2;
            for (int y = 0; y < h; y++)
            {
                float sy = (y + 0.5f) / h * 1024f;
                xs.Clear();
                for (int i = 0; i < n; i++)
                {
                    float x1 = poly[i * 2], y1 = poly[i * 2 + 1];
                    float x2 = poly[((i + 1) % n) * 2], y2 = poly[((i + 1) % n) * 2 + 1];
                    if ((y1 > sy) != (y2 > sy)) xs.Add(x1 + (sy - y1) * (x2 - x1) / (y2 - y1));
                }
                xs.Sort();
                for (int k = 0; k + 1 < xs.Count; k += 2)
                {
                    int xa = (int)Math.Ceiling(xs[k] / 1024f * w - 0.5f), xb = (int)Math.Ceiling(xs[k + 1] / 1024f * w - 0.5f);
                    if (xa < 0) xa = 0;
                    if (xb > w) xb = w;
                    for (int x = xa; x < xb; x++) inside[y * w + x] = true;
                }
            }
        }
        return inside;
    }

    private static bool Garment(Rule rule, bool[] jeans, int x, int yTop, int w, int h, byte r, byte g, byte b)
    {
        Hsv(r, g, b, out float hue, out float sat, out float val);
        switch (rule.Kind)
        {
            case RuleKind.Blue:
                return hue > 190f && hue < 260f && sat > rule.SatMin;
            case RuleKind.Jeans:
                // inside a jeans island, not black padding, and not the skin-colored piping along the edges
                return jeans[yTop * w + x] && val > 0.06f && !(hue < 50f && sat > 0.2f && val > 0.35f);
            case RuleKind.Guard:
            {
                float u = (x + 0.5f) / w, v = (yTop + 0.5f) / h;
                return !(u > 0.405f && u < 0.655f) && v < 0.64f && sat < 0.30f && val < 0.55f;
            }
        }
        return false;
    }

    /// <summary>Returns the atlas for a source texture, creating it if the time budget allows (null while postponed or failed).</summary>
    private static Atlas GetAtlas(Texture src, Rule rule, out bool postponed)
    {
        postponed = false;
        int key = src.GetInstanceID();
        if (Atlases.TryGetValue(key, out var a)) return a;
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
        try { a = MakeAtlas(src, rule); }
        catch (Exception e)
        {
            Plugin.Warn($"Making a masked copy of texture '{src.name}' failed: {e.Message}");
            a = null;
        }
        Atlases[key] = a;
        ClearMipRequest(src);
        return a;
    }

    private static Atlas MakeAtlas(Texture src, Rule rule)
    {
        var sw = Stopwatch.StartNew();
        int w0 = src.width, h0 = src.height;
        float scale = Math.Min(1f, AtlasSize / (float)Math.Max(w0, h0));
        int tw = Math.Max(4, (int)(w0 * scale)), th = Math.Max(4, (int)(h0 * scale));

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
        Object.Destroy(readable);
        int count = pixels.Length;
        var buf = new byte[count * 4];
        bool fast = CopyOut(pixels, buf);

        bool[] jeans = rule.Kind == RuleKind.Jeans ? FillPolys(tw, th, JeansPolys) : null;
        var idx = new List<int>(count / 3);
        double sum = 0; long used = 0;
        for (int row = 0; row < th; row++)
        {
            int yTop = th - 1 - row;
            for (int x = 0; x < tw; x++)
            {
                int i = row * tw + x, o = i * 4;
                if (!Garment(rule, jeans, x, yTop, tw, th, buf[o], buf[o + 1], buf[o + 2])) continue;
                idx.Add(i);
                float l = (0.299f * buf[o] + 0.587f * buf[o + 1] + 0.114f * buf[o + 2]) / 255f;
                if (l > 0.004f) { sum += l; used++; }
            }
        }
        float mean = used > 0 ? (float)(sum / used) : 0.5f;
        var shade = new byte[idx.Count];
        for (int j = 0; j < shade.Length; j++)
        {
            int o = idx[j] * 4;
            float l = (0.299f * buf[o] + 0.587f * buf[o + 1] + 0.114f * buf[o + 2]) / 255f;
            float sh = Mathf.Clamp(1f + (l - mean) * Contrast, ShadeMin, TintScale);
            shade[j] = (byte)(sh / TintScale * 255f + 0.5f);
        }
        var atlas = new Atlas { W = tw, H = th, Src = buf, Idx = idx.ToArray(), ShadeV = shade, Fast = fast };
        Plugin.Info($"Masked copy of '{src.name}' ({rule.Kind}): {w0}x{h0} -> {tw}x{th}, {atlas.Idx.Length} garment pixels ({100.0 * atlas.Idx.Length / count:0}%), mean {mean:0.000}, {sw.Elapsed.TotalMilliseconds:0.0} ms");
        return atlas;
    }

    /// <summary>
    /// Writes the atlas pixels for this color into the baked texture: garment pixels get color * shade, every other
    /// pixel is the original. Returns false if the time budget ran out (the texture keeps its old color; retry later).
    /// Cheap: one pass over the garment pixels with lookup tables, then SetPixels32 + Apply on at most 512x512.
    /// </summary>
    private static bool Bake(Baked bk, Color c, float garment)
    {
        uint key = (uint)(Mathf.Clamp01(c.r) * 255f + 0.5f)
                 | (uint)(Mathf.Clamp01(c.g) * 255f + 0.5f) << 8
                 | (uint)(Mathf.Clamp01(c.b) * 255f + 0.5f) << 16
                 | (uint)(Mathf.Clamp(garment, 0f, 1f) * 100f + 0.5f) << 24;
        if (bk.Key == key) return true;
        if (Watch.IsRunning && Watch.Elapsed.TotalMilliseconds > _budgetMs)
        {
            Postponed = true;
            return false;
        }
        var a = bk.A;
        float k = TintScale * garment;
        float kr = c.r * k, kg = c.g * k, kb = c.b * k;
        var buf = bk.Buf;
        Buffer.BlockCopy(a.Src, 0, buf, 0, buf.Length);
        var idx = a.Idx; var sh = a.ShadeV;
        const int top = SrgbLutN - 1;
        for (int j = 0; j < idx.Length; j++)
        {
            float l = LinLut[sh[j]];
            int o = idx[j] * 4;
            buf[o] = SrgbLut[(int)(Math.Min(1f, l * kr) * top + 0.5f)];
            buf[o + 1] = SrgbLut[(int)(Math.Min(1f, l * kg) * top + 0.5f)];
            buf[o + 2] = SrgbLut[(int)(Math.Min(1f, l * kb) * top + 0.5f)];
        }
        int n = a.W * a.H;
        if (!Scratch.TryGetValue(n, out var arr) || arr == null)
        {
            arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color32>(n);
            Scratch[n] = arr;
        }
        CopyIn(buf, arr, a.Fast);
        bk.Tex.SetPixels32(arr);
        bk.Tex.Apply(true, false);
        bk.Key = key;
        return true;
    }

    // ------------------------------------------------------------------ material copies

    private static bool TryGetVariant(Material orig, Info info, int slot, out Material variant)
    {
        var key = (orig.GetInstanceID(), slot);
        if (Variants.TryGetValue(key, out variant) && variant != null) return true;
        variant = null;
        if (BakedByKey.TryGetValue(key, out var stale))
        {
            // The copy was destroyed behind our back: drop its texture too.
            if (stale.Tex != null) Object.Destroy(stale.Tex);
            BakedByKey.Remove(key);
        }
        Texture shown;
        Baked baked = null;
        if (info.Rule != null)
        {
            var atlas = GetAtlas(info.BaseTex, info.Rule, out _);
            if (atlas == null) return false;   // postponed or failed
            var tex = new Texture2D(atlas.W, atlas.H, TextureFormat.RGBA32, true, false);
            tex.name = "EC baked " + info.BaseTex.name + " (" + slot + ")";
            tex.hideFlags = HideFlags.HideAndDontSave;
            baked = new Baked { A = atlas, Tex = tex, Buf = new byte[atlas.W * atlas.H * 4] };
            shown = tex;
        }
        else
        {
            var neutral = GetNeutral(info.BaseTex, out _);
            if (neutral == null) return false;   // postponed or failed
            shown = neutral;
        }
        var copy = new Material(orig);
        copy.name = orig.name + " (EC " + slot + ")";
        copy.hideFlags = HideFlags.HideAndDontSave;
        if (copy.HasProperty("_BaseMap")) copy.SetTexture("_BaseMap", shown);
        if (copy.HasProperty("_MainTex")) copy.SetTexture("_MainTex", shown);
        Variants[key] = copy;
        int cid = copy.GetInstanceID();
        OriginalOf[cid] = orig;
        GarmentOf[cid] = info.Garment;
        if (baked != null) { BakedByKey[key] = baked; BakedOf[cid] = baked; }
        variant = copy;
        return true;
    }

    /// <summary>Sets the color on a material copy. Returns false if a baked texture couldn't be updated yet (time budget).</summary>
    private static bool SetTint(Material m, Color c)
    {
        int id = m.GetInstanceID();
        float g = GarmentOf.TryGetValue(id, out var gv) ? gv : 1f;
        if (BakedOf.TryGetValue(id, out var bk)) return Bake(bk, c, g);   // the color is in the texture; _BaseColor stays the original's
        float k = TintScale * g;
        var tint = new Color(c.r * k, c.g * k, c.b * k, 1f);
        if (LastTint.TryGetValue(id, out var last) && last == tint) return true;
        LastTint[id] = tint;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", tint);
        if (m.HasProperty("_Color")) m.SetColor("_Color", tint);
        return true;
    }

    /// <summary>
    /// The material to show on a renderer slot that holds <paramref name="original"/>, for this employee slot and color:
    /// a per-slot copy that is already tinted (shade-map materials) or has the color baked into its texture (atlas
    /// materials). Copies are cached per (original, slot) and re-baked only when the color changes, so calling this
    /// many times a second (a color picker being dragged) is cheap. Returns null if the material isn't clothes, or if
    /// the work was postponed by the time budget (<see cref="Postponed"/> is then true; call again later).
    /// </summary>
    internal static Material GetMaterial(Material original, int slot, Color color)
    {
        if (original == null) return null;
        var info = Classify(original);
        if (!info.Clothes) return null;
        if (!TryGetVariant(original, info, slot, out var v)) return null;
        return SetTint(v, color) ? v : null;
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
            if (!SetTint(v, color.Value))
            {
                // A baked texture couldn't be updated within the time budget: an existing one keeps its previous color
                // for now, a brand-new one has no pixels yet and must not be shown.
                complete = false;
                if (BakedOf.TryGetValue(v.GetInstanceID(), out var pending) && pending.Key == uint.MaxValue)
                {
                    if (m == null) { mats[i] = orig; changed = true; }
                    continue;
                }
            }
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

    /// <summary>Applies a color to the given renderers. Returns false if it should be retried.</summary>
    internal static bool ApplyToRenderers(IList<Renderer> rends, int slot, Color? color)
    {
        bool complete = true;
        for (int i = 0; i < rends.Count; i++)
        {
            var r = rends[i];
            if (r == null) continue;
            try { if (!ApplyToRenderer(r, slot, color)) complete = false; }
            catch (Exception e) { Plugin.Debug($"Recoloring renderer '{r.name}' failed: {e.Message}"); }
        }
        return complete;
    }

    /// <summary>Applies a color to all renderers under a model. Returns false if it should be retried.</summary>
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
        // The baked textures are only shown by the copies destroyed above (and nothing shows a copy after the restore).
        foreach (var kv in BakedByKey) if (kv.Value.Tex != null) Object.Destroy(kv.Value.Tex);
        BakedByKey.Clear();
        BakedOf.Clear();
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
        Atlases.Clear();   // plain managed byte arrays, nothing native to destroy
        Scratch.Clear();
        StreamWaits.Clear();
        Registry.Clear();
        InfoCache.Clear();
    }
}
