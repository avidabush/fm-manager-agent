using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ManagerAgent;

/// <summary>Runs the agent's duties against <see cref="FakeFm"/> and checks the outcomes.</summary>
internal static class AgentTests
{
    private static int pass, fail;
    private static string Tmp;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) pass++; else fail++;
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (ok || detail == "" ? "" : "   → " + detail));
    }

    private sealed class Host : IAgentHost
    {
        public FakeFm Fm;
        public IGame Game => Fm;
        public GameText Text { get; } = new();
        public AgentData Data { get; }
        public List<AgentDuty> Duties = new();
        public int Attentions, TermsShown, Dumps;
        public void DumpUi(string reason) { Dumps++; Console.WriteLine("      (dump requested: " + reason + ")"); }
        public Host(FakeFm fm, string name)
        {
            Fm = fm;
            Data = new AgentData(Path.Combine(Tmp, name + ".json"));
            Data.Load();
        }
        public void Attention() => Attentions++;
        public void ShowTerms() => TermsShown++;
        public bool OtherDutyBusy(AgentDuty me) => Duties.Any(d => d != me && d.IsRunning);

        /// <summary>Runs the game + duties until <paramref name="until"/> is true or time runs out.</summary>
        public bool Run(Func<bool> until, double seconds = 120)
        {
            double end = Fm.Now + seconds;
            while (Fm.Now < end)
            {
                Fm.Tick();
                foreach (var d in Duties) d.Tick();
                if (until()) return true;
            }
            return false;
        }
    }

    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--replay") return DumpReplay.Run(args[1]);
        Tmp = Path.Combine(Path.GetTempPath(), "ma-agent-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(Tmp);
        typeof(BepInEx.Paths).GetProperty("ConfigPath")!.SetValue(null, Tmp);
        try
        {
            Parsing();
            Planner();
            PlannerSimulations();
            JobsTable();
            JobHunting();
            Negotiation();
        }
        catch (Exception ex) { fail++; Console.WriteLine("CRASH " + ex); }
        finally { try { Directory.Delete(Tmp, true); } catch { } }
        Console.WriteLine();
        Console.WriteLine($"{pass} passed, {fail} failed");
        return fail == 0 ? 0 : 1;
    }

    // ================================================================ parsing

    private static void Parsing()
    {
        Console.WriteLine("— parsing");
        Check("Money: $725 p/w", Money.TryParse("$725 p/w", out var m) && m.Value == 725 && m.Prefix == "$" && m.Unit == "p/w", $"{m.Value} '{m.Prefix}' '{m.Unit}'");
        Check("Money: $1,000 p/w", Money.Parse("$1,000 p/w") == 1000);
        Check("Money: $17K per week", Money.Parse("$17K per week") == 17000);
        Check("Money: $27.86K", Math.Abs((Money.Parse("$27.86K") ?? 0) - 27860) < 0.01);
        Check("Money: plain 725", Money.Parse("725") == 725);
        Check("Money: €1.200 (dot thousands)", Money.Parse("€1.200 p/w") == 1200);
        Check("Money: £2.5M", Money.Parse("£2.5M") == 2_500_000);
        Check("Money: no number", Money.Parse("Salary") == null);
        Check("Money format", Money.Format(1250, "$", "p/w") == "$1,250 p/w", Money.Format(1250, "$", "p/w"));
        Check("Nice steps", Money.NiceStep(725) == 25 && Money.NiceStep(1000) == 50 && Money.NiceStep(17000) == 500, $"{Money.NiceStep(725)} {Money.NiceStep(1000)} {Money.NiceStep(17000)}");
        Check("Years: 1 year (Jun 2026 - Jun 2027)", YearsText.Parse("1 year (Jun 2026 - Jun 2027)") == 1);
        Check("Years: 3 years (…)", YearsText.Parse("3 years (Jun 2026 - Jun 2029)") == 3);
        Check("Years: 18 months", YearsText.Parse("18 months") == 1.5);
        Check("Years: none in 'Immediate - 6/18/2026'", YearsText.Parse("Immediate - 6/18/2026") == null);
        Check("Game date M/d/yyyy", GameDate.Parse("6/18/2026", null) == new DateTime(2026, 6, 18));
        Check("Game date ambiguous → nearest to previous", GameDate.Parse("7/6/2026", new DateTime(2026, 6, 30)) == new DateTime(2026, 7, 6));
        Check("Clause names match", ClauseNames.Same("Yearly Salary Raise", "yearly salary raise") && !ClauseNames.Same("Promotion Salary Raise", "Yearly Salary Raise"));
    }

    // ================================================================ planner

    private static NegotiationPrefs Prefs(double min, double target, double years = 3, double yearsMin = 1, params string[] clauses) => new()
    {
        WageMin = min, WageTarget = target, YearsWant = years, YearsMin = yearsMin,
        WantClauses = clauses.ToList(), NeverClauses = new List<string> { "Relegation Salary Drop" },
    };

    private static Terms T(double wage, double years, params string[] clauses) => new() { Wage = wage, Years = years, Clauses = clauses.ToList() };

    private static void Planner()
    {
        Console.WriteLine("— negotiation strategy");
        var p = new NegotiationPlanner(Prefs(900, 1500), null);
        var d = p.Next(T(725, 1));
        Check("Opens at target with preferred length", d.Kind == DecisionKind.Propose && d.Terms.Wage == 1500 && d.Terms.Years == 3, d.Terms?.ToString());

        p = new NegotiationPlanner(Prefs(900, 1500), 1200);
        d = p.Next(T(725, 1));
        Check("Never asks above the club maximum", d.Terms.Wage == 1200, d.Terms.ToString());

        p = new NegotiationPlanner(Prefs(900, 1500), null);
        p.Next(T(725, 1));
        d = p.Next(T(1000, 3));
        Check("Counter: meets them about halfway", d.Kind == DecisionKind.Propose && d.Terms.Wage > 1000 && d.Terms.Wage < 1500, d.Terms?.ToString());

        p = new NegotiationPlanner(Prefs(900, 1500), null);
        var off = p.Next(T(1600, 3));
        Check("Offer already above target → no wage haggling", off.Kind != DecisionKind.Propose || off.Terms.Wage == 1600, off.Kind + " " + off.Terms);

        p = new NegotiationPlanner(Prefs(1200, 1500), null);
        p.Next(T(725, 1));
        Decision last = null;
        for (int i = 0; i < 8; i++) { last = p.Next(T(800, 3)); if (last.Kind != DecisionKind.Propose) break; if (last.Terms.Wage < 1200) break; }
        Check("Club stuck below minimum → hands back, never proposes below min", last.Kind == DecisionKind.NeedUser && last.Reason == NeedReason.BelowMinimum, last.Kind + " " + last.Terms);

        p = new NegotiationPlanner(Prefs(900, 1500), 800);
        d = p.Next(T(725, 1));
        Check("Club maximum below minimum → hands back immediately", d.Kind == DecisionKind.NeedUser && d.Reason == NeedReason.ClubMaxBelowMinimum);

        p = new NegotiationPlanner(Prefs(900, 1000, 3, 2), null);
        p.Next(T(1000, 1));
        d = p.Next(T(1000, 1));
        var d2 = p.Next(T(1000, 1));
        Check("Length refused twice below your minimum → hands back", d2.Kind == DecisionKind.NeedUser && d2.Reason == NeedReason.LengthTooShort, d.Kind + " / " + d2.Kind + " " + d2.Reason);

        p = new NegotiationPlanner(Prefs(900, 1000, 3, 1, "Yearly Salary Raise", "Promotion Salary Raise"), null);
        d = p.Next(T(1000, 3));
        Check("Agreed wage → asks first clause", d.Kind == DecisionKind.Propose && d.Terms.Has("Yearly Salary Raise") && d.Terms.Wage == 1000, d.Terms?.ToString());
        d = p.Next(T(1000, 3, "Yearly Salary Raise"));
        Check("Clause accepted → keeps it, asks next", d.Kind == DecisionKind.Propose && d.Terms.Has("Yearly Salary Raise") && d.Terms.Has("Promotion Salary Raise"), d.Terms?.ToString());
        d = p.Next(T(1000, 3, "Yearly Salary Raise"));
        Check("Clause refused → done, keeps the accepted one", d.Kind == DecisionKind.Accept && d.Terms.Has("Yearly Salary Raise") && !d.Terms.Has("Promotion Salary Raise"), d.Kind + " " + d.Terms);

        p = new NegotiationPlanner(Prefs(900, 1000, 3, 1, "Yearly Salary Raise"), null);
        p.Next(T(1000, 3));
        d = p.Next(T(900, 3, "Yearly Salary Raise"));
        Check("Clause costing too much wage → drops it, goes back to agreed", d.Kind == DecisionKind.Propose && !d.Terms.Has("Yearly Salary Raise") && d.Terms.Wage == 1000, d.Terms?.ToString());
        p = new NegotiationPlanner(Prefs(900, 1000, 3, 1, "Yearly Salary Raise"), null);
        p.Next(T(1000, 3));
        d = p.Next(T(975, 3, "Yearly Salary Raise"));
        Check("Clause costing a little (≤5%) → keeps it", d.Kind == DecisionKind.Accept && d.Terms.Has("Yearly Salary Raise"), d.Kind + " " + d.Terms);

        p = new NegotiationPlanner(Prefs(900, 1000), null);
        d = p.Next(T(1000, 3, "Relegation Salary Drop"));
        Check("Removes a never-clause the club included", d.Kind == DecisionKind.Propose && !d.Terms.Has("Relegation Salary Drop"), d.Terms?.ToString());

        var np = Prefs(900, 5000);
        np.MaxSuggestions = 3;
        p = new NegotiationPlanner(np, null);
        p.Next(T(725, 1));
        int proposals = 1;
        double wage = 900;
        for (int i = 0; i < 10; i++)
        {
            d = p.Next(T(wage, 3));
            if (d.Kind != DecisionKind.Propose) break;
            proposals++;
            wage += 10;
        }
        Check("Respects the proposal limit", proposals <= 3 && d.Kind == DecisionKind.Accept, $"{proposals} proposals, {d.Kind}");
    }

    /// <summary>Thousands of random clubs: the agent must never accept below your minimum, never ask above the club's cap, and always finish.</summary>
    private static void PlannerSimulations()
    {
        Console.WriteLine("— negotiation simulations");
        var rng = new Random(27);
        int runs = 3000, deals = 0, handBacks = 0, belowMin = 0, overCap = 0, endless = 0, beatOffer = 0, fair = 0, fairCount = 0;
        for (int run = 0; run < runs; run++)
        {
            double initial = Math.Round((300 + rng.NextDouble() * 20000) / 10) * 10;
            double walk = initial * (1 + rng.NextDouble() * 0.8);
            double cap = walk * (1 + rng.NextDouble() * 0.3);
            double min = initial * (0.8 + rng.NextDouble() * 0.9);
            double target = min * (1 + rng.NextDouble() * 0.8);
            double maxYears = 1 + rng.Next(4);
            bool knownCap = rng.NextDouble() < 0.7;
            var refused = new HashSet<string>(FakeFm.ClauseList.Where(_ => rng.NextDouble() < 0.4), StringComparer.OrdinalIgnoreCase);
            var prefs = Prefs(min, target, 1 + rng.Next(4), 1, "Yearly Salary Raise", "Promotion Salary Raise", "Contract Extension After Promotion");
            var p = new NegotiationPlanner(prefs, knownCap ? cap : null);
            var clubLast = T(initial, 1);
            var current = clubLast.Clone();
            Decision d = null;
            int steps = 0;
            for (; steps < 60; steps++)
            {
                d = p.Next(current);
                if (d.Kind != DecisionKind.Propose) break;
                var prop = d.Terms.Clone();
                if (prop.Wage > Math.Min(target, knownCap ? cap : double.MaxValue) + 1 && prop.Wage > current.Wage + 1) overCap++;
                prop.Wage = Math.Min(prop.Wage, cap); // the game's salary cap
                p.Proposed(prop);
                // club
                var counter = prop.Clone();
                bool ok = true;
                if (prop.Wage > walk) { ok = false; counter.Wage = Math.Round(clubLast.Wage + (walk - clubLast.Wage) * 0.7); }
                if (prop.Years > maxYears) { ok = false; counter.Years = maxYears; }
                foreach (var c in prop.Clauses.ToList()) if (refused.Contains(c)) { ok = false; counter.Clauses.Remove(c); }
                current = ok ? prop.Clone() : counter;
                clubLast = current.Clone();
            }
            if (steps >= 60) endless++;
            if (d.Kind == DecisionKind.Accept)
            {
                deals++;
                if (d.Terms.Wage < min - 1)
                {
                    belowMin++;
                    if (belowMin <= 3) Console.WriteLine($"      below-min case: initial={initial} walk={walk:0} cap={cap:0} min={min:0} target={target:0} knownCap={knownCap} maxYears={maxYears} → {d.Terms} ({d.Why})");
                }
                if (d.Terms.Wage >= initial) beatOffer++;
                if (walk >= min && target >= walk) { fairCount++; if (d.Terms.Wage >= walk * 0.9) fair++; }
            }
            else handBacks++;
        }
        Console.WriteLine($"      {runs} clubs: {deals} deals, {handBacks} handed back; got ≥90% of what the club would pay in {fair}/{fairCount} winnable talks");
        Check("Never accepts below your minimum", belowMin == 0, belowMin.ToString());
        Check("Never asks above your target / the club's maximum", overCap == 0, overCap.ToString());
        Check("Always finishes", endless == 0, endless.ToString());
        Check("Every deal is at least the club's opening offer", beatOffer == deals, $"{beatOffer}/{deals}");
        Check("Gets close to the club's real limit in most winnable talks (≥85%)", fairCount > 0 && fair >= fairCount * 0.85, $"{fair}/{fairCount}");
    }

    // ================================================================ jobs

    private static void JobsTable()
    {
        Console.WriteLine("— jobs page reading");
        var fm = FakeFm.WithJobs();
        var t = new GameText();
        Check("Detects the jobs page", JobsPage.IsShowing(fm.Screen, t));
        var rows = JobsPage.Read(fm.Screen, t, out var scroll, out _, out var right, out _);
        Check("Reads visible rows", rows.Count >= 12, rows.Count.ToString());
        var eg = rows.First();
        Check("Row columns parsed", eg.Team == "Egersund" && eg.League == "First Division" && eg.Rating == ClubRating.National && eg.Nation == "NOR" && eg.IsVacancy && !eg.IsNationalTeam,
            $"{eg.Team}|{eg.League}|{eg.Rating}|{eg.Nation}|{eg.Security}|{eg.Finances}");
        Check("Found the scroll area", scroll != null && scroll.CanScroll);
        var ice = rows.FirstOrDefault(r => r.Team == "Iceland U21s");
        Check("National team detected", ice != null && ice.IsNationalTeam);
        Check("Row selects via the row itself (not the club link)", eg.Select.Type == "TableRowNavigatable");
        Check("Side panel title = selected", JobsPage.IsSelected(fm.Screen, eg, right) && !JobsPage.IsSelected(fm.Screen, rows[1], right));
        fm.ScrollPos = 600; fm.Tick();
        rows = JobsPage.Read(fm.Screen, t, out _, out _, out _, out _);
        Check("Reads rows after scrolling (incl. coach column)", rows.Any(r => r.Team == "Molde" && r.Coach == "Sindre Tjelmeland" && !r.IsVacancy), string.Join(",", rows.Select(r => r.Team)));
        var f = new JobFilter { Nations = new List<string> { "ENG" }, MinRating = ClubRating.Regional };
        Check("Filter: ENG + Regional+", f.Passes(new JobRow { Team = "X", Nation = "ENG", Rating = ClubRating.Regional, IsVacancy = true }, out _) &&
                                        !f.Passes(new JobRow { Team = "Y", Nation = "ENG", Rating = ClubRating.Local, IsVacancy = true }, out _) &&
                                        !f.Passes(new JobRow { Team = "Z", Nation = "NOR", Rating = ClubRating.National, IsVacancy = true }, out _));
    }

    private static void JobHunting()
    {
        Console.WriteLine("— job hunting");
        {
            var fm = FakeFm.WithJobs();
            var h = new Host(fm, "jobs1");
            var jh = new JobHunter(h);
            h.Duties.Add(jh);
            h.Run(() => false, 3);
            Check("Off: does nothing on the jobs page", fm.Presses == 0);
            h.Data.Jobs.ApplyMode = "filtered";
            h.Data.Jobs.Nations = new List<string> { "ENG", "NOR" };
            jh.SetEnabled(true);
            bool done = h.Run(() => !jh.IsRunning && h.Data.Jobs.LastCheckDate.Length > 0, 300);
            var applied = fm.Jobs.Where(j => j.Applied).Select(j => j.Team).OrderBy(x => x).ToList();
            var expected = new[] { "Egersund", "Forest Green", "Gateshead", "Hampton & Richmond", "Slough", "Woking" }.OrderBy(x => x).ToList();
            Check("Applies only to matching vacancies (ENG/NOR clubs), across the scrolled list", done && applied.SequenceEqual(expected), string.Join(", ", applied));
            Check("Never opened a club page by accident", !fm.Events.Any(e => e.StartsWith("opened club page")));
            Check("Remembers applications", h.Data.Jobs.Applied.Count == 6, h.Data.Jobs.Applied.Count.ToString());
            Check("Learned the nations it saw", h.Data.Jobs.KnownNations.Contains("ISL") && !h.Data.Jobs.KnownNations.Contains("GHA"), string.Join(",", h.Data.Jobs.KnownNations));
            Check("Summary logged", jh.LastSummary.Contains("applied to 6"), jh.LastSummary);
        }
        {
            var fm = FakeFm.WithJobs();
            fm.WorkingMethod = 2; // only real mouse clicks work
            fm.ConfirmOnSingleApply = false;
            fm.Jobs[0].Applied = true;
            var h = new Host(fm, "jobs2");
            var jh = new JobHunter(h);
            h.Duties.Add(jh);
            h.Data.Jobs.ApplyMode = "filtered";
            h.Data.Jobs.Nations = new List<string> { "NOR" };
            jh.SetEnabled(true);
            bool done = h.Run(() => !jh.IsRunning && h.Data.Jobs.LastCheckDate.Length > 0, 300);
            Check("Falls back to mouse clicks; skips an already-applied job", done && jh.LastSummary.Contains("1 already applied") && fm.Jobs.Count(j => j.Applied) == 1, jh.LastSummary);
        }
        {
            var fm = FakeFm.WithJobs();
            var h = new Host(fm, "jobs3");
            var jh = new JobHunter(h);
            h.Duties.Add(jh);
            h.Data.Jobs.ApplyMode = "filtered";
            h.Data.Jobs.IncludeNationalTeams = true;
            jh.SetEnabled(true);
            bool done = h.Run(() => !jh.IsRunning && h.Data.Jobs.LastCheckDate.Length > 0, 60);
            Check("Only-matching mode with wide-open filters still uses \"Apply for all jobs\"", done && fm.Events.Contains("applied all"), string.Join(" | ", fm.Events));
        }
        {
            var fm = FakeFm.WithJobs();
            fm.Page = "portal";
            fm.Tick();
            var h = new Host(fm, "jobs4");
            var jh = new JobHunter(h);
            h.Duties.Add(jh);
            h.Data.Jobs.ApplyMode = "filtered";
            h.Data.Jobs.Nations = new List<string> { "LTU" };
            jh.SetEnabled(true);
            bool done = h.Run(() => fm.Jobs.Any(j => j.Applied) && !jh.IsRunning, 200);
            Check("Auto-visit: opens Career → Head Coach Vacancies itself and applies", done && fm.Jobs.Single(j => j.Applied).Team == "Hegelmann", string.Join(",", fm.Jobs.Where(j => j.Applied).Select(j => j.Team)));
            h.Run(() => fm.Page == "portal", 5);
            Check("…then goes back where you were", fm.Page == "portal", fm.Page);
            int before = fm.Presses;
            h.Run(() => false, 20);
            Check("Doesn't revisit the same day", fm.Presses == before, $"{fm.Presses - before} extra presses");
            fm.Date = fm.Date.AddDays(7);
            fm.Jobs.Add(new FakeFm.Job { Team = "Žalgiris", League = "A Lyga", Rating = "National", Nation = "LTU" });
            bool again = h.Run(() => fm.Jobs.Last().Applied, 200);
            Check("Checks again a week later and applies to the new vacancy", again);
        }
        // Your setup from the FM27 test: any nation, national teams skipped, check every 3 days —
        // under each way FM27 might differ from the simulator's defaults.
        var clubVacancies = new[] { "Egersund", "Stjarnan", "Hapoel Acre", "Hapoel Kfar-Saba", "Forest Green", "Gateshead", "Woking", "Hegelmann", "Hampton & Richmond", "Slough" }.OrderBy(x => x).ToList();
        foreach (var (label, tweak) in new (string, Action<FakeFm>)[]
                 {
                     ("as simulated", f => { }),
                     ("headers not lined up with cells", f => f.HeaderShift = 250),
                     ("rows not clickable, no scroll view", f => { f.RowsClickable = false; f.NoScrollView = true; }),
                     ("date shown as two texts", f => f.SplitDate = true),
                     ("only real mouse clicks work", f => f.WorkingMethod = 2),
                 })
        {
          foreach (var mode in new[] { "all", "filtered" })
          {
            var fm = FakeFm.WithJobs();
            fm.Page = "portal";
            tweak(fm);
            fm.Tick();
            var h = new Host(fm, "user-" + mode + "-" + label.Replace(' ', '-').Replace(',', '_'));
            var jh = new JobHunter(h);
            h.Duties.Add(jh);
            h.Data.Jobs.CheckEveryDays = 3;
            h.Data.Jobs.IncludeNationalTeams = false;
            h.Data.Jobs.ApplyMode = mode;
            jh.SetEnabled(true);
            bool done = h.Run(() => !jh.IsRunning && h.Data.Jobs.LastCheckDate.Length > 0, 400);
            var applied = fm.Jobs.Where(j => j.Applied).Select(j => j.Team).OrderBy(x => x).ToList();
            if (mode == "all")
                Check($"Default — apply for all ({label}): opens the page itself, presses \"Apply for all jobs\", confirms, goes back",
                    done && fm.Events.Contains("applied all") && fm.Jobs.Where(j => j.Security == "Available").All(j => j.Applied) && fm.Page == "portal",
                    $"{string.Join(" | ", fm.Events)} | page={fm.Page} | {jh.LastSummary}");
            else
                Check($"Only matching ({label}): opens the page itself and applies to all 10 club vacancies, no national teams",
                    done && applied.SequenceEqual(clubVacancies) && fm.Page == "portal", $"{string.Join(", ", applied)} | page={fm.Page} | {jh.LastSummary}");
            Check($"{(mode == "all" ? "Default" : "Only matching")} ({label}): reads the date, next check in 3 days, nothing looked odd",
                jh.DateKnown && jh.DaysUntilNextCheck == 3 && h.Dumps == 0, $"date={jh.Date.Raw} next={jh.DaysUntilNextCheck} dumps={h.Dumps}");
          }
        }
        // Default mode ("Apply for all jobs") in the ways FM27's button and confirm box might behave.
        foreach (var (label, tweak) in new (string, Action<FakeFm>)[]
                 {
                     ("button wrapped like FM27's", f => f.NestedApplyAll = true),
                     ("confirm box says Apply / Cancel", f => f.OtherConfirmWording = true),
                     ("no confirm box (don't ask again)", f => f.ConfirmOnApplyAll = false),
                     ("only real mouse clicks work", f => f.WorkingMethod = 2),
                 })
        {
            var fm = FakeFm.WithJobs();
            tweak(fm);
            fm.Tick();
            var h = new Host(fm, "all-" + label.Replace(' ', '-').Replace('\'', '_').Replace('(', '_').Replace(')', '_').Replace('/', '_'));
            var jh = new JobHunter(h);
            h.Duties.Add(jh);
            jh.SetEnabled(true);
            bool done = h.Run(() => !jh.IsRunning && h.Data.Jobs.LastCheckDate.Length > 0, 120);
            Check($"Apply for all ({label}): applies for every open job, confirm box gone",
                done && fm.Events.Count(e => e == "applied all") == 1 && fm.Confirm == null && jh.LastSummary.Contains("Applied for all"),
                $"{string.Join(" | ", fm.Events)} | {jh.LastSummary} | {jh.Status}");
        }
        {
            var fm = FakeFm.WithJobs();
            foreach (var j in fm.Jobs) j.Applied = true; // already applied everywhere → button greyed out
            fm.Tick();
            var h = new Host(fm, "all-greyed");
            var jh = new JobHunter(h);
            h.Duties.Add(jh);
            jh.SetEnabled(true);
            bool done = h.Run(() => !jh.IsRunning && h.Data.Jobs.LastCheckDate.Length > 0, 60);
            Check("Apply for all: greyed-out button → nothing new, no presses", done && fm.Presses == 0 && jh.LastSummary.Contains("Nothing new"), jh.LastSummary);
        }
        {
            var fm = FakeFm.WithJobs();
            fm.WorkingMethod = 9; // the game ignores every way of pressing
            fm.Tick();
            var h = new Host(fm, "all-stuck");
            var jh = new JobHunter(h);
            h.Duties.Add(jh);
            jh.SetEnabled(true);
            h.Run(() => !jh.IsRunning && jh.Status.StartsWith("Stopped"), 60);
            Check("Apply for all: if the button never reacts, says so instead of stopping silently", jh.Status.Contains("didn't react") && AgentLog.Recent.Any(l => l.Contains("didn't react")), jh.Status);
        }
        {
            var fm = FakeFm.WithJobs();
            fm.Jobs.RemoveAll(j => j.Finances == "-"); // no national team vacancies today
            fm.Tick();
            var h = new Host(fm, "jobs-all-match");
            var jh = new JobHunter(h);
            h.Duties.Add(jh);
            h.Data.Jobs.ApplyMode = "filtered";
            jh.SetEnabled(true);
            bool done = h.Run(() => !jh.IsRunning && h.Data.Jobs.LastCheckDate.Length > 0, 200);
            Check("Only-matching mode: every open job matches → uses \"Apply for all jobs\"", done && fm.Events.Contains("applied all") && jh.LastSummary.Contains("Apply for all"), jh.LastSummary);
        }
        {
            var fm = FakeFm.WithJobs();
            fm.Tick();
            var h = new Host(fm, "jobs-not-all");
            var jh = new JobHunter(h);
            h.Duties.Add(jh);
            h.Data.Jobs.ApplyMode = "filtered";
            jh.SetEnabled(true);
            h.Run(() => !jh.IsRunning && h.Data.Jobs.LastCheckDate.Length > 0, 400);
            Check("Only-matching mode: national-team vacancies you skip → doesn't use \"Apply for all jobs\"",
                !fm.Events.Contains("applied all") && fm.Jobs.Count(j => j.Applied) == 10 && !fm.Jobs.Any(j => j.Applied && j.Finances == "-"), jh.LastSummary);
        }
        {
            var fm = FakeFm.WithJobs();
            fm.Page = "portal";
            fm.Tick();
            var h = new Host(fm, "jobs-legacy");
            h.Data.Jobs.LastCheckDate = "checked"; // written by 0.3.0 when it couldn't read the date
            var jh = new JobHunter(h);
            h.Duties.Add(jh);
            h.Data.Jobs.Enabled = true;
            bool went = h.Run(() => fm.Page == "jobs" || fm.Jobs.Any(j => j.Applied), 60);
            Check("Settings saved by 0.3.0 (date unreadable) → checks straight away", went);
        }
        {
            var fm = FakeFm.WithJobs();
            fm.HeaderShift = 250;
            fm.Tick();
            var rows = JobsPage.Read(fm.Screen, new GameText(), out _, out _, out _, out _);
            var eg = rows.FirstOrDefault(r => r.Team == "Egersund");
            Check("Headers not lined up: still reads club, league, rating, nation and vacancy correctly",
                rows.Count >= 12 && eg != null && eg.League == "First Division" && eg.Nation == "NOR" && eg.IsVacancy && eg.Rating == ClubRating.National
                && rows.All(r => r.Team != "Available") && rows.Count(r => r.IsVacancy) == rows.Count(r => fm.Jobs.Any(j => j.Team == r.Team && j.Security == "Available")),
                string.Join(" ; ", rows.Take(4).Select(r => r.Debug())));
        }
        {
            var fm = FakeFm.WithJobs();
            fm.Page = "portal";
            fm.Tick();
            var h = new Host(fm, "jobs5");
            var jh = new JobHunter(h);
            h.Duties.Add(jh);
            // You open the page yourself: Career, then "Available Jobs".
            void Click(string label)
            {
                var n = fm.Screen.Button(label);
                jh.OnUserClick(fm.Screen.TextsEq(label).First());
                fm.Press(n, 0, out _);
                h.Run(() => false, 0.5);
            }
            Click("Career");
            Click("Head Coach Vacancies");
            h.Run(() => false, 1);
            Check("Learns the menu path when you open the jobs page", h.Data.Jobs.MenuParentText == "Career" && h.Data.Jobs.MenuItemText == "Head Coach Vacancies",
                h.Data.Jobs.MenuParentText + " → " + h.Data.Jobs.MenuItemText);
        }
    }

    // ================================================================ negotiation

    private static (FakeFm fm, Host h, Negotiator n) Contract(string name, Action<FakeFm> setup = null, Action<AgentData> prefs = null)
    {
        var fm = new FakeFm();
        setup?.Invoke(fm);
        fm.OpenContract();
        var h = new Host(fm, name);
        prefs?.Invoke(h.Data);
        var n = new Negotiator(h);
        h.Duties.Add(n);
        h.Run(() => false, 0.3);
        return (fm, h, n);
    }

    private static void SetRange(AgentData d, double min, double target, double years = 3)
    {
        d.Contract.WageMin = min;
        d.Contract.WageTarget = target;
        d.Contract.YearsWant = years;
    }

    private static void Negotiation()
    {
        Console.WriteLine("— contract negotiation");
        var t = new GameText();
        {
            var fm = new FakeFm();
            fm.OpenContract();
            var cs = ContractScreen.Read(fm.Screen, t, FakeFm.ClauseList);
            Check("Reads the contract screen", cs != null && cs.Wage == 725 && cs.Years == 1 && cs.Club == "Hampton & Richmond" && cs.CanFinalize && !cs.CanSuggest && cs.Minus != null && cs.Plus != null,
                cs == null ? "null" : $"{cs.Wage} {cs.Years} {cs.Club} fin={cs.CanFinalize}");
            fm.PlayerContract = true; fm.Tick();
            Check("Ignores a player's contract screen", !ContractScreen.IsShowing(fm.Screen, t));
        }
        {
            var (fm, h, n) = Contract("neg1", f => { f.C.Max = 1200; f.C.WalkAway = 1100; f.C.RefusedClauses.Add("Promotion Salary Raise"); },
                d => SetRange(d, 900, 1500));
            n.Start();
            h.Run(() => n.Choice != null, 120);
            Check("Finds the club maximum and warns that your target is above it", n.Choice?.Title.Contains("target") == true && n.ClubMax == 1200,
                $"{n.Choice?.Title} / max={n.ClubMax}");
            Check("Put the club's offer back while asking you", fm.Offer.Wage == 725, fm.Offer.Wage.ToString());
            n.Choose("aim");
            h.Run(() => n.Choice != null, 300);
            Check("Reaches a deal and asks before signing", n.Choice?.Title == "Deal ready", n.Choice?.Title + " — " + n.Choice?.Text + " | " + string.Join(" | ", fm.Events));
            Check("Deal: wage between your minimum and the club's limit, near the limit", fm.Offer.Wage >= 900 && fm.Offer.Wage <= 1100 && fm.Offer.Wage >= 1000, fm.Offer.ToString());
            Check("Deal: 3 years (as wanted, within club max)", fm.Offer.Years == 3, fm.Offer.ToString());
            Check("Deal: got Yearly Salary Raise (unlocked by the longer contract), not the refused clause",
                fm.Offer.Has("Yearly Salary Raise") && !fm.Offer.Has("Promotion Salary Raise"), fm.Offer.ToString());
            Check("Hasn't signed without asking", !fm.Signed);
            n.Choose("sign");
            h.Run(() => fm.Signed, 30);
            Check("Signs when you say so", fm.Signed && fm.Events.Any(e => e.StartsWith("signed")));
        }
        {
            var (fm, h, n) = Contract("neg2", f => { f.C.Max = 800; f.C.WalkAway = 800; }, d => SetRange(d, 900, 1200));
            n.Start();
            h.Run(() => n.Choice != null, 120);
            Check("Range above club max → alerts you", n.Choice?.Title.Contains("above this club's maximum") == true && n.Choice.Options.Any(o => o.Key == "lower"), n.Choice?.Title);
            n.Choose("lower");
            h.Run(() => n.Choice != null && n.Choice.Title != "Your range is above this club's maximum", 300);
            Check("…'go for the max with this club' negotiates to the club max", n.Choice?.Title == "Deal ready" && fm.Offer.Wage == 800,
                $"{n.Choice?.Title} {fm.Offer}");
            Check("…without changing your saved range", h.Data.Contract.WageMin == 900 && h.Data.Contract.WageTarget == 1200);
        }
        {
            var (fm, h, n) = Contract("neg3", f => { f.SalaryMode = "list"; f.C.Max = 1300; f.C.WalkAway = 1250; }, d => SetRange(d, 900, 1250));
            n.Start();
            h.Run(() => n.Choice != null, 300);
            Check("List-style salary: reads the club max from the list", n.ClubMax == 1300, $"max={n.ClubMax}");
            Check("List-style salary: reaches a deal", n.Choice?.Title == "Deal ready" && fm.Offer.Wage >= 1100, $"{n.Choice?.Title} {fm.Offer}");
        }
        {
            var (fm, h, n) = Contract("neg4", f => { f.SalaryMode = "buttons"; f.C.Max = 1500; f.C.WalkAway = 1000; }, d => SetRange(d, 850, 1100));
            n.Start();
            h.Run(() => n.Choice != null, 600);
            Check("Buttons-only salary: uses - / + and reaches a deal", n.Choice?.Title == "Deal ready" && fm.Offer.Wage >= 900 && fm.Offer.Wage <= 1000, $"{n.Choice?.Title} {fm.Offer}");
        }
        {
            var (fm, h, n) = Contract("neg5", f => { f.FieldSettable = false; f.C.Max = 2000; f.C.WalkAway = 1200; }, d => SetRange(d, 900, 1300));
            n.Start();
            h.Run(() => n.Choice != null, 600);
            Check("Types with the keyboard when the field can't be set directly", n.Choice != null && fm.Offer.Wage >= 1100, $"{n.Choice?.Title} {fm.Offer}");
        }
        {
            var (fm, h, n) = Contract("neg6", f => { f.C.Max = 1500; f.C.WalkAway = 700; }, d => SetRange(d, 900, 1200));
            n.Start();
            h.Run(() => n.Choice != null && !n.Choice.Title.Contains("maximum"), 600);
            Check("Club won't reach your minimum → hands back, doesn't sign", n.Choice?.Title == "Below your minimum" && !fm.Signed, $"{n.Choice?.Title} {fm.Offer}");
        }
        {
            var (fm, h, n) = Contract("neg7", f => { f.C.Max = 1500; f.C.WalkAway = 1000; f.C.Patience = 1; }, d => SetRange(d, 800, 1400));
            n.Start();
            h.Run(() => !n.IsRunning && n.Choice == null, 300);
            Check("Club ends talks → stops and says so", fm.TalksEnded && !n.IsRunning && n.Status.Length > 0, n.Status);
        }
        {
            var fm = new FakeFm();
            fm.C.Max = 1500; fm.C.WalkAway = 1100;
            fm.OpenOfferMessage();
            var h = new Host(fm, "neg8");
            SetRange(h.Data, 900, 1200);
            h.Data.Contract.AutoNegotiate = true;
            h.Data.Contract.AutoSign = true;
            var n = new Negotiator(h);
            h.Duties.Add(n);
            h.Run(() => fm.Signed, 600);
            Check("Auto mode: opens the offer, negotiates and signs (when you allow it)", fm.Signed && fm.Offer.Wage >= 1000, $"{fm.Offer} | {string.Join(" | ", fm.Events.TakeLast(4))}");
        }
        {
            var (fm, h, n) = Contract("neg9", f => { f.WorkingMethod = 2; f.C.Max = 1500; f.C.WalkAway = 1000; }, d => SetRange(d, 800, 1000, 2));
            n.Start();
            h.Run(() => n.Choice != null, 600);
            Check("Works when only real mouse clicks register", n.Choice?.Title == "Deal ready" && fm.Offer.Wage == 1000 && fm.Offer.Years == 2, $"{n.Choice?.Title} {fm.Offer}");
        }
        {
            var (fm, h, n) = Contract("neg10", null, null);
            n.Start();
            h.Run(() => n.Choice != null, 30);
            Check("No wage range set → asks you first", n.Choice?.Title == "Set your wage range");
            n.Choose("seed");
            h.Run(() => n.Choice != null && n.Choice.Title != "Set your wage range", 600);
            Check("'Start from their offer' sets a range and carries on", h.Data.Contract.WageMin == 725 && h.Data.Contract.WageTarget > 1000 && n.Choice != null,
                $"{h.Data.Contract.WageMin}-{h.Data.Contract.WageTarget} {n.Choice?.Title}");
        }
    }
}
