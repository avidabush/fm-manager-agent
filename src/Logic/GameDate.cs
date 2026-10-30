using System.Globalization;
using System.Text.RegularExpressions;

namespace ManagerAgent;

/// <summary>
/// Reads the in-game date from the top bar ("TUE 6/9/2026", or "TUE" and "6/9/2026" as separate
/// texts, or "Tue 9 Jun 2026").
/// </summary>
internal sealed class GameDate
{
    private static readonly Regex Weekday = new(@"^(MON|TUE|WED|THU|FRI|SAT|SUN)[A-Z]*\.?,?\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Numeric = new(@"\b(\d{1,2}[/.\-]\d{1,2}[/.\-]\d{4}|\d{4}-\d{1,2}-\d{1,2})\b", RegexOptions.Compiled);
    private static readonly Regex Named = new(@"\b(\d{1,2}\s+[A-Za-z]{3,9}\.?\s+\d{4}|[A-Za-z]{3,9}\.?\s+\d{1,2},?\s+\d{4})\b", RegexOptions.Compiled);
    private static readonly string[] NumericFormats = { "M/d/yyyy", "d/M/yyyy", "d.M.yyyy", "M.d.yyyy", "d-M-yyyy", "yyyy-M-d" };
    private static readonly string[] NamedFormats = { "d MMM yyyy", "d MMMM yyyy", "MMM d yyyy", "MMMM d yyyy", "MMM d, yyyy", "MMMM d, yyyy" };

    public string Raw { get; private set; }
    public DateTime? Date { get; private set; }
    /// <summary>When the date last changed (game time is moving).</summary>
    public double ChangedAt { get; private set; } = double.NegativeInfinity;

    public string Iso => Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";

    public void Update(ScreenModel s, double now)
    {
        float topBand = Math.Max(90f, s.Height * 0.09f);
        foreach (var n in s.Texts)
        {
            if (n.Y > topBand) continue;
            var text = Ui.Clean(n.Text);
            var body = Weekday.Replace(text, "");
            var m = Numeric.Match(body);
            string found = m.Success ? m.Value : null;
            if (found == null)
            {
                m = Named.Match(body);
                if (m.Success) found = m.Value;
            }
            if (found == null) continue;
            if (found == Raw) return;
            var d = Parse(found, Date);
            if (d == null) continue;
            Raw = found;
            if (Date != d) ChangedAt = now;
            Date = d;
            return;
        }
    }

    /// <summary>Parses a date; when two readings are possible, takes the one closest to the previous date.</summary>
    public static DateTime? Parse(string text, DateTime? previous)
    {
        text = Weekday.Replace(Ui.Clean(text ?? ""), "").Trim();
        var formats = char.IsDigit(text.FirstOrDefault()) && !text.Any(char.IsLetter) ? NumericFormats : NamedFormats;
        DateTime? best = null;
        foreach (var f in formats)
        {
            if (!DateTime.TryParseExact(text, f, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d)) continue;
            if (best == null) best = d;
            else if (previous != null && Math.Abs((d - previous.Value).TotalDays) < Math.Abs((best.Value - previous.Value).TotalDays)) best = d;
            if (previous == null) break;
        }
        return best;
    }

    /// <summary>Days since an ISO date (yyyy-MM-dd); null if either date is unknown.</summary>
    public int? DaysSince(string iso)
    {
        if (Date == null || !TryIso(iso, out var d)) return null;
        return (int)Math.Round((Date.Value - d).TotalDays);
    }

    public static bool TryIso(string iso, out DateTime d) =>
        DateTime.TryParseExact(iso ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d);
}
