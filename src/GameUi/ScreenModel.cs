using System.Text.RegularExpressions;

namespace ManagerAgent;

/// <summary>
/// One recorded element of the game's UI: a piece of text, something clickable, a text field or a
/// scroll area — with its position in UI coordinates. A snapshot is a flat list of these.
/// </summary>
internal sealed class UiNode
{
    public int Id;
    public int Parent = -1;     // nearest recorded ancestor
    public int Click = -1;      // nearest clickable ancestor (or self)
    public int Order;
    public string Type = "";
    public string Name = "";
    public string Text;         // own text (text elements only)
    public float X, Y, W, H;
    public bool Clickable;
    public bool Enabled = true;
    public bool IsField;
    public string FieldValue;
    public bool IsScroll;
    public float ScrollValue, ScrollLow, ScrollHigh;
    public bool CanScroll;
    /// <summary>Game element behind this node (real game only).</summary>
    public object Handle;

    public float CX => X + W / 2;
    public float CY => Y + H / 2;
    public float Right => X + W;
    public float Bottom => Y + H;
    public bool Contains(float px, float py) => px >= X && px <= X + W && py >= Y && py <= Y + H;
    public override string ToString() => $"#{Id} {Type} \"{Text}\" [{X:0},{Y:0} {W:0}x{H:0}]{(Clickable ? " click" : "")}{(Enabled ? "" : " disabled")}";
}

/// <summary>A snapshot of everything visible on screen.</summary>
internal sealed class ScreenModel
{
    public double Time;
    public float Width = 1920, Height = 1080;
    public readonly List<UiNode> Nodes = new();

    public UiNode this[int id] => id >= 0 && id < Nodes.Count ? Nodes[id] : null;

    public IEnumerable<UiNode> Texts => Nodes.Where(n => n.Text != null);

    public static bool Eq(string a, string b) =>
        a != null && b != null && string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);

    private static string Norm(string s) => Ui.Clean(s).Trim().TrimEnd(':');

    /// <summary>First text node equal to one of the labels (| separated, case-insensitive).</summary>
    public UiNode Text(string labels, Func<UiNode, bool> where = null)
    {
        var options = Split(labels);
        return Texts.FirstOrDefault(n => options.Any(o => Eq(n.Text, o)) && (where == null || where(n)));
    }

    public IEnumerable<UiNode> TextsEq(string labels)
    {
        var options = Split(labels);
        return Texts.Where(n => options.Any(o => Eq(n.Text, o)));
    }

    public IEnumerable<UiNode> TextsMatching(Regex rx) => Texts.Where(n => rx.IsMatch(Ui.Clean(n.Text)));

    public static string[] Split(string labels) =>
        (labels ?? "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();

    /// <summary>The clickable that owns a node (itself, or its nearest clickable ancestor).</summary>
    public UiNode ClickableOf(UiNode n) => n == null ? null : n.Clickable ? n : this[n.Click];

    /// <summary>A button by its label: the clickable owning a text equal to the label.</summary>
    public UiNode Button(string labels, Func<UiNode, bool> where = null)
    {
        foreach (var t in TextsEq(labels))
        {
            var c = ClickableOf(t);
            if (c != null && (where == null || where(c))) return c;
        }
        return null;
    }

    /// <summary>True if <paramref name="n"/> is <paramref name="ancestor"/> or inside it.</summary>
    public bool IsInside(UiNode n, UiNode ancestor)
    {
        for (var cur = n; cur != null; cur = this[cur.Parent])
            if (cur.Id == ancestor.Id) return true;
        return false;
    }

    /// <summary>The topmost recorded ancestor (the screen or panel a node belongs to).</summary>
    public UiNode RootOf(UiNode n)
    {
        if (n == null) return null;
        var cur = n;
        for (int guard = 0; cur.Parent >= 0 && guard < 500; guard++) cur = this[cur.Parent];
        return cur;
    }

    /// <summary>All text inside a node, in order.</summary>
    public string TextOf(UiNode n)
    {
        if (n == null) return "";
        if (n.Text != null && !n.Clickable) return n.Text;
        return string.Join(" ", Texts.Where(t => IsInside(t, n)).Select(t => t.Text));
    }

    /// <summary>Text nodes whose vertical centre lies within [y0, y1].</summary>
    public IEnumerable<UiNode> TextsInBand(float y0, float y1, float xMin = float.MinValue, float xMax = float.MaxValue) =>
        Texts.Where(t => t.CY >= y0 && t.CY <= y1 && t.CX >= xMin && t.CX <= xMax);

    public IEnumerable<UiNode> ClickablesInBand(float y0, float y1, float xMin = float.MinValue, float xMax = float.MaxValue) =>
        Nodes.Where(t => t.Clickable && t.CY >= y0 && t.CY <= y1 && t.CX >= xMin && t.CX <= xMax);

    /// <summary>
    /// This snapshot as text, in the UI dump format (so <c>AgentTests --replay</c> reads it too).
    /// Fields are marked <c>.agent-field</c> with their value as text; disabled elements <c>.disabled</c>.
    /// </summary>
    public string ToDump()
    {
        var sb = new System.Text.StringBuilder();
        var depth = new int[Nodes.Count];
        foreach (var n in Nodes)
        {
            int d = n.Parent >= 0 && n.Parent < n.Id ? depth[n.Parent] + 1 : 0;
            depth[n.Id] = d;
            sb.Append(' ', Math.Min(d, 60) * 2).Append(string.IsNullOrEmpty(n.Type) ? "Node" : n.Type);
            if (!string.IsNullOrEmpty(n.Name)) sb.Append(" #").Append(n.Name.Replace(' ', '_'));
            var cls = new List<string>();
            if (n.IsField) cls.Add("agent-field");
            if (n.IsScroll) cls.Add("agent-scroll");
            if (!n.Enabled) cls.Add("disabled");
            if (cls.Count > 0) sb.Append(" .").Append(string.Join(".", cls));
            sb.Append(System.FormattableString.Invariant($"  [{n.X:0},{n.Y:0} {n.W:0}x{n.H:0}]"));
            if (n.Clickable) sb.Append("  (clickable)");
            var t = n.IsField ? n.FieldValue : n.Text;
            if (!string.IsNullOrEmpty(t)) sb.Append("  \"").Append(t.Replace('\n', ' ')).Append('"');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Smallest node containing a point (for "what did the user click").</summary>
    public UiNode At(float x, float y, Func<UiNode, bool> where = null) =>
        Nodes.Where(n => n.W > 0 && n.Contains(x, y) && (where == null || where(n))).OrderBy(n => n.W * n.H).FirstOrDefault();

    /// <summary>Texts present here but not in <paramref name="before"/> (e.g. a dropdown list that just opened).</summary>
    public List<UiNode> NewTexts(ScreenModel before)
    {
        if (before == null) return Texts.ToList();
        var old = new HashSet<string>(before.Texts.Select(Key));
        return Texts.Where(t => !old.Contains(Key(t))).ToList();
    }

    private static string Key(UiNode t) => KeyOf(t);

    /// <summary>Identity of a piece of text by content and position (stable across snapshots).</summary>
    public static string KeyOf(UiNode t) => t.Text + "@" + (int)Math.Round(t.X / 4) + "," + (int)Math.Round(t.Y / 4);

    /// <summary>True while any of these texts (by content + position) is still on screen — e.g. a list that's still open.</summary>
    public bool AnyOf(HashSet<string> keys) => keys.Count > 0 && Texts.Any(t => keys.Contains(KeyOf(t)));

    public static HashSet<string> Keys(IEnumerable<UiNode> items) => new(items.Select(KeyOf));
}

internal enum GameKey { Escape, Enter }

/// <summary>What the agent's duties can see and do. Implemented by the real game and by the test simulator.</summary>
internal interface IGame
{
    double Now { get; }
    /// <summary>Latest complete snapshot (null until the first scan finishes).</summary>
    ScreenModel Screen { get; }
    /// <summary>Real mouse/keyboard input is still being played.</summary>
    bool Busy { get; }
    int PressMethods { get; }
    /// <summary>Press a node using method #attempt (submit / invoke / mouse …). False if that method can't be tried.</summary>
    bool Press(UiNode node, int attempt, out string how);
    /// <summary>Puts text into a text field directly. False if the field can't be set this way.</summary>
    bool SetField(UiNode field, string text, out string how);
    /// <summary>Types with the keyboard into whatever has focus. False if the game window isn't focused.</summary>
    /// <summary>True if the keyboard focus is in this field. Keys are only ever sent when it is.</summary>
    bool FieldFocused(UiNode field);
    /// <summary>A real click on a harmless spot (closes lists, leaves text boxes).</summary>
    bool ClickAside(UiNode spot, out string how);
    bool Type(string text, bool selectAllFirst, bool pressEnter);
    bool Key(GameKey key);
    /// <summary>Scrolls a scroll area by roughly this many UI pixels (positive = down).</summary>
    bool Scroll(UiNode scroll, float pixels, out string how);
    /// <summary>Ask for frequent snapshots while a duty is working.</summary>
    void WantFast(bool fast);
    /// <summary>Seconds since the user last touched mouse or keyboard.</summary>
    double UserIdleSeconds { get; }
}

/// <summary>Something a script waits for before its next step.</summary>
internal abstract class Wait
{
    public bool TimedOut;
    public abstract bool Ready(IGame g);

    public static Wait Seconds(IGame g, double s) => new TimeWait(g.Now + s);
    /// <summary>A snapshot taken after this moment (i.e. showing the result of what we just did).</summary>
    public static Wait Fresh(IGame g, double settle = 0.15) => new FreshWait(g.Now + settle);
    public static Wait Until(IGame g, Func<ScreenModel, bool> cond, double timeout, double settle = 0.15) =>
        new UntilWait(cond, g.Now + settle, g.Now + timeout);

    private sealed class TimeWait : Wait
    {
        private readonly double _at;
        public TimeWait(double at) { _at = at; }
        public override bool Ready(IGame g) => g.Now >= _at;
    }

    private sealed class FreshWait : Wait
    {
        private readonly double _after;
        public FreshWait(double after) { _after = after; }
        public override bool Ready(IGame g) => g.Screen != null && g.Screen.Time >= _after;
    }

    private sealed class UntilWait : Wait
    {
        private readonly Func<ScreenModel, bool> _cond;
        private readonly double _after, _deadline;
        private double _checked = double.NegativeInfinity;
        public UntilWait(Func<ScreenModel, bool> c, double after, double deadline) { _cond = c; _after = after; _deadline = deadline; }

        public override bool Ready(IGame g)
        {
            var s = g.Screen;
            if (s != null && s.Time >= _after && s.Time > _checked)
            {
                _checked = s.Time;
                if (_cond(s)) return true;
            }
            if (g.Now < _deadline) return false;
            TimedOut = true;
            return true;
        }
    }
}

/// <summary>A choice the agent is waiting for you to make (shown as buttons in the panel).</summary>
internal sealed class UserChoice : Wait
{
    public string Title;
    public string Text;
    public readonly List<(string Label, string Key, BtnKind Kind)> Options = new();
    public string Picked;

    public UserChoice Add(string label, string key, BtnKind kind = BtnKind.Normal)
    {
        Options.Add((label, key, kind));
        return this;
    }

    public override bool Ready(IGame g) => Picked != null;
}

/// <summary>Runs a duty's step-by-step script (a C# iterator yielding <see cref="Wait"/>s).</summary>
internal sealed class ScriptRunner
{
    private IEnumerator<Wait> _it;
    private Wait _wait;
    public string Name { get; private set; }
    public bool Active => _it != null;
    public UserChoice Choice => _wait as UserChoice;

    public void Start(string name, IEnumerable<Wait> script)
    {
        Stop();
        Name = name;
        _it = script.GetEnumerator();
        _wait = null;
    }

    public void Stop()
    {
        try { _it?.Dispose(); } catch { }
        _it = null;
        _wait = null;
    }

    /// <returns>Exception if the script crashed (and was stopped).</returns>
    public Exception Tick(IGame g)
    {
        if (_it == null || g.Busy) return null;
        if (_wait != null && !_wait.Ready(g)) return null;
        try
        {
            for (int guard = 0; guard < 50; guard++)
            {
                if (!_it.MoveNext()) { Stop(); return null; }
                _wait = _it.Current;
                if (_wait == null) continue;
                return null;
            }
            return null;
        }
        catch (Exception ex)
        {
            Stop();
            return ex;
        }
    }
}
