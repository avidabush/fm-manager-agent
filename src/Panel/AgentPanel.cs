namespace ManagerAgent;

/// <summary>
/// The agent's in-game UI, injected into the game's top UI Toolkit panel:
///   • the Agent tab — a small button you can drag anywhere (e.g. into FM's top bar); its spot is remembered
///   • the dropdown panel under it (duty cards, job filters, your contract terms)
///   • an overlay for outlining game elements
/// </summary>
internal sealed class AgentPanel
{
    private enum View { Main, JobFilters, Terms }

    private sealed class Press
    {
        public float StartX, StartY, GrabX, GrabY;
        public bool Dragging;
    }

    private readonly Agent _a;
    private readonly UiKit _kit = new();

    private VisualElement _tab, _tabDot, _tabLabel, _drop, _overlay;
    private long _hostId, _parentId;
    private readonly List<VisualElement> _marks = new();
    private double _marksUntil, _nextAttach, _nextRebuildCheck, _nextPlace;
    private string _builtKey, _tabKey, _placeKey;
    private bool _visible, _open, _tabHover;
    private bool? _appliedVisible, _appliedOpen;
    private bool _needTabPosition = true;
    private Press _press;
    private float _tabLeft = -1, _tabTop = -1;   // last position we set (parent-local), valid before layout catches up

    private View _view = View.Main;

    private const float DragThreshold = 5f;

    public readonly HashSet<long> OwnIds = new();

    public AgentPanel(Agent a)
    {
        _a = a;
        _visible = a.Settings.PanelStartsVisible.Value;
        _open = !a.Settings.PanelStartsCollapsed.Value;
    }

    private PanelRoot Host => _hostId == 0 ? null : Ui.Roots.FirstOrDefault(r => r.Id == _hostId);

    // ================================================================== lifecycle

    public void Tick()
    {
        var now = Clock.Now;
        if (now >= _nextAttach)
        {
            _nextAttach = now + 1.0;
            EnsureAttached();
        }
        if (_tab == null) return;

        if (_appliedVisible != _visible || _appliedOpen != _open)
        {
            _appliedVisible = _visible;
            _appliedOpen = _open;
            S.Shown(_tab, _visible);
            S.Shown(_drop, _visible && _open);
            _placeKey = null;
        }
        if (_marks.Count > 0 && now > _marksUntil) ClearMarks();

        if (_needTabPosition) ApplyTabPosition();
        TrackPress();

        if (now >= _nextRebuildCheck)
        {
            _nextRebuildCheck = now + 0.2;
            var tk = TabKey();
            if (tk != _tabKey) RebuildTab(tk);
            var key = StateKey();
            if (key != _builtKey) Rebuild(key);
        }
        if (_visible && _open && (now >= _nextPlace || _press?.Dragging == true))
        {
            _nextPlace = now + 0.25;
            PlaceDropdown();
        }

        var host = Host;
        if (_visible && host != null && Coords.MouseToPanel(host.Root, out var px, out var py))
        {
            _kit.Hover(px, py, _open && Ui.Contains(_drop, px, py));
            bool overTab = Ui.Contains(_tab, px, py) || _press?.Dragging == true;
            if (overTab != _tabHover)
            {
                _tabHover = overTab;
                S.Bg(_tab, overTab ? Palette.TabHover : Palette.Tab);
            }
        }
    }

    private void EnsureAttached()
    {
        var roots = Ui.Roots;
        if (roots.Count == 0) return;
        var host = roots[^1];

        if (_tab == null)
        {
            _overlay = new VisualElement();
            Reflect.TrySet(_overlay, "name", "ManagerAgent.Overlay");
            S.Absolute(_overlay, 0, 0, 0, 0);
            S.IgnorePointer(_overlay);

            _drop = new VisualElement();
            Reflect.TrySet(_drop, "name", "ManagerAgent.Panel");
            S.Absolute(_drop, right: _a.Settings.PanelOffsetRight.Value, top: _a.Settings.PanelOffsetTop.Value + 52);
            S.CatchPointer(_drop);

            _tab = new VisualElement();
            Reflect.TrySet(_tab, "name", "ManagerAgent.Tab");
            S.CatchPointer(_tab);

            OwnIds.Clear();
            foreach (var v in new[] { _overlay, _drop, _tab }) OwnIds.Add(Il2CppUtil.Id(v));
            _builtKey = _tabKey = _placeKey = null;
            _appliedVisible = _appliedOpen = null;
            _needTabPosition = true;
        }

        var parent = host.HostElement;
        long parentId = Il2CppUtil.Id(parent);
        VisualElement current = null;
        try { current = _tab.parent; } catch { }
        bool attachedHere = current != null && Il2CppUtil.Id(current) == parentId;
        bool onTop = false;
        if (attachedHere)
        {
            int n = Ui.ChildCount(parent);
            onTop = n > 0 && Il2CppUtil.Id(Ui.Child(parent, n - 1)) == Il2CppUtil.Id(_tab);
        }
        if (attachedHere && onTop && _hostId == host.Id) return;

        foreach (var v in new[] { _overlay, _drop, _tab }) { try { v.RemoveFromHierarchy(); } catch { } }
        parent.Add(_overlay);
        parent.Add(_drop);
        parent.Add(_tab);
        if (_hostId != host.Id || _parentId != parentId)
        {
            AgentLog.Debug($"Agent UI attached under {Il2CppUtil.TypeName(parent)} '{Ui.Name(parent)}' (UI root of '{host.Name}')");
            _needTabPosition = true;
        }
        _hostId = host.Id;
        _parentId = parentId;
        _placeKey = null;
    }

    // ================================================================== mouse

    /// <summary>Mouse button went down. Returns true if it landed on the agent's UI (so it isn't a game click).</summary>
    public bool HandleClick()
    {
        if (!_visible || _tab == null) return false;
        var host = Host;
        if (host == null || !Coords.MouseToPanel(host.Root, out var px, out var py)) return false;

        if (Ui.TryBounds(_tab, out var tx, out var ty, out var tw, out var th) &&
            px >= tx && px <= tx + tw && py >= ty && py <= ty + th)
        {
            // Click or drag — decided when the button is released or the mouse moves.
            _press = new Press { StartX = px, StartY = py, GrabX = px - tx, GrabY = py - ty };
            return true;
        }
        if (_open && Ui.Contains(_drop, px, py))
        {
            _kit.Click(px, py);
            _nextRebuildCheck = 0;
            return true;
        }
        return false;
    }

    private void TrackPress()
    {
        var p = _press;
        if (p == null) return;
        var host = Host;
        bool held = _a.Input.Down(Win32.VK_LBUTTON);
        float px = p.StartX, py = p.StartY;
        if (host != null) Coords.MouseToPanel(host.Root, out px, out py);

        if (held)
        {
            if (!p.Dragging && !_a.Settings.LockTabPosition.Value &&
                Math.Abs(px - p.StartX) + Math.Abs(py - p.StartY) > DragThreshold)
                p.Dragging = true;
            if (p.Dragging) MoveTabTo(px - p.GrabX, py - p.GrabY);
            return;
        }

        _press = null;
        if (p.Dragging) SaveTabPosition();
        else
        {
            _open = !_open;
            _nextRebuildCheck = 0;
        }
    }

    // ================================================================== tab position

    private bool ParentRect(out float ox, out float oy, out float w, out float h)
    {
        ox = oy = w = h = 0;
        VisualElement parent = null;
        try { parent = _tab?.parent; } catch { }
        return parent != null && Ui.TryBounds(parent, out ox, out oy, out w, out h);
    }

    /// <summary>Puts the tab where it was left last time (or the default corner).</summary>
    private void ApplyTabPosition()
    {
        float fx = _a.Settings.TabX.Value, fy = _a.Settings.TabY.Value;
        if (fx < 0 || fy < 0)
        {
            S.Absolute(_tab, right: _a.Settings.PanelOffsetRight.Value, top: _a.Settings.PanelOffsetTop.Value);
            S.Set(_tab, "left", "Auto");
            _tabLeft = _tabTop = -1;
            _needTabPosition = false;
            _placeKey = null;
            return;
        }
        if (!ParentRect(out _, out _, out var pw, out var ph)) return; // layout not ready yet; retry next frame
        float tw = 120, th = 40;
        if (Ui.TryBounds(_tab, out _, out _, out var w, out var h)) { tw = w; th = h; }
        _tabLeft = Clamp(fx * pw, 0, pw - tw);
        _tabTop = Clamp(fy * ph, 0, ph - th);
        S.Set(_tab, "position", "Absolute");
        S.Set(_tab, "right", "Auto");
        S.Set(_tab, "left", _tabLeft);
        S.Set(_tab, "top", _tabTop);
        _needTabPosition = false;
        _placeKey = null;
    }

    private void MoveTabTo(float worldX, float worldY)
    {
        if (!ParentRect(out var ox, out var oy, out var pw, out var ph)) return;
        float tw = 120, th = 40;
        if (Ui.TryBounds(_tab, out _, out _, out var w, out var h)) { tw = w; th = h; }
        _tabLeft = Clamp(worldX - ox, 0, pw - tw);
        _tabTop = Clamp(worldY - oy, 0, ph - th);
        S.Set(_tab, "position", "Absolute");
        S.Set(_tab, "right", "Auto");
        S.Set(_tab, "left", _tabLeft);
        S.Set(_tab, "top", _tabTop);
        _placeKey = null;
    }

    /// <summary>
    /// Dropped onto a bar (a wide, short strip such as FM's screen header)? Centre the tab in it
    /// vertically so it lines up with the bar's own buttons.
    /// </summary>
    private void SnapToBar()
    {
        var host = Host;
        if (host == null || _tabLeft < 0 || !ParentRect(out var ox, out var oy, out var pw, out _)) return;
        if (!Ui.TryBounds(_tab, out _, out _, out var tw, out var th)) return;
        float cx = ox + _tabLeft + tw / 2, cy = oy + _tabTop + th / 2;
        VisualElement cur = HitTestVisitor.Run(host.Root, cx, cy, OwnIds);
        for (int i = 0; cur != null && i < 12; i++)
        {
            if (Ui.TryBounds(cur, out _, out var by, out var bw, out var bh) && bw >= pw * 0.5f && bh >= th + 4 && bh <= 160)
            {
                MoveTabTo(ox + _tabLeft, by + (bh - th) / 2);
                AgentLog.Debug($"Agent tab snapped to bar {Il2CppUtil.TypeName(cur)} '{Ui.Name(cur)}' ({bw:0}x{bh:0})");
                return;
            }
            try { cur = cur.parent; } catch { return; }
        }
    }

    private void SaveTabPosition()
    {
        SnapToBar();
        if (_tabLeft < 0 || !ParentRect(out _, out _, out var pw, out var ph)) return;
        _a.Settings.TabX.Value = (float)Math.Round(_tabLeft / pw, 4);
        _a.Settings.TabY.Value = (float)Math.Round(_tabTop / ph, 4);
        _placeKey = null;
        AgentLog.Info("Agent tab moved — it'll stay there.");
    }

    public void ResetTabPosition()
    {
        _a.Settings.TabX.Value = -1;
        _a.Settings.TabY.Value = -1;
        _needTabPosition = true;
        AgentLog.Info("Agent tab back in its default spot.");
    }

    /// <summary>Opens the panel under the tab (or above it when the tab is low on the screen).</summary>
    private void PlaceDropdown()
    {
        if (!ParentRect(out var ox, out var oy, out var pw, out var ph)) return;
        if (!Ui.TryBounds(_tab, out var tx, out var ty, out var tw, out var th)) return;
        tx -= ox; ty -= oy;
        if (_tabLeft >= 0) { tx = _tabLeft; ty = _tabTop; }
        float dw = _a.Settings.PanelWidth.Value;
        bool alignRight = tx + tw / 2 > pw / 2;
        bool below = ty + th / 2 < ph * 0.6f;
        float left = Clamp(alignRight ? tx + tw - dw : tx, 8, pw - dw - 8);
        var key = $"{left:0}|{ty:0}|{th:0}|{below}|{pw:0}|{ph:0}";
        if (key == _placeKey) return;
        _placeKey = key;

        S.Set(_drop, "position", "Absolute");
        S.Set(_drop, "right", "Auto");
        S.Set(_drop, "left", left);
        if (below)
        {
            float top = ty + th + 6;
            S.Set(_drop, "bottom", "Auto");
            S.Set(_drop, "top", top);
            S.MaxHeight(_drop, Math.Max(200f, ph - top - 12));
        }
        else
        {
            S.Set(_drop, "top", "Auto");
            S.Set(_drop, "bottom", ph - ty + 6);
            S.MaxHeight(_drop, Math.Max(200f, ty - 18));
        }
    }

    private static float Clamp(float v, float min, float max) => max < min ? min : Math.Max(min, Math.Min(max, v));

    // ================================================================== show / hide

    public void Toggle()
    {
        _visible = !_visible;
        if (_visible) _open = true;
    }

    /// <summary>Something needs you: show the panel open.</summary>
    public void Attention()
    {
        _visible = true;
        _open = true;
    }

    public void ShowTerms()
    {
        _view = View.Terms;
        _visible = _open = true;
    }

    // ================================================================== highlights

    public void Highlight(List<(VisualElement el, Color color)> items, double seconds)
    {
        ClearMarks();
        if (_overlay == null || items.Count == 0) return;
        // Overlay may not sit at the panel origin; outline positions are relative to it.
        float ox = 0, oy = 0;
        try { var ob = _overlay.worldBound; if (!float.IsNaN(ob.x)) { ox = ob.x; oy = ob.y; } } catch { }
        foreach (var (el, color) in items)
        {
            if (el == null || Ui.RootOf(el)?.Id != _hostId) continue;
            if (!Ui.TryBounds(el, out var x, out var y, out var w, out var h)) continue;
            var m = new VisualElement();
            S.IgnorePointer(m);
            S.Absolute(m, x - ox - 3, y - oy - 3);
            S.Size(m, w + 6, h + 6);
            S.Border(m, 2, color);
            S.Radius(m, 6);
            _overlay.Add(m);
            _marks.Add(m);
        }
        _marksUntil = Clock.Now + seconds;
    }

    private void ClearMarks()
    {
        foreach (var m in _marks) { try { m.RemoveFromHierarchy(); } catch { } }
        _marks.Clear();
    }

    // ================================================================== tab

    private (string label, Color dot) TabStatus()
    {
        var waiting = _a.Duties.FirstOrDefault(d => d.IsWaitingForYou);
        if (waiting != null) return ("Agent · your turn", Palette.Warn);
        var running = _a.Duties.FirstOrDefault(d => d.IsRunning);
        if (running != null) return ("Agent · " + running.TabStatus, Palette.Good);
        var status = _a.Duties.Select(d => d.TabStatus).FirstOrDefault(t => t != null);
        return status != null ? ("Agent · " + status, Palette.Accent) : ("Agent", Palette.Muted);
    }

    private string TabKey() => TabStatus().label + "|" + _a.Settings.PanelFontSize.Value;

    private void RebuildTab(string key)
    {
        _tabKey = key;
        var (label, dot) = TabStatus();
        try { _tab.Clear(); } catch { return; }
        S.Row(_tab);
        S.Bg(_tab, _tabHover ? Palette.TabHover : Palette.Tab);
        S.Border(_tab, 1, Palette.TabBorder);
        S.Radius(_tab, 6);
        S.Pad(_tab, 8, 14, 8, 12);
        float size = _a.Settings.PanelFontSize.Value + 1;

        var d = new TextElement();
        Reflect.TrySet(d, "text", "●");
        S.Font(d, size, false);
        S.Fg(d, dot);
        S.Margin(d, 0, 7, 0, 0);
        S.IgnorePointer(d);
        _tab.Add(d);

        var t = new TextElement();
        Reflect.TrySet(t, "text", label);
        S.Font(t, size, true);
        S.Fg(t, Palette.Text);
        S.IgnorePointer(t);
        _tab.Add(t);
        _tabDot = d;
        _tabLabel = t;
        _placeKey = null;
    }

    // ================================================================== dropdown

    private string StateKey()
    {
        var duties = string.Join(";", _a.Duties.Select(d => d.StateKey));
        return $"{_visible}|{_open}|{_view}|{AgentLog.Version}|{_a.Data.Revision}|{_a.Settings.TabX.Value}|{duties}";
    }

    private void Rebuild(string key)
    {
        _builtKey = key;
        if (!_open) return;
        _kit.Reset();
        _kit.FontSize = _a.Settings.PanelFontSize.Value;
        try { _drop.Clear(); } catch (Exception ex) { Guard.Report("panel-clear", ex); return; }

        S.Size(_drop, _a.Settings.PanelWidth.Value, null);
        S.Bg(_drop, Palette.Bg);
        S.Radius(_drop, 10);
        S.Border(_drop, 1, Palette.Line);
        S.Pad(_drop, 12, 12, 8, 12);
        S.Column(_drop);
        S.Hidden(_drop);
        _placeKey = null;

        switch (_view)
        {
            case View.JobFilters: BuildJobFilters(); break;
            case View.Terms: BuildTerms(); break;
            default: BuildMain(); break;
        }
    }

    private VisualElement Header(string title, string rightLabel, Action rightAction, string secondLabel = null, Action secondAction = null)
    {
        var row = _kit.Box();
        S.Row(row);
        S.JustifyBetween(row);
        S.Margin(row, 0, 0, 10, 0);
        var t = _kit.Text(title, 0.95f, Palette.Text, bold: true);
        row.Add(t);
        var right = _kit.Box();
        S.Row(right);
        if (secondLabel != null) right.Add(Tiny(_kit.Button(secondLabel, secondAction, BtnKind.Ghost, scale: 0.85f)));
        if (rightLabel != null) right.Add(Tiny(_kit.Button(rightLabel, rightAction, BtnKind.Ghost, scale: 0.85f)));
        row.Add(right);
        _drop.Add(row);
        return row;
    }

    private static VisualElement Tiny(VisualElement b)
    {
        S.Margin(b, 0, 0, 0, 4);
        S.Pad(b, 3, 8, 3, 8);
        return b;
    }

    // ------------------------------------------------------------------ building blocks

    private string M(double v) => v > 0 ? Money.Format(v, _a.Data.Contract.WagePrefix, "") : "not set";

    private VisualElement Card(Color? border = null)
    {
        var card = _kit.Box();
        S.Bg(card, Palette.Card);
        S.Radius(card, 8);
        S.Pad(card, 10, 12, 6, 12);
        S.Margin(card, 0, 0, 10, 0);
        if (border.HasValue) S.Border(card, 1, border.Value);
        _drop.Add(card);
        return card;
    }

    private VisualElement CardHeader(VisualElement card, string title, string badge, Color badgeColor)
    {
        var head = _kit.Box();
        S.Row(head);
        S.JustifyBetween(head);
        head.Add(_kit.Text(title.ToUpperInvariant(), 0.78f, Palette.Muted, bold: true));
        if (badge != null) head.Add(_kit.Text(badge, 0.72f, badgeColor, bold: true));
        card.Add(head);
        return head;
    }

    private VisualElement Line(VisualElement into, string text, float scale = 0.9f, Color? color = null, float top = 4, float bottom = 4, bool bold = false)
    {
        var t = _kit.Text(text, scale, color ?? Palette.Text, bold);
        S.Margin(t, top, 0, bottom, 0);
        into.Add(t);
        return t;
    }

    private VisualElement Row(VisualElement into, bool wrap = true)
    {
        var row = _kit.Box();
        S.Row(row, wrap);
        into.Add(row);
        return row;
    }

    private static VisualElement Small(VisualElement b)
    {
        S.Pad(b, 4, 9, 4, 9);
        S.Margin(b, 0, 5, 5, 0);
        return b;
    }

    private VisualElement Chip(VisualElement into, string label, bool on, Action click)
    {
        var b = Small(_kit.Button(label, click, on ? BtnKind.OptionChosen : BtnKind.Option, scale: 0.8f));
        into.Add(b);
        return b;
    }

    /// <summary>A labelled value with − / + buttons (and optional ≪ / ≫ for big steps).</summary>
    private void Stepper(VisualElement into, string label, string value, Action down, Action up, Action bigDown = null, Action bigUp = null)
    {
        var row = _kit.Box();
        S.Row(row);
        S.JustifyBetween(row);
        S.Margin(row, 2, 0, 2, 0);
        var left = _kit.Box();
        S.Row(left);
        var l = _kit.Text(label, 0.86f, Palette.Muted);
        S.Size(l, 90, null);
        left.Add(l);
        left.Add(_kit.Text(value, 0.95f, Palette.Text, bold: true));
        row.Add(left);
        var btns = _kit.Box();
        S.Row(btns);
        if (bigDown != null) btns.Add(Small(_kit.Button("≪", bigDown, BtnKind.Normal, scale: 0.8f)));
        btns.Add(Small(_kit.Button("−", down, BtnKind.Normal, scale: 0.8f)));
        btns.Add(Small(_kit.Button("+", up, BtnKind.Normal, scale: 0.8f)));
        if (bigUp != null) btns.Add(Small(_kit.Button("≫", bigUp, BtnKind.Normal, scale: 0.8f)));
        row.Add(btns);
        into.Add(row);
    }

    private void ChoiceBlock(VisualElement card, AgentDuty duty)
    {
        var c = duty.Choice;
        if (c == null) return;
        Line(card, c.Title, 0.95f, Palette.Warn, 6, 2, bold: true);
        Line(card, c.Text, 0.86f, Palette.Text, 0, 6);
        var row = Row(card);
        foreach (var (label, key, kind) in c.Options)
            row.Add(_kit.Button(label, () => duty.Choose(key), kind, scale: 0.86f));
    }

    // ------------------------------------------------------------------ main

    private void BuildMain()
    {
        Header("MANAGER AGENT", "Close", () => _open = false);
        BuildJobsCard();
        BuildContractCard();

        Line(_drop, "ACTIVITY", 0.72f, Palette.Faint, 2, 4, bold: true);
        var recent = AgentLog.Recent.Skip(Math.Max(0, AgentLog.Recent.Count - 6)).ToList();
        if (recent.Count == 0) Line(_drop, "Nothing yet.", 0.82f, Palette.Faint, 0, 2);
        foreach (var line in recent) Line(_drop, line, 0.8f, Palette.Muted, 0, 2);

        var tools = Row(_drop);
        S.Margin(tools, 8, 0, 0, 0);
        tools.Add(_kit.Button("Dump UI", () => _a.DumpUi(), BtnKind.Ghost, scale: 0.8f));
        if (_a.Settings.TabX.Value >= 0) tools.Add(_kit.Button("Reset tab position", ResetTabPosition, BtnKind.Ghost, scale: 0.8f));
        Line(tools, $"Drag the Agent tab to move it · {_a.Settings.KeyTogglePanel.Value} hides · {_a.Settings.KeyStop.Value} stops", 0.72f, Palette.Faint, 0, 6);
    }

    private void BuildJobsCard()
    {
        var j = _a.Jobs;
        bool on = j.Enabled;
        var card = Card(j.IsRunning ? Palette.Good : (Color?)null);
        CardHeader(card, "Job hunting", j.IsRunning ? "● APPLYING" : on ? "● ON" : "○ OFF", j.IsRunning ? Palette.Good : on ? Palette.Accent : Palette.Faint);

        string status;
        if (!on) status = "Off. Switch on when you're looking for a new job.";
        else if (j.IsRunning) status = j.Status;
        else
        {
            var days = j.DaysUntilNextCheck;
            status = !_a.Data.Jobs.AutoVisit ? "On — I apply whenever you open the jobs page."
                : days == 0 ? "On — checking jobs as soon as you're idle."
                : days == null ? "On — I'll check jobs regularly."
                : $"On — next check in {days} day{(days == 1 ? "" : "s")}.";
        }
        Line(card, status, 0.92f);
        Line(card, _a.Data.ToFilter().Describe(), 0.8f, Palette.Muted, 0, 4);
        if (j.LastSummary.Length > 0) Line(card, j.LastSummary, 0.78f, Palette.Faint, 0, 6);

        var row = Row(card);
        row.Add(_kit.Button(on ? "Turn off" : "Turn on", () => j.SetEnabled(!on), on ? BtnKind.Normal : BtnKind.Primary));
        if (on && !j.IsRunning) row.Add(_kit.Button("Check jobs now", j.CheckNow));
        if (j.IsRunning) row.Add(_kit.Button("Stop", () => j.Stop("you stopped it"), BtnKind.Danger));
        row.Add(_kit.Button("Filters", () => _view = View.JobFilters, BtnKind.Ghost));
    }

    private void BuildContractCard()
    {
        var n = _a.Contract;
        var c = _a.Data.Contract;
        var card = Card(n.IsWaitingForYou ? Palette.Warn : n.IsRunning ? Palette.Good : (Color?)null);
        CardHeader(card, "Contract negotiation",
            n.IsWaitingForYou ? "● YOUR TURN" : n.IsRunning ? "● NEGOTIATING" : n.OnContractScreen ? "● OFFER ON SCREEN" : n.OnOfferMessage ? "● JOB OFFER" : "○ READY",
            n.IsWaitingForYou ? Palette.Warn : n.IsRunning ? Palette.Good : n.OnContractScreen || n.OnOfferMessage ? Palette.Accent : Palette.Faint);

        var wants = c.Clauses.Count(x => x.Want == "want");
        var unit = c.WageUnit.Length > 0 ? " " + c.WageUnit : "";
        Line(card, c.WageMin > 0
                ? $"Wage {M(c.WageMin)} – {M(c.WageTarget)}{unit} · {c.YearsWant.Format()} (min {c.YearsMin.Format()}) · {wants} clause{(wants == 1 ? "" : "s")}"
                : "Wage range not set yet — open Your terms.", 0.86f, c.WageMin > 0 ? Palette.Text : Palette.Warn);
        if (n.ClubMax.HasValue) Line(card, $"{(n.Club.Length > 0 ? n.Club : "Club")} maximum salary: {M(n.ClubMax.Value)}{unit}", 0.8f, Palette.Muted, 0, 2);
        if (n.Status.Length > 0) Line(card, n.Status, 0.9f, n.IsWaitingForYou ? Palette.Warn : Palette.Text);

        if (n.IsWaitingForYou)
        {
            ChoiceBlock(card, n);
        }
        else
        {
            var row = Row(card);
            if (n.IsRunning) row.Add(_kit.Button("Take back control", () => n.Stop("you took over"), BtnKind.Danger));
            else row.Add(_kit.Button(n.OnOfferMessage ? "Open & negotiate for me" : "Negotiate for me", n.Start, BtnKind.Primary, enabled: n.CanStart));
            row.Add(_kit.Button("Your terms", () => _view = View.Terms, BtnKind.Ghost));
        }
        var opts = Row(card);
        S.Margin(opts, 2, 0, 0, 0);
        Chip(opts, c.AutoNegotiate ? "Auto-start on offers: on" : "Auto-start on offers: off", c.AutoNegotiate, () => { c.AutoNegotiate = !c.AutoNegotiate; _a.Data.Save(); });
        Chip(opts, c.AutoSign ? "Sign automatically: on" : "Sign automatically: off", c.AutoSign, () => { c.AutoSign = !c.AutoSign; _a.Data.Save(); });
    }

    // ------------------------------------------------------------------ job filters

    private void BuildJobFilters()
    {
        Header("JOB FILTERS", "Back", () => _view = View.Main);
        var j = _a.Data.Jobs;
        void Save() => _a.Data.Save();

        Line(_drop, "Club rating at least", 0.84f, Palette.Muted, 0, 4);
        var r1 = Row(_drop);
        foreach (var name in new[] { "Local", "Regional", "National", "Continental", "Worldwide" })
        {
            var captured = name;
            Chip(r1, name == "Local" ? "Any" : name, string.Equals(j.MinRating, name, StringComparison.OrdinalIgnoreCase), () => { j.MinRating = captured; Save(); });
        }

        Line(_drop, "National teams", 0.84f, Palette.Muted, 6, 4);
        var r2 = Row(_drop);
        Chip(r2, "Skip", !j.IncludeNationalTeams, () => { j.IncludeNationalTeams = false; Save(); });
        Chip(r2, "Include", j.IncludeNationalTeams, () => { j.IncludeNationalTeams = true; Save(); });

        Line(_drop, j.KnownNations.Count == 0 ? "Nations — open the jobs page once and the nations you can pick appear here." : "Nations (none picked = any nation)", 0.84f, Palette.Muted, 6, 4);
        var r3 = Row(_drop);
        Chip(r3, "Any", j.Nations.Count == 0, () => { j.Nations.Clear(); Save(); });
        foreach (var nat in j.KnownNations.Union(j.Nations).Distinct().OrderBy(x => x))
        {
            var captured = nat;
            bool picked = j.Nations.Contains(nat);
            Chip(r3, nat, picked, () => { if (picked) j.Nations.Remove(captured); else j.Nations.Add(captured); Save(); });
        }

        Line(_drop, "Open the jobs page for me", 0.84f, Palette.Muted, 6, 4);
        var r4 = Row(_drop);
        Chip(r4, "Off (only when I open it)", !j.AutoVisit, () => { j.AutoVisit = false; Save(); });
        foreach (var d in new[] { 3, 7, 14 })
        {
            var captured = d;
            Chip(r4, $"Every {d} days", j.AutoVisit && j.CheckEveryDays == d, () => { j.AutoVisit = true; j.CheckEveryDays = captured; Save(); });
        }
        if (j.AutoVisit)
        {
            var r5 = Row(_drop);
            Chip(r5, j.ReturnAfterCheck ? "Go back afterwards: on" : "Go back afterwards: off", j.ReturnAfterCheck, () => { j.ReturnAfterCheck = !j.ReturnAfterCheck; Save(); });
        }

        if (j.MenuItemText.Length > 0)
            Line(_drop, $"Way to the jobs page: {(j.MenuParentText.Length > 0 ? j.MenuParentText + " → " : "")}{j.MenuItemText}", 0.76f, Palette.Faint, 6, 2);
        var r6 = Row(_drop);
        S.Margin(r6, 6, 0, 0, 0);
        Line(r6, $"{j.Applied.Count} application{(j.Applied.Count == 1 ? "" : "s")} remembered", 0.76f, Palette.Faint, 4, 4);
        if (j.Applied.Count > 0) r6.Add(Small(_kit.Button("Forget them", () => { j.Applied.Clear(); Save(); }, BtnKind.Ghost, scale: 0.76f)));
    }

    // ------------------------------------------------------------------ your terms

    private void BuildTerms()
    {
        Header("YOUR TERMS", "Back", () => _view = View.Main);
        var c = _a.Data.Contract;
        void Save() => _a.Data.Save();
        var offer = _a.Contract.LastSeenOffer;
        double basis = c.WageTarget > 0 ? c.WageTarget : c.WageMin > 0 ? c.WageMin : offer?.Wage ?? 1000;

        void Adjust(bool min, double factor)
        {
            double v = min ? c.WageMin : c.WageTarget;
            if (v <= 0) v = offer?.Wage > 0 ? offer.Wage : basis;
            double step = Money.NiceStep(v, Math.Abs(factor));
            v = Math.Max(0, Math.Round((v + Math.Sign(factor) * step) / step) * step);
            if (min) { c.WageMin = v; if (c.WageTarget < v) c.WageTarget = v; }
            else { c.WageTarget = v; if (c.WageMin > v) c.WageMin = v; }
            Save();
        }

        Line(_drop, "Wage" + (c.WageUnit.Length > 0 ? " (" + c.WageUnit + ")" : ""), 0.84f, Palette.Muted, 0, 2, bold: true);
        Stepper(_drop, "Minimum", M(c.WageMin), () => Adjust(true, -0.05), () => Adjust(true, 0.05), () => Adjust(true, -0.25), () => Adjust(true, 0.25));
        Stepper(_drop, "Target", M(c.WageTarget), () => Adjust(false, -0.05), () => Adjust(false, 0.05), () => Adjust(false, -0.25), () => Adjust(false, 0.25));
        if (offer != null && offer.Wage > 0)
        {
            var r = Row(_drop);
            r.Add(Small(_kit.Button($"Start from the last offer ({M(offer.Wage)})", () =>
            {
                c.WageMin = Money.RoundAsk(offer.Wage);
                c.WageTarget = Money.RoundAsk(offer.Wage * 1.5);
                Save();
            }, BtnKind.Ghost, scale: 0.8f)));
        }
        Line(_drop, "Never go below the minimum; open at the target (or the club's maximum if lower).", 0.74f, Palette.Faint, 0, 6);

        Line(_drop, "Contract length", 0.84f, Palette.Muted, 4, 2, bold: true);
        Stepper(_drop, "I want", c.YearsWant.Format(), () => { c.YearsWant = Math.Max(1, c.YearsWant - 1); if (c.YearsMin > c.YearsWant) c.YearsMin = c.YearsWant; Save(); },
            () => { c.YearsWant = Math.Min(5, c.YearsWant + 1); Save(); });
        Stepper(_drop, "Minimum", c.YearsMin.Format(), () => { c.YearsMin = Math.Max(1, c.YearsMin - 1); Save(); },
            () => { c.YearsMin = Math.Min(c.YearsWant, c.YearsMin + 1); Save(); });

        Line(_drop, "Clauses — tap to change", 0.84f, Palette.Muted, 8, 4, bold: true);
        foreach (var cl in c.Clauses)
        {
            var captured = cl;
            string tag = cl.Want == "want" ? "✓ Ask for" : cl.Want == "never" ? "✕ Remove" : "– Ignore";
            var kind = cl.Want == "want" ? BtnKind.OptionChosen : BtnKind.Option;
            var b = _kit.Button($"{tag}:  {cl.Name}", () =>
            {
                captured.Want = captured.Want == "want" ? "skip" : captured.Want == "skip" ? "never" : "want";
                Save();
            }, kind, scale: 0.8f);
            S.Margin(b, 0, 0, 4, 0);
            S.Pad(b, 5, 10, 5, 10);
            _drop.Add(b);
        }
        Line(_drop, "Ask-for clauses are requested one at a time, most important (top) first, after the wage is agreed.", 0.74f, Palette.Faint, 2, 2);
    }
}
