using System.Text.RegularExpressions;

namespace ManagerAgent;

internal enum ClubRating { Unknown = -1, Local = 0, Regional = 1, National = 2, Continental = 3, Worldwide = 4 }

/// <summary>One line of the "Available Jobs" table.</summary>
internal sealed class JobRow
{
    public string Coach = "", Team = "", League = "", Nation = "", Position = "", Security = "", Finances = "";
    public ClubRating Rating = ClubRating.Unknown;
    public bool IsNationalTeam;
    public bool IsVacancy;
    /// <summary>What to press to select this row.</summary>
    public UiNode Select;
    public float Top, BottomY;

    public string Key => (Team + "|" + League).ToLowerInvariant();
    public string Debug() => $"{Team} | {League} | {Rating} | {Nation} | {Security}{(IsVacancy ? " (open)" : "")} | {Finances}{(IsNationalTeam ? " | national" : "")}";
    public string Describe() => Team + (League.Length > 1 ? " (" + League + (Nation.Length > 0 ? ", " + Nation : "") + ")" : Nation.Length > 0 ? " (" + Nation + ")" : "");
}

/// <summary>Your job-hunting filters.</summary>
internal sealed class JobFilter
{
    public ClubRating MinRating = ClubRating.Local;
    public bool IncludeNationalTeams;
    /// <summary>Nation codes as shown in the table (e.g. "ENG"). Empty = any nation.</summary>
    public List<string> Nations = new();
    public List<string> ExcludeTeams = new();

    public bool IsWideOpen => MinRating <= ClubRating.Local && IncludeNationalTeams && Nations.Count == 0 && ExcludeTeams.Count == 0;

    public bool Passes(JobRow r, out string why)
    {
        why = null;
        if (!r.IsVacancy) { why = "not a vacancy"; return false; }
        if (r.IsNationalTeam && !IncludeNationalTeams) { why = "national team"; return false; }
        if (r.Rating != ClubRating.Unknown && r.Rating < MinRating) { why = "club rating " + r.Rating; return false; }
        if (Nations.Count > 0 && !Nations.Any(n => string.Equals(n, r.Nation, StringComparison.OrdinalIgnoreCase))) { why = "nation " + r.Nation; return false; }
        if (ExcludeTeams.Any(t => string.Equals(t, r.Team, StringComparison.OrdinalIgnoreCase))) { why = "excluded"; return false; }
        return true;
    }

    public string Describe()
    {
        var parts = new List<string>();
        parts.Add(IncludeNationalTeams ? "clubs + national teams" : "club jobs");
        parts.Add(Nations.Count == 0 ? "any nation" : string.Join(", ", Nations));
        if (MinRating > ClubRating.Local) parts.Add(MinRating + "+ clubs");
        if (ExcludeTeams.Count > 0) parts.Add($"{ExcludeTeams.Count} excluded");
        return string.Join(" · ", parts);
    }
}

/// <summary>Reads the "Available Jobs" page from a screen snapshot.</summary>
internal static class JobsPage
{
    public static bool IsShowing(ScreenModel s, GameText t) => Title(s, t) != null && s.Text(t.ApplyForJob) != null;

    public static UiNode Title(ScreenModel s, GameText t)
    {
        var rx = new Regex(@"^\d+\s+(" + string.Join("|", ScreenModel.Split(t.AvailableJobs).Select(Regex.Escape)) + ")$", RegexOptions.IgnoreCase);
        return s.TextsMatching(rx).FirstOrDefault();
    }

    public static ClubRating ParseRating(string text, GameText t)
    {
        var names = ScreenModel.Split(t.Ratings);
        for (int i = 0; i < names.Length && i <= (int)ClubRating.Worldwide; i++)
            if (ScreenModel.Eq(text, names[i])) return (ClubRating)i;
        return ClubRating.Unknown;
    }

    private enum Kind { Dash, Rating, Vacancy, Security, Finance, Ordinal, Caps, Other }

    private static readonly Regex Ordinal = new(@"^\d+(st|nd|rd|th|e|º|°|\.)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Caps = new(@"^[A-ZÀ-Ý]{2,4}$", RegexOptions.Compiled);

    private static Kind Classify(string text, GameText t)
    {
        var x = Ui.Clean(text);
        if (x == "-" || x == "–" || x == "—") return Kind.Dash;
        if (ParseRating(x, t) != ClubRating.Unknown) return Kind.Rating;
        if (ScreenModel.Split(t.Vacancy).Any(v => ScreenModel.Eq(x, v))) return Kind.Vacancy;
        if (ScreenModel.Split(t.SecurityWords).Any(v => ScreenModel.Eq(x, v))) return Kind.Security;
        if (ScreenModel.Split(t.FinanceWords).Any(v => ScreenModel.Eq(x, v))) return Kind.Finance;
        if (Ordinal.IsMatch(x)) return Kind.Ordinal;
        if (Caps.IsMatch(x)) return Kind.Caps;
        return Kind.Other;
    }

    /// <summary>
    /// The table's rows currently on screen (top to bottom), plus the area they scroll in.
    ///
    /// Values are recognised by what they are, not only by which header they sit under: every row
    /// has exactly one club-rating word (the anchor); "Available" only appears for open jobs; the nation
    /// is the short capitals code right of the rating; club and league are the texts left of it.
    /// This keeps working when header positions don't line up exactly with the cells.
    /// </summary>
    public static List<JobRow> Read(ScreenModel s, GameText t, out UiNode scroll, out float tableLeft, out float tableRight, out float headerBottom)
    {
        scroll = null;
        tableLeft = tableRight = headerBottom = 0;
        var rows = new List<JobRow>();

        var team = s.Text(t.ColTeam);
        var rating = s.Text(t.ColRating, n => team == null || Math.Abs(n.CY - team.CY) < 20);
        if (team == null || rating == null) return rows;
        float hy = team.CY;
        var headers = new List<(string col, UiNode n)>();
        void H(string col, string labels)
        {
            var n = s.Text(labels, x => Math.Abs(x.CY - hy) < 20);
            if (n != null) headers.Add((col, n));
        }
        H("coach", t.ColCoach); H("team", t.ColTeam); H("rating", t.ColRating); H("nation", t.ColNation);
        H("position", t.ColPosition); H("security", t.ColSecurity); H("finances", t.ColFinances);
        headers.Sort((a, b) => a.n.X.CompareTo(b.n.X));
        tableLeft = headers[0].n.X - 70;
        tableRight = headers[^1].n.Right + 140;
        headerBottom = headers.Max(h => h.n.Bottom);
        float tl = tableLeft, tr = tableRight, hb = headerBottom;
        var tableWidth = tr - tl;
        var teamHeader = headers.First(h => h.col == "team").n;

        // Row anchors: one club-rating word per row (prefer the ones under the Club Rating header;
        // otherwise the column of rating words — ignoring strays such as the job panel's "Reputation").
        var ratingWords = s.Texts.Where(n => n.CY > hb + 4 && n.CX > tl && n.CX < tr && ParseRating(n.Text, t) != ClubRating.Unknown).ToList();
        var underHeader = ratingWords.Where(n => n.CX >= rating.X - 40 && n.X <= rating.Right + 160).ToList();
        var anchors = underHeader.Count > 0 ? underHeader : ratingWords;
        if (anchors.Count > 2)
        {
            var xs = anchors.Select(n => n.X).OrderBy(x => x).ToList();
            float median = xs[xs.Count / 2];
            anchors = anchors.Where(n => Math.Abs(n.X - median) < 150).ToList();
        }
        anchors = anchors.OrderBy(n => n.CY).ToList();
        if (anchors.Count == 0) return rows;

        float pitch = 60;
        if (anchors.Count > 1)
        {
            var diffs = anchors.Zip(anchors.Skip(1), (a, b) => b.CY - a.CY).Where(d => d > 10).OrderBy(d => d).ToList();
            if (diffs.Count > 0) pitch = diffs[diffs.Count / 2];
        }

        UiNode RowElement(UiNode a) => s.Nodes.Where(n => n.Clickable && n.Contains(a.CX, a.CY) && n.W >= tableWidth * 0.5f && n.H < pitch * 1.8f)
            .OrderBy(n => n.W * n.H).FirstOrDefault();

        // The rows' own edges are more reliable than the headers' when the rows are real elements.
        var rowEls = anchors.Select(RowElement).Where(n => n != null).ToList();
        if (rowEls.Count > 0)
        {
            tableLeft = tl = Math.Min(tl, rowEls.Min(n => n.X));
            tableRight = tr = rowEls.Max(n => n.Right);
        }

        float lastBottom = float.MinValue;
        foreach (var a in anchors)
        {
            if (a.CY < lastBottom - 2) continue; // second rating word inside the same row
            var rowEl = RowElement(a);
            float top = rowEl?.Y ?? a.CY - pitch / 2, bottom = rowEl?.Bottom ?? a.CY + pitch / 2;
            lastBottom = bottom;

            var r = new JobRow { Top = top, BottomY = bottom, Rating = ParseRating(a.Text, t) };
            var cells = s.TextsInBand(top + 1, bottom - 1, tl, tr).Where(c => c.Id != a.Id).ToList();

            // Left of the rating: coach, club, league.
            var left = cells.Where(c => c.CX < a.X).Select(c => (c, k: Classify(c.Text, t))).ToList();
            var teamSide = left.Where(x => x.c.X >= teamHeader.X - 20).ToList();
            if (!teamSide.Any(x => x.k == Kind.Other))
            {
                // Header doesn't line up: take the right-most column of texts left of the rating.
                var others = left.Where(x => x.k is Kind.Other or Kind.Caps or Kind.Dash).ToList();
                if (others.Count > 0)
                {
                    float maxX = others.Max(x => x.c.X);
                    teamSide = others.Where(x => x.c.X >= maxX - 80).ToList();
                }
            }
            var teamCol = teamSide.OrderBy(x => x.c.Y).ThenBy(x => x.c.X).ToList();
            var teamCell = teamCol.FirstOrDefault(x => x.k is Kind.Other or Kind.Caps);
            r.Team = teamCell.c?.Text ?? "";
            var leagueCell = teamCol.FirstOrDefault(x => teamCell.c != null && x.c.Id != teamCell.c.Id && x.c.Y >= teamCell.c.Y);
            r.League = leagueCell.c?.Text ?? "";
            var coach = left.Where(x => (teamCell.c == null || x.c.X < teamCell.c.X - 30) && x.k is Kind.Other or Kind.Dash).OrderBy(x => x.c.X).FirstOrDefault();
            r.Coach = coach.c?.Text ?? "";

            // Right of the rating: nation, position, form, job security, finances.
            var right = cells.Where(c => c.X >= a.Right - 2).OrderBy(c => c.X).Select(c => (c, k: Classify(c.Text, t))).ToList();
            r.Nation = right.FirstOrDefault(x => x.k == Kind.Caps).c?.Text ?? "";
            r.Position = right.FirstOrDefault(x => x.k == Kind.Ordinal).c?.Text ?? "";
            var vac = right.FirstOrDefault(x => x.k == Kind.Vacancy);
            var sec = vac.c != null ? vac : right.FirstOrDefault(x => x.k == Kind.Security);
            r.Security = sec.c?.Text ?? "";
            var fin = right.Where(x => sec.c == null || x.c.X > sec.c.X + 10).LastOrDefault(x => x.k is Kind.Finance or Kind.Security or Kind.Dash);
            r.Finances = fin.c?.Text ?? "";
            r.IsVacancy = vac.c != null;
            r.IsNationalTeam = IsNational(r);

            // What to press: the row itself, else a small control at its left (the radio), else the rating text.
            var radio = s.Nodes.Where(n => n.Clickable && n.CY > top && n.CY < bottom && n.W < 60 && n.CX < headers[0].n.X + 20)
                .OrderBy(n => n.X).FirstOrDefault();
            r.Select = rowEl ?? radio ?? a;
            if (r.Team.Length > 0) rows.Add(r);
        }

        scroll = s.Nodes.Where(n => n.IsScroll && n.W > tableWidth * 0.5f && anchors.Any(a => n.Contains(a.CX, a.CY)))
            .OrderBy(n => n.W * n.H).FirstOrDefault();
        return rows;
    }

    /// <summary>How many table rows show "Available" (to double-check the reading). A badge may contain the word twice.</summary>
    public static int VacancyWordsOnScreen(ScreenModel s, GameText t, float tableLeft, float tableRight, float headerBottom, UiNode table = null)
    {
        // Only words inside the jobs table: the screen underneath (e.g. a vacancies widget on your home
        // screen) stays loaded behind the jobs page.
        var ys = s.Texts.Where(n => n.CY > headerBottom + 4 && n.CX > tableLeft && n.CX < tableRight && (table == null || s.IsInside(n, table))
                                    && ScreenModel.Split(t.Vacancy).Any(v => ScreenModel.Eq(n.Text, v)))
            .Select(n => n.CY).OrderBy(y => y).ToList();
        int rows = 0;
        float last = float.MinValue;
        foreach (var y in ys)
        {
            if (y - last > 12) rows++;
            last = y;
        }
        return rows;
    }

    /// <summary>The confirm button of a "Please Confirm" box (the one nearest its title).</summary>
    public static UiNode ConfirmButton(ScreenModel s, GameText t)
    {
        var yes = ScreenModel.Split(t.Yes);
        var title = s.Text(t.ConfirmTitle);
        if (title != null)
        {
            // The yes-button nearest the title (labels in order of preference).
            foreach (var label in yes)
            {
                UiNode best = null;
                float bestD = float.MaxValue;
                foreach (var n in s.TextsEq(label))
                {
                    var c = s.ClickableOf(n);
                    if (c == null) continue;
                    float d = Math.Abs(c.CX - title.CX) + Math.Abs(c.CY - title.CY);
                    if (d < bestD) { bestD = d; best = c; }
                }
                if (best != null) return best;
            }
        }
        // Any other wording: a yes-button with a no-button right beside it is a confirmation box.
        var noButtons = s.TextsEq(t.No).Select(s.ClickableOf).Where(c => c != null).ToList();
        if (noButtons.Count == 0) return null;
        foreach (var label in yes)
            foreach (var n in s.TextsEq(label))
            {
                var c = s.ClickableOf(n);
                if (c != null && noButtons.Any(nb => nb.Id != c.Id && Math.Abs(nb.CY - c.CY) < 40 && Math.Abs(nb.CX - c.CX) < 500))
                    return c;
            }
        return null;
    }

    /// <summary>True while a confirmation box is showing.</summary>
    public static bool ConfirmShowing(ScreenModel s, GameText t) => s.Text(t.ConfirmTitle) != null || ConfirmButton(s, t) != null;

    /// <summary>Text of the job details panel (right of the table), used to see that a row got selected.</summary>
    public static string SidePanelKey(ScreenModel s, float tableRight, float headerBottom) =>
        string.Join("|", s.Texts.Where(n => n.X > tableRight - 40 && n.Y > headerBottom - 60 && n.Y < headerBottom + 260).OrderBy(n => n.Y).Select(n => n.Text));

    private static readonly Regex NationalHint = new(@"\bU-?\d{2}s?\b|Quals|Qualif|Nations|World Cup|Ch'ship|Championship Qual", RegexOptions.IgnoreCase);

    private static bool IsNational(JobRow r)
    {
        if (r.Finances == "-" || r.Finances == "–") return true;
        if (r.League == "-" || r.League == "–") return true;
        return NationalHint.IsMatch(r.Team) || NationalHint.IsMatch(r.League);
    }

    /// <summary>The job title in the side panel ("Egersund Head Coach Position") names the selected team.</summary>
    public static bool IsSelected(ScreenModel s, JobRow r, float tableRight) =>
        s.Texts.Any(n => n.X > tableRight - 40 && (ScreenModel.Eq(n.Text, r.Team) || Ui.Clean(n.Text).StartsWith(r.Team + " ", StringComparison.OrdinalIgnoreCase)));
}
