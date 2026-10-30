using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ManagerAgent;

/// <summary>
/// A simulated Football Manager, laid out like the real FM27 screens (jobs page, offer message,
/// contract offer screen), with a club that negotiates. Implements <see cref="IGame"/> so the agent's
/// duties run against it exactly as they do in the game.
/// </summary>
internal sealed class FakeFm : IGame
{
    // ---------------------------------------------------------------- IGame
    public double Now { get; private set; }
    public ScreenModel Screen { get; private set; }
    public bool Busy => false;
    public int PressMethods => 3;
    public double UserIdleSeconds { get; set; } = 30;
    public void WantFast(bool fast) { }

    /// <summary>Which press method (0 submit, 1 invoke, 2 mouse) actually works; others are ignored by the "game".</summary>
    public int WorkingMethod = 0;
    public bool KeyboardWorks = true;
    public bool FieldSettable = true;

    public readonly List<string> Events = new();
    public int Presses;

    // ---------------------------------------------------------------- world
    public string Page = "portal";     // portal | jobs | message | contract
    public string PreviousPage = "portal";
    public DateTime Date = new(2026, 6, 8);
    public bool CareerMenuOpen;

    public sealed class Job
    {
        public string Coach = "-", Team, League, Rating = "National", Nation = "ENG", Position = "10th", Security = "Available", Finances = "Okay";
        public bool Applied;
    }
    public readonly List<Job> Jobs = new();
    public int Selected;
    public float ScrollPos;
    // FM27-like variations
    public bool SplitDate;          // "TUE" and "6/9/2026" as two separate texts
    public bool RowsClickable = true;
    public bool NoScrollView;       // the list scrolls, but isn't a recognisable scroll view
    public float HeaderShift;       // header texts don't line up with the cells
    public bool ConfirmOnSingleApply = true;
    public string Confirm;            // null | "all" | job team
    public const float RowH = 60, ListTop = 220, ListH = 780;

    // contract
    public string ClubName = "Hampton & Richmond";
    public sealed class Club
    {
        public double Initial = 725, Max = 1500, WalkAway = 1100;
        public double InitialYears = 1, MaxYears = 3;
        public int Patience = 10;
        public HashSet<string> RefusedClauses = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> ClauseCost = new(StringComparer.OrdinalIgnoreCase);
        public double Step = 25;
    }
    public Club C = new();
    public Terms Offer;               // what's on screen
    public Terms ClubLast;            // the club's latest position
    public int Suggestions;
    public bool TalksEnded, Signed;
    public string Chat = "Here is our initial offer:";
    public string SalaryMode = "field";   // field | list | buttons
    public bool ClampToMax = true;
    public bool Editing;
    public string FieldText;
    public string Popup;              // salary | length | clause
    public bool PlayerContract;       // a player's contract (no "Job" row)

    public static readonly string[] ClauseList =
    {
        "Yearly Salary Raise", "Promotion Salary Raise", "Relegation Salary Drop",
        "Contract Extension After Promotion", "Percentage Of Club Compensation Required For Coaching Role",
    };

    /// <summary>Clauses that need a contract of at least this many years (greyed out otherwise — as on your screen).</summary>
    public static double ClauseMinYears(string c) => c.Contains("Raise") || c.Contains("Drop") ? 2 : 1;

    public FakeFm()
    {
        Rebuild();
    }

    public void OpenContract()
    {
        Page = "contract";
        Offer = new Terms { Wage = C.Initial, Years = C.InitialYears };
        ClubLast = Offer.Clone();
        Suggestions = 0;
        Chat = "Here is our initial offer:";
        Rebuild();
    }

    public void OpenOfferMessage()
    {
        Page = "message";
        Rebuild();
    }

    public void Tick(double dt = 0.05)
    {
        Now += dt;
        Rebuild();
    }

    // ---------------------------------------------------------------- actions

    public bool Press(UiNode node, int attempt, out string how)
    {
        how = attempt switch { 0 => "submit", 1 => "invoke", _ => "mouse" };
        if (attempt < 0 || attempt >= PressMethods) return false;
        Presses++;
        if (attempt != WorkingMethod) return true; // pressed, but the game ignores this method
        if (node.Handle is Action a) { a(); Rebuild(); }
        return true;
    }

    public bool SetField(UiNode field, string text, out string how)
    {
        how = "field value";
        if (!FieldSettable || !Editing) return false;
        CommitSalary(text);
        Rebuild();
        return true;
    }

    public bool Type(string text, bool selectAllFirst, bool pressEnter)
    {
        if (KeyboardWorks && Editing)
        {
            FieldText = selectAllFirst ? text : FieldText + text;
            if (pressEnter) CommitSalary(FieldText);
            Rebuild();
        }
        return true;
    }

    public bool Key(GameKey key)
    {
        if (key == GameKey.Escape) { Popup = null; Editing = false; }
        if (key == GameKey.Enter && Editing) CommitSalary(FieldText);
        Rebuild();
        return true;
    }

    public bool Scroll(UiNode scroll, float pixels, out string how)
    {
        how = "scrollbar";
        ScrollPos = Math.Max(0, Math.Min(MaxScroll, ScrollPos + pixels));
        Rebuild();
        return true;
    }

    private float MaxScroll => Math.Max(0, Jobs.Count * RowH - ListH);

    private void CommitSalary(string text)
    {
        Editing = false;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return;
        SetWage(v);
    }

    private void SetWage(double v)
    {
        if (ClampToMax) v = Math.Min(v, C.Max);
        v = Math.Max(0, v);
        Offer.Wage = v;
        Events.Add($"wage {v}");
    }

    private bool Dirty => Offer != null && !Offer.SameAs(ClubLast, 0.5);

    private void Suggest()
    {
        Suggestions++;
        Events.Add($"suggest {Offer}");
        if (Suggestions > C.Patience)
        {
            TalksEnded = true;
            Page = "portal";
            Events.Add("talks ended");
            return;
        }
        var p = Offer.Clone();
        var counter = p.Clone();
        bool ok = true;
        if (p.Wage > C.WalkAway + 0.5)
        {
            ok = false;
            var last = ClubLast.Wage;
            counter.Wage = Math.Round((last + (C.WalkAway - last) * 0.7) / C.Step) * C.Step;
            counter.Wage = Math.Min(counter.Wage, C.WalkAway);
        }
        if (p.Years > C.MaxYears) { ok = false; counter.Years = C.MaxYears; }
        foreach (var cl in p.Clauses.ToList())
        {
            if (C.RefusedClauses.Contains(cl)) { ok = false; counter.Clauses.Remove(cl); }
            else if (C.ClauseCost.TryGetValue(cl, out var cost) && !ClubLast.Has(cl)) { ok = false; counter.Wage = Math.Min(counter.Wage, p.Wage) - cost; }
        }
        if (ok)
        {
            Chat = "We are happy to agree to those terms.";
            ClubLast = p.Clone();
        }
        else
        {
            Chat = $"We can offer {counter.Wage:#,0} per week.";
            ClubLast = counter.Clone();
            Offer = counter.Clone();
        }
    }

    // ---------------------------------------------------------------- screen

    private ScreenModel _s;
    private readonly Stack<int> _parents = new();

    private UiNode Add(string text, float x, float y, float w, float h, Action click = null, bool enabled = true, bool clickable = false)
    {
        var n = new UiNode
        {
            Id = _s.Nodes.Count,
            Order = _s.Nodes.Count,
            Text = text,
            X = x, Y = y, W = w, H = h,
            Clickable = clickable || click != null,
            Enabled = enabled,
            Handle = click,
            Type = click != null ? "SIButton" : text != null ? "SIText" : "VisualElement",
            Parent = _parents.Count > 0 ? _parents.Peek() : -1,
        };
        var p = _s[n.Parent];
        n.Click = n.Clickable ? n.Id : p == null ? -1 : p.Clickable ? p.Id : p.Click;
        _s.Nodes.Add(n);
        return n;
    }

    /// <summary>A button: clickable box with its label text inside.</summary>
    private UiNode Button(string label, float x, float y, float w, float h, Action click, bool enabled = true)
    {
        var b = Add(null, x, y, w, h, enabled ? click : null, enabled, clickable: true);
        _parents.Push(b.Id);
        Add(label, x + 10, y + h / 2 - 10, Math.Max(10, label.Length * 8), 20);
        _parents.Pop();
        return b;
    }

    private void Rebuild()
    {
        _s = new ScreenModel { Time = Now, Width = 1920, Height = 1080 };
        _parents.Clear();

        // top bar
        Add(null, 12, 4, 36, 56, () => { var p = Page; Page = PreviousPage; PreviousPage = p; CareerMenuOpen = false; });
        if (SplitDate)
        {
            Add(Date.ToString("ddd", CultureInfo.InvariantCulture).ToUpperInvariant(), 1483, 22, 30, 18);
            Add(Date.ToString("M/d/yyyy", CultureInfo.InvariantCulture), 1517, 22, 62, 18);
        }
        else Add(Date.ToString("ddd", CultureInfo.InvariantCulture).ToUpperInvariant() + " " + Date.ToString("M/d/yyyy", CultureInfo.InvariantCulture), 1483, 22, 95, 18);
        Add("11:00", 1592, 22, 40, 18);
        Button("Career", 490, 64, 70, 26, () => CareerMenuOpen = !CareerMenuOpen);
        Button("Player Database", 570, 64, 120, 26, () => { });
        if (CareerMenuOpen)
        {
            int i = 0;
            foreach (var item in new[] { "Profile", "History", "Head Coach Vacancies", "Job Applications" })
            {
                var captured = item;
                Button(item, 490, 100 + i++ * 34, 200, 30, () =>
                {
                    CareerMenuOpen = false;
                    if (captured == "Head Coach Vacancies") { PreviousPage = Page; Page = "jobs"; }
                });
            }
        }

        switch (Page)
        {
            case "jobs": BuildJobs(); break;
            case "message": BuildMessage(); break;
            case "contract": BuildContract(); break;
            default: Add("Portal", 115, 72, 40, 18); break;
        }
        Screen = _s;
    }

    private void BuildJobs()
    {
        Add($"{Jobs.Count(j => j.Security == "Available")} Available Jobs", 41, 147, 175, 24);
        Button("Apply for all jobs", 1660, 140, 128, 36, () => Confirm = "all");
        float hy = 200, hs = HeaderShift;
        Add("Current Head Coach", 90 + hs, hy, 105, 16); Add("Team", 329 + hs, hy, 30, 16); Add("Club Rating", 607 + hs, hy, 60, 16);
        Add("Nation", 746 + hs, hy, 36, 16); Add("Position", 868 + hs, hy, 42, 16); Add("Form", 933 + hs, hy, 26, 16);
        Add("Job Security", 1037 + hs, hy, 66, 16); Add("Finances", 1159 + hs, hy, 46, 16);

        var scroll = Add(null, 42, ListTop, 1255, ListH);
        if (!NoScrollView) { scroll.IsScroll = true; scroll.Type = "ScrollView"; }
        else scroll.Type = "StreamedTable";
        scroll.ScrollValue = ScrollPos; scroll.ScrollLow = 0; scroll.ScrollHigh = MaxScroll; scroll.CanScroll = MaxScroll > 0;
        _parents.Push(scroll.Id);
        for (int i = 0; i < Jobs.Count; i++)
        {
            float y = ListTop + i * RowH - ScrollPos;
            if (y + RowH < ListTop - RowH || y > ListTop + ListH + RowH) continue; // virtualised list
            var j = Jobs[i];
            int idx = i;
            var row = RowsClickable ? Add(null, 42, y, 1255, RowH - 2, () => Selected = idx) : Add(null, 42, y, 1255, RowH - 2);
            row.Type = RowsClickable ? "TableRowNavigatable" : "VisualElement";
            _parents.Push(row.Id);
            Add(null, 55, y + 20, 20, 20, () => Selected = idx);
            Add(j.Coach, 90, y + 20, 80, 18);
            var link = Add(null, 393, y + 4, 120, 22, () => Events.Add("opened club page " + j.Team));
            _parents.Push(link.Id);
            Add(j.Team, 393, y + 5, j.Team.Length * 9, 20);
            _parents.Pop();
            Add(j.League, 412, y + 30, 120, 18);
            Add(j.Rating, 639, y + 20, 70, 18);
            Add(j.Nation, 770, y + 20, 34, 18);
            Add(j.Position, 872, y + 20, 30, 18);
            Add(j.Security, 1043, y + 20, 64, 22);
            Add(j.Finances, 1159, y + 20, 40, 18);
            _parents.Pop();
        }
        _parents.Pop();

        if (Selected >= 0 && Selected < Jobs.Count)
        {
            var j = Jobs[Selected];
            Add(j.Team, 1385, 228, j.Team.Length * 11, 26);
            Add("Head Coach Position", 1385 + j.Team.Length * 11 + 8, 228, 200, 26);
            Add("Reputation", 1330, 383, 70, 16);
            Add(j.Rating, 1359, 403, 60, 18);
            Add("Finances", 1330, 442, 60, 16);
            Add(j.Finances, 1330, 462, 40, 18);
            Button("Declare Interest In Job", 1562, 983, 168, 36, () => { });
            Button("Apply For Job", 1748, 983, 110, 36, () =>
            {
                if (ConfirmOnSingleApply) Confirm = j.Team;
                else { j.Applied = true; Events.Add("applied " + j.Team); }
            }, enabled: !j.Applied && j.Security == "Available");
        }

        if (Confirm != null)
        {
            Add("Please Confirm", 892, 465, 136, 26);
            Add(Confirm == "all" ? "Apply to all teams with a vacancy for head coach?" : $"Apply for the {Confirm} job?", 794, 505, 330, 18);
            Button("Yes", 910, 543, 40, 34, () =>
            {
                if (Confirm == "all") { foreach (var x in Jobs.Where(x => x.Security == "Available")) x.Applied = true; Events.Add("applied all"); }
                else { var x = Jobs.First(q => q.Team == Confirm); x.Applied = true; Events.Add("applied " + x.Team); }
                Confirm = null;
            });
            Button("No", 962, 543, 40, 34, () => Confirm = null);
        }
    }

    private void BuildMessage()
    {
        Add($"{ClubName} approach you", 668, 167, 350, 22);
        Add($"{ClubName} have offered you a one-year contract on ${C.Initial:#,0} per week to take the head coach job.", 623, 228, 1200, 20);
        Add("Must Respond", 636, 1003, 120, 20);
        Button("Reject Offer", 1508, 995, 102, 36, () => { Page = "portal"; Events.Add("rejected"); });
        Button("Enter Contract Negotiations", 1620, 995, 232, 36, () => { OpenContract(); });
    }

    private void BuildContract()
    {
        Add("About Club", 48, 515, 72, 18);
        Add(ClubName, 130, 545, 190, 22);
        Add("England", 154, 570, 56, 16);
        Add("Transfer Budget", 48, 730, 92, 16); Add("$0", 152, 730, 20, 16);
        Add("Payroll Budget", 48, 753, 84, 16); Add("$17K per week", 144, 753, 82, 16);

        Add("Current Offer", 428, 147, 116, 22);
        Button("Use Existing Contract", 1336, 144, 164, 36, () => { });
        if (!PlayerContract)
        {
            Add("Job", 505, 212, 26, 18);
            Add("Must be Head Coach", 505, 232, 140, 16);
        }
        else Add("Squad Status", 505, 212, 90, 18);

        // salary
        Add("Salary", 505, 263, 48, 20);
        if (Editing)
        {
            var f = Add(null, 1208, 256, 170, 36, null, clickable: false);
            f.IsField = true; f.Type = "TextField"; f.FieldValue = FieldText;
        }
        else
        {
            Button($"${Offer.Wage:#,0} p/w", 1208, 256, 170, 36, () =>
            {
                if (SalaryMode == "field") { Editing = true; FieldText = Math.Round(Offer.Wage).ToString(CultureInfo.InvariantCulture); }
                else if (SalaryMode == "list") Popup = Popup == "salary" ? null : "salary";
            });
        }
        Button("-", 1390, 256, 30, 36, () => SetWage(Offer.Wage - C.Step));
        Button("+", 1428, 256, 30, 36, () => { if (Offer.Wage + C.Step <= C.Max) SetWage(Offer.Wage + C.Step); });

        Add("Start Date", 505, 307, 80, 20);
        Button("Immediate - 6/18/2026", 1248, 299, 210, 36, () => { });

        Add("Contract Length", 505, 350, 126, 20);
        Button(YearsLabel(Offer.Years), 1216, 342, 242, 36, () => Popup = Popup == "length" ? null : "length");

        Add("Bonuses & Clauses", 424, 437, 145, 20);
        Button("Add Bonus", 1154, 420, 158, 36, () => { });
        Button("Add Clause", 1321, 420, 160, 36, () => Popup = Popup == "clause" ? null : "clause");
        int k = 0;
        foreach (var cl in Offer.Clauses.ToList())
        {
            float y = 480 + k++ * 40;
            Add(cl, 440, y, cl.Length * 8, 20);
            var captured = cl;
            Button("×", 1450, y - 2, 24, 24, () => Offer.Clauses.Remove(captured));
        }
        Button("Reset", 424, 996, 58, 36, () => Offer = ClubLast.Clone());

        // chat + actions
        Add("Ben Hudson", 1605, 108, 80, 22);
        Add("Chairperson", 1605, 132, 72, 16);
        Add(Chat, 1658, 182, 170, 20);
        if (Dirty) Button("Suggest Terms", 1550, 930, 355, 48, Suggest);
        else Button("Finalize Deal", 1550, 930, 355, 48, () => { Signed = true; Events.Add("signed " + Offer); Page = "portal"; });
        Button("Delay for one week", 1550, 980, 355, 48, () => Events.Add("delayed"));

        // popups (drawn last, on top)
        if (Popup == "clause")
        {
            int i = 0;
            foreach (var cl in ClauseList)
            {
                var captured = cl;
                bool enabled = Offer.Years >= ClauseMinYears(cl) && !Offer.Has(cl);
                float y = 465 + i++ * 34;
                var item = Add(null, 990, y, 490, 32, enabled ? () => { Offer.Clauses.Add(captured); Popup = null; } : null, enabled, clickable: true);
                _parents.Push(item.Id);
                var t = Add(cl, 1024, y + 6, cl.Length * 8, 20);
                t.Enabled = enabled;
                _parents.Pop();
            }
        }
        else if (Popup == "length")
        {
            for (int y = 1; y <= 5; y++)
            {
                int captured = y;
                Button(YearsLabel(y), 1216, 385 + (y - 1) * 34, 242, 32, () => { Offer.Years = captured; Popup = null; });
            }
        }
        else if (Popup == "salary")
        {
            int i = 0;
            var values = new List<double>();
            for (double v = C.Initial; v < C.Max; v += Math.Max(C.Step, Math.Round((C.Max - C.Initial) / 8 / C.Step) * C.Step)) values.Add(v);
            values.Add(C.Max); // the list tops out at the club's maximum
            foreach (var v in values)
            {
                double captured = v;
                Button($"${v:#,0} p/w", 1208, 300 + i++ * 30, 170, 28, () => { SetWage(captured); Popup = null; });
            }
        }
    }

    public static string YearsLabel(double y)
    {
        int n = (int)Math.Round(y);
        return $"{n} year{(n == 1 ? "" : "s")} (Jun 2026 - Jun {2026 + n})";
    }

    // ---------------------------------------------------------------- jobs fixture

    public static FakeFm WithJobs()
    {
        var f = new FakeFm { Page = "jobs" };
        void J(string team, string league, string rating, string nation, string security = "Available", string fin = "Okay", string coach = "-") =>
            f.Jobs.Add(new Job { Team = team, League = league, Rating = rating, Nation = nation, Security = security, Finances = fin, Coach = coach });
        J("Egersund", "First Division", "National", "NOR");
        J("Stjarnan", "Premier Division", "National", "ISL");
        J("Hapoel Acre", "National League", "National", "ISR");
        J("Hapoel Kfar-Saba", "National League", "National", "ISR");
        J("Forest Green", "Enterprise National", "Regional", "ENG");
        J("Gateshead", "Enterprise National", "Regional", "ENG");
        J("Woking", "Enterprise National", "Regional", "ENG", fin: "Insecure");
        J("Hegelmann", "TOPLYGA", "National", "LTU");
        J("Hampton & Richmond", "Enterprise South", "Local", "ENG");
        J("Slough", "Enterprise South", "Local", "ENG");
        J("Iceland U21s", "Euro U21 Ch'ship Quals", "Continental", "ISL", fin: "-");
        J("Ghana", "African Nations Quals", "Continental", "GHA", fin: "-");
        J("Ireland U19s", "-", "Continental", "IRL", fin: "-");
        J("Italy U19s", "-", "Worldwide", "ITA", fin: "-");
        J("Lithuania", "-", "Continental", "LTU", fin: "-");
        J("Molde", "Premier Division", "National", "NOR", "Insecure", coach: "Sindre Tjelmeland");
        J("Shamrock Rovers", "Premier Division", "National", "IRL", "Insecure", "Secure", "Stephen Bradley");
        J("Waterford", "Premier Division", "National", "IRL", "Insecure", coach: "Graham Coughlan");
        J("Finn Harps", "First Division", "National", "IRL", "Insecure", coach: "Patrick McEleney");
        J("Moss", "First Division", "National", "NOR", "Insecure", coach: "Geir Bakke");
        f.Rebuild();
        return f;
    }
}
