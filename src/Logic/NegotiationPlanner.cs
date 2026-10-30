namespace ManagerAgent;

/// <summary>One side of a contract: wage (in the game's displayed unit), length in years, clauses.</summary>
internal sealed class Terms
{
    public double Wage;
    public double Years;
    public List<string> Clauses = new();

    public Terms Clone() => new() { Wage = Wage, Years = Years, Clauses = Clauses.ToList() };

    public bool Has(string clause) => Clauses.Any(c => ClauseNames.Same(c, clause));

    public Terms With(string clause)
    {
        var t = Clone();
        if (!t.Has(clause)) t.Clauses.Add(clause);
        return t;
    }

    public Terms Without(string clause)
    {
        var t = Clone();
        t.Clauses.RemoveAll(c => ClauseNames.Same(c, clause));
        return t;
    }

    public bool SameAs(Terms o, double wageTolerance)
    {
        if (o == null) return false;
        if (Math.Abs(Wage - o.Wage) > wageTolerance) return false;
        if (Math.Abs(Years - o.Years) > 0.01) return false;
        return Clauses.Count == o.Clauses.Count && Clauses.All(o.Has);
    }

    public string Describe(string prefix = "$", string unit = "p/w")
    {
        var s = Money.Format(Wage, prefix, unit) + ", " + Years.Format();
        return Clauses.Count == 0 ? s : s + " + " + string.Join(", ", Clauses);
    }

    public override string ToString() => Describe();
}

internal static class YearsExt
{
    public static string Format(this double y) => YearsText.Format(y);
}

internal static class ClauseNames
{
    public static bool Same(string a, string b)
    {
        if (a == null || b == null) return false;
        var x = TextMatch.Basic(a);
        var y = TextMatch.Basic(b);
        return x == y || (x.Length > 8 && y.Length > 8 && (x.Contains(y) || y.Contains(x)));
    }
}

/// <summary>What you want from a contract. Wage numbers are in whatever unit the game shows (e.g. per week).</summary>
internal sealed class NegotiationPrefs
{
    public double WageMin;
    public double WageTarget;
    public double YearsWant = 3;
    public double YearsMin = 1;
    /// <summary>Clauses to ask for, most important first.</summary>
    public List<string> WantClauses = new();
    /// <summary>Clauses to remove if the club includes them.</summary>
    public List<string> NeverClauses = new();
    /// <summary>Total "Suggest Terms" presses before settling (the club may lose patience).</summary>
    public int MaxSuggestions = 8;
    /// <summary>Accept a clause even if the club trims the wage by up to this many percent for it.</summary>
    public double ClauseTradeMaxPct = 5;
    /// <summary>Wage counter-rounds before settling on the club's figure.</summary>
    public int MaxWageRounds = 4;

    public bool WageSet => WageMin > 0 && WageTarget > 0;
}

internal enum DecisionKind { Propose, Accept, NeedUser }

internal enum NeedReason { None, BelowMinimum, LengthTooShort, TooManyRounds, ClubMaxBelowMinimum }

internal sealed class Decision
{
    public DecisionKind Kind;
    public Terms Terms;
    public NeedReason Reason;
    /// <summary>Plain-language explanation for the activity log / panel.</summary>
    public string Why;

    public static Decision Propose(Terms t, string why) => new() { Kind = DecisionKind.Propose, Terms = t, Why = why };
    public static Decision Accept(Terms t, string why) => new() { Kind = DecisionKind.Accept, Terms = t, Why = why };
    public static Decision Need(NeedReason r, Terms best, string why) => new() { Kind = DecisionKind.NeedUser, Terms = best, Reason = r, Why = why };
}

/// <summary>
/// The agent's negotiating strategy. Pure logic: give it the club's current offer, it says what to
/// propose next, when to accept, or when to hand the decision back to you.
///
///   1. Wage + length: open at your target (capped at the club's maximum), ask for your preferred
///      length; after each counter, meet them roughly halfway, never going below your minimum.
///      Settle when the gap is small or after a few rounds.
///   2. Clauses: ask for your wished clauses one at a time on top of the agreed wage, keeping each one
///      the club accepts, unless it costs more wage than you allow.
///   3. Accept the result if it meets your minimums; otherwise hand back to you.
/// </summary>
internal sealed class NegotiationPlanner
{
    private enum Phase { Main, Clauses, Done }

    private readonly NegotiationPrefs _p;
    private double? _clubMax;
    private Phase _phase = Phase.Main;
    private Terms _last;          // our last proposal
    private Terms _agreed;        // best club-approved terms that meet your minimums
    private int _wageRounds, _lengthRefusals;
    private double _yearsAsk;
    private string _pendingClause;
    private bool _reverting;
    private readonly HashSet<string> _skip = new(StringComparer.OrdinalIgnoreCase);

    public int Suggestions { get; private set; }
    public double? ClubMax => _clubMax;
    public Terms Agreed => _agreed;

    public NegotiationPlanner(NegotiationPrefs prefs, double? clubMax)
    {
        _p = prefs;
        _clubMax = clubMax;
        _yearsAsk = Math.Max(prefs.YearsWant, prefs.YearsMin);
    }

    /// <summary>The most the agent will ask for: your target, but never above the club's maximum.</summary>
    public double Cap => Math.Min(_p.WageTarget, _clubMax ?? double.MaxValue);

    public void SetClubMax(double max)
    {
        if (max > 0 && (_clubMax == null || max < _clubMax)) _clubMax = max;
    }

    public void MarkClauseUnavailable(string clause) => _skip.Add(TextMatch.Basic(clause));

    /// <summary>What actually went on screen for the last proposal (wage may be capped, length rounded to an option).</summary>
    public void Proposed(Terms actual)
    {
        if (actual != null) _last = actual.Clone();
    }

    private double Tol => Math.Max(1, Cap * 0.002);

    /// <summary>Strict: a deal is never accepted even a little below your minimum.</summary>
    private bool MeetsMinimums(Terms t) =>
        t.Wage >= _p.WageMin - 0.5 && t.Years >= _p.YearsMin - 0.01 && !_p.NeverClauses.Any(t.Has);

    /// <param name="current">The club's current offer as shown on screen (their initial offer, or their reply to our last proposal).</param>
    public Decision Next(Terms current)
    {
        if (_clubMax.HasValue && _clubMax.Value < _p.WageMin - Tol)
            return Decision.Need(NeedReason.ClubMaxBelowMinimum, current,
                $"The club's maximum ({_clubMax:#,0}) is below your minimum ({_p.WageMin:#,0}).");

        var d = _phase == Phase.Main ? Main(current) : null;
        if (d != null) return Limit(d, current);
        return Limit(Clauses(current), current);
    }

    /// <summary>Stops proposing once the suggestion budget is spent.</summary>
    private Decision Limit(Decision d, Terms current)
    {
        if (d.Kind != DecisionKind.Propose) return d;
        if (Suggestions >= _p.MaxSuggestions)
        {
            return MeetsMinimums(current)
                ? Decision.Accept(current, $"Reached {_p.MaxSuggestions} proposals — taking their current offer.")
                : Decision.Need(NeedReason.TooManyRounds, current, $"Reached {_p.MaxSuggestions} proposals without a deal that meets your minimums.");
        }
        Suggestions++;
        _last = d.Terms.Clone();
        return d;
    }

    // ------------------------------------------------------------------ wage + length

    private Decision Main(Terms current)
    {
        var cap = Cap;
        if (_last == null)
        {
            _yearsAsk = Math.Max(_yearsAsk, current.Years);
            var clean = current.Clauses.Where(c => !_p.NeverClauses.Any(n => ClauseNames.Same(n, c))).ToList();
            if (current.Wage >= cap - Tol && current.Years >= _yearsAsk - 0.01 && clean.Count == current.Clauses.Count)
            {
                _agreed = current.Clone();
                _phase = Phase.Clauses;
                return null;
            }
            var open = new Terms { Wage = Math.Max(cap, current.Wage), Years = _yearsAsk, Clauses = clean };
            return Decision.Propose(open, $"Opening: asking {Money.Format(open.Wage, "", "")} and {open.Years.Format()}.");
        }

        if (current.SameAs(_last, Tol))
        {
            _agreed = current.Clone();
            _phase = Phase.Clauses;
            return null;
        }

        if (current.Years < _last.Years - 0.01) _lengthRefusals++;
        bool wageOk = current.Wage >= _last.Wage - Tol;

        if (wageOk)
        {
            // Only the length (or a clause) was pushed back.
            if (current.Years >= _p.YearsMin - 0.01 && (_lengthRefusals >= 2 || current.Years >= _yearsAsk - 0.01))
                return Settle(current, "They agreed the wage; settling on their contract length.");
            if (_lengthRefusals >= 2)
                return Decision.Need(NeedReason.LengthTooShort, current,
                    $"They won't offer more than {current.Years.Format()}; your minimum is {_p.YearsMin.Format()}.");
            _yearsAsk = Math.Max(_p.YearsMin, Math.Max(current.Years, _yearsAsk - 1));
            var t = new Terms { Wage = _last.Wage, Years = _yearsAsk, Clauses = current.Clauses.ToList() };
            return Decision.Propose(t, $"They pushed back on length — trying {t.Years.Format()}.");
        }

        _wageRounds++;
        if (current.Years < _yearsAsk - 0.01 && _lengthRefusals >= 1)
            _yearsAsk = Math.Max(_p.YearsMin, Math.Max(current.Years, _yearsAsk - 1));
        double yearsNext = _lengthRefusals >= 2 ? Math.Max(current.Years, _p.YearsMin) : _yearsAsk;

        double gap = _last.Wage - current.Wage;
        double minGap = Math.Max(Money.NiceStep(current.Wage, 0.02), cap * 0.01);
        bool atFloor = _last.Wage <= _p.WageMin + Tol;

        if (_wageRounds >= _p.MaxWageRounds || gap <= minGap || atFloor)
        {
            if (current.Wage < _p.WageMin - 0.5)
                return Decision.Need(NeedReason.BelowMinimum, current,
                    $"Their best wage is {current.Wage:#,0}, below your minimum {_p.WageMin:#,0}.");
            if (current.Years >= _p.YearsMin - 0.01 && (yearsNext <= current.Years + 0.01 || _lengthRefusals >= 2))
                return Settle(current, "Close enough on wage — taking their figure.");
            if (_lengthRefusals >= 2)
                return Decision.Need(NeedReason.LengthTooShort, current,
                    $"They won't offer more than {current.Years.Format()}; your minimum is {_p.YearsMin.Format()}.");
            var keepWage = new Terms { Wage = current.Wage, Years = yearsNext, Clauses = current.Clauses.ToList() };
            return Decision.Propose(keepWage, $"Accepting their wage, asking for {keepWage.Years.Format()}.");
        }

        double next = Money.RoundAsk(current.Wage + gap * 0.5);
        next = Math.Max(next, _p.WageMin);
        next = Math.Min(next, Math.Min(cap, _last.Wage - Math.Max(1, minGap / 2)));
        if (next <= current.Wage + Tol) next = Math.Min(_last.Wage, current.Wage + minGap);
        var counter = new Terms { Wage = next, Years = yearsNext, Clauses = current.Clauses.Where(c => !_p.NeverClauses.Any(n => ClauseNames.Same(n, c))).ToList() };
        return Decision.Propose(counter, $"They offered {current.Wage:#,0} — countering at {next:#,0}.");
    }

    private Decision Settle(Terms current, string why)
    {
        _agreed = current.Clone();
        _phase = Phase.Clauses;
        _settledWhy = why;
        return null;
    }

    private string _settledWhy;

    // ------------------------------------------------------------------ clauses

    private Decision Clauses(Terms current)
    {
        if (_phase == Phase.Done) return Final(current);

        if (_reverting)
        {
            _reverting = false;
            if (current.SameAs(_last, Tol) || MeetsMinimums(current)) _agreed = current.Clone();
            else return Decision.Need(NeedReason.BelowMinimum, current, "After dropping a clause their offer fell below your minimums.");
        }
        else if (_pendingClause != null)
        {
            var clause = _pendingClause;
            _pendingClause = null;
            bool got = current.Has(clause);
            bool wageHeld = current.Wage >= _agreed.Wage * (1 - _p.ClauseTradeMaxPct / 100.0) - Tol;
            if (got && wageHeld && MeetsMinimums(current))
            {
                _agreed = current.Clone();
            }
            else
            {
                _skip.Add(TextMatch.Basic(clause));
                bool stillFine = current.Wage >= _agreed.Wage - Tol && current.Years >= _agreed.Years - 0.01 && !current.Has(clause);
                if (stillFine && MeetsMinimums(current))
                {
                    _agreed = current.Clone();
                }
                else
                {
                    _reverting = true;
                    var revert = _agreed.Clone();
                    return Decision.Propose(revert, got
                        ? $"{clause} would cost too much wage — dropping it."
                        : $"They refused {clause} and changed the terms — going back to the agreed deal.");
                }
            }
        }

        foreach (var c in _p.WantClauses)
        {
            if (_skip.Contains(TextMatch.Basic(c)) || current.Has(c)) continue;
            if (_p.NeverClauses.Any(n => ClauseNames.Same(n, c))) continue;
            _pendingClause = c;
            _skip.Add(TextMatch.Basic(c)); // ask once
            return Decision.Propose(_agreed.With(c), $"Asking for: {c}.");
        }

        _phase = Phase.Done;
        return Final(current);
    }

    private Decision Final(Terms current)
    {
        if (MeetsMinimums(current))
            return Decision.Accept(current, _settledWhy ?? "Deal meets your terms.");
        return Decision.Need(NeedReason.BelowMinimum, current, "The final offer doesn't meet your minimums.");
    }
}
