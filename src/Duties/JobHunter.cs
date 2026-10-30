namespace ManagerAgent;

/// <summary>
/// Job hunting. While switched on, the agent applies for every vacancy that matches your filters:
///   • whenever you open the "Available Jobs" page, and
///   • (optionally) by opening that page itself every few in-game days while you're idle.
/// It learns the menu path to the jobs page the first time you open it yourself.
/// </summary>
internal sealed class JobHunter : AgentDuty
{
    public readonly GameDate Date = new();

    private bool _onPage, _passDoneThisVisit, _navigatedByUs;
    private string _status = "";
    private string _lastSummary = "";
    private double _navBlockedUntil;
    private string _navBlockedDate = "";
    private readonly List<(string text, double at)> _clicks = new();

    public JobHunter(IAgentHost host) : base(host) { }

    public override string Id => "jobs";
    public override string Title => "Job hunting";
    public bool Enabled => Data.Jobs.Enabled;
    public string Status => _status;
    public string LastSummary => _lastSummary;
    public bool OnJobsPage => _onPage;

    public override string TabStatus => IsRunning ? "applying" : Enabled ? "job hunting" : null;

    public override string StateKey => $"{Enabled}|{Runner.Active}|{_status}|{_lastSummary}|{_onPage}|{Date.Iso}|{Data.Revision}";

    public void SetEnabled(bool on)
    {
        Data.Jobs.Enabled = on;
        Data.Save();
        if (!on)
        {
            Stop("job hunting switched off");
            _status = "";
            AgentLog.Info("Job hunting off.");
        }
        else
        {
            _passDoneThisVisit = false;
            AgentLog.Info("Job hunting on — " + Data.ToFilter().Describe() + ".");
        }
    }

    public void CheckNow()
    {
        if (Runner.Active) return;
        if (_onPage) Runner.Start("jobs", Pass(false));
        else Runner.Start("jobs", NavigateAndPass());
    }

    /// <summary>Days until the next automatic check (null = unknown, 0 = due).</summary>
    public int? DaysUntilNextCheck
    {
        get
        {
            if (string.IsNullOrEmpty(Data.Jobs.LastCheckDate)) return 0;
            var since = Date.DaysSince(Data.Jobs.LastCheckDate);
            return since == null ? null : Math.Max(0, Data.Jobs.CheckEveryDays - since.Value);
        }
    }

    // ------------------------------------------------------------------ watching

    protected override void Watch()
    {
        var s = Game.Screen;
        if (s == null) return;
        Date.Update(s);

        bool onPage = JobsPage.IsShowing(s, Text);
        if (onPage && !_onPage) Arrived();
        if (!onPage && _onPage) { _passDoneThisVisit = false; _navigatedByUs = false; }
        _onPage = onPage;

        if (!Enabled || Runner.Active || Host.OtherDutyBusy(this)) return;

        if (onPage && !_passDoneThisVisit)
        {
            _passDoneThisVisit = true;
            Runner.Start("jobs", Pass(_navigatedByUs));
            return;
        }

        bool due = DaysUntilNextCheck == 0;
        if (!onPage && due && Data.Jobs.AutoVisit && Game.Now >= _navBlockedUntil && _navBlockedDate != Date.Iso &&
            Game.UserIdleSeconds >= 3 && s.Text(Text.ConfirmTitle) == null &&
            ContractScreen.OfferMessageButton(s, Text) == null && !ContractScreen.IsShowing(s, Text))
        {
            Runner.Start("jobs", NavigateAndPass());
        }
    }

    public override void OnUserClick(UiNode clicked)
    {
        var s = Game.Screen;
        var text = clicked?.Text ?? (s != null ? s.TextOf(clicked) : "");
        if (!string.IsNullOrWhiteSpace(text) && text.Length < 40)
        {
            _clicks.Add((text.Trim(), Game.Now));
            while (_clicks.Count > 4) _clicks.RemoveAt(0);
        }
        if (IsRunning) Stop("you took over");
    }

    /// <summary>You opened the jobs page yourself: remember which menu entries got you there.</summary>
    private void Arrived()
    {
        if (_navigatedByUs || Runner.Active) return;
        var now = Game.Now;
        var recent = _clicks.Where(c => now - c.at < 8).ToList();
        if (recent.Count == 0) return;
        var item = recent[^1];
        if (now - item.at > 4) return;
        bool changed = false;
        if (Data.Jobs.MenuItemText != item.text) { Data.Jobs.MenuItemText = item.text; changed = true; }
        if (recent.Count >= 2)
        {
            var parent = recent[^2].text;
            if (parent != item.text && Data.Jobs.MenuParentText != parent) { Data.Jobs.MenuParentText = parent; changed = true; }
        }
        if (changed)
        {
            Data.Save();
            AgentLog.Info($"Learned the way to the jobs page: {(Data.Jobs.MenuParentText.Length > 0 ? Data.Jobs.MenuParentText + " → " : "")}{Data.Jobs.MenuItemText}.");
        }
    }

    protected override void OnStopped() => _status = Enabled ? "On" : "";

    // ------------------------------------------------------------------ scripts

    private IEnumerable<Wait> NavigateAndPass()
    {
        _status = "Opening the jobs page…";
        var start = Game.Screen;
        var parentLabels = string.IsNullOrEmpty(Data.Jobs.MenuParentText) ? Text.CareerMenu : Data.Jobs.MenuParentText + "|" + Text.CareerMenu;

        var r = new Result();
        foreach (var w in PressUntil(
                     s => s.Button(parentLabels, c => c.Y < 160) ?? s.Text(parentLabels, n => n.Y < 160),
                     s => MenuItem(s, start) != null || JobsPage.IsShowing(s, Text),
                     "the " + ScreenModel.Split(parentLabels)[0] + " menu", r, 2.0))
            yield return w;

        if (r.Ok && !JobsPage.IsShowing(Game.Screen, Text))
        {
            foreach (var w in PressUntil(s => MenuItem(s, start), s => JobsPage.IsShowing(s, Text), "the jobs menu entry", r, 5.0))
                yield return w;
        }

        if (!r.Ok || !JobsPage.IsShowing(Game.Screen, Text))
        {
            _navBlockedUntil = Game.Now + 120;
            _navBlockedDate = Date.Iso;
            _status = "Couldn't open the jobs page";
            AgentLog.Warn("I couldn't open the jobs page by myself. Open it once yourself (Career → jobs) and I'll learn the way.");
            yield break;
        }

        _navigatedByUs = true;
        _onPage = true;
        _passDoneThisVisit = true;
        foreach (var w in Pass(true)) yield return w;
    }

    private UiNode MenuItem(ScreenModel s, ScreenModel before)
    {
        var fresh = s.NewTexts(before);
        UiNode Pick(Func<UiNode, bool> p) => fresh.Where(p).Select(s.ClickableOf).FirstOrDefault(c => c != null);
        if (!string.IsNullOrEmpty(Data.Jobs.MenuItemText))
        {
            var learned = Pick(n => ScreenModel.Eq(n.Text, Data.Jobs.MenuItemText));
            if (learned != null) return learned;
        }
        return Pick(n => ScreenModel.Split(Text.JobsMenuItems).Any(l => ScreenModel.Eq(n.Text, l)))
               ?? Pick(n => n.Text.IndexOf("job", StringComparison.OrdinalIgnoreCase) >= 0 && n.Text.Length < 30);
    }

    private IEnumerable<Wait> Pass(bool navigated)
    {
        _status = "Checking jobs…";
        var filter = Data.ToFilter();
        int applied = 0, already = 0, failed = 0;
        var skipped = new Dictionary<string, int>();
        yield return Fresh();
        if (!JobsPage.IsShowing(Game.Screen, Text)) { _status = "Not on the jobs page"; yield break; }

        if (filter.IsWideOpen && Game.Screen.Button(Text.ApplyForAllJobs) != null)
        {
            // Everything matches: use the game's own "Apply for all jobs".
            var ra = new Result();
            foreach (var w in PressUntil(s => s.Button(Text.ApplyForAllJobs), s => s.Text(Text.ConfirmTitle) != null, "Apply for all jobs", ra))
                yield return w;
            if (ra.Ok)
            {
                foreach (var w in PressUntil(s => s.Text(Text.ConfirmTitle) != null ? s.Button(Text.Yes) : null, s => s.Text(Text.ConfirmTitle) == null, "Yes", ra))
                    yield return w;
            }
            Finish(navigated, ra.Ok ? "Applied for all available jobs." : "Couldn't use \"Apply for all jobs\": " + ra.Error);
            foreach (var w in GoBack(navigated)) yield return w;
            yield break;
        }

        // Start from the top of the list.
        JobsPage.Read(Game.Screen, Text, out var sc0, out _, out _, out _);
        if (sc0 != null && sc0.ScrollValue > sc0.ScrollLow + 1)
        {
            Game.Scroll(sc0, -(sc0.ScrollValue - sc0.ScrollLow) - 100, out _);
            yield return Wait.Fresh(Game, 0.3);
        }

        var attempted = new HashSet<string>();
        var recentlyApplied = RecentlyApplied();
        int noProgress = 0;
        for (int loop = 0; loop < 300; loop++)
        {
            yield return Fresh();
            var s = Game.Screen;
            if (!JobsPage.IsShowing(s, Text)) { _status = "Left the jobs page"; yield break; }
            var rows = JobsPage.Read(s, Text, out var scroll, out _, out var tableRight, out _);
            LearnNations(rows);

            var visible = rows.Where(r => scroll == null ? r.BottomY <= s.Height : r.Top >= scroll.Y - 4 && r.BottomY <= scroll.Bottom + 4).ToList();
            var next = visible.FirstOrDefault(r => !attempted.Contains(r.Key));
            if (next != null)
            {
                noProgress = 0;
                attempted.Add(next.Key);
                if (!filter.Passes(next, out var why))
                {
                    skipped[why] = skipped.TryGetValue(why, out var k) ? k + 1 : 1;
                    continue;
                }
                if (recentlyApplied.Contains(next.Key)) { already++; continue; }

                _status = $"Applying… {applied} so far ({next.Team})";
                var res = new Result();
                foreach (var w in ApplyTo(next.Key, next.Team, res)) yield return w;
                if (res.Value > 0) applied++;
                else if (res.Value == 0) already++;
                else failed++;
                continue;
            }

            // Nothing new on screen: scroll down.
            var target = scroll ?? rows.LastOrDefault()?.Select;
            bool atEnd = scroll != null && (!scroll.CanScroll || scroll.ScrollValue >= scroll.ScrollHigh - 1);
            if (target == null || atEnd || ++noProgress > 2) break;
            float page = scroll != null ? scroll.H * 0.8f : 300f;
            if (!Game.Scroll(target, page, out _)) break;
            yield return Wait.Fresh(Game, 0.3);
        }

        var parts = new List<string> { $"applied to {applied}" };
        if (already > 0) parts.Add($"{already} already applied");
        if (skipped.Count > 0) parts.Add($"{skipped.Values.Sum()} didn't match ({string.Join(", ", skipped.OrderByDescending(k => k.Value).Take(3).Select(k => $"{k.Value} {k.Key}"))})");
        if (failed > 0) parts.Add($"{failed} couldn't be applied for");
        Finish(navigated, "Jobs checked: " + string.Join(", ", parts) + ".");
        foreach (var w in GoBack(navigated)) yield return w;
    }

    private void Finish(bool navigated, string summary)
    {
        _lastSummary = (Date.Raw ?? "") + (Date.Raw != null ? ": " : "") + summary;
        AgentLog.Info(summary);
        if (Date.Iso.Length > 0) Data.Jobs.LastCheckDate = Date.Iso;
        else if (string.IsNullOrEmpty(Data.Jobs.LastCheckDate)) Data.Jobs.LastCheckDate = "checked";
        Data.Save();
        _status = "On";
    }

    private IEnumerable<Wait> GoBack(bool navigated)
    {
        if (!navigated || !Data.Jobs.ReturnAfterCheck) yield break;
        var back = Game.Screen?.Nodes.Where(n => n.Clickable && n.X < 80 && n.Y < 80 && n.W < 80 && n.H < 80).OrderBy(n => n.X).FirstOrDefault();
        if (back == null) yield break;
        Game.Press(back, 0, out _);
        yield return Wait.Fresh(Game, 0.3);
        _navigatedByUs = false;
    }

    /// <summary>Result.Value: 1 applied, 0 already applied / not open to you, -1 failed.</summary>
    private IEnumerable<Wait> ApplyTo(string key, string team, Result res)
    {
        res.Value = -1;
        JobRow Find(ScreenModel s) => JobsPage.Read(s, Text, out _, out _, out _, out _).FirstOrDefault(r => r.Key == key);
        float tableRight = 0;
        JobsPage.Read(Game.Screen, Text, out _, out _, out tableRight, out _);

        var r = new Result();
        foreach (var w in PressUntil(s => Find(s)?.Select,
                     s => { var row = Find(s); return row == null || JobsPage.IsSelected(s, row, tableRight); },
                     team, r, 2.0))
            yield return w;
        if (!r.Ok)
        {
            AgentLog.Warn($"Couldn't select {team}: {r.Error}");
            yield break;
        }
        yield return Wait.Fresh(Game, 0.25);

        var btn = Game.Screen.Button(Text.ApplyForJob);
        if (btn == null || !btn.Enabled)
        {
            res.Value = 0;
            yield break;
        }

        bool Applied(ScreenModel s)
        {
            if (s.Text(Text.ConfirmTitle) != null) return true;
            var b = s.Button(Text.ApplyForJob);
            return b == null || !b.Enabled;
        }

        foreach (var w in PressUntil(s => s.Button(Text.ApplyForJob), Applied, "Apply For Job", r, 2.5))
            yield return w;
        if (!r.Ok)
        {
            AgentLog.Warn($"Applying to {team} didn't work: {r.Error}");
            yield break;
        }

        if (Game.Screen.Text(Text.ConfirmTitle) != null)
        {
            foreach (var w in PressUntil(s => s.Text(Text.ConfirmTitle) != null ? s.Button(Text.Yes) : null, s => s.Text(Text.ConfirmTitle) == null, "Yes", r))
                yield return w;
            if (!r.Ok)
            {
                AgentLog.Warn($"Couldn't confirm the application to {team}.");
                yield break;
            }
        }

        res.Value = 1;
        var row0 = Find(Game.Screen);
        AgentLog.Info("Applied: " + (row0?.Describe() ?? team));
        Data.Jobs.Applied.Add(new AppliedJob { Team = team, League = row0?.League ?? "", Date = Date.Iso });
        while (Data.Jobs.Applied.Count > 200) Data.Jobs.Applied.RemoveAt(0);
        Data.Save();
    }

    /// <summary>Jobs applied for in the last 30 in-game days (no point applying twice).</summary>
    private HashSet<string> RecentlyApplied()
    {
        var set = new HashSet<string>();
        foreach (var a in Data.Jobs.Applied)
        {
            var since = Date.DaysSince(a.Date);
            if (since == null || since < 30) set.Add((a.Team + "|" + a.League).ToLowerInvariant());
        }
        return set;
    }

    private void LearnNations(IEnumerable<JobRow> rows)
    {
        bool changed = false;
        foreach (var r in rows)
        {
            if (r.Nation.Length is < 2 or > 4 || r.IsNationalTeam) continue;
            if (Data.Jobs.KnownNations.Contains(r.Nation)) continue;
            Data.Jobs.KnownNations.Add(r.Nation);
            changed = true;
        }
        if (changed)
        {
            Data.Jobs.KnownNations.Sort(StringComparer.OrdinalIgnoreCase);
            Data.Save();
        }
    }
}
