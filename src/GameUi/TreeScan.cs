namespace ManagerAgent;

internal struct PathItem
{
    public string Type;
    public string Name;
}

internal interface IScanVisitor
{
    /// <summary>
    /// Called for every shown element in tree order. <paramref name="path"/>[0..depth-1] are its
    /// ancestors, path[depth] is the element itself. Return false to skip its children.
    /// </summary>
    bool Visit(VisualElement ve, int depth, int order, string type, string name, PathItem[] path);
}

/// <summary>
/// Depth-first walk over the UI tree that can be spread across frames (Step with a budget),
/// so scanning FM's large UI never causes a visible hitch. Hidden subtrees are skipped.
/// </summary>
internal sealed class TreeScan
{
    private const int MaxDepth = 250;
    private readonly Stack<(VisualElement el, int depth)> _stack = new();
    private readonly PathItem[] _path = new PathItem[MaxDepth + 1];
    private readonly IScanVisitor _visitor;
    private readonly HashSet<long> _skip;
    private int _order;

    public int Visited { get; private set; }
    public bool Done => _stack.Count == 0;

    public TreeScan(IEnumerable<VisualElement> roots, IScanVisitor visitor, HashSet<long> skip)
    {
        _visitor = visitor;
        _skip = skip ?? new HashSet<long>();
        foreach (var r in roots.Reverse())
            if (r != null) _stack.Push((r, 0));
    }

    /// <summary>Scan starting below the root: <paramref name="start"/>'s real ancestors are pre-filled into the path.</summary>
    public TreeScan(VisualElement start, IScanVisitor visitor, HashSet<long> skip)
    {
        _visitor = visitor;
        _skip = skip ?? new HashSet<long>();
        var ancestors = new List<PathItem>();
        VisualElement cur = null;
        try { cur = start.parent; } catch { }
        for (int guard = 0; cur != null && guard < MaxDepth; guard++)
        {
            ancestors.Add(new PathItem { Type = Il2CppUtil.TypeName(cur), Name = Ui.Name(cur) });
            try { cur = cur.parent; } catch { break; }
        }
        ancestors.Reverse();
        for (int i = 0; i < ancestors.Count && i < MaxDepth; i++) _path[i] = ancestors[i];
        _stack.Push((start, Math.Min(ancestors.Count, MaxDepth)));
    }

    /// <summary>Processes up to <paramref name="budget"/> elements. Returns true when finished.</summary>
    public bool Step(int budget)
    {
        while (budget-- > 0 && _stack.Count > 0)
        {
            var (el, depth) = _stack.Pop();
            if (el == null) continue;
            long id;
            try { id = el.Pointer.ToInt64(); } catch { continue; }
            if (_skip.Contains(id)) continue;
            if (depth > 0 && !Ui.SelfShown(el)) continue;

            Visited++;
            var type = Il2CppUtil.TypeName(el);
            var name = Ui.Name(el);
            _path[depth] = new PathItem { Type = type, Name = name };

            bool descend;
            try { descend = _visitor.Visit(el, depth, _order++, type, name, _path); }
            catch (Exception ex) { Guard.Report("scan-visit", ex); descend = true; }

            if (!descend || depth >= MaxDepth) continue;
            int n = Ui.ChildCount(el);
            for (int i = n - 1; i >= 0; i--)
            {
                var c = Ui.Child(el, i);
                if (c != null) _stack.Push((c, depth + 1));
            }
        }
        return Done;
    }

    public void RunToEnd() => Step(int.MaxValue);
}

/// <summary>Finds the topmost, deepest element under a point (for the "teach" hover).</summary>
internal sealed class HitTestVisitor : IScanVisitor
{
    private readonly float _x, _y;
    public VisualElement Hit;

    public HitTestVisitor(float x, float y) { _x = x; _y = y; }

    public bool Visit(VisualElement ve, int depth, int order, string type, string name, PathItem[] path)
    {
        if (!Ui.TryBounds(ve, out var x, out var y, out var w, out var h))
            return true; // zero-size wrappers can still have visible children
        bool inside = _x >= x && _x <= x + w && _y >= y && _y <= y + h;
        if (!inside) return false; // prune: content rarely overflows its container
        if (depth == 0) return true;

        bool ignoresPointer = Reflect.Get(ve, "pickingMode")?.ToString() == "Ignore";
        bool hasText = Ui.IsTextElement(ve);
        if (!ignoresPointer || hasText || Ui.LooksClickable(ve)) Hit = ve;
        return true;
    }

    public static VisualElement Run(VisualElement root, float px, float py, HashSet<long> skip)
    {
        var v = new HitTestVisitor(px, py);
        new TreeScan(new[] { root }, v, skip).RunToEnd();
        return v.Hit;
    }
}
