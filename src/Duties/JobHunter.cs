namespace ManagerAgent;

/// <summary>
/// Job hunting. While switched on, the agent applies for open jobs:
///   • by opening the jobs page itself every few in-game days (when you're idle), and
///   • whenever you open the jobs page yourself.
/// By default it presses the game's own "Apply for all jobs" and confirms. If you choose
/// "only jobs matching my filters", it reads the list and applies to those one by one
/// (still using "Apply for all jobs" when every open job happens to match).
/// It learns the menu path to the jobs page the first time you open it yourself.
/// </summary>
internal sealed class JobHunter : AgentDuty
{
    public readonly GameDate Date = new();

    /// <summary>Real-time fallback when the in-game date can't be read.</summary>
    public const double FallbackMinutes = 15;
    public const double IdleBeforeVisit = 8;

    private bool _onPage, _passDoneThisVisit, _navigatedByUs, _dumped, _dateWarned;
    private string _status = "";
    private string _lastSummary = "";
    private double _navBlockedUntil, _lastRealCheck = double.NegativeInfinity;
    private string _navBlockedDate = "";
    private readonly List<(string text, double at)> _clicks = new();

    public JobHunter(IAgentHost host) : base(host) { }

    public override string Id => "jobs";
    public override string Title => "Job hunting";
    public bool Enabled => Data.Jobs.Enabled;
    public string Status => _status;
    public string LastSummary => _lastSummary;
    public bool OnJobsPage => _onPage;
    public bool DateKnown => Date.Date != null;

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
            AgentLog.Info("Job hunting on — " + (ApplyToAll ? "applying for all open jobs" : "only jobs matching: " + Data.ToFilter().Describe()) + ".");
        }
    }

    public void CheckNow()
    {
        if (Runner.Active) return;
        if (_onPage) Runner.Start("jobs", Pass(false));
        else Runner.Start("jobs", NavigateAndPass());
    }

    /// <summary>Days until the next automatic check (null = date unknown, 0 = due).</summary>
    public int? DaysUntilNextCheck
    {
        get
        {
            if (!GameDate.TryIso(Data.Jobs.LastCheckDate, out _)) return 0; // never checked (or old format)
            var since = Date.DaysSince(Data.Jobs.LastCheckDate);
            return since == null ? null : Math.Max(0, Data.Jobs.CheckEveryDays - since.Value);
        }
    }

    private bool Due
    {
        get
        {
            var days = DaysUntilNextCheck;
            if (days == 0 && (DateKnown || Game.Now - _lastRealCheck >= FallbackMinutes * 60)) return true;
            if (days == null) return Game.Now - _lastRealCheck >= FallbackMinutes * 60;
            return false;
        }
    }

    // ------------------------------------------------------------------ watching

    protected override void Watch()
    {
        var s = Game.Screen;
        if (s == null) return;
        Date.Update(s, Game.Now);

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

        if (!onPage && Data.Jobs.AutoVisit && Due && Game.Now >= _navBlockedUntil && (_navBlockedDate == "" || _navBlockedDate != Date.Iso) &&
            Game.UserIdleSeconds >= IdleBeforeVisit && Game.Now - Date.ChangedAt > 3 && !JobsPage.ConfirmShowing(s, Text) &&
            ContractScreen.OfferMessageButton(s, Text) == null && !ContractScreen.IsShowing(s, Text))
        {
            if (!DateKnown && !_dateWarned)
            {
                _dateWarned = true;
                AgentLog.Info($"Couldn't read the in-game date — I'll check jobs every {FallbackMinutes:0} minutes instead.");
            }
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

    // ------------------------------------------------------------------ navigation

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
            _lastRealCheck = Game.Now;
            _status = "Couldn't open the jobs page";
            AgentLog.Warn("I couldn't open the jobs page by myself. Open it once yourself (Career → Head Coach Vacancies) and I'll learn the way.");
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
        foreach (var label in ScreenModel.Split(Text.JobsMenuItems))
        {
            var item = Pick(n => ScreenModel.Eq(n.Text, label));
            if (item != null) return item;
        }
        return Pick(n => (n.Text.IndexOf("vacanc", StringComparison.OrdinalIgnoreCase) >= 0 || n.Text.IndexOf("job", StringComparison.OrdinalIgnoreCase) >= 0) && n.Text.Length < 30);
    }

    // ------------------------------------------------------------------ the pass

    public bool ApplyToAll => !string.Equals(Data.Jobs.ApplyMode, "filtered", StringComparison.OrdinalIgnoreCase);

    private IEnumerable<Wait> Pass(bool navigated)
    {
        yield return Fresh();
        if (!JobsPage.IsShowing(Game.Screen, Text)) { Quit("I'm not on the jobs page any more"); yield break; }
        var inner = ApplyToAll ? ApplyAllPass() : FilteredPass();
        foreach (var w in inner) yield return w;
        foreach (var w in GoBack(navigated)) yield return w;
    }

    /// <summary>Stops a pass early, saying why (in the panel and the log).</summary>
    private void Quit(string why)
    {
        _status = "Stopped: " + why;
        AgentLog.Warn("Job hunting: " + why + ". I'll try again tomorrow (in-game), or press \"Check jobs now\".");
        _lastRealCheck = Game.Now;
        _navBlockedUntil = Game.Now + 600;
        _navBlockedDate = Date.Iso ?? "";
    }

    /// <summary>Default: the game's own "Apply for all jobs", then confirm.</summary>
    private IEnumerable<Wait> ApplyAllPass()
    {
        _status = "Applying for all jobs…";
        var s0 = Game.Screen;
        var btn = s0.Button(Text.ApplyForAllJobs);
        if (btn == null)
        {
            Finish("No \"Apply for all jobs\" button on the jobs page — nothing new to apply for (if there are open jobs, use \"Dump UI\" and send me the file).");
            yield break;
        }
        if (!btn.Enabled) { Finish("Nothing new — \"Apply for all jobs\" is greyed out (already applied everywhere)."); yield break; }

        // Success = the confirm box appears, or (if you've ticked "don't show again") the selected job's
        // "Apply For Job" goes grey.
        var single = s0.Button(Text.ApplyForJob);
        bool singleWasEnabled = single != null && single.Enabled;
        bool Reacted(ScreenModel s)
        {
            if (JobsPage.ConfirmButton(s, Text) != null) return true;
            var b = s.Button(Text.ApplyForJob);
            return singleWasEnabled && b != null && !b.Enabled;
        }

        var r = new Result();
        foreach (var w in PressUntil(x => x.Button(Text.ApplyForAllJobs), Reacted, "Apply for all jobs", r, 3.0))
            yield return w;
        if (!r.Ok)
        {
            Quit(singleWasEnabled ? "\"Apply for all jobs\" didn't react" : "I pressed \"Apply for all jobs\" but couldn't see a confirm box");
            yield break;
        }

        AgentLog.Info("Pressed \"Apply for all jobs\" (" + r.How + ").", panel: false);
        if (JobsPage.ConfirmButton(Game.Screen, Text) != null)
        {
            _status = "Confirming…";
            foreach (var w in PressUntil(x => JobsPage.ConfirmButton(x, Text), x => !JobsPage.ConfirmShowing(x, Text), "the confirm button", r, 3.0))
                yield return w;
            if (!r.Ok) { Quit("I couldn't press the confirm button (" + r.Error + ")"); yield break; }
        }
        Finish("Applied for all open jobs (Apply for all jobs).");
    }

    /// <summary>Only jobs matching your filters.</summary>
    private IEnumerable<Wait> FilteredPass()
    {
        _status = "Reading the job list…";
        var filter = Data.ToFilter();

        // 1. Read the whole list.
        var all = new Dictionary<string, JobRow>();
        var order = new List<string>();
        foreach (var w in ScanList(all, order)) yield return w;
        if (!JobsPage.IsShowing(Game.Screen, Text)) { Quit("the jobs page closed while I was reading it"); yield break; }
        var rows = order.Select(k => all[k]).ToList();
        LearnNations(rows);
        AgentLog.Info($"Jobs page: read {rows.Count} rows — " + string.Join("; ", rows.Take(40).Select(r => r.Debug())), panel: false);

        var vacancies = rows.Where(r => r.IsVacancy).ToList();
        var skipped = new Dictionary<string, int>();
        var matching = new List<JobRow>();
        foreach (var r in vacancies)
        {
            if (filter.Passes(r, out var why)) matching.Add(r);
            else skipped[why] = skipped.TryGetValue(why, out var k) ? k + 1 : 1;
        }
        var recent = RecentlyApplied();
        var todo = matching.Where(r => !recent.Contains(r.Key)).ToList();
        int already = matching.Count - todo.Count;

        // 2. Apply.
        int applied = 0, failed = 0;
        var applyAll = Game.Screen.Button(Text.ApplyForAllJobs);
        bool everyVacancyMatches = vacancies.Count > 0 && matching.Count == vacancies.Count;
        bool usedApplyAll = false;

        if (todo.Count >= 2 && everyVacancyMatches && applyAll != null && applyAll.Enabled)
        {
            _status = $"Applying for all {vacancies.Count} jobs…";
            var ra = new Result();
            foreach (var w in ApplyAll(ra)) yield return w;
            if (ra.Ok)
            {
                usedApplyAll = true;
                applied = todo.Count;
                foreach (var r in todo) Remember(r);
                AgentLog.Info($"Applied for all {vacancies.Count} open jobs (they all match your filters).");
            }
            else AgentLog.Warn("\"Apply for all jobs\" didn't work (" + ra.Error + ") — applying one by one.");
        }

        if (!usedApplyAll && todo.Count > 0)
        {
            var left = new HashSet<string>(todo.Select(r => r.Key));
            foreach (var w in ScrollToTop()) yield return w;
            int noProgress = 0;
            for (int loop = 0; loop < 300 && left.Count > 0; loop++)
            {
                yield return Fresh();
                var s = Game.Screen;
                if (!JobsPage.IsShowing(s, Text)) { Quit("the jobs page closed while I was applying"); yield break; }
                var visible = Visible(s, out var scroll);
                var next = visible.FirstOrDefault(r => left.Contains(r.Key));
                if (next != null)
                {
                    noProgress = 0;
                    left.Remove(next.Key);
                    _status = $"Applying… {applied} of {todo.Count} ({next.Team})";
                    var res = new Result();
                    foreach (var w in ApplyTo(next, res)) yield return w;
                    if (res.Value > 0) applied++;
                    else if (res.Value == 0) already++;
                    else failed++;
                    continue;
                }
                if (++noProgress > 2) break;
                var sr = new Result();
                foreach (var w in ScrollDown(visible, scroll, sr)) yield return w;
                if (!sr.Ok) break;
            }
            failed += left.Count;
        }

        var parts = new List<string> { $"{vacancies.Count} open, applied to {applied}" };
        if (usedApplyAll) parts[0] += " (Apply for all jobs)";
        if (already > 0) parts.Add($"{already} already applied");
        if (skipped.Count > 0) parts.Add($"{skipped.Values.Sum()} didn't match ({string.Join(", ", skipped.OrderByDescending(k => k.Value).Take(3).Select(k => $"{k.Value} {k.Key}"))})");
        if (failed > 0) parts.Add($"{failed} couldn't be applied for");
        Finish("Jobs checked: " + string.Join(", ", parts) + ".");
    }

    private void Finish(string summary)
    {
        _lastSummary = (Date.Raw != null ? Date.Raw + ": " : "") + summary;
        AgentLog.Info(summary);
        Data.Jobs.LastCheckDate = Date.Iso;
        _lastRealCheck = Game.Now;
        Data.Save();
        _status = "On";
    }

    private IEnumerable<Wait> GoBack(bool navigated)
    {
        if (!navigated || !Data.Jobs.ReturnAfterCheck) yield break;
        UiNode Back(ScreenModel s) => s.Nodes.Where(n => n.Clickable && n.X < 80 && n.Y < 80 && n.W < 80 && n.H < 80).OrderBy(n => n.X).FirstOrDefault();
        if (Back(Game.Screen) == null) yield break;
        var r = new Result();
        foreach (var w in PressUntil(Back, s => !JobsPage.IsShowing(s, Text), "the back button", r, 2.0)) yield return w;
        _navigatedByUs = false;
    }

    // ------------------------------------------------------------------ list reading

    private List<JobRow> Visible(ScreenModel s, out UiNode scroll)
    {
        var rows = JobsPage.Read(s, Text, out scroll, out var tl, out var tr, out var hb);
        var sc = scroll;
        var visible = rows.Where(r => sc == null ? r.BottomY <= s.Height + 1 : r.Top >= sc.Y - 4 && r.BottomY <= sc.Bottom + 4).ToList();

        // Double-check the reading: every "Available" in the table should belong to an open job we read.
        int words = JobsPage.VacancyWordsOnScreen(s, Text, tl, tr, hb, scroll);
        int open = rows.Count(r => r.IsVacancy);
        if (!_dumped && (words > open || rows.Any(r => r.Team.Length == 0 || ScreenModel.Split(Text.Vacancy).Any(v => ScreenModel.Eq(r.Team, v)))))
        {
            _dumped = true;
            Host.DumpUi($"jobs page read oddly ({open} open jobs read, {words} \"Available\" on screen)");
        }
        return visible;
    }

    private IEnumerable<Wait> ScanList(Dictionary<string, JobRow> all, List<string> order)
    {
        foreach (var w in ScrollToTop()) yield return w;
        int stale = 0;
        for (int page = 0; page < 60; page++)
        {
            yield return Fresh();
            var s = Game.Screen;
            if (!JobsPage.IsShowing(s, Text)) yield break;
            var visible = Visible(s, out var scroll);
            int added = 0;
            foreach (var r in visible)
            {
                if (all.ContainsKey(r.Key)) continue;
                all[r.Key] = r;
                order.Add(r.Key);
                added++;
            }
            _status = $"Reading the job list… {order.Count} so far";
            stale = added == 0 ? stale + 1 : 0;
            if (stale >= 2) yield break;
            var sr = new Result();
            foreach (var w in ScrollDown(visible, scroll, sr)) yield return w;
            if (!sr.Ok) yield break;
        }
    }

    private IEnumerable<Wait> ScrollToTop()
    {
        var rows = JobsPage.Read(Game.Screen, Text, out var sc, out _, out _, out _);
        if (sc != null)
        {
            if (sc.ScrollValue <= sc.ScrollLow + 1) yield break;
            Game.Scroll(sc, -(sc.ScrollValue - sc.ScrollLow) - 100, out _);
            yield return Wait.Fresh(Game, 0.3);
            yield break;
        }
        // No scroll view to read: scroll up a page at a time until the list stops moving.
        for (int i = 0; i < 30 && rows.Count > 0; i++)
        {
            var before = string.Join("|", rows.Select(v => v.Key));
            float page = Math.Max(120f, rows[^1].BottomY - rows[0].Top) * 0.8f;
            if (!Game.Scroll(rows[0].Select, -page, out _)) yield break;
            var w = Wait.Until(Game, s => string.Join("|", JobsPage.Read(s, Text, out _, out _, out _, out _).Select(v => v.Key)) != before, 1.0, 0.2);
            yield return w;
            if (w.TimedOut) yield break;
            rows = JobsPage.Read(Game.Screen, Text, out _, out _, out _, out _);
        }
    }

    /// <summary>Scrolls the list down by most of a page. r.Ok = false at the end of the list.</summary>
    private IEnumerable<Wait> ScrollDown(List<JobRow> visible, UiNode scroll, Result r)
    {
        r.Ok = false;
        if (scroll != null && (!scroll.CanScroll || scroll.ScrollValue >= scroll.ScrollHigh - 1)) yield break;
        var target = scroll ?? visible.LastOrDefault()?.Select;
        if (target == null) yield break;
        float page = scroll != null ? scroll.H * 0.8f : Math.Max(120f, (visible.LastOrDefault()?.BottomY ?? 400) - (visible.FirstOrDefault()?.Top ?? 0)) * 0.8f;
        var before = string.Join("|", visible.Select(v => v.Key));
        if (!Game.Scroll(target, page, out _)) yield break;
        var w = Wait.Until(Game, s => string.Join("|", JobsPage.Read(s, Text, out _, out _, out _, out _).Select(v => v.Key)) != before, 1.5, 0.2);
        yield return w;
        r.Ok = !w.TimedOut;
    }

    // ------------------------------------------------------------------ applying

    private IEnumerable<Wait> ApplyAll(Result r)
    {
        foreach (var w in PressUntil(s => s.Button(Text.ApplyForAllJobs), s => JobsPage.ConfirmButton(s, Text) != null, "Apply for all jobs", r))
            yield return w;
        if (!r.Ok) yield break;
        foreach (var w in PressUntil(s => JobsPage.ConfirmButton(s, Text), s => !JobsPage.ConfirmShowing(s, Text), "the confirm button", r))
            yield return w;
    }

    /// <summary>Result.Value: 1 applied, 0 already applied / not open to you, -1 failed.</summary>
    private IEnumerable<Wait> ApplyTo(JobRow row, Result res)
    {
        res.Value = -1;
        string key = row.Key, team = row.Team;
        JobRow Find(ScreenModel s) => JobsPage.Read(s, Text, out _, out _, out _, out _).FirstOrDefault(r => r.Key == key);
        JobsPage.Read(Game.Screen, Text, out _, out _, out var tableRight, out var headerBottom);

        // Select the row (unless it already is). Selection shows in the job panel on the right.
        if (!JobsPage.IsSelected(Game.Screen, row, tableRight))
        {
            var panelBefore = JobsPage.SidePanelKey(Game.Screen, tableRight, headerBottom);
            var r = new Result();
            foreach (var w in PressUntil(s => Find(s)?.Select,
                         s => { var x = Find(s); return x == null || JobsPage.IsSelected(s, x, tableRight) || JobsPage.SidePanelKey(s, tableRight, headerBottom) != panelBefore; },
                         team, r, 2.0))
                yield return w;
            if (!r.Ok)
            {
                AgentLog.Warn($"Couldn't select {team}: {r.Error}");
                yield break;
            }
            yield return Wait.Fresh(Game, 0.25);
        }

        var btn = Game.Screen.Button(Text.ApplyForJob);
        if (btn == null || !btn.Enabled)
        {
            res.Value = 0;
            yield break;
        }

        bool Applied(ScreenModel s)
        {
            if (JobsPage.ConfirmShowing(s, Text)) return true;
            var b = s.Button(Text.ApplyForJob);
            return b == null || !b.Enabled;
        }

        var ar = new Result();
        foreach (var w in PressUntil(s => s.Button(Text.ApplyForJob), Applied, "Apply For Job", ar, 2.5))
            yield return w;
        if (!ar.Ok)
        {
            AgentLog.Warn($"Applying to {team} didn't work: {ar.Error}");
            yield break;
        }

        if (JobsPage.ConfirmShowing(Game.Screen, Text))
        {
            foreach (var w in PressUntil(s => JobsPage.ConfirmButton(s, Text), s => !JobsPage.ConfirmShowing(s, Text), "the confirm button", ar))
                yield return w;
            if (!ar.Ok)
            {
                AgentLog.Warn($"Couldn't confirm the application to {team}.");
                yield break;
            }
        }

        res.Value = 1;
        AgentLog.Info("Applied: " + row.Describe());
        Remember(row);
    }

    private void Remember(JobRow row)
    {
        Data.Jobs.Applied.Add(new AppliedJob { Team = row.Team, League = row.League, Date = Date.Iso });
        while (Data.Jobs.Applied.Count > 300) Data.Jobs.Applied.RemoveAt(0);
        Data.Save();
    }

    /// <summary>Jobs applied for in the last 30 in-game days (no point applying twice). The game itself also greys out
    /// "Apply For Job" for jobs you've applied for, so this only saves time.</summary>
    private HashSet<string> RecentlyApplied()
    {
        var set = new HashSet<string>();
        foreach (var a in Data.Jobs.Applied)
        {
            var since = Date.DaysSince(a.Date);
            if (since != null && since >= 0 && since < 30) set.Add((a.Team + "|" + a.League).ToLowerInvariant());
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
