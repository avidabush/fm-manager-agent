namespace ManagerAgent;

/// <summary>
/// Represents you in contract talks. On the manager contract screen (or straight from the job-offer
/// message) it checks the club's maximum salary against your range, then works wage, contract length
/// and clauses using <see cref="NegotiationPlanner"/>, pressing "Suggest Terms" each round. It stops
/// before "Finalize Deal" and asks you, unless you've switched on automatic signing.
/// </summary>
internal sealed class Negotiator : AgentDuty
{
    private bool _onScreen, _onOffer, _finalizing;
    private string _status = "", _club = "";
    private string _handledClub, _handledOffer;
    private double _readTime = -1;
    private ContractScreen _cs;
    private UiNode _offerBtn;
    private Terms _lastSeen;
    private double? _clubMax;

    public Negotiator(IAgentHost host) : base(host) { }

    public override string Id => "contract";
    public override string Title => "Contract negotiation";
    public string Status => _status;
    public string Club => _club;
    public double? ClubMax => _clubMax;
    public Terms LastSeenOffer => _lastSeen;
    public bool OnContractScreen => _onScreen;
    public bool OnOfferMessage => _onOffer && !_onScreen;
    public bool CanStart => !Runner.Active && (_onScreen || _onOffer);

    public override string TabStatus =>
        IsWaitingForYou ? "your turn" : IsRunning ? "negotiating" : _onScreen ? "contract offer" : _onOffer ? "job offer" : null;

    public override string StateKey =>
        $"{_status}|{_club}|{_onScreen}|{_onOffer}|{Runner.Active}|{Choice?.Title}|{Choice?.Text}|{_clubMax}|{Data.Revision}";

    private string P => Data.Contract.WagePrefix;
    private string U => Data.Contract.WageUnit;
    private string M(double v) => Money.Format(v, P, U);

    public void Start()
    {
        if (!CanStart) return;
        Runner.Start("contract", Negotiate(fromOffer: !_onScreen));
    }

    // ------------------------------------------------------------------ watching

    private ContractScreen Read(ScreenModel s) => s == null ? null : ContractScreen.Read(s, Text, Data.KnownClauseNames);
    private ContractScreen Read() => Read(Game.Screen);

    protected override void Watch()
    {
        var s = Game.Screen;
        if (s == null) return;
        if (s.Time != _readTime)
        {
            _readTime = s.Time;
            _cs = Read(s);
            _offerBtn = ContractScreen.OfferMessageButton(s, Text);
        }

        bool on = _cs != null;
        if (on && !_onScreen)
        {
            _club = _cs.Club;
            _lastSeen = _cs.Terms;
            RememberStyle(_cs);
            if (!Runner.Active) _status = $"Offer from {Or(_club, "the club")}: {M(_lastSeen.Wage)}, {_lastSeen.Years.Format()}";
            Host.Attention();
        }
        if (!on && _onScreen)
        {
            _handledClub = null;
            if (Runner.Active && !_finalizing)
            {
                Stop("the contract screen closed (the club may have ended talks)");
                _status = "The contract screen closed — the club may have ended talks.";
            }
            else if (!_finalizing && _status.StartsWith("Offer from")) _status = "";
        }
        _onScreen = on;

        bool offer = _offerBtn != null;
        if (offer && !_onOffer && !on && !Runner.Active)
        {
            _status = "Job offer waiting";
            Host.Attention();
        }
        if (!offer && _onOffer) _handledOffer = null;
        _onOffer = offer;

        if (Runner.Active || !Data.Contract.AutoNegotiate || !Data.ToPrefs().WageSet || Host.OtherDutyBusy(this)) return;
        if (on && _handledClub == null)
        {
            Runner.Start("contract", Negotiate(false));
        }
        else if (offer && !on && _handledOffer == null)
        {
            _handledOffer = "x";
            Runner.Start("contract", Negotiate(true));
        }
    }

    private void RememberStyle(ContractScreen cs)
    {
        if (cs.WagePrefix != Data.Contract.WagePrefix || cs.WageUnit != Data.Contract.WageUnit)
        {
            Data.Contract.WagePrefix = cs.WagePrefix;
            Data.Contract.WageUnit = cs.WageUnit;
            Data.Save();
        }
    }

    public override void OnUserClick(UiNode clicked)
    {
        if (!IsRunning || _cs == null || clicked == null) return;
        // A click inside the offer (left of the chat column) means you're taking over.
        var action = _cs.Suggest ?? _cs.Finalize;
        if (action == null || clicked.CX < action.X - 20) Stop("you took over");
    }

    protected override void OnStopped()
    {
        _finalizing = false;
        if (_status.StartsWith("Negotiating") || _status.StartsWith("Checking") || _status.StartsWith("Proposing") || _status.StartsWith("Opening"))
            _status = "Stopped";
    }

    private static string Or(string a, string b) => string.IsNullOrWhiteSpace(a) ? b : a;

    // ------------------------------------------------------------------ main script

    private IEnumerable<Wait> Negotiate(bool fromOffer)
    {
        _finalizing = false;
        _clubMax = null;
        var r = new Result();

        if (fromOffer)
        {
            _status = "Opening negotiations…";
            foreach (var w in PressUntil(s => ContractScreen.OfferMessageButton(s, Text), s => ContractScreen.IsShowing(s, Text), "Enter Contract Negotiations", r, 6))
                yield return w;
            if (!r.Ok) { Fail("Couldn't open the negotiations: " + r.Error); yield break; }
            yield return Wait.Fresh(Game, 0.4);
        }
        else yield return Fresh();

        var cs = Read();
        if (cs == null) { Fail("This isn't a head coach contract screen."); yield break; }
        _club = Or(cs.Club, "the club");
        _handledClub = _club;
        RememberStyle(cs);
        var initial = cs.Terms;
        _lastSeen = initial;
        AgentLog.Info($"{_club}'s offer: {initial.Describe(P, U)}.");

        // 1. Your range must be set.
        while (!Data.ToPrefs().WageSet)
        {
            var c = new UserChoice
            {
                Title = "Set your wage range",
                Text = $"{_club} offers {M(initial.Wage)}. Tell me your minimum and target wage (Your terms), then press Continue.",
            }.Add("Start from their offer", "seed", BtnKind.Primary).Add("Your terms", "edit").Add("Continue", "go").Add("Stop", "stop", BtnKind.Danger);
            yield return c;
            if (c.Picked == "stop") { Done("Stopped"); yield break; }
            if (c.Picked == "seed") SeedFromOffer(initial.Wage);
            if (c.Picked == "edit") Host.ShowTerms();
        }

        // 2. The club's maximum salary.
        _status = "Checking the club's maximum salary…";
        var mr = new Result();
        foreach (var w in ProbeMax(mr)) yield return w;
        if (mr.Ok)
        {
            _clubMax = mr.Value;
            AgentLog.Info($"{_club}'s maximum salary: {M(mr.Value)}.");
        }
        else AgentLog.Info($"Couldn't see {_club}'s maximum salary ({mr.Error}); I'll find their limit by negotiating.");

        // 3. Is your range possible here?
        double? minOverride = null;   // "go for the club's maximum" applies to this club only
        while (_clubMax.HasValue)
        {
            var p = Data.ToPrefs();
            if (minOverride.HasValue) p.WageMin = minOverride.Value;
            UserChoice c = null;
            if (_clubMax < p.WageMin - 0.5)
            {
                c = new UserChoice
                {
                    Title = "Your range is above this club's maximum",
                    Text = $"{_club} can pay at most {M(_clubMax.Value)}, below your minimum of {M(p.WageMin)}.",
                }.Add($"Go for {M(_clubMax.Value)} with this club", "lower", BtnKind.Primary).Add("Adjust my range", "edit").Add("Stop", "stop", BtnKind.Danger);
            }
            else if (_clubMax < p.WageTarget - 0.5)
            {
                c = new UserChoice
                {
                    Title = "Your target is above this club's maximum",
                    Text = $"{_club} can pay at most {M(_clubMax.Value)}; your target is {M(p.WageTarget)}. Your minimum ({M(p.WageMin)}) is still reachable.",
                }.Add($"Aim for {M(_clubMax.Value)}", "aim", BtnKind.Primary).Add("Adjust my range", "edit").Add("Stop", "stop", BtnKind.Danger);
            }
            if (c == null) break;
            _status = c.Title;
            yield return c;
            if (c.Picked == "stop") { Done("Stopped"); yield break; }
            if (c.Picked == "aim") break;
            if (c.Picked == "lower")
            {
                minOverride = _clubMax.Value;
                AgentLog.Info($"For {_club} only: aiming for their maximum, {M(_clubMax.Value)}.");
                break;
            }
            if (c.Picked == "edit")
            {
                Host.ShowTerms();
                var again = new UserChoice { Title = "Adjust your range", Text = "Change your wage range in Your terms, then press Continue." }
                    .Add("Continue", "go", BtnKind.Primary).Add("Stop", "stop", BtnKind.Danger);
                yield return again;
                if (again.Picked == "stop") { Done("Stopped"); yield break; }
            }
        }

        // 4. Negotiate.
        var prefs = Data.ToPrefs();
        if (minOverride.HasValue) { prefs.WageMin = minOverride.Value; prefs.WageTarget = Math.Max(prefs.WageMin, Math.Min(prefs.WageTarget, minOverride.Value)); }
        var planner = new NegotiationPlanner(prefs, _clubMax);
        for (int round = 0; round < 40; round++)
        {
            yield return Fresh();
            cs = Read();
            if (cs == null) { Fail("The contract screen closed — the club may have ended talks."); yield break; }
            var current = cs.Terms;
            _lastSeen = current;
            var d = planner.Next(current);

            if (d.Kind == DecisionKind.Propose)
            {
                var target = d.Terms;
                _status = $"Proposing {target.Describe(P, U)}";
                AgentLog.Info(Pretty(d.Why));
                var ar = new Result();
                foreach (var w in ApplyTerms(target, planner, ar)) yield return w;
                if (!ar.Ok)
                {
                    var c = new UserChoice { Title = "I got stuck", Text = ar.Error + " You can finish this one yourself." }
                        .Add("Try again", "retry", BtnKind.Primary).Add("I'll take over", "stop");
                    _status = "Stuck";
                    yield return c;
                    if (c.Picked == "retry") continue;
                    Done("You took over");
                    yield break;
                }

                yield return Fresh();
                cs = Read();
                if (cs?.CanSuggest == true)
                {
                    var before = cs.Terms;
                    _status = $"Waiting for {_club}'s answer…";
                    foreach (var w in PressUntil(s => Read(s)?.Suggest, s => { var x = Read(s); return x == null || x.Suggest == null; }, "Suggest Terms", r, 10))
                        yield return w;
                    if (!r.Ok) { Fail("Suggest Terms didn't work: " + r.Error); yield break; }
                    yield return Wait.Seconds(Game, 0.8);
                    yield return Fresh();
                    var after = Read();
                    if (after == null) { Fail($"{_club} ended the talks."); yield break; }
                    var reply = after.Terms;
                    bool accepted = reply.SameAs(before, Math.Max(1, before.Wage * 0.002));
                    AgentLog.Info(accepted ? $"{_club} agreed: {reply.Describe(P, U)}." : $"{_club} countered: {reply.Describe(P, U)}.");
                }
                continue;
            }

            if (d.Kind == DecisionKind.Accept)
            {
                var deal = current.Describe(P, U);
                AgentLog.Info($"Deal ready with {_club}: {deal}.");
                if (Data.Contract.AutoSign)
                {
                    foreach (var w in Sign(current)) yield return w;
                    yield break;
                }
                var c = new UserChoice { Title = "Deal ready", Text = $"{_club}: {deal}. {Pretty(d.Why)}" }
                    .Add("Sign it", "sign", BtnKind.Primary).Add("I'll finish it myself", "leave");
                _status = "Deal ready — waiting for you";
                yield return c;
                if (c.Picked == "sign") foreach (var w in Sign(current)) yield return w;
                else Done("Deal ready — over to you");
                yield break;
            }

            // NeedUser
            {
                var best = d.Terms ?? current;
                var c = new UserChoice { Title = TitleFor(d.Reason), Text = $"{Pretty(d.Why)} Their offer now: {best.Describe(P, U)}." };
                if (cs.CanFinalize) c.Add("Accept it anyway", "sign");
                c.Add("I'll take over", "leave", BtnKind.Primary);
                _status = c.Title;
                yield return c;
                if (c.Picked == "sign") foreach (var w in Sign(current)) yield return w;
                else Done("Over to you");
                yield break;
            }
        }
        Fail("Talks went on too long — over to you.");
    }

    private static string TitleFor(NeedReason r) => r switch
    {
        NeedReason.BelowMinimum => "Below your minimum",
        NeedReason.LengthTooShort => "Contract too short",
        NeedReason.TooManyRounds => "No deal yet",
        NeedReason.ClubMaxBelowMinimum => "Your range is above this club's maximum",
        _ => "Your call",
    };

    /// <summary>Planner explanations use bare numbers; show them as money.</summary>
    private string Pretty(string why) => System.Text.RegularExpressions.Regex.Replace(why ?? "", @"(?<![\w$€£])(\d{1,3}(?:,\d{3})+|\d{3,})(?![\w%])",
        m => Money.TryParse(m.Value, out var v) ? Money.Format(v.Value, P, "") : m.Value);

    private void SeedFromOffer(double offer)
    {
        Data.Contract.WageMin = Money.RoundAsk(offer);
        Data.Contract.WageTarget = Money.RoundAsk(offer * 1.5);
        Data.Save();
        AgentLog.Info($"Wage range set to {M(Data.Contract.WageMin)} – {M(Data.Contract.WageTarget)} (change it in Your terms).");
    }

    private void Fail(string why)
    {
        AgentLog.Warn(why);
        _status = why;
    }

    private void Done(string status) => _status = status;

    private IEnumerable<Wait> Sign(Terms deal)
    {
        _finalizing = true;
        _status = "Signing…";
        var r = new Result();
        foreach (var w in PressUntil(s => Read(s)?.Finalize, s => !ContractScreen.IsShowing(s, Text) || s.Text(Text.ConfirmTitle) != null, "Finalize Deal", r, 6))
            yield return w;
        if (r.Ok && Game.Screen.Text(Text.ConfirmTitle) != null)
            foreach (var w in PressUntil(s => s.Text(Text.ConfirmTitle) != null ? s.Button(Text.Yes) : null, s => s.Text(Text.ConfirmTitle) == null, "Yes", r, 4))
                yield return w;
        _finalizing = false;
        if (!r.Ok) { Fail("Couldn't press Finalize Deal: " + r.Error); yield break; }
        AgentLog.Info($"Signed with {_club}: {deal.Describe(P, U)}.");
        _status = $"Signed with {_club}";
    }

    // ------------------------------------------------------------------ applying terms

    private IEnumerable<Wait> ApplyTerms(Terms target, NegotiationPlanner planner, Result res)
    {
        res.Ok = false;
        var actual = target.Clone();
        var cs = Read();
        if (cs == null) { res.Error = "The contract screen closed."; yield break; }
        var r = new Result();

        // Length first: some clauses only unlock on longer contracts.
        if (cs.Years == null || Math.Abs(cs.Years.Value - target.Years) > 0.01)
        {
            foreach (var w in SetYears(target.Years, r)) yield return w;
            if (!r.Ok) { res.Error = "I couldn't change the contract length: " + r.Error; yield break; }
            actual.Years = r.Value;
        }

        cs = Read();
        double tol = Math.Max(1, target.Wage * 0.002);
        if (cs?.Wage == null || Math.Abs(cs.Wage.Value - target.Wage) > tol)
        {
            foreach (var w in SetWage(target.Wage, r)) yield return w;
            if (!r.Ok) { res.Error = "I couldn't change the salary: " + r.Error; yield break; }
            actual.Wage = r.Value;
            if (r.Value < target.Wage - tol)
            {
                planner.SetClubMax(r.Value);
                _clubMax = r.Value;
                AgentLog.Info($"The salary stops at {M(r.Value)} — that's {_club}'s maximum.");
            }
        }

        foreach (var clause in target.Clauses.ToList())
        {
            cs = Read();
            if (cs == null) { res.Error = "The contract screen closed."; yield break; }
            if (cs.Clauses.Any(c => ClauseNames.Same(c, clause))) continue;
            foreach (var w in AddClause(clause, r)) yield return w;
            if (!r.Ok)
            {
                planner.MarkClauseUnavailable(clause);
                actual.Clauses.RemoveAll(c => ClauseNames.Same(c, clause));
                AgentLog.Info($"{clause}: {r.Error} — skipping it.");
            }
        }

        cs = Read();
        foreach (var clause in cs?.Clauses.ToList() ?? new List<string>())
        {
            if (target.Has(clause)) continue;
            foreach (var w in RemoveClause(clause, r)) yield return w;
            if (!r.Ok) { res.Error = $"I couldn't remove {clause}: {r.Error}"; yield break; }
        }

        cs = Read();
        if (cs != null) actual.Clauses = cs.Clauses.ToList();
        planner.Proposed(actual);
        res.Ok = true;
    }

    // --- wage

    private List<UiNode> MoneyItems(ScreenModel s, ScreenModel before, ContractScreen cs)
    {
        var display = cs?.SalaryValue;
        return s.NewTexts(before).Where(n => Money.TryParse(n.Text, out _) && n.Text.Any(char.IsDigit) &&
                                             (display == null || !s.IsInside(n, display)) && (cs?.SalaryField == null || !s.IsInside(n, cs.SalaryField)))
            .ToList();
    }

    private IEnumerable<Wait> SetWage(double value, Result r)
    {
        r.Ok = false;
        double tol = Math.Max(1, value * 0.002);
        var digits = Money.Digits(value);
        var cs = Read();
        double? start = cs?.Wage;
        bool Done(ScreenModel s)
        {
            var c = Read(s);
            return c != null && c.SalaryField == null && c.Wage.HasValue && Math.Abs(c.Wage.Value - value) <= tol;
        }

        // A. Click the salary to type into it (or to open its list).
        var before = Game.Screen;
        if (cs?.SalaryField == null)
        {
            var open = new Result();
            foreach (var w in PressUntil(s => Read(s)?.SalaryValue, s => { var c = Read(s); return c?.SalaryField != null || MoneyItems(s, before, c).Count >= 2; }, "the salary", open, 1.5))
                yield return w;
        }
        cs = Read();
        if (cs?.SalaryField != null)
        {
            if (Game.SetField(cs.SalaryField, digits, out _))
            {
                var w = Wait.Until(Game, s => Done(s) || Read(s)?.SalaryField == null, 1.5);
                yield return w;
            }
            cs = Read();
            if (cs?.SalaryField != null && !Done(Game.Screen))
            {
                if (Game.Type(digits, true, true))
                    yield return Wait.Until(Game, s => Done(s) || Read(s)?.SalaryField == null, 2.5);
                cs = Read();
                if (cs?.SalaryField != null) { Game.Key(GameKey.Enter); yield return Wait.Fresh(Game, 0.3); }
            }
        }
        else
        {
            var items = MoneyItems(Game.Screen, before, cs);
            if (items.Count >= 2)
            {
                var keys = ScreenModel.Keys(items);
                var pick = items.OrderBy(i => Math.Abs((Money.Parse(i.Text) ?? 0) - value)).First();
                var pickKey = ScreenModel.KeyOf(pick);
                var pr = new Result();
                foreach (var w in PressUntil(s => s.Texts.Where(n => ScreenModel.KeyOf(n) == pickKey).Select(s.ClickableOf).FirstOrDefault(c => c != null),
                             s => !s.AnyOf(keys), pick.Text, pr, 1.5))
                    yield return w;
            }
        }

        yield return Fresh();
        cs = Read();
        if (Done(Game.Screen)) { r.Ok = true; r.Value = value; yield break; }
        if (cs?.Wage != null && start.HasValue && cs.Wage.Value < value - tol && cs.Wage.Value > start.Value + tol && cs.SalaryField == null)
        {
            r.Ok = true; // capped by the game: this is the club's maximum
            r.Value = cs.Wage.Value;
            yield break;
        }

        // B. The - / + buttons, one press at a time.
        foreach (var w in StepWage(value, r)) yield return w;
    }

    private IEnumerable<Wait> StepWage(double value, Result r)
    {
        r.Ok = false;
        int method = 0;
        double step = 0;
        for (int presses = 0; presses < 150; presses++)
        {
            yield return Fresh();
            var cs = Read();
            if (cs?.Wage == null) { r.Error = "can't read the salary"; yield break; }
            double cur = cs.Wage.Value;
            if (Math.Abs(cur - value) <= Math.Max(1, value * 0.002) || (step > 0 && Math.Abs(cur - value) < step / 2))
            {
                r.Ok = true; r.Value = cur; yield break;
            }
            bool up = cur < value;
            var btn = up ? cs.Plus : cs.Minus;
            if (btn == null) { r.Error = "no " + (up ? "+" : "-") + " button"; yield break; }
            if (step > 0 && Math.Abs(cur - value) < step && !up) { r.Ok = true; r.Value = cur; yield break; }

            if (!Game.Press(btn, method, out var how))
            {
                if (++method >= Game.PressMethods) { r.Error = "the + / - buttons didn't respond"; yield break; }
                continue;
            }
            var w = Wait.Until(Game, s => Read(s)?.Wage is double nv && Math.Abs(nv - cur) > 0.01, 1.2);
            yield return w;
            if (w.TimedOut)
            {
                if (up && presses > 0)
                {
                    r.Ok = true; r.Value = cur; // + stopped working: the club's maximum
                    yield break;
                }
                if (++method >= Game.PressMethods) { r.Error = "the + / - buttons didn't respond"; yield break; }
                continue;
            }
            var now = Read()?.Wage ?? cur;
            step = Math.Max(step, Math.Abs(now - cur));
            if (up && now > value) { r.Ok = true; r.Value = now; yield break; } // can't hit it exactly; first step above
        }
        r.Error = "too many presses";
    }

    private IEnumerable<Wait> ProbeMax(Result r)
    {
        r.Ok = false;
        r.Error = "nothing shown";
        var cs = Read();
        if (cs?.SalaryValue == null) { r.Error = "salary not found"; yield break; }
        double original = cs.Wage ?? 0;
        var before = Game.Screen;

        var open = new Result();
        foreach (var w in PressUntil(s => Read(s)?.SalaryValue, s => { var c = Read(s); return c?.SalaryField != null || MoneyItems(s, before, c).Count >= 2; }, "the salary", open, 1.5))
            yield return w;
        if (!open.Ok) { r.Error = "the salary control didn't open"; yield break; }

        cs = Read();
        var items = MoneyItems(Game.Screen, before, cs);
        if (items.Count >= 2)
        {
            // A list of salaries: its top entry is the most they can pay.
            r.Value = items.Max(i => Money.Parse(i.Text) ?? 0);
            r.Ok = r.Value > 0;
            var keys = ScreenModel.Keys(items);
            foreach (var w in ClosePopup(s => s.AnyOf(keys), s => Read(s)?.SalaryValue)) yield return w;
            yield break;
        }

        if (cs?.SalaryField != null)
        {
            // Type something huge and see where the game caps it.
            double huge = Math.Max(9_999_999, Data.Contract.WageTarget * 20);
            var digits = Money.Digits(huge);
            bool set = Game.SetField(cs.SalaryField, digits, out _);
            if (set) yield return Wait.Until(Game, s => Read(s)?.SalaryField == null, 1.5);
            if (Read()?.SalaryField != null && Game.Type(digits, true, true))
                yield return Wait.Until(Game, s => Read(s)?.SalaryField == null, 2.5);
            if (Read()?.SalaryField != null) { Game.Key(GameKey.Escape); yield return Wait.Fresh(Game, 0.3); }
            yield return Fresh();
            var shown = Read()?.Wage;
            if (shown.HasValue && shown.Value < huge * 0.5 && Math.Abs(shown.Value - original) > 0.5)
            {
                r.Ok = true;
                r.Value = shown.Value;
            }
            else r.Error = "the salary field isn't capped";

            // Put their offer back while you decide.
            if (shown.HasValue && Math.Abs(shown.Value - original) > 0.5)
            {
                var back = new Result();
                foreach (var w in SetWage(original, back)) yield return w;
            }
        }
    }

    private IEnumerable<Wait> ClosePopup(Func<ScreenModel, bool> isOpen, Func<ScreenModel, UiNode> opener)
    {
        if (!isOpen(Game.Screen)) yield break;
        if (Game.Key(GameKey.Escape))
        {
            var w = Wait.Until(Game, s => !isOpen(s), 1.0);
            yield return w;
            if (!w.TimedOut) yield break;
        }
        var o = opener(Game.Screen);
        if (o != null && Game.Press(o, 0, out _))
            yield return Wait.Until(Game, s => !isOpen(s), 1.0);
    }

    // --- length

    private IEnumerable<Wait> SetYears(double years, Result r)
    {
        r.Ok = false;
        var before = Game.Screen;
        List<UiNode> Items(ScreenModel s)
        {
            var display = Read(s)?.LengthValue;
            return s.NewTexts(before).Where(n => YearsText.Parse(n.Text) != null && (display == null || !s.IsInside(n, display))).ToList();
        }

        var open = new Result();
        foreach (var w in PressUntil(s => Read(s)?.LengthValue, s => Items(s).Count > 0, "Contract Length", open, 1.5))
            yield return w;
        if (!open.Ok) { r.Error = open.Error; yield break; }

        var options = Items(Game.Screen);
        var keys = ScreenModel.Keys(options);
        var pick = options.OrderBy(i => Math.Abs(YearsText.Parse(i.Text).Value - years)).ThenByDescending(i => YearsText.Parse(i.Text).Value).First();
        double pickYears = YearsText.Parse(pick.Text).Value;
        var pickKey = ScreenModel.KeyOf(pick);
        foreach (var w in PressUntil(s => s.Texts.Where(n => ScreenModel.KeyOf(n) == pickKey).Select(s.ClickableOf).FirstOrDefault(c => c != null),
                     s => { var c = Read(s); return c?.Years != null && Math.Abs(c.Years.Value - pickYears) < 0.01 && !s.AnyOf(keys); },
                     pick.Text, r, 2.0))
            yield return w;
        r.Value = pickYears;
        if (!r.Ok) foreach (var w in ClosePopup(s => s.AnyOf(keys), s => Read(s)?.LengthValue)) yield return w;
    }

    // --- clauses

    private IEnumerable<Wait> AddClause(string name, Result r)
    {
        r.Ok = false;
        var before = Game.Screen;
        List<UiNode> Items(ScreenModel s) => s.NewTexts(before).Where(n => n.Text.Length >= 6 && !Money.TryParse(n.Text, out _) && s.ClickableOf(n) != null).ToList();

        var open = new Result();
        foreach (var w in PressUntil(s => Read(s)?.AddClause, s => Items(s).Count > 0, "Add Clause", open, 1.5))
            yield return w;
        if (!open.Ok) { r.Error = "the Add Clause list didn't open"; yield break; }

        var items = Items(Game.Screen);
        var keys = ScreenModel.Keys(items);
        Data.LearnClauses(items.Select(i => i.Text));
        var item = items.FirstOrDefault(i => ClauseNames.Same(i.Text, name));
        var click = item == null ? null : Game.Screen.ClickableOf(item);
        if (item == null || click == null || !click.Enabled || !item.Enabled)
        {
            r.Error = item == null ? "not offered by this club" : "not available on these terms";
            foreach (var w in ClosePopup(s => s.AnyOf(keys), s => Read(s)?.AddClause)) yield return w;
            yield break;
        }
        var itemKey = ScreenModel.KeyOf(item);
        foreach (var w in PressUntil(s => s.Texts.Where(n => ScreenModel.KeyOf(n) == itemKey).Select(s.ClickableOf).FirstOrDefault(c => c != null),
                     s => !s.AnyOf(keys) && Read(s)?.Clauses.Any(c => ClauseNames.Same(c, name)) == true,
                     name, r, 2.0))
            yield return w;
        if (!r.Ok)
        {
            r.Error = "couldn't add it";
            foreach (var w in ClosePopup(s => s.AnyOf(keys), s => Read(s)?.AddClause)) yield return w;
        }
    }

    private IEnumerable<Wait> RemoveClause(string name, Result r)
    {
        r.Ok = false;
        var s0 = Game.Screen;
        var cs = Read(s0);
        var text = s0.Texts.FirstOrDefault(n => ClauseNames.Same(n.Text, name) && (cs?.ClausesHeader == null || n.Y > cs.ClausesHeader.Bottom));
        if (text == null) { r.Ok = true; yield break; }
        UiNode Remover(ScreenModel s)
        {
            var row = s.Texts.FirstOrDefault(n => ClauseNames.Same(n.Text, name) && Math.Abs(n.CY - text.CY) < 30);
            if (row == null) return null;
            var inRow = s.ClickablesInBand(row.CY - 20, row.CY + 20, row.Right).OrderByDescending(n => n.X).ToList();
            return inRow.FirstOrDefault(n => new[] { "×", "x", "X", "✕", "✖", "Remove", "-", "−" }.Contains(Ui.Clean(s.TextOf(n))))
                   ?? inRow.FirstOrDefault(n => n.W < 50);
        }
        foreach (var w in PressUntil(Remover, s => Read(s)?.Clauses.Any(c => ClauseNames.Same(c, name)) != true, "the remove button for " + name, r, 1.5))
            yield return w;
    }
}
