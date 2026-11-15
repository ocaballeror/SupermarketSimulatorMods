using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ExtraComputers;

/// <summary>Makes the computer's shop/box icon: a render of the computer model, or a drawn monitor if
/// rendering doesn't produce anything usable.</summary>
internal static class IconMaker
{
    private const int Size = 256;
    // Background key colour: rendered on magenta, then magenta pixels become transparent. This works
    // even if the render pipeline drops the alpha channel.
    private static readonly Color Key = new(1f, 0f, 1f, 0f);

    internal static Texture2D Render(Transform model)
    {
        if (model == null) return null;
        GameObject root = null, camGo = null, lightGo = null;
        RenderTexture rt = null;
        var prevActive = RenderTexture.active;
        try
        {
            int layer = FreeLayer();
            root = BuildRenderCopy(model, layer);
            if (root == null) return null;

            var b = ModelBoundsWorld(root.transform);
            float radius = Mathf.Max(b.extents.magnitude, 0.05f);

            // Look at the front of the computer (the side the PC camera looks from), a bit from the right and above.
            Vector3 front = Vector3.forward;
            if (ComputerFurniture.HaveFrontDirection)
            {
                var d = ComputerFurniture.CamLocalPos - ComputerFurniture.ScreenLocalPos;
                d.y = 0;
                if (d.sqrMagnitude > 1e-6f) front = d.normalized;
            }
            var dir = Quaternion.AngleAxis(-30f, Vector3.up) * front;
            dir = (dir + Vector3.up * 0.45f).normalized;

            camGo = new GameObject("ExtraComputers.IconCamera");
            var cam = camGo.AddComponent<Camera>();
            cam.enabled = false;
            cam.cullingMask = 1 << layer;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Key;
            cam.fieldOfView = 25f;
            cam.nearClipPlane = 0.01f;
            float dist = radius / Mathf.Sin(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.05f;
            cam.farClipPlane = dist + radius * 4f;
            camGo.transform.position = b.center + dir * dist;
            camGo.transform.LookAt(b.center);

            // The copy sits far below the map, away from the store's lights (and has no lightmaps), so light it
            // ourselves: a key light from the camera's side, only for the render layer.
            lightGo = new GameObject("ExtraComputers.IconLight");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.3f;
            light.color = Color.white;
            light.cullingMask = 1 << layer;
            light.shadows = LightShadows.None;
            lightGo.transform.rotation = Quaternion.LookRotation(-(Quaternion.AngleAxis(-25f, Vector3.up) * dir), Vector3.up);

            rt = new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            cam.targetTexture = rt;
            cam.Render();

            RenderTexture.active = rt;
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
            tex.Apply();
            cam.targetTexture = null;

            if (!KeyOut(tex, out float brightness))
            {
                Plugin.Warn("The computer icon render came out empty; using a drawn icon instead.");
                Object.Destroy(tex);
                return null;
            }
            if (brightness < 0.06f)
            {
                Plugin.Warn($"The computer icon render came out too dark (brightness {brightness:0.00}); using a drawn icon instead.");
                Object.Destroy(tex);
                return null;
            }
            Plugin.Debug($"Computer icon rendered from the model (brightness {brightness:0.00})");
            return tex;
        }
        catch (Exception e)
        {
            Plugin.Warn("Rendering the computer icon failed (" + e.Message + "); using a drawn icon instead.");
            return null;
        }
        finally
        {
            RenderTexture.active = prevActive;
            if (rt != null) { rt.Release(); Object.Destroy(rt); }
            if (camGo != null) Object.Destroy(camGo);
            if (lightGo != null) Object.Destroy(lightGo);
            if (root != null) Object.Destroy(root);
        }
    }

    /// <summary>A plain copy of the visible meshes only (no scripts that could wake up), far below the map.</summary>
    private static GameObject BuildRenderCopy(Transform model, int layer)
    {
        var skip = new HashSet<IntPtr>();
        var lodGroups = model.GetComponentsInChildren<LODGroup>(true);
        for (int g = 0; g < lodGroups.Length; g++)
        {
            var lods = lodGroups[g].GetLODs();
            for (int l = 1; l < lods.Length; l++)
            {
                var rs = lods[l].renderers;
                if (rs == null) continue;
                for (int r = 0; r < rs.Length; r++) if (rs[r] != null) skip.Add(rs[r].Pointer);
            }
        }

        var root = new GameObject("ExtraComputers.IconModel");
        root.transform.position = new Vector3(0f, -5000f, 0f);
        int count = 0;
        var rends = model.GetComponentsInChildren<MeshRenderer>(true);
        for (int i = 0; i < rends.Length; i++)
        {
            var mr = rends[i];
            if (mr == null || !mr.enabled || skip.Contains(mr.Pointer) || !mr.gameObject.activeSelf) continue;
            var mf = mr.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) continue;
            var go = new GameObject(mr.name) { layer = layer };
            go.transform.SetParent(root.transform, false);
            // Same pose relative to the model root as in the model.
            go.transform.localPosition = model.InverseTransformPoint(mr.transform.position);
            go.transform.localRotation = Quaternion.Inverse(model.rotation) * mr.transform.rotation;
            go.transform.localScale = ComputerFurniture.Div(mr.transform.lossyScale, model.lossyScale);
            go.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = mr.sharedMaterials;
            count++;
        }
        if (count == 0) { Object.Destroy(root); return null; }
        root.transform.localScale = model.lossyScale;
        return root;
    }

    private static Bounds ModelBoundsWorld(Transform root)
    {
        var rends = root.GetComponentsInChildren<MeshRenderer>();
        var b = rends[0].bounds;
        for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
        return b;
    }

    private static int FreeLayer()
    {
        for (int i = 31; i >= 8; i--)
            if (string.IsNullOrEmpty(LayerMask.LayerToName(i))) return i;
        return 31;
    }

    private static bool Magentaish(Color32 c) => c.r > 200 && c.g < 60 && c.b > 200;

    /// <summary>Turns the magenta background transparent. Returns false if nothing was drawn.
    /// Only pixels that are exactly the clear colour, plus magenta-ish pixels connected to them (the anti-aliased
    /// edge), are removed, so magenta-ish pixels inside the model survive.</summary>
    private static bool KeyOut(Texture2D tex, out float brightness)
    {
        brightness = 0f;
        var px = tex.GetPixels32();
        var bg = new bool[px.Length];
        var stack = new Stack<int>();
        for (int i = 0; i < px.Length; i++)
        {
            var c = px[i];
            if (c.a < 10 || (c.r >= 250 && c.g <= 5 && c.b >= 250)) { bg[i] = true; stack.Push(i); }
        }
        // Grow the background into connected magenta-ish pixels (the fringe).
        while (stack.Count > 0)
        {
            int i = stack.Pop();
            int x = i % Size, y = i / Size;
            for (int d = 0; d < 4; d++)
            {
                int nx = x + (d == 0 ? 1 : d == 1 ? -1 : 0), ny = y + (d == 2 ? 1 : d == 3 ? -1 : 0);
                if (nx < 0 || ny < 0 || nx >= Size || ny >= Size) continue;
                int n = ny * Size + nx;
                if (bg[n] || !Magentaish(px[n])) continue;
                bg[n] = true;
                stack.Push(n);
            }
        }
        int solid = 0;
        for (int i = 0; i < px.Length; i++)
        {
            if (bg[i]) { px[i] = new Color32(255, 255, 255, 0); continue; }
            var c = px[i];
            c.a = 255;
            px[i] = c;
            solid++;
            brightness += (0.299f * c.r + 0.587f * c.g + 0.114f * c.b) / 255f;
        }
        if (solid < px.Length / 100) return false;
        brightness /= solid;
        tex.SetPixels32(px);
        tex.Apply();
        return true;
    }

    /// <summary>The game's flat computer symbol (sprite icon_device_63, white on transparent), tinted dark so it shows on
    /// the light shop cards and box labels. Sprite textures usually aren't readable, so it's copied through a
    /// RenderTexture.</summary>
    internal static Texture2D FromGameSymbol()
    {
        RenderTexture rt = null;
        var prevActive = RenderTexture.active;
        try
        {
            Sprite sprite = null;
            var all = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Sprite>());
            for (int i = 0; i < all.Length && sprite == null; i++)
                if (all[i] != null && all[i].name == "icon_device_63") sprite = all[i].TryCast<Sprite>();
            if (sprite == null || sprite.texture == null)
            {
                Plugin.Warn("The game's computer symbol (icon_device_63) wasn't found; using the model picture.");
                return null;
            }
            var src = sprite.texture;
            var r = sprite.textureRect;
            rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(src, rt);
            RenderTexture.active = rt;
            int w = Mathf.Max(1, (int)r.width), h = Mathf.Max(1, (int)r.height);
            var glyph = new Texture2D(w, h, TextureFormat.RGBA32, false);
            glyph.ReadPixels(new Rect(r.x, r.y, w, h), 0, 0);
            glyph.Apply();

            // Centre it on a square canvas with a margin, tinted like the drawn icon's frame.
            int side = Mathf.Max(w, h) * 5 / 4;
            var outTex = new Texture2D(side, side, TextureFormat.RGBA32, false);
            var px = new Color32[side * side];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 255, 255, 0);
            var gp = glyph.GetPixels32();
            int ox = (side - w) / 2, oy = (side - h) / 2;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var c = gp[y * w + x];
                    px[(y + oy) * side + x + ox] = new Color32(45, 48, 54, c.a);
                }
            Object.Destroy(glyph);
            outTex.SetPixels32(px);
            outTex.Apply();
            Plugin.Debug($"Computer icon taken from the game's symbol icon_device_63 ({w}x{h})");
            return outTex;
        }
        catch (Exception e)
        {
            Plugin.Warn("Using the game's computer symbol failed (" + e.Message + "); using the model picture.");
            return null;
        }
        finally
        {
            RenderTexture.active = prevActive;
            if (rt != null) RenderTexture.ReleaseTemporary(rt);
        }
    }

    /// <summary>Fallback icon: a simple monitor on a stand with a keyboard.</summary>
    internal static Texture2D Draw()
    {
        var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
        var px = new Color32[Size * Size];
        for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 255, 255, 0);
        var frame = new Color32(45, 48, 54, 255);
        var screenTop = new Color32(70, 140, 220, 255);
        var screenBottom = new Color32(30, 80, 160, 255);
        var keys = new Color32(80, 84, 92, 255);

        void Rect(int x0, int y0, int x1, int y1, Color32 c)
        {
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++) px[y * Size + x] = c;
        }
        // y = 0 is the bottom row.
        Rect(28, 92, 228, 228, frame);                        // monitor body
        for (int y = 104; y < 216; y++)                        // screen with a vertical gradient
        {
            float t = (y - 104) / 112f;
            var c = Color32.Lerp(screenBottom, screenTop, t);
            for (int x = 40; x < 216; x++) px[y * Size + x] = c;
        }
        Rect(116, 60, 140, 92, frame);                         // neck
        Rect(84, 52, 172, 62, frame);                          // foot
        Rect(36, 18, 220, 40, keys);                           // keyboard
        for (int row = 0; row < 2; row++)
            for (int k = 0; k < 14; k++)
                Rect(42 + k * 12, 22 + row * 9, 50 + k * 12, 28 + row * 9, frame);
        tex.SetPixels32(px);
        tex.Apply();
        return tex;
    }
}
