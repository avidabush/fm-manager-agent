using System.Globalization;
using System.Text.RegularExpressions;

namespace ManagerAgent;

/// <summary>Reads the in-game date from the top bar ("MON 6/8/2026").</summary>
internal sealed class GameDate
{
    private static readonly Regex Rx = new(@"^(MON|TUE|WED|THU|FRI|SAT|SUN)[A-Z]*\.?,?\s+(\S+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] Formats = { "M/d/yyyy", "d/M/yyyy", "d.M.yyyy", "yyyy-MM-dd", "d-M-yyyy" };

    public string Raw { get; private set; }
    public DateTime? Date { get; private set; }

    public string Iso => Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";

    public void Update(ScreenModel s)
    {
        var n = s.Texts.FirstOrDefault(t => t.Y < 90 && Rx.IsMatch(Ui.Clean(t.Text)));
        if (n == null) return;
        var raw = Ui.Clean(n.Text);
        if (raw == Raw) return;
        Raw = raw;
        var d = Parse(Rx.Match(raw).Groups[2].Value, Date);
        if (d != null) Date = d;
    }

    /// <summary>Parses a date; when two readings are possible, takes the one closest to the previous date.</summary>
    public static DateTime? Parse(string text, DateTime? previous)
    {
        DateTime? best = null;
        foreach (var f in Formats)
        {
            if (!DateTime.TryParseExact(text, f, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
            if (best == null) best = d;
            else if (previous != null && Math.Abs((d - previous.Value).TotalDays) < Math.Abs((best.Value - previous.Value).TotalDays)) best = d;
            if (previous == null) break;
        }
        return best;
    }

    /// <summary>Days since an ISO date (yyyy-MM-dd); null if either date is unknown.</summary>
    public int? DaysSince(string iso)
    {
        if (Date == null || string.IsNullOrEmpty(iso)) return null;
        if (!DateTime.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return null;
        return (int)Math.Round((Date.Value - d).TotalDays);
    }
}
