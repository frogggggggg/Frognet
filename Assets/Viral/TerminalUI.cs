using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The focus-mode terminal look shared by its screens (RopeRadialMenu, GenomeView): palette,
/// generated sprites, OS terminal fonts, uGUI builders and text that types itself in. Sprites
/// and textures made here are DontSave; whoever asks for one destroys it.
/// </summary>
public static class TerminalUI
{
    // Navy panels, cyan lines, acid green = engaged (matches the focus sweep).
    public static readonly Color Panel = new Color(0.016f, 0.055f, 0.16f, 0.85f);
    public static readonly Color Line = new Color(0.55f, 0.93f, 1f, 1f);
    public static readonly Color Live = new Color(0.62f, 1f, 0.45f, 1f);
    public static readonly Color Text = new Color(0.86f, 0.97f, 1f, 1f);
    public static readonly Color Blood = new Color(1f, 0.28f, 0.34f, 1f);
    public static readonly Color Target = new Color(0.33f, 0.62f, 1f, 1f); // command mode: hover / selection boxes
    public static readonly Color Task = new Color(1f, 0.74f, 0.3f, 1f);    // command mode: target groups (tasks)

    public const float Chamfer = 9f;

    public static string[] DefaultFonts => new[] { "OCR A Extended", "Consolas", "Lucida Console", "Menlo", "Courier New" };

    /// <summary>Text that types itself in after its delay, and again whenever its content changes.</summary>
    public class Typed
    {
        public UnityEngine.UI.Text text;
        public string full = "";
        public float start, delay;

        // retype: type the new value in again (off: a ticking readout, swapped in place once typed).
        public void Set(string value, bool retype = true)
        {
            if (full == value) return;
            full = value;
            if (retype) start = Time.unscaledTime - delay; // without waiting on the open delay again
        }

        public void Tick(float now, float speed)
        {
            int n = Mathf.Clamp(Mathf.FloorToInt((now - start - delay) * speed), 0, full.Length);
            string shown = n >= full.Length ? full : full.Substring(0, n) + "_";
            if (text.text != shown) text.text = shown;
        }
    }

    public static Font Font(string[] names)
    {
        string[] installed = UnityEngine.Font.GetOSInstalledFontNames();
        foreach (string name in names)
            if (System.Array.IndexOf(installed, name) >= 0)
                return UnityEngine.Font.CreateDynamicFontFromOSFont(name, 32);
        return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
    }

    /// <summary>A screen-space overlay canvas scaled against 1080p.</summary>
    public static Canvas Canvas(string name, Transform parent, int order)
    {
        var canvasObject = new GameObject(name);
        canvasObject.transform.SetParent(parent, false);
        var canvas = canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = order;
        var scaler = canvasObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        canvasObject.AddComponent<GraphicRaycaster>();
        return canvas;
    }

    public static RectTransform Rect(string name, Transform parent, Vector2 at, Vector2 size)
    {
        var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = at;
        rect.sizeDelta = size;
        return rect;
    }

    public static T Graphic<T>(string name, Transform parent, Vector2 at, Vector2 size) where T : Graphic =>
        Rect(name, parent, at, size).gameObject.AddComponent<T>();

    public static void Style(UnityEngine.UI.Text t, Font font, int size, Color color, TextAnchor anchor)
    {
        t.font = font;
        t.fontSize = size;
        t.color = color;
        t.alignment = anchor;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.raycastTarget = false;
        t.text = "";
    }

    public static ColorBlock Tints()
    {
        ColorBlock c = ColorBlock.defaultColorBlock;
        c.normalColor = Color.white;
        c.highlightedColor = new Color(1.6f, 1.9f, 2f, 1f);
        c.pressedColor = new Color(0.7f, 0.9f, 1f, 1f);
        c.selectedColor = Color.white;
        c.colorMultiplier = 2f;
        c.fadeDuration = 0.05f;
        return c;
    }

    // ---------------- generated art ----------------

    // Anti-aliased by 4x4 supersampling.
    public static Texture2D Paint(int size, System.Func<float, float, float> alpha)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float a = 0f;
            for (int sy = 0; sy < 4; sy++)
            for (int sx = 0; sx < 4; sx++)
                a += alpha(x + (sx + 0.5f) / 4f, y + (sy + 0.5f) / 4f);
            pixels[y * size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(a / 16f) * 255f));
        }
        tex.SetPixels32(pixels);
        tex.Apply();
        return tex;
    }

    // Rectangle with its top-left and bottom-right corners cut; 9-sliced so it stretches.
    public static Sprite ChamferSprite(bool frame)
    {
        const int S = 32;
        const float width = 1.5f;
        Texture2D tex = Paint(S, (x, y) =>
        {
            float diagTL = (x + (S - y) - Chamfer) * 0.7071f;
            float diagBR = ((S - x) + y - Chamfer) * 0.7071f;
            float d = Mathf.Min(Mathf.Min(Mathf.Min(x, S - x), Mathf.Min(y, S - y)), Mathf.Min(diagTL, diagBR));
            if (d < 0f) return 0f;
            return frame ? (d < width ? 1f : 0f) : 1f;
        });
        const float border = Chamfer + 2f;
        return Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 100f, 0,
                             SpriteMeshType.FullRect, new Vector4(border, border, border, border));
    }

    // Outer: 12 segments plus a tick every 10 degrees inside them. Inner: a thin ring with 4 gaps.
    public static Sprite DialSprite(bool outer)
    {
        const int S = 128;
        const float c = S * 0.5f;
        Texture2D tex = Paint(S, (x, y) =>
        {
            float dx = x - c, dy = y - c;
            float r = Mathf.Sqrt(dx * dx + dy * dy);
            float angle = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg + 180f;
            if (outer)
            {
                bool segment = r > c - 5f && r < c - 1f && Mathf.Repeat(angle, 30f) < 24f;
                float tickArc = Mathf.Abs(Mathf.Repeat(angle + 5f, 10f) - 5f) * Mathf.Deg2Rad * r;
                bool tick = r > c - 13f && r < c - 8f && tickArc < 0.8f;
                return segment || tick ? 1f : 0f;
            }
            bool ring = Mathf.Abs(r - (c - 3f)) < 1.2f && Mathf.Repeat(angle + 20f, 90f) > 40f;
            return ring ? 1f : 0f;
        });
        return Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 100f);
    }

    // A filled disc (frame: just its rim).
    public static Sprite DiscSprite(bool frame)
    {
        const int S = 128;
        const float c = S * 0.5f;
        Texture2D tex = Paint(S, (x, y) =>
        {
            float r = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
            return frame ? (r < c - 1f && r > c - 2.5f ? 1f : 0f) : (r < c - 1f ? 1f : 0f);
        });
        return Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 100f);
    }

    // Input prompt outline: a circle that 9-slices into a pill for wide keys (SPACE, ESC).
    public static Sprite PillSprite()
    {
        const int S = 32;
        const float c = S * 0.5f;
        Texture2D tex = Paint(S, (x, y) =>
        {
            float r = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
            return r < c - 1f && r > c - 2.6f ? 1f : 0f;
        });
        return Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 100f, 0,
                             SpriteMeshType.FullRect, new Vector4(c - 1f, c - 1f, c - 1f, c - 1f));
    }

    // A mouse, outlined, with one button filled: 0 left, 1 right, 2 the wheel, -1 none (move it).
    public static Sprite MouseSprite(int button)
    {
        const int S = 32;
        const float width = 1.6f, left = 8f, right = 24f, bottom = 3f, top = 29f, round = 8f, split = 18f, mid = 16f;
        Texture2D tex = Paint(S, (x, y) =>
        {
            // Rounded rectangle: distance inside it (negative outside).
            float qx = Mathf.Max(left + round - x, x - (right - round), 0f);
            float qy = Mathf.Max(bottom + round - y, y - (top - round), 0f);
            float inside = round - Mathf.Sqrt(qx * qx + qy * qy);
            inside = Mathf.Min(inside, Mathf.Min(x - left, right - x), Mathf.Min(y - bottom, top - y));
            if (inside < 0f) return 0f;
            if (inside < width) return 1f;                                            // outline
            if (y > split && y < split + width) return 1f;                            // buttons' lower edge
            if (y > split && Mathf.Abs(x - mid) < width * 0.5f) return 1f;             // between the buttons
            if (button == 0 && y > split && x < mid) return 1f;
            if (button == 1 && y > split && x > mid) return 1f;
            if (button == 2 && Mathf.Abs(x - mid) < 1.8f && y > split + 2f && y < top - 4f) return 1f;
            return 0f;
        });
        return Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 100f);
    }

    // A plain box outline 'width' pixels thick, 9-sliced so any size keeps the same line.
    public static Sprite BoxSprite(float width)
    {
        const int S = 16;
        Texture2D tex = Paint(S, (x, y) => Mathf.Min(Mathf.Min(x, S - x), Mathf.Min(y, S - y)) < width ? 1f : 0f);
        float border = Mathf.Ceil(width) + 1f;
        return Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 100f, 0,
                             SpriteMeshType.FullRect, new Vector4(border, border, border, border));
    }

    // One dark row in three, tiled vertically.
    public static Texture2D ScanTexture()
    {
        var tex = new Texture2D(1, 3, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Point, hideFlags = HideFlags.DontSave
        };
        tex.SetPixels32(new[] { new Color32(0, 0, 0, 70), new Color32(0, 0, 0, 0), new Color32(0, 0, 0, 0) });
        tex.Apply();
        return tex;
    }
}
