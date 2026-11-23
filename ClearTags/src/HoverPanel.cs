using System;
using System.Text;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ClearTags;

/// <summary>
/// Draws the hover text as a small uGUI panel in the game's own menu style, instead of IMGUI.
///
/// How the game styles its UI (read from the shipped assets with UnityPy and from the screenshots of the key-hint list and the
/// Daily Statistics screen; see ClearTags/CLAUDE.md):
///  - the key-hint list ("Interaction Hints Canvas", child of "Leftside Layout", the object behind
///    <c>InteractionHintsManager.m_HintsWindow</c>) is an <c>Image</c> with the sprite "Frame_SpeechBubble01" (a rounded
///    frame), colour #162226 and a black <c>Outline</c>; its texts (the prefab <c>InteractionHintsManager.m_HintTextPrefab</c>)
///    use the TMP font asset "UptownBoy" in white;
///  - the Daily Statistics screen uses the same font, white labels, light-blue (#55BCFF) section headers, green (#53FF81)
///    and red (#FF7153) numbers (<c>DailyStatisticsScreen.m_PositiveTextColor</c> and friends), thin white separator lines.
/// At runtime the panel takes the font from the hint prefab's text (fallback: the statistics screen's day text, then any
/// loaded TMP_FontAsset called "UptownBoy"), and sprite, material, colour and outline from the hint window's Image. The
/// positive colour comes from the statistics screen when that exists. Anything that can't be found falls back to the
/// constants measured from the screenshots, except the font: without it the panel isn't built and the old IMGUI text is
/// used (<see cref="UseFallback"/>).
///
/// Built lazily from the behaviour's Update (never from Plugin.Load), as one screen-space overlay Canvas with a panel
/// that is laid out only when the content (<see cref="HoverData.Signature"/>) or the text size changes (sizes are in 1080p
/// reference units; a CanvasScaler scales them to the real screen); nothing is allocated per frame. The product icon is a RawImage filled by
/// <c>ProductAtlasManager.GetProductIcon</c>, again only when the icon key changes. Furniture boxes use the furniture's
/// sprite (texture + rect) in the same RawImage.
/// </summary>
internal static class HoverPanel
{
    private static readonly Color HintPanel = new(0.086f, 0.133f, 0.149f, 1f);   // #162226
    private static readonly Color Header = new(0.333f, 0.737f, 1f, 1f);           // #55BCFF
    private static readonly Color Green = new(0.325f, 1f, 0.506f, 1f);            // #53FF81
    private static readonly Color White = Color.white;

    private static GameObject _root;
    private static RectTransform _panel;
    private static Image _bg;
    private static RawImage _icon;
    private static RectTransform _iconRt;
    private static TMP_Text _title, _labels, _values;
    private static RectTransform _titleRt, _labelsRt, _valuesRt, _sepRt;
    private static Image _sep;
    private static Color _positive = Green;

    private static int _attempts;
    private static int _errors;
    private static float _nextAttempt;
    private static bool _built;
    private static bool _shown;
    private static string _lastSig;
    private static string _lastIconKey;
    private static float _lastFs;
    private static string _fallbackText;
    private static string _fallbackSig;

    /// <summary>True when the uGUI panel could not be built; the behaviour then draws <see cref="FallbackText"/> with IMGUI.</summary>
    internal static bool UseFallback { get; private set; }

    internal static string FallbackText => _fallbackText;

    /// <summary>Show, update or hide the panel for this frame's hover data (null = hide). Never throws.</summary>
    internal static void Update(HoverData d)
    {
        try
        {
            if (d == null) { Hide(); _fallbackText = null; _fallbackSig = null; return; }
            if (!_built && !UseFallback) TryBuild();
            if (UseFallback)
            {
                string fsig = d.Signature;
                if (fsig != _fallbackSig) { _fallbackSig = fsig; _fallbackText = Plain(d); }   // rebuilt only when the content changes
                return;
            }
            if (!_built) return;   // waiting for the next attempt
            if (_root == null) { _built = false; return; }   // destroyed (scene change): build again next frame

            float fs = Mathf.Max(8f, Mathf.Round(Plugin.HoverTextSize.Value));   // reference units (1080p); the CanvasScaler does the rest
            string sig = d.Signature;
            if (!_shown || sig != _lastSig || !Mathf.Approximately(fs, _lastFs))
            {
                _panel.gameObject.SetActive(true);   // TMP needs an active object to measure text
                Layout(d, fs);
                _lastSig = sig;
                _lastFs = fs;
            }
            _shown = true;
            _errors = 0;   // only consecutive failures count
        }
        catch (Exception e)
        {
            _errors++;
            if (_errors <= 3) Plugin.Logger.LogWarning($"[ClearTags] hover panel failed ({_errors}/3): {e}");
            _shown = false;
            try { if (_panel != null) _panel.gameObject.SetActive(false); } catch (Exception) { }   // never leave a half-laid-out panel up
            if (_errors >= 3)
            {
                // Give up on uGUI: tear it down and draw the plain IMGUI text from now on.
                UseFallback = true;
                try { if (_root != null) Object.Destroy(_root); } catch (Exception) { }
                _root = null; _built = false; _shown = false;
                _fallbackText = null; _fallbackSig = null;   // filled by the next Update
                Plugin.Logger.LogWarning("[ClearTags] hover panel disabled, using the plain IMGUI text");
            }
        }
    }

    internal static void Hide()
    {
        if (!_shown) return;
        _shown = false;
        try { if (_panel != null) _panel.gameObject.SetActive(false); } catch (Exception) { }
    }

    /// <summary>The same content as one rich-text string (the IMGUI fallback).</summary>
    private static string Plain(HoverData d)
    {
        var sb = new StringBuilder("<b>").Append(d.Title).Append("</b>");
        foreach (var r in d.Rows) sb.Append('\n').Append(r.Label).Append(' ').Append(r.Value);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ build

    private static void TryBuild()
    {
        float now = Time.unscaledTime;
        if (now < _nextAttempt) return;
        _attempts++;
        _nextAttempt = now + 3f;
        try
        {
            Build();
            _built = true;
            _attempts = 0;   // only consecutive failures count
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[ClearTags] hover panel attempt {_attempts} failed: {e.Message}");
            try { if (_root != null) Object.Destroy(_root); } catch (Exception) { }
            _root = null;
            if (_attempts >= 3)
            {
                UseFallback = true;
                Plugin.Logger.LogWarning("[ClearTags] hover panel unavailable, using the plain IMGUI text");
            }
        }
    }

    private static TMP_FontAsset FindFont(out string source)
    {
        source = null;
        try
        {
            if (NoktaSingleton<InteractionHintsManager>.HasInstance)
            {
                var prefab = NoktaSingleton<InteractionHintsManager>.Instance.m_HintTextPrefab;
                var t = prefab != null ? prefab.GetComponentInChildren<TMP_Text>(true) : null;
                if (t != null && t.font != null) { source = "hint prefab text"; return t.font; }
            }
        }
        catch (Exception) { }
        try
        {
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<DailyStatisticsScreen>()))
            {
                var scr = o.TryCast<DailyStatisticsScreen>();
                var t = scr != null ? scr.m_DayText : null;
                if (t != null && t.font != null) { source = "Daily Statistics screen text"; return t.font; }
            }
        }
        catch (Exception) { }
        try
        {
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<TMP_FontAsset>()))
            {
                var f = o.TryCast<TMP_FontAsset>();
                if (f != null && f.name != null && f.name.IndexOf("UptownBoy", StringComparison.OrdinalIgnoreCase) >= 0)
                { source = "loaded font asset " + f.name; return f; }
            }
        }
        catch (Exception) { }
        return null;
    }

    private static Image FindHintImage()
    {
        try
        {
            if (NoktaSingleton<InteractionHintsManager>.HasInstance)
            {
                var m = NoktaSingleton<InteractionHintsManager>.Instance;
                var w = m.m_HintsWindow;
                var img = w != null ? w.GetComponent<Image>() : null;
                if (img != null) return img;
                var vl = m.m_ParentVerticalLayout;
                var child = vl != null ? vl.Find("Interaction Hints Canvas") : null;
                img = child != null ? child.GetComponent<Image>() : null;
                if (img != null) return img;
            }
        }
        catch (Exception) { }
        try
        {
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Image>()))
            {
                var img = o.TryCast<Image>();
                if (img != null && img.gameObject.name == "Interaction Hints Canvas") return img;
            }
        }
        catch (Exception) { }
        return null;
    }

    private static GameObject NewUi(string name, Transform parent)
    {
        var go = new GameObject(name);
        go.AddComponent<RectTransform>();
        if (parent != null) go.transform.SetParent(parent, false);
        return go;
    }

    private static void TopLeft(RectTransform rt)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
    }

    private static TMP_Text NewText(string name, Transform parent, TMP_FontAsset font, Color color, out RectTransform rt)
    {
        var go = NewUi(name, parent);
        rt = go.GetComponent<RectTransform>();
        TopLeft(rt);
        var t = go.AddComponent<TextMeshProUGUI>();
        t.font = font;
        t.color = color;
        t.richText = true;
        t.raycastTarget = false;
        t.textWrappingMode = TextWrappingModes.NoWrap;
        t.overflowMode = TextOverflowModes.Overflow;
        t.alignment = TextAlignmentOptions.TopLeft;
        t.margin = Vector4.zero;
        return t;
    }

    private static void Build()
    {
        var font = FindFont(out var fontSource);
        if (font == null) throw new InvalidOperationException("no TMP font asset found (hint prefab, statistics screen)");

        _shown = false; _lastSig = null; _lastIconKey = null; _lastFs = 0f;   // a rebuilt panel starts empty and hidden

        // Everything is built under an inactive root so TMP's Awake only runs once the font is set.
        _root = new GameObject("ClearTags.HoverCanvas");
        _root.SetActive(false);
        Object.DontDestroyOnLoad(_root);
        _root.AddComponent<RectTransform>();
        var canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 120;
        // Reference resolution 1920x1080, matched on height: sprite border, outline and everything else scale with the screen like
        // the game's own UI, so all sizes below are in 1080p units.
        var scaler = _root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 1f;

        var panelGo = NewUi("Panel", _root.transform);
        _panel = panelGo.GetComponent<RectTransform>();
        _panel.anchorMin = _panel.anchorMax = new Vector2(0.5f, 0.5f);
        _panel.pivot = new Vector2(0.5f, 1f);

        // Background: copy the key-hint window's look.
        _bg = panelGo.AddComponent<Image>();
        _bg.raycastTarget = false;
        _bg.color = HintPanel;
        string bgSource = "plain colour (hint window Image not found)";
        var src = FindHintImage();
        if (src != null)
        {
            try
            {
                if (src.sprite != null)
                {
                    _bg.sprite = src.sprite;
                    _bg.type = src.type;
                    _bg.pixelsPerUnitMultiplier = src.pixelsPerUnitMultiplier;
                    _bg.fillCenter = src.fillCenter;
                }
                var c = src.color;
                if (c.a > 0.5f) _bg.color = c;
                if (src.material != null && src.material != Image.defaultGraphicMaterial) _bg.material = src.material;
                bgSource = $"hint window Image (sprite {(src.sprite != null ? src.sprite.name : "none")}, material {(src.material != null ? src.material.name : "none")}, colour #{ColorUtility.ToHtmlStringRGB(src.color)})";
                var ol = src.GetComponent<Outline>();
                if (ol != null)
                {
                    var mine = panelGo.AddComponent<Outline>();
                    mine.effectColor = ol.effectColor;
                    mine.effectDistance = ol.effectDistance;
                    mine.useGraphicAlpha = ol.useGraphicAlpha;
                    bgSource += $", outline {ol.effectDistance} #{ColorUtility.ToHtmlStringRGBA(ol.effectColor)}";
                }
            }
            catch (Exception e) { bgSource += " (partly copied: " + e.Message + ")"; }
        }

        // Positive colour from the Daily Statistics screen when it exists.
        string posSource = "built-in #53FF81";
        try
        {
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<DailyStatisticsScreen>()))
            {
                var scr = o.TryCast<DailyStatisticsScreen>();
                if (scr == null) continue;
                var pc = scr.m_PositiveTextColor;
                if (pc.a > 0.5f && pc.g > pc.r) { _positive = pc; posSource = "DailyStatisticsScreen.m_PositiveTextColor #" + ColorUtility.ToHtmlStringRGB(pc); }
                break;
            }
        }
        catch (Exception) { }

        var iconGo = NewUi("Icon", _panel);
        _iconRt = iconGo.GetComponent<RectTransform>();
        TopLeft(_iconRt);
        _icon = iconGo.AddComponent<RawImage>();
        _icon.raycastTarget = false;
        _icon.color = Color.white;

        _title = NewText("Title", _panel, font, Header, out _titleRt);
        var sepGo = NewUi("Separator", _panel);
        _sepRt = sepGo.GetComponent<RectTransform>();
        TopLeft(_sepRt);
        _sep = sepGo.AddComponent<Image>();
        _sep.raycastTarget = false;
        _sep.color = new Color(1f, 1f, 1f, 0.9f);
        _labels = NewText("Labels", _panel, font, White, out _labelsRt);
        _values = NewText("Values", _panel, font, White, out _valuesRt);

        _panel.gameObject.SetActive(false);
        _root.SetActive(true);
        Plugin.Info($"hover panel created: font '{font.name}' from {fontSource}; background: {bgSource}; positive colour: {posSource}; header colour #{ColorUtility.ToHtmlStringRGB(Header)} (measured from the Daily Statistics screenshot)");
    }

    // ------------------------------------------------------------------ layout

    private static void Place(RectTransform rt, float x, float y, float w, float h)
    {
        rt.anchoredPosition = new Vector2(x, -y);
        rt.sizeDelta = new Vector2(w, h);
    }

    private static void Layout(HoverData d, float fs)
    {
        float pad = Mathf.Round(fs * 0.65f);
        float gap = Mathf.Round(fs * 0.6f);
        float colGap = Mathf.Round(fs * 0.5f);
        float sepGap = Mathf.Round(fs * 0.3f);
        float sepH = Mathf.Max(1f, Mathf.Round(fs * 0.07f));

        bool hasRows = d.Rows.Count > 0;
        bool hasIcon = SetIcon(d);
        float iconSize = hasIcon ? Mathf.Round(fs * 2.6f) : 0f;

        _title.fontSize = fs * 1.25f;
        _title.text = d.Title ?? "";
        float tw = _title.preferredWidth + 2f, th = _title.preferredHeight;

        float lw = 0f, vw = 0f, rh = 0f;
        if (hasRows)
        {
            var lb = new StringBuilder();
            var vb = new StringBuilder();
            for (int i = 0; i < d.Rows.Count; i++)
            {
                var r = d.Rows[i];
                if (i > 0) { lb.Append('\n'); vb.Append('\n'); }
                lb.Append(r.Label);
                var col = r.Positive ? _positive : White;
                vb.Append("<color=#").Append(ColorUtility.ToHtmlStringRGB(col)).Append('>').Append(r.Value).Append("</color>");
            }
            _labels.fontSize = fs;
            _values.fontSize = fs;
            _labels.text = lb.ToString();
            _values.text = vb.ToString();
            lw = _labels.preferredWidth + 2f;
            vw = _values.preferredWidth + 2f;
            rh = Mathf.Max(_labels.preferredHeight, _values.preferredHeight);
        }
        _labels.gameObject.SetActive(hasRows);
        _values.gameObject.SetActive(hasRows);
        _sep.gameObject.SetActive(hasRows);

        float textW = Mathf.Max(tw, hasRows ? lw + colGap + vw : 0f);
        float textH = th + (hasRows ? sepGap + sepH + sepGap + rh : 0f);
        float contentH = Mathf.Max(iconSize, textH);
        float x0 = pad + (hasIcon ? iconSize + gap : 0f);
        float w = x0 + textW + pad;
        float h = pad * 2f + contentH;

        Place(_panel, 0f, 40f, w, h);   // top centre, 40 units (px at 1080p) below the crosshair
        if (hasIcon) Place(_iconRt, pad, pad + (contentH - iconSize) * 0.5f, iconSize, iconSize);
        _icon.enabled = hasIcon;

        float ty = pad + (contentH - textH) * 0.5f;
        Place(_titleRt, x0, ty, textW, th);
        if (hasRows)
        {
            float ry = ty + th + sepGap;
            Place(_sepRt, x0, ry, textW, sepH);
            ry += sepH + sepGap;
            Place(_labelsRt, x0, ry, lw, rh);
            Place(_valuesRt, x0 + lw + colGap, ry, vw, rh);
        }
    }

    /// <summary>Fills the icon RawImage when the icon key changed. Returns whether there is an icon to show.</summary>
    private static bool SetIcon(HoverData d)
    {
        if (string.IsNullOrEmpty(d.IconKey)) { _lastIconKey = null; return false; }
        if (d.IconKey == _lastIconKey) return _icon.texture != null;
        _icon.texture = null;
        try
        {
            if (d.ProductId > 0 && NoktaSingleton<ProductAtlasManager>.HasInstance)
            {
                _icon.uvRect = new Rect(0f, 0f, 1f, 1f);
                NoktaSingleton<ProductAtlasManager>.Instance.GetProductIcon(d.ProductId, _icon);   // sets texture (atlas) and uvRect
            }
            else if (d.IconSprite != null)
            {
                var tex = d.IconSprite.texture;
                if (tex != null && tex.width > 0 && tex.height > 0)
                {
                    var r = d.IconSprite.textureRect;
                    _icon.texture = tex;
                    _icon.uvRect = new Rect(r.x / tex.width, r.y / tex.height, r.width / tex.width, r.height / tex.height);
                }
            }
        }
        catch (Exception e) { Plugin.Info("icon failed: " + e.Message); _icon.texture = null; }
        bool ok = _icon.texture != null;
        _lastIconKey = ok ? d.IconKey : null;   // a failure is retried at the next layout, not remembered
        return ok;
    }
}
