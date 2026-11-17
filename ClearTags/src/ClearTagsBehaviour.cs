using System;
using UnityEngine;

namespace ClearTags;

/// <summary>
/// Per-frame hook (registered into IL2CPP via BasePlugin.AddComponent): finds the hover target in Update,
/// draws the text in OnGUI (IMGUI: a dark box with the text under the crosshair), and re-checks all boxes
/// every few seconds as a safety net for the tag scale.
/// </summary>
public class ClearTagsBehaviour : MonoBehaviour
{
    public ClearTagsBehaviour(IntPtr ptr) : base(ptr) { }

    private const float SweepInterval = 3f;

    private string _text;
    private float _nextSweep = 2f;
    private bool _drawLogged;
    private GUIStyle _style;
    private GUIContent _content;

    private void Update()
    {
        try
        {
            float now = Time.unscaledTime;
            if (Plugin.Active && now >= _nextSweep)
            {
                _nextSweep = now + SweepInterval;
                TagScaler.ResizeAll("sweep", logMissed: true);
            }
        }
        catch (Exception e) { Plugin.Logger.LogError($"[ClearTags] sweep failed: {e}"); }

        try { _text = HoverInfo.Resolve(); }
        catch (Exception e)
        {
            _text = null;
            if (!_errLogged) { _errLogged = true; Plugin.Logger.LogError($"[ClearTags] hover failed: {e}"); }
        }
    }

    private bool _errLogged;

    private void OnGUI()
    {
        if (_text == null) return;
        try
        {
            if (Event.current.type != EventType.Repaint) return;
            if (!_drawLogged)
            {
                _drawLogged = true;
                Plugin.Logger.LogInfo("[ClearTags] OnGUI is drawing the hover text (first time)");
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
            if (!_errLogged) { _errLogged = true; Plugin.Logger.LogError($"[ClearTags] OnGUI failed: {e}"); }
        }
    }
}
