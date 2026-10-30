using System.Reflection;

namespace ManagerAgent;

/// <summary>
/// The real game, as seen by the duties: builds <see cref="ScreenModel"/> snapshots of FM's UI
/// (a few thousand elements per frame, so there's no hitch) and carries out presses, typing and scrolling.
/// </summary>
internal sealed class UnityGame : IGame
{
    private readonly Agent _a;
    private readonly KeyboardPlayer _keys = new();
    private ScreenModel _screen;
    private Builder _building;
    private double _nextScan;
    private bool _fast;
    private List<ClickStrategy> _methods;
    private string _methodsCfg;

    public UnityGame(Agent a) { _a = a; }

    public double Now => Clock.Now;
    public ScreenModel Screen => _screen;
    public bool Busy => _a.Clicker.Busy || _keys.Busy;
    public double UserIdleSeconds => Win32.IdleSeconds;

    public void WantFast(bool fast) => _fast |= fast;

    private List<ClickStrategy> Methods
    {
        get
        {
            var cfg = _a.Settings.ClickStrategies.Value;
            if (_methods == null || cfg != _methodsCfg) { _methods = Clicker.ParseStrategies(cfg); _methodsCfg = cfg; }
            return _methods;
        }
    }

    public int PressMethods => Methods.Count;

    public void Tick()
    {
        _keys.Tick();
        if (_building == null && Clock.Now >= _nextScan)
        {
            _building = new Builder(_a.OwnElementIds);
        }
        if (_building != null && _building.Step(_fast ? 8000 : 3000))
        {
            _screen = _building.Result;
            _building = null;
            _nextScan = Clock.Now + (_fast ? 0.12 : 1.0);
            _fast = false;
        }
    }

    /// <summary>Starts a fresh scan on the next frame (e.g. right after an action).</summary>
    public void Rescan()
    {
        _building = null;
        _nextScan = 0;
    }

    // ------------------------------------------------------------------ actions

    public bool Press(UiNode node, int attempt, out string how)
    {
        how = "";
        if (node?.Handle is not VisualElement el) { how = "no element"; return false; }
        var methods = Methods;
        if (attempt < 0 || attempt >= methods.Count) { how = "no more ways to press"; return false; }
        if (!Ui.IsAttached(el)) { how = "element gone"; return false; }
        var ok = _a.Clicker.TryPress(methods[attempt], el, Ui.RootOf(el), out how);
        how = methods[attempt].ToString().ToLowerInvariant() + (ok ? "" : " (" + how + ")");
        if (ok) Rescan();
        return ok;
    }

    public bool SetField(UiNode field, string text, out string how)
    {
        how = "";
        if (field?.Handle is not VisualElement el) { how = "no field"; return false; }
        try
        {
            var p = el.GetType().GetProperty("value", BindingFlags.Public | BindingFlags.Instance);
            if (p == null || !p.CanWrite) { how = "field has no value"; return false; }
            object v = text;
            var pt = p.PropertyType;
            if (pt != typeof(string))
            {
                if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) return false;
                v = Convert.ChangeType(d, pt, System.Globalization.CultureInfo.InvariantCulture);
            }
            p.SetValue(el, v);
            // Commit like leaving the field.
            Reflect.TryCall(el, "Blur");
            how = "field value";
            Rescan();
            return true;
        }
        catch (Exception ex)
        {
            how = (ex.InnerException ?? ex).Message;
            return false;
        }
    }

    public bool Type(string text, bool selectAllFirst, bool pressEnter)
    {
        var ok = _keys.Type(text, selectAllFirst, pressEnter);
        if (ok) Rescan();
        return ok;
    }

    public bool Key(GameKey key)
    {
        var ok = _keys.Press(key == GameKey.Escape ? Win32.VK_ESCAPE : Win32.VK_RETURN);
        if (ok) Rescan();
        return ok;
    }

    public bool Scroll(UiNode scroll, float pixels, out string how)
    {
        how = "";
        if (scroll?.Handle is not VisualElement el) { how = "no scroll area"; return false; }
        var scroller = Reflect.Get(el, "verticalScroller");
        if (scroller != null && Reflect.Get(scroller, "value") is float v)
        {
            float lo = Reflect.Get(scroller, "lowValue") as float? ?? 0, hi = Reflect.Get(scroller, "highValue") as float? ?? float.MaxValue;
            float nv = Math.Max(lo, Math.Min(hi, v + pixels));
            if (Reflect.TrySet(scroller, "value", nv)) { how = "scrollbar"; Rescan(); return true; }
        }
        int notches = (int)Math.Ceiling(Math.Abs(pixels) / 100f) * Math.Sign(pixels);
        if (_a.Clicker.Wheel(el, Ui.RootOf(el), notches)) { how = "mouse wheel"; Rescan(); return true; }
        how = "can't scroll";
        return false;
    }

    public void Cancel()
    {
        _keys.Cancel();
        _a.Clicker.Cancel();
    }

    // ------------------------------------------------------------------ snapshot builder

    private sealed class Builder : IScanVisitor
    {
        private static PropertyInfo _enabledProp;
        private static bool _enabledSearched;

        private readonly TreeScan _scan;
        private readonly int[] _recAt = new int[260];
        private readonly int[] _clickAt = new int[260];
        public readonly ScreenModel Result = new();

        public Builder(HashSet<long> skip)
        {
            Result.Time = Clock.Now;
            var roots = Ui.Roots;
            if (roots.Count > 0)
            {
                try
                {
                    var l = roots[^1].Root.layout;
                    if (l.width > 1) { Result.Width = l.width; Result.Height = l.height; }
                }
                catch { }
            }
            _scan = new TreeScan(roots.Select(r => r.Root), this, skip);
        }

        public bool Step(int budget) => _scan.Step(budget) || Result.Nodes.Count > 30000;

        public bool Visit(VisualElement ve, int depth, int order, string type, string name, PathItem[] path)
        {
            if (depth >= _recAt.Length - 1) return false;
            int parentRec = depth > 0 ? _recAt[depth - 1] : -1;
            int parentClick = depth > 0 ? _clickAt[depth - 1] : -1;
            _recAt[depth] = parentRec;
            _clickAt[depth] = parentClick;

            string text = Ui.IsTextElement(ve) ? Ui.OwnText(ve) : null;
            if (text != null && text.Length == 0) text = null;
            bool clickable = Ui.LooksClickable(ve);
            var chain = Il2CppUtil.TypeChain(ve);
            bool field = chain.Any(c => c.Contains("TextField") || c.Contains("TextInput") || c.Contains("IntegerField") || c.Contains("FloatField") || c.Contains("DoubleField") || c.Contains("LongField"));
            bool scroll = chain.Any(c => c.Contains("ScrollView"));
            if (text == null && !clickable && !field && !scroll) return true;

            var n = new UiNode
            {
                Id = Result.Nodes.Count,
                Parent = parentRec,
                Click = clickable ? -2 : parentClick,
                Order = order,
                Type = type,
                Name = name,
                Text = text,
                Clickable = clickable,
                IsField = field,
                IsScroll = scroll,
                Handle = ve,
            };
            if (Ui.TryBounds(ve, out var x, out var y, out var w, out var h)) { n.X = x; n.Y = y; n.W = w; n.H = h; }
            else if (!scroll) return true;
            if (clickable) n.Click = n.Id;
            n.Enabled = IsEnabled(ve);
            if (field) n.FieldValue = Reflect.Get(ve, "value")?.ToString() ?? Reflect.Get(ve, "text")?.ToString();
            if (scroll)
            {
                var sc = Reflect.Get(ve, "verticalScroller");
                if (sc != null)
                {
                    n.ScrollValue = Reflect.Get(sc, "value") as float? ?? 0;
                    n.ScrollLow = Reflect.Get(sc, "lowValue") as float? ?? 0;
                    n.ScrollHigh = Reflect.Get(sc, "highValue") as float? ?? 0;
                    n.CanScroll = n.ScrollHigh > n.ScrollLow + 1;
                }
            }
            Result.Nodes.Add(n);
            _recAt[depth] = n.Id;
            if (clickable) _clickAt[depth] = n.Id;
            return true;
        }

        private static bool IsEnabled(VisualElement ve)
        {
            if (!_enabledSearched)
            {
                _enabledSearched = true;
                _enabledProp = typeof(VisualElement).GetProperty("enabledInHierarchy", BindingFlags.Public | BindingFlags.Instance);
            }
            try
            {
                if (_enabledProp != null && _enabledProp.GetValue(ve) is bool b && !b) return false;
            }
            catch { }
            return !(Ui.HasClass(ve, "disabled") || Ui.HasClass(ve, "unity-disabled") || Ui.HasClass(ve, "is-disabled"));
        }
    }
}
