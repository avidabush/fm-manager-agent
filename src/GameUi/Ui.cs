using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace ManagerAgent;

/// <summary>A UI Toolkit panel's top-level element (panel.visualTree), found via the game's UIDocuments.</summary>
internal sealed class PanelRoot
{
    public VisualElement Root;
    public long Id;
    public float Sorting;
    public string Name;
    /// <summary>The panel's UIDocument roots, lowest sorting first.</summary>
    public readonly List<(VisualElement root, float sorting)> Docs = new();

    /// <summary>
    /// Best element to host the agent panel: the top-most full-size, shown UIDocument root
    /// (so the panel inherits the game's fonts), else the panel root itself.
    /// </summary>
    public VisualElement HostElement
    {
        get
        {
            float panelW = 0;
            try { panelW = Root.layout.width; } catch { }
            foreach (var (root, _) in Docs.OrderByDescending(d => d.sorting))
            {
                float w = 0;
                try { w = root.layout.width; } catch { }
                if (w >= panelW * 0.5f && w > 1 && Ui.IsShownInTree(root)) return root;
            }
            return Root;
        }
    }
}

/// <summary>Helpers for reading the game's UI Toolkit tree safely from IL2CPP.</summary>
internal static class Ui
{
    private static readonly Regex Tags = new("<[^>]{1,200}>", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
    private static bool _resolvedStyleWorks = true;
    private static int _resolvedStyleFailures;

    // ---------------------------------------------------------------- roots

    private static List<PanelRoot> _roots = new();
    private static double _nextRootRefresh;

    /// <summary>All panel roots, lowest sorting order first (i.e. last = drawn on top).</summary>
    public static List<PanelRoot> Roots
    {
        get
        {
            if (Clock.Now >= _nextRootRefresh) RefreshRoots();
            return _roots;
        }
    }

    public static void RefreshRoots()
    {
        _nextRootRefresh = Clock.Now + 2.0;
        var list = new List<PanelRoot>();
        var byId = new Dictionary<long, PanelRoot>();
        try
        {
            foreach (var doc in FindDocuments())
            {
                if (doc == null) continue;
                VisualElement rve;
                try { rve = doc.rootVisualElement; } catch { continue; }
                if (rve == null) continue;
                var top = TopOf(rve);
                var id = Il2CppUtil.Id(top);
                float sorting = SortingOf(doc);
                if (byId.TryGetValue(id, out var existing))
                {
                    existing.Sorting = Math.Max(existing.Sorting, sorting);
                    existing.Docs.Add((rve, sorting));
                    continue;
                }
                string name = "?";
                try { name = doc.gameObject.name; } catch { }
                var pr = new PanelRoot { Root = top, Id = id, Sorting = sorting, Name = name };
                pr.Docs.Add((rve, sorting));
                byId[id] = pr;
                list.Add(pr);
            }
        }
        catch (Exception ex) { Guard.Report("roots", ex); }
        list.Sort((a, b) => a.Sorting.CompareTo(b.Sorting));
        _roots = list;
    }

    private static Func<IEnumerable> _findDocs;

    /// <summary>
    /// All active UIDocuments. Late-bound so it works whichever of Unity's "find objects"
    /// APIs this game build exposes (and so the plugin doesn't depend on its exact signature).
    /// </summary>
    private static IEnumerable<UIDocument> FindDocuments()
    {
        if (_findDocs == null)
        {
            var objType = typeof(UnityEngine.Object);
            var statics = objType.GetMethods(BindingFlags.Public | BindingFlags.Static);
            var generic = statics.FirstOrDefault(m => m.Name == "FindObjectsOfType" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            var byType = statics.FirstOrDefault(m => m.Name == "FindObjectsByType" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
            if (generic != null)
            {
                var closed = generic.MakeGenericMethod(typeof(UIDocument));
                _findDocs = () => closed.Invoke(null, null) as IEnumerable;
            }
            else if (byType != null)
            {
                var closed = byType.MakeGenericMethod(typeof(UIDocument));
                var none = Enum.ToObject(closed.GetParameters()[0].ParameterType, 0);
                _findDocs = () => closed.Invoke(null, new[] { none }) as IEnumerable;
            }
            else
            {
                AgentLog.Warn("Can't find Unity's FindObjectsOfType — the agent can't see the game UI.", panel: false);
                _findDocs = () => null;
            }
        }
        var result = _findDocs();
        if (result == null) yield break;
        foreach (var o in result)
            if (o is UIDocument d) yield return d;
    }

    private static float SortingOf(UIDocument doc)
    {
        float s = 0;
        var ps = Reflect.Get(doc, "panelSettings");
        if (Reflect.Get(ps, "sortingOrder") is float f) s = f;
        if (Reflect.Get(doc, "sortingOrder") is float d) s += d / 1000f;
        return s;
    }

    public static VisualElement TopOf(VisualElement ve)
    {
        var cur = ve;
        for (int guard = 0; guard < 128; guard++)
        {
            VisualElement p;
            try { p = cur.parent; } catch { break; }
            if (p == null) break;
            cur = p;
        }
        return cur;
    }

    public static PanelRoot RootOf(VisualElement ve)
    {
        if (ve == null) return null;
        var id = Il2CppUtil.Id(TopOf(ve));
        foreach (var r in Roots) if (r.Id == id) return r;
        return null;
    }

    /// <summary>True while the element is still attached to one of the live panels.</summary>
    public static bool IsAttached(VisualElement ve) => ve != null && RootOf(ve) != null;

    // ---------------------------------------------------------------- tree

    public static int ChildCount(VisualElement ve)
    {
        try { return ve.childCount; } catch { return 0; }
    }

    public static VisualElement Child(VisualElement ve, int i)
    {
        try { return ve[i]; } catch { return null; }
    }

    public static string Name(VisualElement ve)
    {
        try { return ve.name ?? ""; } catch { return ""; }
    }

    /// <summary>Element and ancestors visible (visible flag + display != none).</summary>
    public static bool SelfShown(VisualElement ve)
    {
        try { if (!ve.visible) return false; } catch { }
        return !DisplayNone(ve);
    }

    private static bool DisplayNone(VisualElement ve)
    {
        if (_resolvedStyleWorks)
        {
            try { return ve.resolvedStyle.display == DisplayStyle.None; }
            catch
            {
                if (++_resolvedStyleFailures > 25) _resolvedStyleWorks = false;
            }
        }
        // Fallback: inline style only (misses USS), read late-bound.
        var d = Reflect.Get(ve.style, "display");
        return d != null && Reflect.Get(d, "keyword")?.ToString() == "Undefined" &&
               Reflect.Get(d, "value")?.ToString() == "None";
    }

    public static bool IsShownInTree(VisualElement ve)
    {
        var cur = ve;
        for (int guard = 0; cur != null && guard < 128; guard++)
        {
            if (!SelfShown(cur)) return false;
            try { cur = cur.parent; } catch { break; }
        }
        return true;
    }

    public static bool TryBounds(VisualElement ve, out float x, out float y, out float w, out float h)
    {
        x = y = w = h = 0;
        try
        {
            var r = ve.worldBound;
            x = r.x; y = r.y; w = r.width; h = r.height;
            return !(float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(w) || float.IsNaN(h)) && w > 1f && h > 1f;
        }
        catch { return false; }
    }

    public static bool Contains(VisualElement ve, float px, float py)
    {
        return TryBounds(ve, out var x, out var y, out var w, out var h) && px >= x && px <= x + w && py >= y && py <= y + h;
    }

    public static bool HasClass(VisualElement ve, string cls)
    {
        try { return ve.ClassListContains(cls); } catch { return false; }
    }

    /// <summary>
    /// The element's USS classes. GetClasses() isn't guaranteed to exist in every build,
    /// so it's late-bound; returns empty if unavailable.
    /// </summary>
    public static List<string> Classes(VisualElement ve)
    {
        var result = new List<string>();
        try
        {
            if (!Reflect.TryCall(ve, "GetClasses", out var enumerable) || enumerable == null)
                enumerable = Reflect.Get(ve, "classList") ?? Reflect.Get(ve, "m_ClassList");
            if (enumerable == null) return result;

            // Il2Cpp List<string>: Count + indexer (get_Item).
            var count = Reflect.Get(enumerable, "Count");
            if (count == null && enumerable is Il2CppObjectBase ob)
            {
                var listType = Reflect.FindType("List`1");
                // Fall back to casting IEnumerable<string> proxy to List<string>.
                var closed = listType?.MakeGenericType(typeof(string));
                var asList = closed != null ? Reflect.TryCast(ob, closed) : null;
                if (asList != null) { enumerable = asList; count = Reflect.Get(asList, "Count"); }
            }
            if (count is int n)
            {
                for (int i = 0; i < n && i < 64; i++)
                    if (Reflect.TryCall(enumerable, "get_Item", out var item, i) && item is string s && s.Length > 0)
                        result.Add(s);
            }
        }
        catch { }
        return result;
    }

    // ---------------------------------------------------------------- text

    public static bool IsTextElement(VisualElement ve) => Il2CppUtil.IsA(ve, "TextElement");

    public static string OwnText(VisualElement ve)
    {
        if (!IsTextElement(ve)) return "";
        try
        {
            var te = ve.TryCast<TextElement>();
            return te == null ? "" : Clean(te.text);
        }
        catch { return ""; }
    }

    /// <summary>All visible text under (and including) the element, in tree order.</summary>
    public static string AggregateText(VisualElement root, int maxLen = 800, int maxNodes = 400)
    {
        var sb = new StringBuilder();
        string last = null;
        var stack = new Stack<VisualElement>();
        stack.Push(root);
        int visited = 0;
        while (stack.Count > 0 && visited++ < maxNodes && sb.Length < maxLen)
        {
            var ve = stack.Pop();
            if (ve == null) continue;
            if (visited > 1 && !SelfShown(ve)) continue;
            var t = OwnText(ve);
            if (t.Length > 0 && t != last)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(t);
                last = t;
            }
            int n = ChildCount(ve);
            for (int i = n - 1; i >= 0; i--) stack.Push(Child(ve, i));
        }
        var s = sb.ToString();
        return s.Length > maxLen ? s.Substring(0, maxLen) : s;
    }

    public static string Clean(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = Tags.Replace(s, " ");
        s = s.Replace(' ', ' ').Replace('​', ' ');
        return Spaces.Replace(s, " ").Trim();
    }

    // ---------------------------------------------------------------- interaction heuristics

    private static readonly string[] ClickableTypeHints =
        { "Button", "Navigatable", "Clickable", "Radio", "Toggle", "Selectable", "Option", "Choice", "Response" };

    /// <summary>Best guess at whether an element is something you click.</summary>
    public static bool LooksClickable(VisualElement ve)
    {
        foreach (var n in Il2CppUtil.TypeChain(ve))
        {
            if (n == "VisualElement" || n == "TextElement") break;
            foreach (var hint in ClickableTypeHints)
                if (n.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return HasClass(ve, "si-clickable") || HasClass(ve, "unity-button") || HasClass(ve, "clickable");
    }

    public static VisualElement ClickableAncestorOrSelf(VisualElement ve, int maxUp = 8)
    {
        var cur = ve;
        for (int i = 0; cur != null && i <= maxUp; i++)
        {
            if (LooksClickable(cur)) return cur;
            try { cur = cur.parent; } catch { break; }
        }
        return null;
    }

    public static string Describe(VisualElement ve, int maxText = 60)
    {
        if (ve == null) return "(none)";
        var name = Name(ve);
        var text = AggregateText(ve, maxText + 1, 60);
        if (text.Length > maxText) text = text.Substring(0, maxText) + "…";
        return $"[{Il2CppUtil.TypeName(ve)}{(name.Length > 0 ? " #" + name : "")}] \"{text}\"";
    }
}

/// <summary>
/// Converts between window client pixels, game render pixels and panel (UI) coordinates.
/// Handles letterboxing when the render resolution doesn't match the window.
/// </summary>
internal static class Coords
{
    private static bool Viewport(out float scale, out float offX, out float offY, out float sw, out float sh)
    {
        scale = 1; offX = offY = 0;
        sw = Screen.width; sh = Screen.height;
        if (sw <= 0 || sh <= 0) return false;
        if (!GameWindow.TryClientSize(out var cw, out var ch)) return false;
        scale = Math.Min(cw / sw, ch / sh);
        offX = (cw - sw * scale) / 2f;
        offY = (ch - sh * scale) / 2f;
        return scale > 0;
    }

    public static bool MouseToPanel(VisualElement root, out float px, out float py)
    {
        px = py = 0;
        if (!GameWindow.TryCursorClient(out var cx, out var cy)) return false;
        return ClientToPanel(root, cx, cy, out px, out py);
    }

    public static bool ClientToPanel(VisualElement root, int cx, int cy, out float px, out float py)
    {
        px = py = 0;
        if (!Viewport(out var scale, out var offX, out var offY, out var sw, out var sh)) return false;
        if (!PanelSize(root, out var pw, out var ph)) return false;
        float rx = (cx - offX) / scale, ry = (cy - offY) / scale;
        px = rx * pw / sw;
        py = ry * ph / sh;
        return true;
    }

    public static bool PanelToClient(VisualElement root, float px, float py, out int cx, out int cy)
    {
        cx = cy = 0;
        if (!Viewport(out var scale, out var offX, out var offY, out var sw, out var sh)) return false;
        if (!PanelSize(root, out var pw, out var ph)) return false;
        float rx = px * sw / pw, ry = py * sh / ph;
        cx = (int)Math.Round(rx * scale + offX);
        cy = (int)Math.Round(ry * scale + offY);
        return true;
    }

    private static bool PanelSize(VisualElement root, out float w, out float h)
    {
        w = h = 0;
        try
        {
            var l = root.layout;
            w = l.width; h = l.height;
        }
        catch { return false; }
        return w > 1 && h > 1 && !float.IsNaN(w) && !float.IsNaN(h);
    }
}
