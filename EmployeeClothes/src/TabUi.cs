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
using TabComponent = __Project__.Scripts.Computer.Management.CustomizationTab.CustomizationTab;
using Object = UnityEngine.Object;

namespace EmployeeClothes;

/// <summary>
/// The card in the computer's Customization tab and the panel it opens. Everything is cloned from existing UI (see the
/// header of Plugin.cs): the card from "Store Name UI", texts and buttons from that card's children, the background from
/// the tab's BG, the colour picker from the store-name colour picker. Layout uses anchor fractions of the tab (695x367 in
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
    private static bool _hooked;          // the picker's onColorChanged is hooked (otherwise PerFrame polls its colour)
    private static Color _polled;

    internal static bool Injected => _card != null;

    // ------------------------------------------------------------------ injection

    internal static void Ensure(TabComponent tab)
    {
        if (!Plugin.Active || tab == null) return;
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
            Plugin.Error("Adding the Employee Clothes card failed: " + e);
            Remove();
        }
    }

    private static void Build(TabComponent tab, Transform content, Transform template)
    {
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

        // The colour picker template (cloned from the store-name colour picker when it exists).
        _pickerTpl = FindPickerSource();

        // The card in the grid.
        _card = Object.Instantiate(template.gameObject, _holder.transform);
        _card.name = CardName;
        Strip(_card);
        SetText(_card.transform.Find("Title"), "Employee Clothes");
        SetText(_card.transform.Find("Title/Description"), "Choose the colour of your employees' clothes.");
        SetText(_card.transform.Find("Cost Info/Cost"), "Cost:");
        _cardCost = _card.transform.Find("Cost Info/Cost Text")?.GetComponent<TMP_Text>();
        var buttonTr = _card.transform.Find("Purcase Button");
        _cardButton = buttonTr != null ? buttonTr.GetComponent<Button>() : null;
        if (_cardButton == null) throw new InvalidOperationException("the card has no button");
        ResetButton(_cardButton, OnCardClicked);
        SetText(buttonTr.Find("Text (TMP)"), "Open");
        _card.transform.SetParent(content, false);
        _card.SetActive(true);
        UpdateCard();

        BuildPanel(tab);
        Plugin.Info($"Card added to the Customization tab (colour picker {(_picker != null ? "cloned" : "not available: swatches only")})");
    }

    /// <summary>Removes the injected UI (mod switched off or scene change).</summary>
    internal static void Remove()
    {
        try { CloseInternal(); } catch (Exception) { }
        if (_card != null) Object.Destroy(_card);
        if (_panel != null) Object.Destroy(_panel);
        if (_holder != null) Object.Destroy(_holder);
        Forget();
    }

    /// <summary>A new scene was loaded: the UI went with the old scene.</summary>
    internal static void Forget()
    {
        _card = null; _panel = null; _holder = null; _btnTpl = null; _textTpl = null; _pickerTpl = null;
        _cardButton = null; _cardCost = null; _kindText = null; _infoText = null; _toggleText = null; _raw = null; _picker = null;
        _open = false;
        _warnedMissing = false;
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
        catch (Exception e) { Plugin.Warn("Looking for the colour picker failed: " + e.Message); }
        Plugin.Warn("No game colour picker found; the panel will offer colour swatches only.");
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

        var title = MakeText(_panel.transform, "Title", "Employee clothes: pick a colour", 0.02f, 0.90f, 0.78f, 0.99f, TextAlignmentOptions.Left, 26f);
        MakeButton(_panel.transform, "Done", "Done", 0.80f, 0.915f, 0.98f, 0.985f, CloseInternal);

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
        var toggle = MakeButton(_panel.transform, "Apply to all", "[ ] Apply to all", 0.02f, 0.03f, 0.31f, 0.125f, OnToggleAll);
        _toggleText = toggle.GetComponentInChildren<TMP_Text>(true);
        MakeButton(_panel.transform, "Reset", "Reset this type", 0.33f, 0.03f, 0.61f, 0.125f, OnReset);

        // Right column: picker, swatches, apply.
        if (_pickerTpl != null) BuildPicker(tw, th);
        for (int i = 0; i < Swatches.Length; i++) MakeSwatch(_panel.transform, i);
        MakeButton(_panel.transform, "Apply", "Apply", 0.64f, 0.03f, 0.98f, 0.125f, OnApply);

        _panel.SetActive(false);
    }

    private static void BuildPicker(float tw, float th)
    {
        var src = _pickerTpl.gameObject;
        var go = Object.Instantiate(src, _holder.transform);
        go.name = "Color picker";
        var img = go.GetComponent<Image>();
        // The source may already have made its own material; the copy must make a fresh one in Initialize.
        if (img != null) img.material = null;
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
        if (_picker.colorPickerShader == null)
        {
            var sh = Shader.Find("UI/ColorPicker");
            if (sh != null) _picker.colorPickerShader = sh;
            else Plugin.Warn("The colour picker has no shader assigned and 'UI/ColorPicker' wasn't found; use the swatches.");
        }
        try
        {
            _picker.add_onColorChanged((Il2CppSystem.Action<Color>)OnPicked);
            _hooked = true;
        }
        catch (Exception e)
        {
            _hooked = false;
            Plugin.Warn("Hooking the colour picker's event failed (" + e.Message + "); polling its colour instead.");
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

    private static string PriceText() => "$" + Plugin.Price.Value.ToString("N0", CultureInfo.InvariantCulture);

    private static void UpdateCard()
    {
        if (_cardButton == null) return;
        bool guest = ClothesStore.IsGuest;
        bool money = true;
        try
        {
            float p = Plugin.Price.Value;
            money = p <= 0f || (NoktaSingleton<MoneyManager>.HasInstance && NoktaSingleton<MoneyManager>.Instance.HasMoney(p));
        }
        catch (Exception) { }
        _cardButton.interactable = !guest && money;
        if (_cardCost != null)
        {
            var s = guest ? "Host only" : PriceText();
            if (_cardCost.text != s) _cardCost.text = s;
        }
    }

    private static void OnCardClicked()
    {
        try
        {
            if (ClothesStore.IsGuest || _panel == null) return;
            float price = Plugin.Price.Value;
            if (price > 0f)
            {
                var mm = NoktaSingleton<MoneyManager>.Instance;
                if (!mm.HasMoney(price)) { Plugin.Info("Not enough money for the Employee Clothes card"); return; }
                // Same charge the game makes for the store name (ChangeNameOverlay.ApproveStoreName): UPGRADE_COSTS.
                mm.MoneyTransition(-price, MoneyManager.TransitionType.UPGRADE_COSTS, true);
                Plugin.Info($"Charged {PriceText()} for opening Employee Clothes");
            }
            Open();
        }
        catch (Exception e) { Plugin.Error("Opening Employee Clothes failed: " + e); }
    }

    // ------------------------------------------------------------------ opening, switching, applying

    private static void Open()
    {
        for (int i = 0; i < Pending.Length; i++) Pending[i] = ClothesStore.Get((HrEmployeeKind)i);
        _applyAll = false;
        RefreshToggle();
        _open = true;
        _polled = default;
        _panel.SetActive(true);
        _panel.transform.SetAsLastSibling();
        ShowKind(_kindIdx);
    }

    private static void CloseInternal()
    {
        _open = false;
        if (_panel != null) _panel.SetActive(false);
        Preview.Hide();
    }

    private static HrEmployeeKind CurrentKind => ClothesStore.Kinds[_kindIdx];

    private static void ShowKind(int idx)
    {
        int n = ClothesStore.Kinds.Length;
        _kindIdx = ((idx % n) + n) % n;
        var kind = CurrentKind;
        var pend = Pending[(int)kind];
        bool ok = Preview.Show(kind, pend);
        if (_raw != null) _raw.texture = ok ? Preview.Texture : null;
        if (_picker != null)
        {
            _suppress = true;
            try { _picker.Initialize(pend ?? new Color(0.20f, 0.45f, 0.90f, 1f)); }
            catch (Exception e) { Plugin.Warn("Setting the colour picker failed: " + e.Message); }
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
            if (!pend.HasValue) s = "Original clothes. Pick a colour, then press Apply.";
            else
            {
                string hex = "#" + ClothesStore.Hex(pend.Value);
                bool applied = stored.HasValue && ClothesStore.Hex(stored.Value) == ClothesStore.Hex(pend.Value);
                s = applied ? $"Colour {hex} (applied)" : $"Colour {hex} (press Apply)";
            }
            _infoText.text = s;
        }
    }

    private static void SetIfChanged(TMP_Text t, string s)
    {
        if (t != null && t.text != s) t.text = s;
    }

    private static void RefreshToggle()
    {
        if (_toggleText != null) _toggleText.text = _applyAll ? "[x] Apply to all" : "[ ] Apply to all";
    }

    private static void SetPending(Color c)
    {
        c.a = 1f;
        Pending[(int)CurrentKind] = c;
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
            var pend = Pending[(int)CurrentKind];
            if (!pend.HasValue) { if (_infoText != null) _infoText.text = "Pick a colour first."; return; }
            if (_applyAll)
                foreach (var k in ClothesStore.Kinds) { ClothesStore.Set(k, pend); Pending[(int)k] = pend; }
            else
                ClothesStore.Set(CurrentKind, pend);
            ClothesStore.Save();
            Plugin.Info($"Applied #{ClothesStore.Hex(pend.Value)} to {(_applyAll ? "all employee types" : ClothesStore.NameOf(CurrentKind))}");
            Employees.Sweep();
            RefreshTexts();
        }
        catch (Exception e) { Plugin.Error("Applying the colour failed: " + e); }
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
            Plugin.Info($"Reset to the original clothes: {(_applyAll ? "all employee types" : ClothesStore.NameOf(CurrentKind))}");
            Employees.Sweep();
            RefreshTexts();
        }
        catch (Exception e) { Plugin.Error("Resetting the colour failed: " + e); }
    }

    // ------------------------------------------------------------------ per frame / per pass

    /// <summary>Fallback when the picker's event couldn't be hooked: poll its colour while the panel is open.</summary>
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

    internal static void Tick()
    {
        if (_card == null)
        {
            // The OnEnable patch normally injects the card; this is the fallback.
            if (Time.unscaledTime < _nextFind) return;
            _nextFind = Time.unscaledTime + 3f;
            if (!NoktaSingleton<IDManager>.HasInstance) return;
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<TabComponent>());
            for (int i = 0; i < all.Length; i++)
            {
                var tab = all[i]?.TryCast<TabComponent>();
                if (tab != null && tab.m_UnlockedUI != null) { Ensure(tab); break; }
            }
            return;
        }
        // The tab or the screen was hidden while the panel was open: close it (it costs again next time).
        if (_open && (_panel == null || !_panel.activeInHierarchy)) CloseInternal();
        if (_card.activeInHierarchy) UpdateCard();
        if (_open && _infoText != null) RefreshTexts();
    }
}
