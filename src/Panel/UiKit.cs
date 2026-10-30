using System.Reflection;
using UnityEngine;

namespace ManagerAgent;

internal static class Palette
{
    // Built from fields (not the constructor), which every Unity build is guaranteed to have.
    private static Color C(float r, float g, float b, float a) => new Color { r = r, g = g, b = b, a = a };

    public static readonly Color Bg = C(0.067f, 0.078f, 0.102f, 0.97f);
    public static readonly Color Card = C(0.106f, 0.122f, 0.157f, 1f);
    public static readonly Color Line = C(0.212f, 0.239f, 0.298f, 1f);
    public static readonly Color Text = C(0.925f, 0.933f, 0.949f, 1f);
    public static readonly Color Muted = C(0.600f, 0.639f, 0.702f, 1f);
    public static readonly Color Faint = C(0.420f, 0.455f, 0.518f, 1f);
    public static readonly Color Accent = C(0.153f, 0.533f, 0.937f, 1f);
    public static readonly Color AccentHover = C(0.290f, 0.627f, 0.988f, 1f);
    public static readonly Color Button = C(0.165f, 0.188f, 0.235f, 1f);
    public static readonly Color ButtonHover = C(0.231f, 0.263f, 0.322f, 1f);
    public static readonly Color Good = C(0.298f, 0.788f, 0.486f, 1f);
    public static readonly Color GoodBg = C(0.118f, 0.290f, 0.188f, 1f);
    public static readonly Color GoodBgHover = C(0.149f, 0.369f, 0.239f, 1f);
    public static readonly Color Warn = C(0.976f, 0.733f, 0.282f, 1f);
    public static readonly Color WarnBg = C(0.322f, 0.239f, 0.078f, 1f);
    public static readonly Color Danger = C(0.937f, 0.388f, 0.388f, 1f);
    public static readonly Color DangerBg = C(0.341f, 0.122f, 0.129f, 1f);
    public static readonly Color DangerBgHover = C(0.431f, 0.153f, 0.161f, 1f);
    public static readonly Color Clear = C(0f, 0f, 0f, 0f);
    // The Agent tab: translucent so it blends into whatever bar it's dropped on.
    public static readonly Color Tab = C(0.02f, 0.03f, 0.05f, 0.42f);
    public static readonly Color TabHover = C(0.02f, 0.03f, 0.05f, 0.62f);
    public static readonly Color TabBorder = C(1f, 1f, 1f, 0.16f);
}

/// <summary>
/// Inline style setters. Properties and values are applied by reflection: if a property or
/// value constructor doesn't exist in a particular game build, that one style is skipped
/// (logged once) instead of the whole plugin failing to compile or the panel breaking.
/// </summary>
internal static class S
{
    private static readonly Dictionary<string, PropertyInfo> Props = new();
    private static readonly HashSet<string> Reported = new();

    /// <summary>Sets style.&lt;prop&gt; from a raw value (float, Color, enum, StyleKeyword).</summary>
    public static void Set(VisualElement v, string prop, object raw)
    {
        if (v == null || raw == null) return;
        try
        {
            var style = v.style;
            if (style == null) return;
            if (!Props.TryGetValue(prop, out var pi))
            {
                pi = style.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance);
                Props[prop] = pi;
            }
            if (pi == null || !pi.CanWrite) { Once(prop, "property not available in this game build"); return; }
            var value = StyleValue.Make(pi.PropertyType, raw);
            if (value == null) { Once(prop, $"can't convert {raw.GetType().Name} to {pi.PropertyType.Name}"); return; }
            pi.SetValue(style, value);
        }
        catch (Exception ex)
        {
            Once(prop, (ex.InnerException ?? ex).Message);
        }
    }

    private static void Once(string prop, string why)
    {
        if (Reported.Add(prop)) AgentLog.Debug($"style.{prop} skipped: {why}");
    }

    public static void Absolute(VisualElement v, float? left = null, float? top = null, float? right = null, float? bottom = null)
    {
        Set(v, "position", "Absolute");
        if (left.HasValue) Set(v, "left", left.Value);
        if (top.HasValue) Set(v, "top", top.Value);
        if (right.HasValue) Set(v, "right", right.Value);
        if (bottom.HasValue) Set(v, "bottom", bottom.Value);
    }

    public static void Size(VisualElement v, float? w, float? h)
    {
        if (w.HasValue) Set(v, "width", w.Value);
        if (h.HasValue) Set(v, "height", h.Value);
    }

    public static void AutoWidth(VisualElement v) => Set(v, "width", "Auto");
    public static void MaxHeight(VisualElement v, float h) => Set(v, "maxHeight", h);
    public static void Bg(VisualElement v, Color c) => Set(v, "backgroundColor", c);
    public static void Fg(VisualElement v, Color c) => Set(v, "color", c);
    public static void Grow(VisualElement v, float g) => Set(v, "flexGrow", g);
    public static void Shrink(VisualElement v, float s) => Set(v, "flexShrink", s);
    public static void Hidden(VisualElement v) => Set(v, "overflow", "Hidden");

    public static void Pad(VisualElement v, float t, float r, float b, float l)
    {
        Set(v, "paddingTop", t);
        Set(v, "paddingRight", r);
        Set(v, "paddingBottom", b);
        Set(v, "paddingLeft", l);
    }

    public static void Pad(VisualElement v, float all) => Pad(v, all, all, all, all);

    public static void Margin(VisualElement v, float t, float r, float b, float l)
    {
        Set(v, "marginTop", t);
        Set(v, "marginRight", r);
        Set(v, "marginBottom", b);
        Set(v, "marginLeft", l);
    }

    public static void Radius(VisualElement v, float r)
    {
        Set(v, "borderTopLeftRadius", r);
        Set(v, "borderTopRightRadius", r);
        Set(v, "borderBottomLeftRadius", r);
        Set(v, "borderBottomRightRadius", r);
    }

    public static void Border(VisualElement v, float w, Color c)
    {
        foreach (var side in new[] { "Top", "Right", "Bottom", "Left" })
        {
            Set(v, $"border{side}Width", w);
            Set(v, $"border{side}Color", c);
        }
    }

    public static void Row(VisualElement v, bool wrap = false)
    {
        Set(v, "flexDirection", "Row");
        Set(v, "alignItems", "Center");
        if (wrap) Set(v, "flexWrap", "Wrap");
    }

    public static void Column(VisualElement v) => Set(v, "flexDirection", "Column");
    public static void JustifyBetween(VisualElement v) => Set(v, "justifyContent", "SpaceBetween");

    public static void Font(VisualElement v, float size, bool bold = false)
    {
        Set(v, "fontSize", size);
        Set(v, "unityFontStyleAndWeight", bold ? "Bold" : "Normal");
    }

    public static void WrapText(VisualElement v) => Set(v, "whiteSpace", "Normal");
    public static void Shown(VisualElement v, bool shown) => Set(v, "display", shown ? "Flex" : "None");

    public static void IgnorePointer(VisualElement v) => Reflect.TrySetEnum(v, "pickingMode", "Ignore");
    public static void CatchPointer(VisualElement v) => Reflect.TrySetEnum(v, "pickingMode", "Position");
}

/// <summary>
/// Builds UI Toolkit style values (StyleLength, StyleFloat, StyleColor, StyleEnum&lt;T&gt;) from
/// raw values using whatever constructor or implicit conversion the interop exposes.
/// </summary>
internal static class StyleValue
{
    private static readonly Dictionary<(Type target, Type raw), Func<object, object>> Converters = new();

    public static object Make(Type target, object raw)
    {
        var rawType = raw.GetType();
        if (target.IsAssignableFrom(rawType)) return raw;
        var key = (target, rawType);
        if (!Converters.TryGetValue(key, out var conv))
        {
            conv = Build(target, rawType);
            Converters[key] = conv;
        }
        return conv?.Invoke(raw);
    }

    private static Func<object, object> Build(Type target, Type rawType)
    {
        var ctors = target.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Where(c => c.GetParameters().Length == 1).ToList();
        var ops = target.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "op_Implicit" && m.ReturnType == target && m.GetParameters().Length == 1).ToList();

        // Enum given by name, e.g. "Absolute" → StyleEnum<Position>, "Auto" → StyleLength(StyleKeyword.Auto).
        // The style's own enum is preferred over the generic StyleKeyword ("None" exists in both).
        if (rawType == typeof(string))
        {
            var candidates = new List<(Type enumType, Func<object, object> make)>();
            foreach (var c in ctors)
            {
                var pt = c.GetParameters()[0].ParameterType;
                if (pt.IsEnum) candidates.Add((pt, e => c.Invoke(new[] { e })));
            }
            foreach (var m in ops)
            {
                var pt = m.GetParameters()[0].ParameterType;
                if (pt.IsEnum && candidates.All(x => x.enumType != pt)) candidates.Add((pt, e => m.Invoke(null, new[] { e })));
            }
            if (candidates.Count == 0) return null;
            candidates = candidates.OrderBy(x => x.enumType.Name == "StyleKeyword" ? 1 : 0).ToList();
            return r =>
            {
                foreach (var (enumType, make) in candidates)
                    if (TryEnum(enumType, (string)r, out var e)) return make(e);
                return null;
            };
        }

        // target(raw) constructor, then implicit operator
        var ctor = ctors.FirstOrDefault(c => c.GetParameters()[0].ParameterType == rawType);
        if (ctor != null) return r => ctor.Invoke(new[] { r });
        var op = ops.FirstOrDefault(m => m.GetParameters()[0].ParameterType == rawType);
        if (op != null) return r => op.Invoke(null, new[] { r });

        // floats for length properties: StyleLength(new Length(v, LengthUnit.Pixel)) — the form known to work in FM26
        if (rawType == typeof(float))
        {
            foreach (var c in ctors)
            {
                var lengthType = c.GetParameters()[0].ParameterType;
                var lengthCtor = lengthType.GetConstructors().FirstOrDefault(lc =>
                {
                    var ps = lc.GetParameters();
                    return ps.Length == 2 && ps[0].ParameterType == typeof(float) && ps[1].ParameterType.IsEnum;
                });
                if (lengthCtor == null) continue;
                var unitType = lengthCtor.GetParameters()[1].ParameterType;
                if (!TryEnum(unitType, "Pixel", out var pixel)) continue;
                return r => c.Invoke(new[] { lengthCtor.Invoke(new[] { r, pixel }) });
            }
        }
        return null;
    }

    private static bool TryEnum(Type enumType, string name, out object value)
    {
        value = null;
        foreach (var n in Enum.GetNames(enumType))
        {
            if (!string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) continue;
            value = Enum.Parse(enumType, n);
            return true;
        }
        return false;
    }
}

internal enum BtnKind { Normal, Primary, Danger, Ghost, Option, OptionChosen }

/// <summary>
/// Builds panel elements and handles their clicks/hover itself (by hit-testing the mouse
/// against element bounds each frame), so it doesn't depend on UI Toolkit's Button class or
/// event callbacks — either of which might be stripped from a given game build.
/// </summary>
internal sealed class UiKit
{
    public sealed class Clickable
    {
        public VisualElement El;
        public Action OnClick;
        public Color Normal, Hover;
        public bool Enabled;
        public bool IsHovered;
    }

    private readonly List<Clickable> _clickables = new();
    public float FontSize = 14f;

    public void Reset() => _clickables.Clear();

    public VisualElement Box()
    {
        var v = new VisualElement();
        S.CatchPointer(v);
        return v;
    }

    public TextElement Text(string s, float scale = 1f, Color? color = null, bool bold = false)
    {
        var t = new TextElement();
        Reflect.TrySet(t, "text", s ?? "");
        S.Font(t, FontSize * scale, bold);
        S.Fg(t, color ?? Palette.Text);
        S.WrapText(t);
        S.CatchPointer(t);
        return t;
    }

    public VisualElement Button(string label, Action onClick, BtnKind kind = BtnKind.Normal, bool enabled = true, float scale = 0.93f)
    {
        var (bg, hover, fg) = kind switch
        {
            BtnKind.Primary => (Palette.Accent, Palette.AccentHover, Palette.Text),
            BtnKind.Danger => (Palette.DangerBg, Palette.DangerBgHover, Palette.Danger),
            BtnKind.Ghost => (Palette.Clear, Palette.ButtonHover, Palette.Muted),
            BtnKind.Option => (Palette.Card, Palette.ButtonHover, Palette.Text),
            BtnKind.OptionChosen => (Palette.GoodBg, Palette.GoodBgHover, Palette.Good),
            _ => (Palette.Button, Palette.ButtonHover, Palette.Text),
        };
        if (!enabled) { hover = bg; fg = Palette.Faint; }

        var b = Box();
        S.Bg(b, bg);
        S.Radius(b, 6);
        bool isOption = kind is BtnKind.Option or BtnKind.OptionChosen;
        S.Pad(b, isOption ? 6 : 7, 11, isOption ? 6 : 7, 11);
        S.Margin(b, 0, isOption ? 0 : 6, 6, 0);
        if (kind is BtnKind.Option) S.Border(b, 1, Palette.Line);
        if (kind is BtnKind.OptionChosen) S.Border(b, 1, Palette.Good);

        var t = Text(label, scale, fg, bold: kind == BtnKind.Primary);
        S.IgnorePointer(t);
        b.Add(t);

        _clickables.Add(new Clickable { El = b, OnClick = onClick, Normal = bg, Hover = hover, Enabled = enabled && onClick != null });
        return b;
    }

    /// <summary>Returns true if a panel button handled the click.</summary>
    public bool Click(float px, float py)
    {
        for (int i = _clickables.Count - 1; i >= 0; i--)
        {
            var c = _clickables[i];
            if (!c.Enabled || !Ui.Contains(c.El, px, py)) continue;
            Guard.Run("panel-click", () => c.OnClick());
            return true;
        }
        return false;
    }

    public void Hover(float px, float py, bool inside)
    {
        foreach (var c in _clickables)
        {
            bool h = inside && c.Enabled && Ui.Contains(c.El, px, py);
            if (h == c.IsHovered) continue;
            c.IsHovered = h;
            S.Bg(c.El, h ? c.Hover : c.Normal);
        }
    }
}
