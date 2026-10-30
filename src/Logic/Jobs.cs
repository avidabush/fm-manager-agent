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

    /// <summary>The table's rows currently on screen (top to bottom), plus the area they scroll in.</summary>
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

        string ColumnAt(float x)
        {
            string col = null;
            foreach (var (c, n) in headers)
                if (x >= n.X - 12) col = c;
            return col;
        }

        // Each row has exactly one club-rating word: use those as row anchors.
        var anchors = s.Texts.Where(n => n.CY > hb + 4 && n.CX > tl && n.CX < tr && ParseRating(n.Text, t) != ClubRating.Unknown)
            .Where(n => ColumnAt(n.CX) == "rating")
            .OrderBy(n => n.CY).ToList();
        if (anchors.Count == 0) return rows;

        float pitch = 60;
        if (anchors.Count > 1)
        {
            var diffs = anchors.Zip(anchors.Skip(1), (a, b) => b.CY - a.CY).Where(d => d > 10).OrderBy(d => d).ToList();
            if (diffs.Count > 0) pitch = diffs[diffs.Count / 2];
        }

        var tableWidth = tr - tl;
        foreach (var a in anchors)
        {
            // Prefer the real row element (a wide clickable around the anchor).
            var rowEl = s.Nodes.Where(n => n.Clickable && n.Contains(a.CX, a.CY) && n.W >= tableWidth * 0.5f && n.H < pitch * 1.8f)
                .OrderBy(n => n.W * n.H).FirstOrDefault();
            float top = rowEl?.Y ?? a.CY - pitch / 2, bottom = rowEl?.Bottom ?? a.CY + pitch / 2;

            var r = new JobRow { Top = top, BottomY = bottom, Rating = ParseRating(a.Text, t) };
            var cells = s.TextsInBand(top + 1, bottom - 1, tl, tr).OrderBy(n => n.Y).ThenBy(n => n.X).ToList();
            var teamTexts = new List<string>();
            foreach (var c in cells)
            {
                switch (ColumnAt(c.CX))
                {
                    case "coach": if (r.Coach.Length == 0) r.Coach = c.Text; break;
                    case "team": teamTexts.Add(c.Text); break;
                    case "nation": if (r.Nation.Length == 0) r.Nation = c.Text; break;
                    case "position": if (r.Position.Length == 0) r.Position = c.Text; break;
                    case "security": if (r.Security.Length == 0) r.Security = c.Text; break;
                    case "finances": if (r.Finances.Length == 0) r.Finances = c.Text; break;
                }
            }
            r.Team = teamTexts.ElementAtOrDefault(0) ?? "";
            r.League = teamTexts.ElementAtOrDefault(1) ?? "";
            r.IsVacancy = ScreenModel.Split(t.Vacancy).Any(v => ScreenModel.Eq(r.Security, v));
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
