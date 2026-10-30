using System.Globalization;
using System.Text.RegularExpressions;

namespace ManagerAgent;

/// <summary>A wage as shown by the game, e.g. "$1,000 p/w" → 1000 with prefix "$" and unit "p/w".</summary>
internal readonly struct MoneyText
{
    public readonly double Value;
    public readonly string Prefix;
    public readonly string Unit;

    public MoneyText(double value, string prefix, string unit)
    {
        Value = value;
        Prefix = prefix ?? "";
        Unit = unit ?? "";
    }
}

internal static class Money
{
    private static readonly Regex Rx = new(
        @"(?<pre>[^\d\s\-+]{0,3})\s?(?<num>\d{1,3}(?:[,.  ]\d{3})+(?:[.,]\d+)?|\d+(?:[.,]\d+)?)\s?(?<suf>[KkMm](?![a-z]))?\s*(?<unit>.*)$",
        RegexOptions.Compiled);

    /// <summary>Parses the first amount in a game text. "$725 p/w", "$1,000 p/w", "$17K per week", "725".</summary>
    public static bool TryParse(string text, out MoneyText money)
    {
        money = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = Ui.Clean(text);
        var m = Rx.Match(t);
        if (!m.Success) return false;
        if (!TryNumber(m.Groups["num"].Value, out var v)) return false;
        var suf = m.Groups["suf"].Value.ToUpperInvariant();
        if (suf == "K") v *= 1_000;
        else if (suf == "M") v *= 1_000_000;
        var pre = m.Groups["pre"].Value.Trim();
        if (pre.Any(char.IsLetter) && pre.Length > 2) pre = ""; // "Max" etc. isn't a currency
        money = new MoneyText(v, pre, m.Groups["unit"].Value.Trim());
        return true;
    }

    public static double? Parse(string text) => TryParse(text, out var m) ? m.Value : null;

    private static bool TryNumber(string s, out double v)
    {
        s = s.Replace(" ", "").Replace(" ", "");
        int commas = s.Count(c => c == ','), dots = s.Count(c => c == '.');
        if (commas > 0 && dots > 0)
        {
            // "1,234.56" or "1.234,56": the last separator is the decimal one
            if (s.LastIndexOf(',') > s.LastIndexOf('.')) s = s.Replace(".", "").Replace(',', '.');
            else s = s.Replace(",", "");
        }
        else if (commas > 0)
        {
            s = Regex.IsMatch(s, @"^\d{1,3}(,\d{3})+$") ? s.Replace(",", "") : s.Replace(',', '.');
        }
        else if (dots > 0 && Regex.IsMatch(s, @"^\d{1,3}(\.\d{3})+$"))
        {
            s = s.Replace(".", ""); // "1.000" = one thousand
        }
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    public static string Format(double v, string prefix = "$", string unit = "")
    {
        var n = v >= 100 || Math.Abs(v - Math.Round(v)) < 0.005
            ? Math.Round(v).ToString("#,0", CultureInfo.InvariantCulture)
            : v.ToString("#,0.##", CultureInfo.InvariantCulture);
        return (prefix ?? "") + n + (string.IsNullOrEmpty(unit) ? "" : " " + unit);
    }

    /// <summary>Digits only, for typing into the game's salary field ("1000").</summary>
    public static string Digits(double v) => Math.Round(v).ToString("0", CultureInfo.InvariantCulture);

    /// <summary>A comfortable button step for adjusting a wage of this size (about 5%, on a 1-2-2.5-5 scale).</summary>
    public static double NiceStep(double v, double fraction = 0.05)
    {
        var target = Math.Max(1, Math.Abs(v) * fraction);
        var b = Math.Pow(10, Math.Floor(Math.Log10(target)));
        foreach (var m in new[] { 5, 2.5, 2, 1 })
            if (m * b <= target + 1e-9) return m * b;
        return b;
    }

    /// <summary>Rounds an ask to a believable number (no "$1,037").</summary>
    public static double RoundAsk(double v)
    {
        double step = v < 1_000 ? 5 : v < 10_000 ? 25 : v < 100_000 ? 250 : 1_000;
        return Math.Round(v / step) * step;
    }
}

internal static class YearsText
{
    private static readonly Regex YearRx = new(@"(\d+(?:[.,]\d+)?)\s*(?:years?|yrs?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MonthRx = new(@"(\d+)\s*months?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>"1 year (Jun 2026 - Jun 2027)" → 1, "3 years (…)" → 3, "18 months" → 1.5.</summary>
    public static double? Parse(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = YearRx.Match(text);
        if (m.Success && double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return y;
        m = MonthRx.Match(text);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var mo)) return Math.Round(mo / 12.0, 2);
        return null;
    }

    public static string Format(double y) =>
        Math.Abs(y - Math.Round(y)) < 0.01 ? $"{y:0} year{(Math.Round(y) == 1 ? "" : "s")}" : $"{y * 12:0} months";
}
