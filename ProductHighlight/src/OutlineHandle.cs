using System;
using System.Collections.Generic;
using EPOOutline;
using UnityEngine;

namespace ProductHighlight;

/// <summary>
/// One outline this mod owns: a child GameObject with its own EPOOutline <see cref="Outlinable"/>, targeting a set of renderers.
///
/// Why our own component and not the game's highlight objects: the game draws every outline with <c>Outlinable</c> (the EPOOutline
/// asset; the main camera has the <c>Outliner</c>). <c>HighlightManager</c> adds an <c>Outlinable</c> to a <c>Highlightable</c>'s
/// GameObject when it highlights it, sets <c>outlineParameters.color</c> to <c>HighlightManager.m_OutlineColor</c> (the green), adds one
/// <c>OutlineTarget(renderer, submesh)</c> per renderer of <c>Highlightable.m_RenderersToHighlight</c> and sets
/// <c>Highlightable.Highlighted</c>; un-highlighting destroys the component again. We do exactly that (same colour, same target
/// construction), but on a separate child object that nobody else touches, so
///   - the game's own highlights (hover, the racked boxes, the crate, <c>HighlightManager.ClearScrollHighlights</c>) never remove ours
///     and ours never disturb theirs,
///   - mods that drive the game's highlights (WholeTruckHighlight uses <c>Highlightable.AddOrRemoveRenderer</c> on a vehicle) can't
///     interfere either,
///   - a whole shelf can be outlined although its <c>Highlightable</c> only covers what the game decided.
/// The child object is destroyed with its parent (display or slot), and by <see cref="Destroy"/>.
/// </summary>
internal sealed class OutlineHandle
{
    public GameObject Go;
    public Outlinable Outlinable;
    /// <summary>Hash of the renderer list the targets were built from (product groups are rebuilt when it changes).</summary>
    public int Signature;
    public int TargetCount;

    public bool Alive => Go != null && Outlinable != null;

    public static OutlineHandle Create(Transform parent, string name, List<Renderer> renderers, int signature, Color color)
    {
        var go = new GameObject(name);
        try
        {
            go.transform.SetParent(parent, false);
            var o = go.AddComponent<Outlinable>();
            var p = o.outlineParameters;
            if (p == null) throw new InvalidOperationException("Outlinable.outlineParameters is null");
            p.enabled = true;
            p.color = color;
            int targets = 0;
            for (int i = 0; i < renderers.Count; i++)
            {
                var r = renderers[i];
                if (r == null) continue;
                int submeshes = Math.Max(1, o.GetSubmeshCount(r));
                for (int s = 0; s < submeshes; s++)
                    if (o.TryAddTarget(new OutlineTarget(r, s))) targets++;
            }
            return new OutlineHandle { Go = go, Outlinable = o, Signature = signature, TargetCount = targets };
        }
        catch (Exception)
        {
            UnityEngine.Object.Destroy(go);
            throw;
        }
    }

    public void Destroy()
    {
        try { if (Go != null) UnityEngine.Object.Destroy(Go); } catch (Exception) { }
        Go = null;
        Outlinable = null;
    }
}
