using System;
using System.Globalization;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Localization.Components;
using UnityEngine.UI;
using __Project__.Scripts.Computer.Management.CustomizationTab;
using __Project__.Scripts.UI;
using __Project__.Scripts.ControllerInputModule;
using ComputerT = __Project__.Scripts.Computer.Computer;
using TabComponent = __Project__.Scripts.Computer.Management.CustomizationTab.CustomizationTab;
using Object = UnityEngine.Object;

namespace EmployeeClothes;

/// <summary>
/// The card in the computer's Customization tab and the panel it opens. Everything is cloned from existing UI (see the
/// header of Plugin.cs): the card from "Store Name UI", texts and buttons from that card's children, the background from
/// the tab's BG, the color picker from the store-name color picker. Layout uses anchor fractions of the tab (695x367 in
/// the shipped scene), so it doesn't depend on the exact size.
/// </summary>
internal static class TabUi
{
    private const string CardName = "Employee Clothes UI";
    private const string PanelName = "Employee Clothes Panel";

    private static readonly Color[] Swatches =
    {
        new(1f, 1f, 1f), new(0.72f, 0.74f, 0.78f), new(0.12f, 0.12f, 0.14f), new(0.85f, 0.12f, 0.12f),
        new(0.95f, 0.50f, 0.10f), new(0.96f, 0.82f, 0.15f), new(0.20f, 0.65f, 0.28f), new(0.10f, 0.62f, 0.62f),
        new(0.15f, 0.40f, 0.85f), new(0.10f, 0.16f, 0.45f), new(0.52f, 0.25f, 0.75f), new(0.92f, 0.45f, 0.70f),
    };

    private static GameObject _holder;     // inactive parent of the templates (nothing in it ever runs Awake)
    private static GameObject _card, _panel;
    private static Transform _btnTpl, _textTpl, _pickerTpl;
    private static Button _cardButton;
    private static TMP_Text _cardCost, _kindText, _infoText, _toggleText;
    private static RawImage _raw;
    private static ColorPicker _picker;

    private static int _kindIdx;
    private static bool _applyAll;
    private static bool _suppress;
    private static bool _open;
    private static readonly Color?[] Pending = new Color?[8];
    private static float _nextFind;
    private static bool _warnedMissing;
    private static bool _buildFailed;     // Build threw once: log it once and stop retrying (until a new scene)
    private static ComputerT _comp;       // the store computer (to notice the player leaving it)
    private static Transform _toggleTpl;   // the game's own ToggleSwitch (HR tab), cloned for "Apply to all"
    private static ToggleSwitch _toggleSwitch;
    private static string _message;       // a one-off line for the info text (cleared when the color or type changes)
    private static Image _toggleImage;
    private static Color _toggleBase = Color.white;
    private static bool _hooked;          // the picker's onColorChanged is hooked (otherwise PerFrame polls its color)
    private static Color _polled;

    internal static bool Injected => _card != null;

    // ------------------------------------------------------------------ injection

    internal static void Ensure(TabComponent tab)
    {
        if (!Plugin.Active || tab == null || _buildFailed) return;
        if (_card != null) return;
        try
        {
            var unlocked = tab.m_UnlockedUI;
            var content = unlocked != null ? unlocked.transform.Find("Sections Scroll View/Viewport/Content") : null;
            var template = content != null ? content.Find("Store Name UI") : null;
            if (content == null || template == null)
            {
                if (!_warnedMissing)
                {
                    _warnedMissing = true;
                    Plugin.Warn("The Customization tab doesn't have the expected layout (Unlocked/Sections Scroll View/Viewport/Content/Store Name UI); no card added.");
                }
                return;
            }
            Build(tab, content, template);
        }
        catch (Exception e)
        {
            Plugin.Error("Adding the Employee Clothes card failed (not retried until the next scene): " + e);
            Remove();
            _buildFailed = true;
        }
    }

    private static void Build(TabComponent tab, Transform content, Transform template)
    {
        // A card or panel left over from an earlier injection into this same tab must not be duplicated.
        var oldCard = content.Find(CardName);
        if (oldCard != null) Object.DestroyImmediate(oldCard.gameObject);
        var oldPanel = tab.transform.Find(PanelName);
        if (oldPanel != null) Object.DestroyImmediate(oldPanel.gameObject);

        _holder = new GameObject("EmployeeClothes.UiHolder");
        _holder.SetActive(false);

        // Templates for texts and buttons: a stripped copy of the store-name card.
        var tpl = Object.Instantiate(template.gameObject, _holder.transform);
        tpl.name = "Template";
        Strip(tpl);
        _btnTpl = tpl.transform.Find("Purcase Button");
        _textTpl = tpl.transform.Find("Title");
        if (_btnTpl == null || _textTpl == null) throw new InvalidOperationException("the store-name card has no 'Purcase Button' or 'Title'");
        var desc = _textTpl.Find("Description");
        if (desc != null) Object.DestroyImmediate(desc.gameObject);
        ResetButton(_btnTpl.GetComponent<Button>(), null);

        // The color picker template (cloned from the store-name color picker when it exists).
        _pickerTpl = FindPickerSource();
        _toggleTpl = MakeToggleTemplate();

        // The card in the grid.
        _card = Object.Instantiate(template.gameObject, _holder.transform);
        _card.name = CardName;
        Strip(_card);
        SetText(_card.transform.Find("Title"), "Employee Clothes");
        SetText(_card.transform.Find("Title/Description"), "Change the color of the employee's uniform");
        FitOneLine(_card.transform.Find("Title/Description"));
        SetText(_card.transform.Find("Cost Info/Cost"), "Cost:");
        _cardCost = _card.transform.Find("Cost Info/Cost Text")?.GetComponent<TMP_Text>();
        var buttonTr = _card.transform.Find("Purcase Button");
        _cardButton = buttonTr != null ? buttonTr.GetComponent<Button>() : null;
        if (_cardButton == null) throw new InvalidOperationException("the card has no button");
        ResetButton(_cardButton, OnCardClicked);
        SetText(buttonTr.Find("Text (TMP)"), "Open");
        MakeGamepadInert(_card);
        _card.transform.SetParent(content, false);
        _card.SetActive(true);
        UpdateCard();
        // The grid (2 fixed columns, cells 300x120, spacing 30) lays the card out as the 4th cell, in the free slot of row 2.
        var contentRt = content.GetComponent<RectTransform>();
        if (contentRt != null) LayoutRebuilder.ForceRebuildLayoutImmediate(contentRt);

        BuildPanel(tab);
        Plugin.Info($"Card added to the Customization tab (color picker {(_picker != null ? "cloned" : "not available: swatches only")})");
    }

    /// <summary>Removes the injected UI (mod switched off or scene change).</summary>
    internal static void Remove()
    {
        try { EndSession(); } catch (Exception) { }
        if (_card != null) Object.Destroy(_card);
        if (_panel != null) Object.Destroy(_panel);
        if (_holder != null) Object.Destroy(_holder);
        Forget();
    }

    /// <summary>A new scene was loaded: the UI went with the old scene.</summary>
    internal static void Forget()
    {
        // Destroy what's still alive (a no-op for objects that went with the old scene), so nothing is left behind
        // to be duplicated by the next injection.
        try
        {
            if (_card != null) Object.Destroy(_card);
            if (_panel != null) Object.Destroy(_panel);
            if (_holder != null) Object.Destroy(_holder);
        }
        catch (Exception) { }
        _card = null; _panel = null; _holder = null; _btnTpl = null; _textTpl = null; _pickerTpl = null;
        _cardButton = null; _cardCost = null; _kindText = null; _infoText = null; _toggleText = null; _toggleImage = null; _toggleTpl = null; _toggleSwitch = null; _raw = null; _picker = null;
        _open = false;
        _warnedMissing = false;
        _buildFailed = false;
        _comp = null;
    }

    private static Transform FindPickerSource()
    {
        try
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<ColorPicker>());
            for (int i = 0; i < all.Length; i++)
            {
                var cp = all[i]?.TryCast<ColorPicker>();
                if (cp == null) continue;
                var parent = cp.transform.parent;
                if (parent != null && parent.name == "Text Color Picker") return cp.transform;
            }
            for (int i = 0; i < all.Length; i++)
            {
                var cp = all[i]?.TryCast<ColorPicker>();
                if (cp != null && cp.gameObject.scene.IsValid() && cp.transform.root.name != "EmployeeClothes.UiHolder"
                    && (cp.transform.parent == null || cp.transform.parent.name != PanelName)) return cp.transform;
            }
        }
        catch (Exception e) { Plugin.Warn("Looking for the color picker failed: " + e.Message); }
        Plugin.Warn("No game color picker found; the panel will offer color swatches only.");
        return null;
    }

    // ------------------------------------------------------------------ the panel

    private static void BuildPanel(TabComponent tab)
    {
        var tabRt = tab.transform as RectTransform;
        float tw = 695f, th = 367f;
        if (tabRt != null && tabRt.rect.width > 50f && tabRt.rect.height > 50f) { tw = tabRt.rect.width; th = tabRt.rect.height; }

        _panel = new GameObject(PanelName);
        _panel.layer = tab.gameObject.layer;
        var prt = _panel.AddComponent<RectTransform>();
        _panel.transform.SetParent(tab.transform, false);
        Anchors(prt, 0f, 0f, 1f, 1f);

        var bgSrc = tab.transform.Find("BG");
        if (bgSrc != null)
        {
            var bg = Object.Instantiate(bgSrc.gameObject, _panel.transform);
            bg.name = "BG";
            Anchors(bg.GetComponent<RectTransform>(), 0f, 0f, 1f, 1f);
        }

        var title = MakeText(_panel.transform, "Title", "Employee clothes: pick a color", 0.02f, 0.90f, 0.78f, 0.99f, TextAlignmentOptions.Left, 26f);
        MakeButton(_panel.transform, "Done", "Done", 0.80f, 0.915f, 0.98f, 0.985f, () => EndSession("Done"));

        MakeButton(_panel.transform, "Prev", "<", 0.02f, 0.78f, 0.09f, 0.89f, () => ShowKind(_kindIdx - 1));
        _kindText = MakeText(_panel.transform, "Kind", "", 0.10f, 0.78f, 0.53f, 0.89f, TextAlignmentOptions.Center, 24f);
        MakeButton(_panel.transform, "Next", ">", 0.54f, 0.78f, 0.61f, 0.89f, () => ShowKind(_kindIdx + 1));

        var rawGo = new GameObject("Preview");
        rawGo.layer = _panel.layer;
        var rawRt = rawGo.AddComponent<RectTransform>();
        rawGo.transform.SetParent(_panel.transform, false);
        Anchors(rawRt, 0.02f, 0.22f, 0.61f, 0.77f);
        _raw = rawGo.AddComponent<RawImage>();
        _raw.color = Color.white;
        _raw.raycastTarget = false;

        _infoText = MakeText(_panel.transform, "Info", "", 0.02f, 0.135f, 0.61f, 0.21f, TextAlignmentOptions.Left, 16f);
        if (!BuildToggle())
        {
            // No game toggle to copy: a text button that looks like a checkbox (tinted green while on).
            var toggle = MakeButton(_panel.transform, "Apply to all", "[ ] Apply to all", 0.02f, 0.03f, 0.31f, 0.125f, OnToggleAll);
            _toggleText = toggle.GetComponentInChildren<TMP_Text>(true);
            _toggleImage = toggle.image;
            if (_toggleImage != null) _toggleBase = _toggleImage.color;
        }
        MakeButton(_panel.transform, "Reset", "Reset this type", 0.33f, 0.03f, 0.61f, 0.125f, OnReset);

        // Right column: picker, swatches, apply.
        if (_pickerTpl != null) BuildPicker(tw, th);
        for (int i = 0; i < Swatches.Length; i++) MakeSwatch(_panel.transform, i);
        MakeButton(_panel.transform, "Apply", "Apply", 0.64f, 0.03f, 0.98f, 0.125f, OnApply);

        _panel.AddComponent<PanelWatcher>();   // ends the session at once when the panel gets deactivated (tab switch)
        _panel.SetActive(false);
    }

    /// <summary>The panel was deactivated (its tab was hidden, the screen closed, ...): the session is over.</summary>
    internal static void OnPanelDisabled()
    {
        if (_open) EndSession("panel disabled");
    }

    /// <summary>
    /// Finds one of the game's own switches (ToggleSwitch: a Slider with a handle button and on/off colors, used for the
    /// working / not-working switch of the HR employee cards and in the licence items) and keeps a stripped copy as a
    /// template. The HR one (51x18) is preferred over the big licence one. Prefab assets referenced by loaded objects
    /// are found by FindObjectsOfTypeAll even when no HR card is on screen; if none is loaded the text button is used.
    /// </summary>
    private static Transform MakeToggleTemplate()
    {
        try
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<ToggleSwitch>());
            ToggleSwitch best = null;
            for (int i = 0; i < all.Length; i++)
            {
                var ts = all[i]?.TryCast<ToggleSwitch>();
                if (ts == null || ts.m_toggleSlider == null || ts.m_handle == null) continue;
                var rt = ts.transform as RectTransform;
                float w = rt != null ? rt.rect.width : 0f;
                if (w > 20f && w < 80f) { best = ts; break; }
                best ??= ts;
            }
            if (best == null) { Plugin.Warn("No game ToggleSwitch is loaded; \"Apply to all\" is a text button."); return null; }
            var copy = Object.Instantiate(best.gameObject, _holder.transform);
            copy.name = "Toggle template";
            // No gamepad glyph and no caption (the HR card's "XP" label); nothing on the copy may act on an employee.
            var gp = copy.GetComponentsInChildren<GamepadSwitch>(true);
            for (int i = 0; i < gp.Length; i++) if (gp[i] != null) Object.DestroyImmediate(gp[i].gameObject);
            var tmps = copy.GetComponentsInChildren<TMP_Text>(true);
            for (int i = 0; i < tmps.Length; i++) if (tmps[i] != null) Object.DestroyImmediate(tmps[i].gameObject);
            var cts = copy.GetComponent<ToggleSwitch>();
            if (cts == null) { Object.DestroyImmediate(copy); return null; }
            cts.onValueChanged = null; // a runtime delegate set by the HR card code; Instantiate doesn't copy it, but be sure
            DisablePersistent(cts.m_handle != null ? cts.m_handle.onClick : null);
            Plugin.Debug($"Using the game's ToggleSwitch '{best.gameObject.name}' (under '{(best.transform.parent != null ? best.transform.parent.name : "-")}') for Apply to all");
            return copy.transform;
        }
        catch (Exception e)
        {
            Plugin.Warn("Cloning the game's toggle failed (" + e.Message + "); \"Apply to all\" is a text button.");
            return null;
        }
    }

    private static void DisablePersistent(UnityEvent ev)
    {
        if (ev == null) return;
        int n = ev.GetPersistentEventCount();
        for (int i = 0; i < n; i++) ev.SetPersistentListenerState(i, UnityEventCallState.Off);
    }

    /// <summary>Puts the cloned game switch and its label in the bottom-left slot. False if there is no template.</summary>
    private static bool BuildToggle()
    {
        if (_toggleTpl == null) return false;
        try
        {
            var go = Object.Instantiate(_toggleTpl.gameObject, _panel.transform);
            go.name = "Apply to all toggle";
            var ts2 = go.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < ts2.Length; i++) ts2[i].gameObject.layer = _panel.layer;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.06f, 0.0775f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.localScale = new Vector3(1.25f, 1.25f, 1f);
            _toggleSwitch = go.GetComponent<ToggleSwitch>();
            _toggleSwitch.onValueChanged = (Il2CppSystem.Action<bool>)OnToggled;
            MakeText(_panel.transform, "Apply to all label", "Apply to all", 0.11f, 0.03f, 0.31f, 0.125f, TextAlignmentOptions.Left, 18f);
            go.SetActive(true);
            _toggleSwitch.SetSlider(false);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Warn("Placing the game's toggle failed (" + e.Message + "); \"Apply to all\" is a text button.");
            _toggleSwitch = null;
            return false;
        }
    }

    private static void BuildPicker(float tw, float th)
    {
        var src = _pickerTpl.gameObject;
        var go = Object.Instantiate(src, _holder.transform);
        go.name = "Color picker";
        var img = go.GetComponent<Image>();
        go.transform.SetParent(_panel.transform, false);
        go.layer = _panel.layer;
        var rt = go.GetComponent<RectTransform>();

        float srcW = 183f, srcH = 307f;
        var srt = src.GetComponent<RectTransform>();
        if (srt != null && srt.rect.width > 10f && srt.rect.height > 10f) { srcW = srt.rect.width; srcH = srt.rect.height; }
        // Right column x 0.64..0.98, y 0.31..0.89 of the tab.
        float regionW = 0.34f * tw, regionH = 0.58f * th;
        float s = Math.Min(regionW / srcW, regionH / srcH);
        rt.anchorMin = rt.anchorMax = new Vector2(0.81f, 0.60f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(srcW * s, srcH * s);

        _picker = go.GetComponent<ColorPicker>();
        // The copy must not share (and later destroy) the source's runtime material: Initialize makes its own.
        _picker.generatedMaterial = null;
        // Give the copy its own material with the picker shader. (Initialize(Color) only draws the color when the image
        // already has that shader; in its wrong-shader branch it makes the material and returns without ApplyColor, so
        // the first color would be missing.) The picker's OnDestroy destroys generatedMaterial.
        var sh = _picker.colorPickerShader != null ? _picker.colorPickerShader : Shader.Find("UI/ColorPicker");
        if (sh != null && img != null)
        {
            _picker.colorPickerShader = sh;
            var mat = new Material(sh);
            mat.hideFlags = HideFlags.HideAndDontSave;
            img.material = mat;
            _picker.generatedMaterial = mat;
        }
        else
        {
            if (img != null) img.material = null;
            Plugin.Warn("The color picker has no shader assigned and 'UI/ColorPicker' wasn't found; use the swatches.");
        }
        try
        {
            _picker.add_onColorChanged((Il2CppSystem.Action<Color>)OnPicked);
            _hooked = true;
        }
        catch (Exception e)
        {
            _hooked = false;
            Plugin.Warn("Hooking the color picker's event failed (" + e.Message + "); polling its color instead.");
        }
        go.SetActive(true);
    }

    private static void MakeSwatch(Transform parent, int i)
    {
        var go = new GameObject("Swatch " + i);
        go.layer = _panel.layer;
        var rt = go.AddComponent<RectTransform>();
        go.transform.SetParent(parent, false);
        float cell = 0.34f / Swatches.Length;
        float x0 = 0.64f + i * cell;
        Anchors(rt, x0, 0.155f, x0 + cell, 0.235f);
        rt.offsetMin = new Vector2(1f, 1f);
        rt.offsetMax = new Vector2(-1f, -1f);
        var img = go.AddComponent<Image>();
        img.color = Swatches[i];
        var b = go.AddComponent<Button>();
        b.targetGraphic = img;
        var c = Swatches[i];
        b.onClick.AddListener((UnityAction)(Action)(() => OnSwatch(c)));
    }

    // ------------------------------------------------------------------ small UI helpers

    private static void Anchors(RectTransform rt, float x0, float y0, float x1, float y1)
    {
        rt.anchorMin = new Vector2(x0, y0);
        rt.anchorMax = new Vector2(x1, y1);
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    private static TMP_Text MakeText(Transform parent, string name, string text, float x0, float y0, float x1, float y1,
        TextAlignmentOptions align, float maxSize)
    {
        var go = Object.Instantiate(_textTpl.gameObject, parent);
        go.name = name;
        go.SetActive(true);
        Anchors(go.GetComponent<RectTransform>(), x0, y0, x1, y1);
        var t = go.GetComponent<TMP_Text>();
        t.text = text;
        t.alignment = align;
        t.enableAutoSizing = true;
        t.fontSizeMin = 6f;
        t.fontSizeMax = maxSize;
        t.raycastTarget = false;
        return t;
    }

    private static Button MakeButton(Transform parent, string name, string label, float x0, float y0, float x1, float y1, Action onClick)
    {
        var go = Object.Instantiate(_btnTpl.gameObject, parent);
        go.name = name;
        go.SetActive(true);
        Anchors(go.GetComponent<RectTransform>(), x0, y0, x1, y1);
        var b = go.GetComponent<Button>();
        ResetButton(b, onClick);
        var lbl = go.GetComponentInChildren<TMP_Text>(true);
        if (lbl != null)
        {
            lbl.text = label;
            lbl.enableAutoSizing = true;
            lbl.fontSizeMin = 6f;
            lbl.fontSizeMax = 20f;
        }
        return b;
    }

    private static void ResetButton(Button b, Action onClick)
    {
        if (b == null) return;
        b.onClick.RemoveAllListeners();
        int n = b.onClick.GetPersistentEventCount();
        for (int i = 0; i < n; i++) b.onClick.SetPersistentListenerState(i, UnityEventCallState.Off);
        if (onClick != null) b.onClick.AddListener((UnityAction)onClick);
    }

    /// <summary>
    /// The copied card keeps the store-name card's GamepadUISelectable. Its persistent event targets belonged to the
    /// original card, so every event is emptied; Submit (A) is wired to open this card instead. Whether the card is
    /// reachable with a gamepad (the tab's selectable parent may have discovered its children before the card
    /// existed) is unverified.
    /// </summary>
    private static void MakeGamepadInert(GameObject card)
    {
        try
        {
            var gs = card.GetComponent<GamepadUISelectable>();
            if (gs == null) return;
            ClearEvent(gs.submitAction);
            ClearEvent(gs.northAction);
            ClearEvent(gs.westAction);
            ClearEvent(gs.backAction);
            ClearEvent(gs.leftTriggerAction);
            ClearEvent(gs.rightTriggerAction);
            ClearEvent(gs.rightJoystickClickAction);
            if (gs.submitAction != null) gs.submitAction.AddListener((UnityAction)(Action)OnCardClicked);
        }
        catch (Exception e) { Plugin.Warn("Making the card's gamepad component inert failed: " + e.Message); }
    }

    private static void ClearEvent(UnityEvent ev)
    {
        if (ev == null) return;
        ev.RemoveAllListeners();
        int n = ev.GetPersistentEventCount();
        for (int i = 0; i < n; i++) ev.SetPersistentListenerState(i, UnityEventCallState.Off);
    }

    /// <summary>
    /// The description box of the store-name card is 200x22 (one line of "Change the apperance of the entrance"); a longer
    /// text shrinks to fit that one line instead of wrapping out of the card.
    /// </summary>
    private static void FitOneLine(Transform t)
    {
        try
        {
            var tmp = t != null ? t.GetComponent<TMP_Text>() : null;
            if (tmp == null) return;
            float size = tmp.fontSize;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.enableAutoSizing = true;
            tmp.fontSizeMax = size;
            tmp.fontSizeMin = Math.Max(6f, size * 0.6f);
        }
        catch (Exception e) { Plugin.Debug("Fitting the card description failed: " + e.Message); }
    }

    private static void SetText(Transform t, string text)
    {
        if (t == null) return;
        var tmp = t.GetComponent<TMP_Text>();
        if (tmp != null) tmp.text = text;
    }

    /// <summary>Removes what would run game logic on a copy: the card's script and the localisation components.</summary>
    private static void Strip(GameObject root)
    {
        var item = root.GetComponent<ChangeNameItem>();
        if (item != null) Object.DestroyImmediate(item);
        var ev = root.GetComponentsInChildren<LocalizeStringEvent>(true);
        for (int i = 0; i < ev.Length; i++) if (ev[i] != null) Object.DestroyImmediate(ev[i]);
        var auto = root.GetComponentsInChildren<LocalizeAutoSize>(true);
        for (int i = 0; i < auto.Length; i++) if (auto[i] != null) Object.DestroyImmediate(auto[i]);
    }

    // ------------------------------------------------------------------ the card

    private static string PriceText() => "$" + Plugin.Price.Value.ToString("0.00", CultureInfo.InvariantCulture);

    private static void UpdateCard()
    {
        if (_cardButton == null) return;
        bool guest = ClothesStore.IsGuest;
        // Like ChangeNameItem.UpdatePurchaseButton / CheckRequirements: the button is disabled while money is short.
        _cardButton.interactable = !guest && !_open && Affordable(Plugin.Price.Value);
        if (_cardCost != null)
        {
            var s = guest ? "Host only" : PriceText();
            if (_cardCost.text != s) _cardCost.text = s;
        }
    }

    /// <summary>
    /// The card was clicked: charge, and open the panel only if the charge worked. The charge is the whole price of a
    /// session (see EndSession); the same click on the card again, after the session ended, charges again.
    /// </summary>
    private static void OnCardClicked()
    {
        bool charged = false;
        float price = 0f;
        try
        {
            if (ClothesStore.IsGuest || _panel == null || _open) return;
            price = Plugin.Price.Value;
            if (!Affordable(price)) { Plugin.Info("Not enough money for Employee Clothes"); UpdateCard(); return; }
            if (price > 0f)
            {
                Charge(price);
                charged = true;
                Plugin.Info($"Charged {PriceText()} for opening Employee Clothes");
            }
            Open();
        }
        catch (Exception e)
        {
            Plugin.Error("Opening Employee Clothes failed: " + e);
            try { EndSession("open failed"); } catch (Exception) { }
            if (charged)
            {
                try
                {
                    NoktaSingleton<MoneyManager>.Instance.MoneyTransition(price, MoneyManager.TransitionType.UPGRADE_COSTS, true);
                    Plugin.Info("Refunded the charge");
                }
                catch (Exception e2) { Plugin.Error("Refunding the charge failed: " + e2.Message); }
            }
        }
    }

    // ------------------------------------------------------------------ the charge (per opening)

    private static bool Affordable(float price)
    {
        if (price <= 0f) return true;
        try { return NoktaSingleton<MoneyManager>.HasInstance && NoktaSingleton<MoneyManager>.Instance.HasMoney(price); }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Takes the money the way the game does when the store name is approved (ChangeNameOverlay.ApproveStoreName):
    /// offline or host MoneyTransition(-price, UPGRADE_COSTS, true); a guest asks the master. Guests can't open the panel
    /// (host only), the branch just mirrors the game.
    /// </summary>
    private static void Charge(float price)
    {
        var mm = NoktaSingleton<MoneyManager>.Instance;
        if (ClothesStore.IsGuest) mm.NetworkMoneyManager.OnMoneyTransition_MasterRequest(-price, MoneyManager.TransitionType.UPGRADE_COSTS, true);
        else mm.MoneyTransition(-price, MoneyManager.TransitionType.UPGRADE_COSTS, true);
    }

    // ------------------------------------------------------------------ opening, switching, applying

    private static void Open()
    {
        for (int i = 0; i < Pending.Length; i++) Pending[i] = ClothesStore.Get((HrEmployeeKind)i);
        _applyAll = false;
        RefreshToggle();
        _open = true;
        _message = null;
        _polled = default;
        _panel.SetActive(true);
        _panel.transform.SetAsLastSibling();
        ShowKind(_kindIdx);
    }

    /// <summary>
    /// Ends the paid session. A session lasts from the (paid) click on the card until: Done, switching to another tab
    /// (the panel is deactivated: PanelWatcher.OnDisable), leaving the computer (Computer.OpenPC set to false, or the
    /// PC camera inactive), a scene change (Forget) or the mod being switched off. The panel is then deactivated for
    /// good: it is only ever activated by a successful, charged click on the card, so it can't come back for free.
    /// </summary>
    internal static void EndSession(string reason = null)
    {
        bool was = _open;
        _open = false;
        try { if (_panel != null && _panel.activeSelf) _panel.SetActive(false); } catch (Exception) { }
        Preview.Hide();
        if (was) Plugin.Debug("Employee Clothes session ended" + (reason != null ? " (" + reason + ")" : ""));
    }

    private static HrEmployeeKind CurrentKind => ClothesStore.Kinds[_kindIdx];

    private static void ShowKind(int idx)
    {
        int n = ClothesStore.Kinds.Length;
        _kindIdx = ((idx % n) + n) % n;
        _message = null;
        _applyAll = false; // "Apply to all" is for one Apply; switching type untick it so a single type can be overridden
        RefreshToggle();
        var kind = CurrentKind;
        var pend = Pending[(int)kind];
        bool ok = Preview.Show(kind, pend);
        if (_raw != null) _raw.texture = ok ? Preview.Texture : null;
        if (_picker != null)
        {
            _suppress = true;
            try { _picker.Initialize(pend ?? new Color(0.20f, 0.45f, 0.90f, 1f)); }
            catch (Exception e) { Plugin.Warn("Setting the color picker failed: " + e.Message); }
            finally { _suppress = false; }
            if (!_hooked) { try { _polled = _picker.color; } catch (Exception) { } }
        }
        RefreshTexts();
    }

    private static void RefreshTexts()
    {
        var kind = CurrentKind;
        SetIfChanged(_kindText, $"{ClothesStore.NameOf(kind)}  ({_kindIdx + 1}/{ClothesStore.Kinds.Length})  - {Preview.ModelCount} model(s)");
        if (_infoText != null)
        {
            var pend = Pending[(int)kind];
            var stored = ClothesStore.Get(kind);
            string s;
            if (_message != null) s = _message;
            else if (!pend.HasValue) s = "Original clothes. Pick a color, then press Apply.";
            else
            {
                string hex = "#" + ClothesStore.Hex(pend.Value);
                bool applied = stored.HasValue && ClothesStore.Hex(stored.Value) == ClothesStore.Hex(pend.Value);
                s = applied ? $"Color {hex} (applied)" : $"Color {hex} (press Apply)";
            }
            _infoText.text = s;
        }
    }

    private static void SetIfChanged(TMP_Text t, string s)
    {
        if (t != null && t.text != s) t.text = s;
    }

    private static void OnToggled(bool on)
    {
        _applyAll = on;
        _message = null;
        RefreshTexts();
    }

    private static void RefreshToggle()
    {
        if (_toggleSwitch != null)
        {
            try { if (_toggleSwitch.isOn != _applyAll) _toggleSwitch.SetSlider(_applyAll); }
            catch (Exception) { }
            return;
        }
        if (_toggleText != null) _toggleText.text = _applyAll ? "[x] Apply to all" : "[ ] Apply to all";
        // Looks like a checkbox: the button is tinted green while it is on.
        if (_toggleImage != null) _toggleImage.color = _applyAll ? new Color(0.35f, 0.85f, 0.45f, 1f) : _toggleBase;
    }

    private static void SetPending(Color c)
    {
        c.a = 1f;
        Pending[(int)CurrentKind] = c;
        _message = null;
        Preview.SetColor(c);
        RefreshTexts();
    }

    private static void OnPicked(Color c)
    {
        if (_suppress || !_open) return;
        SetPending(c);
    }

    private static void OnSwatch(Color c)
    {
        try
        {
            if (!_open) return;
            if (_picker != null)
            {
                _suppress = true;
                try { _picker.Initialize(c); } finally { _suppress = false; }
            }
            SetPending(c);
        }
        catch (Exception e) { Plugin.Warn("Choosing a swatch failed: " + e.Message); }
    }

    private static void OnToggleAll()
    {
        _applyAll = !_applyAll;
        RefreshToggle();
    }

    private static void OnApply()
    {
        try
        {
            var kind = CurrentKind;
            var pend = Pending[(int)kind];
            if (!pend.HasValue) { _message = "Pick a color first."; RefreshTexts(); return; }

            // Info only: say so when every targeted type already has this color (applying again is harmless and free).
            string hex = ClothesStore.Hex(pend.Value);
            bool changes = false;
            foreach (var k in ClothesStore.Kinds)
            {
                if (!_applyAll && k != kind) continue;
                var cur = ClothesStore.Get(k);
                if (!cur.HasValue || ClothesStore.Hex(cur.Value) != hex) changes = true;
            }
            if (!changes) { _message = "Already applied."; RefreshTexts(); return; }

            if (_applyAll)
                foreach (var k in ClothesStore.Kinds) { ClothesStore.Set(k, pend); Pending[(int)k] = pend; }
            else
                ClothesStore.Set(kind, pend);
            ClothesStore.Save();   // saved to the mod's own file at every Apply (and Reset), so closing needs no extra step
            Plugin.Info($"Applied #{hex} to {(_applyAll ? "all employee types" : ClothesStore.NameOf(kind))}");
            Employees.Sweep();
            _applyAll = false;
            RefreshToggle();
            _message = "Applied.";
            RefreshTexts();
        }
        catch (Exception e)
        {
            Plugin.Error("Applying the color failed: " + e);
            _message = "Applying failed, see the log.";
            RefreshTexts();
        }
    }

    private static void OnReset()
    {
        try
        {
            if (_applyAll)
                foreach (var k in ClothesStore.Kinds) { ClothesStore.Set(k, null); Pending[(int)k] = null; }
            else
            {
                ClothesStore.Set(CurrentKind, null);
                Pending[(int)CurrentKind] = null;
            }
            ClothesStore.Save();
            Preview.SetColor(null);
            _message = "Reset to the original clothes.";
            Plugin.Info($"Reset to the original clothes: {(_applyAll ? "all employee types" : ClothesStore.NameOf(CurrentKind))}");
            Employees.Sweep();
            _applyAll = false;
            RefreshToggle();
            RefreshTexts();
        }
        catch (Exception e) { Plugin.Error("Resetting the color failed: " + e); }
    }

    // ------------------------------------------------------------------ per frame / per pass

    /// <summary>Fallback when the picker's event couldn't be hooked: poll its color while the panel is open.</summary>
    internal static void PerFrame()
    {
        if (!_open || _picker == null || _hooked || _suppress) return;
        var c = _picker.color;
        if (Math.Abs(c.r - _polled.r) + Math.Abs(c.g - _polled.g) + Math.Abs(c.b - _polled.b) > 0.002f)
        {
            _polled = c;
            SetPending(c);
        }
    }

    /// <summary>
    /// The player left the computer: Computer.set_OpenPC(false) only deactivates m_PCCam, the screen objects (and so our
    /// panel) stay active, which would keep the preview camera and models running.
    /// </summary>
    private static bool ScreenClosed()
    {
        try
        {
            if (_comp == null)
            {
                var o = Object.FindObjectOfType(Il2CppType.Of<ComputerT>());
                _comp = o != null ? o.TryCast<ComputerT>() : null;
            }
            if (_comp == null) return false;
            var cam = _comp.m_PCCam;
            return !_comp.OpenPC || (cam != null && !cam.activeInHierarchy);
        }
        catch (Exception) { return false; }
    }

    internal static void Tick()
    {
        if (_card == null)
        {
            // The OnEnable patch normally injects the card; this is the fallback.
            if (_buildFailed || Time.unscaledTime < _nextFind) return;
            _nextFind = Time.unscaledTime + 1f;
            if (!NoktaSingleton<IDManager>.HasInstance) return;
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<TabComponent>());
            for (int i = 0; i < all.Length; i++)
            {
                var tab = all[i]?.TryCast<TabComponent>();
                if (tab != null && tab.m_UnlockedUI != null) { Ensure(tab); break; }
            }
            return;
        }
        // Safety net for the immediate hooks (PanelWatcher.OnDisable, the OpenPC patch): the panel got hidden, or the PC
        // camera went inactive, while a session was open: end it (the next one costs again).
        if (_open && (_panel == null || !_panel.activeInHierarchy || ScreenClosed())) EndSession("panel or screen hidden");
        if (_card.activeInHierarchy) UpdateCard();
        if (_open && _infoText != null) RefreshTexts();
    }
}

/// <summary>Sits on the panel: OnDisable fires when the tab is switched away or the screen is hidden.</summary>
public class PanelWatcher : MonoBehaviour
{
    public PanelWatcher(IntPtr ptr) : base(ptr) { }

    private void OnDisable()
    {
        try { TabUi.OnPanelDisabled(); }
        catch (Exception) { }
    }
}
