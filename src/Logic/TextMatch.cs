using System.Text;

namespace ManagerAgent;

internal enum MatchKind
{
    None,
    /// <summary>Same text after normalising case/punctuation/numbers.</summary>
    Exact,
    /// <summary>Same text once names (clubs, people, competitions) are masked out.</summary>
    Template,
    /// <summary>Very similar wording, clearly closer than any alternative.</summary>
    Fuzzy,
    /// <summary>Several stored items fit equally well — the agent won't guess.</summary>
    Ambiguous,
}

internal readonly struct MatchResult
{
    public readonly int Index;
    public readonly MatchKind Kind;
    public readonly double Score;

    public MatchResult(int index, MatchKind kind, double score)
    {
        Index = index; Kind = kind; Score = score;
    }

    public bool Ok => Index >= 0 && Kind is MatchKind.Exact or MatchKind.Template or MatchKind.Fuzzy;
    public static MatchResult NoMatch => new(-1, MatchKind.None, 0);
    public override string ToString() => Kind == MatchKind.Fuzzy ? $"fuzzy {Score:0.00}" : Kind.ToString().ToLowerInvariant();
}

/// <summary>
/// Text normalisation and matching used to recognise questions and answers across
/// interviews at different clubs, where names change but the wording doesn't.
/// </summary>
internal static class TextMatch
{
    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "a","an","the","and","or","but","to","of","in","on","for","with","at","by","from","as","is","are","was","were",
        "be","been","it","its","this","that","these","those","you","your","we","our","i","me","my","do","does","did",
        "have","has","had","what","how","would","will","can","could","should","about","there","their","they","them","if","so",
    };

    private static readonly HashSet<string> NotNames = new(StringComparer.Ordinal)
    {
        "I","I'm","I'd","I'll","I've","OK","Ok",
    };

    /// <summary>Lowercase, punctuation → space, digit runs → '#', collapsed whitespace.</summary>
    public static string Basic(string s)
    {
        s = Ui.Clean(s);
        var sb = new StringBuilder(s.Length);
        bool lastSpace = true, lastHash = false;
        foreach (var ch in s)
        {
            if (char.IsDigit(ch))
            {
                if (!lastHash) { sb.Append('#'); lastHash = true; lastSpace = false; }
                continue;
            }
            lastHash = false;
            if (char.IsLetter(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastSpace = false;
            }
            else if (ch == '\'' || ch == '’')
            {
                // drop apostrophes so "club's" == "clubs"
            }
            else if (!lastSpace)
            {
                sb.Append(' ');
                lastSpace = true;
            }
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Like <see cref="Basic"/>, but capitalised words that don't start a sentence (club,
    /// player, competition names...) become a single '@' placeholder per run.
    /// "What do you think of Wigan Athletic's squad?" → "what do you think of @ squad"
    /// </summary>
    public static string Template(string s)
    {
        s = Ui.Clean(s);
        var outTokens = new List<string>();
        bool sentenceStart = true;
        foreach (var raw in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var core = raw.Trim('"', '\'', '“', '”', '‘', '’', '(', ')', '[', ']', ',', '.', '!', '?', ':', ';', '-', '–', '—', '…');
            if (core.EndsWith("'s", StringComparison.Ordinal) || core.EndsWith("’s", StringComparison.Ordinal))
                core = core.Substring(0, core.Length - 2);

            bool endsSentence = raw.EndsWith(".") || raw.EndsWith("?") || raw.EndsWith("!") || raw.EndsWith(":")
                                || raw.EndsWith(".\"") || raw.EndsWith("?\"") || raw.EndsWith("!\"") || raw.EndsWith("…");
            if (core.Length == 0) { sentenceStart |= endsSentence; continue; }

            string tok;
            if (core.Any(char.IsDigit)) tok = "#";
            else if (!sentenceStart && char.IsUpper(core[0]) && !NotNames.Contains(core)) tok = "@";
            else tok = Basic(core);

            if (tok.Length > 0 && !(tok == "@" && outTokens.Count > 0 && outTokens[^1] == "@"))
                outTokens.Add(tok);
            sentenceStart = endsSentence;
        }
        return string.Join(" ", outTokens);
    }

    /// <summary>Weighted token Dice similarity on template forms (stop-words count half).</summary>
    public static double Similarity(string a, string b)
    {
        var ta = new HashSet<string>(Template(a).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var tb = new HashSet<string>(Template(b).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (ta.Count == 0 || tb.Count == 0) return 0;
        double W(string t) => t == "@" || t == "#" ? 0.4 : Stop.Contains(t) ? 0.5 : 1.0;
        double sa = ta.Sum(W), sb = tb.Sum(W), inter = ta.Where(tb.Contains).Sum(W);
        return 2 * inter / (sa + sb);
    }

    /// <summary>
    /// Finds <paramref name="text"/> among <paramref name="candidates"/>: exact, then template,
    /// then fuzzy. Two equally good candidates → Ambiguous (the caller should ask the user).
    /// </summary>
    public static MatchResult Find(string text, IReadOnlyList<string> candidates, double fuzzyThreshold, double fuzzyMargin)
    {
        if (string.IsNullOrWhiteSpace(text) || candidates == null || candidates.Count == 0) return MatchResult.NoMatch;

        var basic = Basic(text);
        var exact = new List<int>();
        for (int i = 0; i < candidates.Count; i++)
            if (Basic(candidates[i]) == basic) exact.Add(i);
        if (exact.Count == 1) return new MatchResult(exact[0], MatchKind.Exact, 1);
        if (exact.Count > 1) return new MatchResult(exact[0], MatchKind.Ambiguous, 1);

        var tmpl = Template(text);
        var templ = new List<int>();
        for (int i = 0; i < candidates.Count; i++)
            if (Template(candidates[i]) == tmpl) templ.Add(i);
        if (templ.Count == 1) return new MatchResult(templ[0], MatchKind.Template, 1);
        if (templ.Count > 1) return new MatchResult(templ[0], MatchKind.Ambiguous, 1);

        int best = -1;
        double bestScore = 0, second = 0;
        for (int i = 0; i < candidates.Count; i++)
        {
            var s = Similarity(text, candidates[i]);
            if (s > bestScore) { second = bestScore; bestScore = s; best = i; }
            else if (s > second) second = s;
        }
        if (best < 0 || bestScore < fuzzyThreshold) return new MatchResult(-1, MatchKind.None, bestScore);
        if (bestScore - second < fuzzyMargin) return new MatchResult(best, MatchKind.Ambiguous, bestScore);
        return new MatchResult(best, MatchKind.Fuzzy, bestScore);
    }
}
