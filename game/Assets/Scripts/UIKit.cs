using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

/// <summary>
/// Shared look + builders for the menu and the in-game HUD.
/// Palette: near-black ink, hazard amber, team blue / team red.
/// </summary>
public static class UIKit
{
    public static Font Font;   // set by MainMenuUI if you assign a custom font

    public static readonly Vector2 C = new Vector2(0.5f, 0.5f);

    public static readonly Color Ink = new Color(0.035f, 0.04f, 0.05f, 0.95f);
    public static readonly Color Amber = new Color(1f, 0.62f, 0.08f, 1f);
    public static readonly Color Blue = new Color(0.22f, 0.55f, 1f, 1f);
    public static readonly Color Red = new Color(0.95f, 0.24f, 0.2f, 1f);
    public static readonly Color Soft = new Color(1f, 1f, 1f, 0.6f);
    public static readonly Color Warn = new Color(1f, 0.55f, 0.45f, 1f);

    static Sprite gradR, gradL, vig;

    // ------------------------------------------------------------ teams

    public static Color TeamColor(int team) { return team == 0 ? Blue : team == 1 ? Red : Color.white; }
    public static string TeamHex(int team) { return ColorUtility.ToHtmlStringRGB(TeamColor(team)); }
    public static string TeamName(int team) { return team == 0 ? "BLUE" : team == 1 ? "RED" : "-"; }

    // ------------------------------------------------------------ generated sprites (no art files needed)

    /// <summary>Opaque on the left, fades to transparent on the right.</summary>
    public static Sprite GradientRight { get { if (gradR == null) gradR = MakeGradient(true); return gradR; } }
    /// <summary>Transparent on the left, opaque on the right.</summary>
    public static Sprite GradientLeft { get { if (gradL == null) gradL = MakeGradient(false); return gradL; } }

    /// <summary>White texture, transparent in the centre, opaque in the corners. Tint it black for a vignette.</summary>
    public static Sprite Vignette
    {
        get
        {
            if (vig != null) return vig;
            const int n = 128;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false);
            t.wrapMode = TextureWrapMode.Clamp;
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = x / (n - 1f) - 0.5f, dy = y / (n - 1f) - 0.5f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy) * 2f;
                    px[y * n + x] = new Color(1f, 1f, 1f, Mathf.SmoothStep(0.1f, 0.8f, d) * 0.9f);
                }
            t.SetPixels(px);
            t.Apply();
            vig = Sprite.Create(t, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
            return vig;
        }
    }

    static Sprite MakeGradient(bool opaqueLeft)
    {
        var t = new Texture2D(256, 1, TextureFormat.RGBA32, false);
        t.wrapMode = TextureWrapMode.Clamp;
        var px = new Color[256];
        for (int x = 0; x < 256; x++)
        {
            float f = x / 255f;
            float a = opaqueLeft ? Mathf.SmoothStep(1f, 0f, f) : Mathf.SmoothStep(0f, 1f, f);
            px[x] = new Color(1f, 1f, 1f, a);
        }
        t.SetPixels(px);
        t.Apply();
        return Sprite.Create(t, new Rect(0, 0, 256, 1), new Vector2(0.5f, 0.5f), 100f);
    }

    // ------------------------------------------------------------ font

    public static Font GetFont()
    {
        if (Font != null) return Font;
        var f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (f == null) f = Resources.GetBuiltinResource<Font>("Arial.ttf");
        Font = f;
        return f;
    }

    // ------------------------------------------------------------ layout helpers

    public static RectTransform Rect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return go.GetComponent<RectTransform>();
    }

    /// <summary>Anchor and pivot are the same point, so pos is the offset from that point.</summary>
    public static void Place(RectTransform rt, Vector2 anchor, Vector2 pos, Vector2 size)
    {
        rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
    }

    public static void Fill(RectTransform rt, float pad = 0f)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(pad, pad);
        rt.offsetMax = new Vector2(-pad, -pad);
    }

    public static void Skew(Component c, float amount)
    {
        if (amount == 0f) return;
        c.gameObject.AddComponent<UISkew>().skew = amount;
    }

    // ------------------------------------------------------------ builders

    public static Image Img(Transform parent, string name, Color color, Sprite sprite = null, bool raycast = false)
    {
        var rt = Rect(name, parent);
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = sprite;
        img.color = color;
        img.raycastTarget = raycast;
        return img;
    }

    public static Text Txt(Transform parent, string s, int size, TextAnchor align, Color color,
                           FontStyle style = FontStyle.Normal, float skew = 0f, bool shadow = true)
    {
        var rt = Rect("Text", parent);
        var t = rt.gameObject.AddComponent<Text>();
        t.font = GetFont();
        t.text = s;
        t.fontSize = size;
        t.alignment = align;
        t.color = color;
        t.fontStyle = style;
        t.raycastTarget = false;
        t.supportRichText = false;
        if (shadow)
        {
            var sh = rt.gameObject.AddComponent<Shadow>();
            sh.effectColor = new Color(0f, 0f, 0f, 0.7f);
            sh.effectDistance = new Vector2(2f, -2f);
        }
        Skew(t, skew);
        return t;
    }

    public static Text Label(Transform parent, string s, int size, TextAnchor align, Vector2 anchor, Vector2 pos,
                             Vector2 box, FontStyle style = FontStyle.Normal, float skew = 0f, Color? color = null)
    {
        var t = Txt(parent, s, size, align, color ?? Color.white, style, skew);
        Place(t.rectTransform, anchor, pos, box);
        return t;
    }

    /// <summary>Angled button. primary = orange, otherwise dark steel with an orange edge.</summary>
    public static Button MakeButton(Transform parent, string label, Vector2 anchor, Vector2 pos, Vector2 size,
                                    bool primary, UnityAction onClick, int fontSize = 32, float skew = 0.2f)
    {
        var rt = Rect("Btn_" + label, parent);
        Place(rt, anchor, pos, size);

        var img = rt.gameObject.AddComponent<Image>();
        img.color = Color.white;
        var btn = rt.gameObject.AddComponent<Button>();
        btn.targetGraphic = img;

        Color normal = primary ? new Color(0.88f, 0.42f, 0.04f, 0.96f) : new Color(0.11f, 0.12f, 0.14f, 0.94f);
        Color hover = primary ? new Color(1f, 0.64f, 0.14f, 1f) : new Color(0.28f, 0.3f, 0.34f, 1f);
        var cb = btn.colors;
        cb.normalColor = normal;
        cb.highlightedColor = hover;
        cb.selectedColor = normal;
        cb.pressedColor = Color.Lerp(normal, Color.black, 0.3f);
        cb.fadeDuration = 0.08f;
        btn.colors = cb;
        Skew(img, skew);

        if (!primary)
        {
            var edge = Img(rt, "Edge", Amber);
            Place(edge.rectTransform, new Vector2(0f, 0.5f), Vector2.zero, new Vector2(8f, size.y));
            Skew(edge, skew);
        }

        var t = Txt(rt, label, fontSize, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold, skew);
        Fill(t.rectTransform);

        btn.onClick.AddListener(onClick);
        return btn;
    }

    /// <summary>Flat dark input with an amber underline (not skewed, so the caret stays straight).</summary>
    public static InputField MakeInput(Transform parent, string placeholder, Vector2 anchor, Vector2 pos, Vector2 size,
                                       int fontSize, TextAnchor align, int limit)
    {
        var rt = Rect("Input", parent);
        Place(rt, anchor, pos, size);
        var img = rt.gameObject.AddComponent<Image>();
        img.color = new Color(1f, 1f, 1f, 0.08f);
        var field = rt.gameObject.AddComponent<InputField>();
        field.targetGraphic = img;

        var line = Img(rt, "Underline", Amber);
        line.rectTransform.anchorMin = new Vector2(0f, 0f);
        line.rectTransform.anchorMax = new Vector2(1f, 0f);
        line.rectTransform.pivot = new Vector2(0.5f, 0f);
        line.rectTransform.anchoredPosition = Vector2.zero;
        line.rectTransform.sizeDelta = new Vector2(0f, 3f);

        var txt = Txt(rt, "", fontSize, align, Color.white, FontStyle.Bold, 0f, false);
        Fill(txt.rectTransform, 12f);
        var ph = Txt(rt, placeholder, fontSize, align, new Color(1f, 1f, 1f, 0.3f), FontStyle.Italic, 0f, false);
        Fill(ph.rectTransform, 12f);

        field.textComponent = txt;
        field.placeholder = ph;
        field.characterLimit = limit;
        return field;
    }
}
