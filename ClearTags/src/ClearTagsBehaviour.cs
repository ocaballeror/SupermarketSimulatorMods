using System;
using UnityEngine;

namespace ClearTags;

/// <summary>
/// Per-frame hook (registered into IL2CPP via BasePlugin.AddComponent): finds the hover target in Update and hands it to
/// <see cref="HoverPanel"/> (a uGUI panel in the game's style). Only when that panel can't be built does OnGUI draw the old
/// IMGUI text (a dark box with the text under the crosshair). It also re-checks all boxes
/// every 30 s as a diagnostic safety net for the tag scale (only while VerboseLogging is on; the Box hooks
/// are what normally resize new boxes, a full scan is too heavy to run for nothing).
/// </summary>
public class ClearTagsBehaviour : MonoBehaviour
{
    public ClearTagsBehaviour(IntPtr ptr) : base(ptr) { }

    private const float SweepInterval = 30f;
    private const int PruneThreshold = 500;
    private const float PruneInterval = 120f;

    private string _text;   // IMGUI fallback only
    private float _nextSweep = 5f;
    private float _nextPrune = 60f;
    private int _pruneLive;
    private bool _drawLogged;
    private GUIStyle _style;
    private GUIContent _content;

    private void Update()
    {
        try
        {
            float now = Time.unscaledTime;
            if (Plugin.Active && Plugin.VerboseLogging.Value && now >= _nextSweep)
            {
                _nextSweep = now + SweepInterval;
                TagScaler.ResizeAll("sweep", logMissed: true);
            }
        }
        catch (Exception e) { Plugin.Logger.LogError($"[ClearTags] sweep failed: {e}"); }

        try
        {
            // Boxes come and go through Photon all session; ResizeAll forgets the tags of destroyed ones. Rare, and only when the list has grown.
            float now = Time.unscaledTime;
            if (Plugin.Active && TagScaler.StateCount > Math.Max(PruneThreshold, 2 * _pruneLive) && now >= _nextPrune)
            {
                _nextPrune = now + PruneInterval;
                TagScaler.ResizeAll("prune", logMissed: true);
                _pruneLive = TagScaler.StateCount;   // live tags after pruning: the next prune waits until the list has doubled (a big store never rescans every 120 s)
            }
        }
        catch (Exception e) { Plugin.Logger.LogError($"[ClearTags] prune failed: {e}"); }

        HoverData data = null;
        try { data = HoverInfo.Resolve(); }
        catch (Exception e)
        {
            data = null;
            if (!_hoverErrLogged) { _hoverErrLogged = true; Plugin.Logger.LogError($"[ClearTags] hover failed: {e}"); }
        }
        HoverPanel.Update(data);   // never throws
        _text = HoverPanel.UseFallback ? HoverPanel.FallbackText : null;
    }

    private bool _hoverErrLogged;
    private bool _guiErrLogged;

    private void OnGUI()
    {
        if (_text == null) return;
        try
        {
            if (Event.current.type != EventType.Repaint) return;
            if (!_drawLogged)
            {
                _drawLogged = true;
                Plugin.Info("OnGUI is drawing the hover text (plain fallback, first time)");
            }

            float scale = Screen.height / 1080f;
            int size = Mathf.Max(8, Mathf.RoundToInt(Plugin.HoverTextSize.Value * scale));
            if (_style == null)
            {
                _style = new GUIStyle();
                _style.richText = true;
                _style.wordWrap = false;
                _style.alignment = TextAnchor.UpperLeft;
                _style.normal.textColor = Color.white;
                _content = new GUIContent();
            }
            _style.fontSize = size;
            _content.text = _text;

            var textSize = _style.CalcSize(_content);
            float pad = 8f * scale;
            var rect = new Rect(
                Screen.width / 2f - textSize.x / 2f - pad,
                Screen.height / 2f + 36f * scale,
                textSize.x + pad * 2f,
                textSize.y + pad * 2f);

            var old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.72f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = old;
            GUI.Label(new Rect(rect.x + pad, rect.y + pad, textSize.x + 2f, textSize.y + 2f), _content, _style);
        }
        catch (Exception e)
        {
            if (!_guiErrLogged) { _guiErrLogged = true; Plugin.Logger.LogError($"[ClearTags] OnGUI failed: {e}"); }
        }
    }
}
