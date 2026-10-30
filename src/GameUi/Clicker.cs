using System.Reflection;

namespace ManagerAgent;

internal enum ClickStrategy { Submit, Invoke, Mouse }

/// <summary>
/// Presses a game element. Three ways, tried in the configured order by the caller,
/// which checks after each whether the game actually reacted:
///   submit — dispatch a UI Toolkit NavigationSubmitEvent (what Enter / pad "A" does)
///   invoke — call FM's own click handler on the element (found by reflection)
///   mouse  — move the real cursor there and click (needs the game window focused)
/// </summary>
internal sealed class Clicker
{
    private readonly AgentSettings _s;
    private MouseJob _job;
    private double _syntheticUntil;

    // FM/SI element types known to carry click handlers (from FM26 mod research).
    private static readonly string[] HandlerTypes =
        { "NavigatableVisualElement", "SIRadioButton", "TableRowNavigatable", "SIButton", "Button" };
    private static readonly string[] DelegateMembers =
        { "m_elementClickedOrSubmitted", "m_clicked", "m_onClick", "clicked", "onClick" };
    private static readonly string[] HandlerMethods =
        { "OnClickedOrSubmitted", "OnNavigationSubmit", "OnClicked", "Click" };

    public Clicker(AgentSettings s) { _s = s; }

    public bool Busy => _job != null;

    /// <summary>True while our own synthetic mouse click is in flight (so it isn't mistaken for yours).</summary>
    public bool SyntheticMouseActive => Clock.Now < _syntheticUntil;

    public static List<ClickStrategy> ParseStrategies(string csv)
    {
        var list = new List<ClickStrategy>();
        foreach (var part in (csv ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            if (Enum.TryParse<ClickStrategy>(part.Trim(), true, out var s) && !list.Contains(s)) list.Add(s);
        if (list.Count == 0) list.AddRange(new[] { ClickStrategy.Submit, ClickStrategy.Invoke, ClickStrategy.Mouse });
        return list;
    }

    /// <summary>Attempts a press. Returns false if this strategy couldn't even be attempted.</summary>
    public bool TryPress(ClickStrategy strategy, VisualElement el, PanelRoot root, out string detail)
    {
        detail = "";
        if (el == null) { detail = "no element"; return false; }
        try
        {
            return strategy switch
            {
                ClickStrategy.Submit => Submit(el, out detail),
                ClickStrategy.Invoke => Invoke(el, out detail),
                ClickStrategy.Mouse => StartMouse(el, root, out detail),
                _ => false,
            };
        }
        catch (Exception ex)
        {
            detail = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    // ------------------------------------------------------------------ submit

    private static bool Submit(VisualElement el, out string detail)
    {
        Reflect.TryCall(el, "Focus");
        var evt = NewSubmitEvent(el);
        if (evt == null) { detail = "NavigationSubmitEvent unavailable"; return false; }
        try
        {
            if (!Reflect.TryCall(el, "SendEvent", evt))
            {
                detail = "SendEvent unavailable";
                return false;
            }
            detail = "submit → " + Il2CppUtil.TypeName(el);
            return true;
        }
        finally
        {
            Reflect.TryCall(evt, "Dispose");
        }
    }

    private static MethodInfo _getPooled;
    private static bool _getPooledSearched;

    /// <summary>NavigationSubmitEvent.GetPooled() with its target set — all late-bound.</summary>
    internal static object NewSubmitEvent(VisualElement target)
    {
        if (!_getPooledSearched)
        {
            _getPooledSearched = true;
            var t = Reflect.FindType("NavigationSubmitEvent");
            _getPooled = t?.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                .Where(m => m.Name == "GetPooled" && !m.IsGenericMethodDefinition && m.ReturnType.IsAssignableFrom(t))
                .OrderBy(m => m.GetParameters().Length)
                .FirstOrDefault(m => m.GetParameters().All(p => p.IsOptional));
        }
        if (_getPooled == null) return null;
        try
        {
            var args = _getPooled.GetParameters().Select(p => p.DefaultValue is DBNull ? null : p.DefaultValue).ToArray();
            var evt = _getPooled.Invoke(null, args);
            var targetProp = evt?.GetType().GetProperty("target", BindingFlags.Public | BindingFlags.Instance);
            if (targetProp != null)
            {
                var handler = Reflect.TryCast(target, targetProp.PropertyType) ?? target;
                if (targetProp.PropertyType.IsInstanceOfType(handler)) targetProp.SetValue(evt, handler);
            }
            return evt;
        }
        catch (Exception ex)
        {
            AgentLog.Debug("GetPooled failed: " + (ex.InnerException ?? ex).Message);
            return null;
        }
    }

    // ------------------------------------------------------------------ invoke

    private bool Invoke(VisualElement el, out string detail)
    {
        foreach (var candidate in Candidates(el))
        {
            foreach (var typeName in HandlerTypes)
            {
                if (!Il2CppUtil.IsA(candidate, typeName)) continue;
                var t = Reflect.FindType(typeName);
                if (t == null) continue;
                var cast = Reflect.TryCast(candidate, t);
                if (cast == null) continue;
                if (TryHandlers(cast, candidate, out detail))
                {
                    detail = $"{typeName}.{detail}";
                    return true;
                }
            }
        }
        detail = "no FM click handler found";
        return false;
    }

    /// <summary>The element itself, a few levels of children, then a few ancestors.</summary>
    private static IEnumerable<VisualElement> Candidates(VisualElement el)
    {
        yield return el;
        var queue = new Queue<(VisualElement, int)>();
        queue.Enqueue((el, 0));
        int yielded = 0;
        while (queue.Count > 0 && yielded < 24)
        {
            var (cur, d) = queue.Dequeue();
            if (d >= 2) continue;
            int n = Ui.ChildCount(cur);
            for (int i = 0; i < n; i++)
            {
                var c = Ui.Child(cur, i);
                if (c == null) continue;
                yielded++;
                yield return c;
                queue.Enqueue((c, d + 1));
            }
        }
        var p = el;
        for (int i = 0; i < 2; i++)
        {
            try { p = p.parent; } catch { yield break; }
            if (p == null) yield break;
            yield return p;
        }
    }

    private static bool TryHandlers(object target, VisualElement el, out string detail)
    {
        detail = "";
        foreach (var member in DelegateMembers)
        {
            var del = Reflect.Get(target, member);
            if (del == null) continue;
            var invoke = del.GetType().GetMethod("Invoke", BindingFlags.Public | BindingFlags.Instance);
            if (invoke == null) continue;
            var ps = invoke.GetParameters();
            object[] args = ps.Length switch
            {
                0 => Array.Empty<object>(),
                1 when ps[0].ParameterType.IsInstanceOfType(target) => new[] { target },
                1 when ps[0].ParameterType.IsInstanceOfType(el) => new object[] { el },
                _ => null,
            };
            if (args == null) continue;
            try
            {
                invoke.Invoke(del, args);
                detail = member;
                return true;
            }
            catch (TargetInvocationException ex)
            {
                AgentLog.Debug($"{member} threw {ex.InnerException?.Message}");
            }
        }

        foreach (var method in HandlerMethods)
        {
            if (Reflect.TryCall(target, method)) { detail = method + "()"; return true; }
            if (Reflect.TryCall(target, method, target)) { detail = method + "(self)"; return true; }
            if (Reflect.TryCall(target, method, el)) { detail = method + "(el)"; return true; }
            if (method == "OnNavigationSubmit")
            {
                var evt = NewSubmitEvent(el);
                if (evt == null) continue;
                try
                {
                    if (Reflect.TryCall(target, method, evt)) { detail = method + "(evt)"; return true; }
                }
                finally { Reflect.TryCall(evt, "Dispose"); }
            }
        }
        return false;
    }

    // ------------------------------------------------------------------ mouse

    private sealed class MouseJob
    {
        public int TargetX, TargetY, SavedX, SavedY;
        public bool HaveSaved;
        public double T0;
        public int Stage;
    }

    private bool StartMouse(VisualElement el, PanelRoot root, out string detail)
    {
        if (_job != null) { detail = "busy"; return false; }
        if (!GameWindow.IsForeground) { detail = "game window not focused"; return false; }
        root ??= Ui.RootOf(el);
        if (root == null || !Ui.TryBounds(el, out var x, out var y, out var w, out var h))
        {
            detail = "element has no position";
            return false;
        }
        if (!Coords.PanelToClient(root.Root, x + w / 2, y + h / 2, out var cx, out var cy) ||
            !GameWindow.TryClientSize(out var cw, out var ch) || cx < 0 || cy < 0 || cx >= cw || cy >= ch ||
            !GameWindow.TryClientToScreen(cx, cy, out var sx, out var sy))
        {
            detail = "couldn't map element to the screen";
            return false;
        }

        var job = new MouseJob { TargetX = sx, TargetY = sy, T0 = Clock.Now };
        if (Win32.GetCursorPos(out var saved)) { job.SavedX = saved.X; job.SavedY = saved.Y; job.HaveSaved = true; }
        Win32.SetCursorPos(sx, sy);
        _syntheticUntil = Clock.Now + 0.5;
        _job = job;
        detail = $"mouse click at {sx},{sy}";
        return true;
    }

    /// <summary>Advances an in-flight mouse click (move → down → up → restore) across frames.</summary>
    public void Tick()
    {
        var j = _job;
        if (j == null) return;
        double t = Clock.Now - j.T0;
        if (j.Stage == 0 && t >= 0.06)
        {
            Win32.SetCursorPos(j.TargetX, j.TargetY);
            Win32.mouse_event(Win32.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            j.Stage = 1;
            _syntheticUntil = Clock.Now + 0.4;
        }
        else if (j.Stage == 1 && t >= 0.13)
        {
            Win32.mouse_event(Win32.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            j.Stage = 2;
            _syntheticUntil = Clock.Now + 0.3;
        }
        else if (j.Stage == 2 && t >= 0.22)
        {
            if (j.HaveSaved && _s.RestoreCursorAfterClick.Value) Win32.SetCursorPos(j.SavedX, j.SavedY);
            _job = null;
        }
    }

    /// <summary>Real mouse-wheel scroll over a point (game window must be focused).</summary>
    public bool Wheel(VisualElement over, PanelRoot root, int notches)
    {
        if (_job != null || !GameWindow.IsForeground || notches == 0) return false;
        root ??= Ui.RootOf(over);
        if (root == null || !Ui.TryBounds(over, out var x, out var y, out var w, out var h)) return false;
        if (!Coords.PanelToClient(root.Root, x + w / 2, y + h / 2, out var cx, out var cy) ||
            !GameWindow.TryClientToScreen(cx, cy, out var sx, out var sy)) return false;
        Win32.GetCursorPos(out var saved);
        Win32.SetCursorPos(sx, sy);
        _syntheticUntil = Clock.Now + 0.3;
        Win32.mouse_event(Win32.MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)(-120 * notches)), UIntPtr.Zero);
        if (_s.RestoreCursorAfterClick.Value) Win32.SetCursorPos(saved.X, saved.Y);
        return true;
    }

    public void Cancel()
    {
        if (_job != null && _job.Stage == 1)
            Win32.mouse_event(Win32.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        _job = null;
    }
}
