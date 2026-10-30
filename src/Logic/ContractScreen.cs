namespace ManagerAgent;

/// <summary>Reads the manager contract screen ("Current Offer": Salary, Contract Length, Bonuses &amp; Clauses).</summary>
internal sealed class ContractScreen
{
    public UiNode SalaryLabel, SalaryValue, SalaryField, Minus, Plus;
    public UiNode LengthLabel, LengthValue;
    public UiNode AddClause, ClausesHeader, Suggest, Finalize, ResetButton;
    public double? Wage;
    public string WagePrefix = "$", WageUnit = "p/w";
    public double? Years;
    public readonly List<string> Clauses = new();
    public string Club = "";
    public string LastChat = "";

    public bool CanSuggest => Suggest != null;
    public bool CanFinalize => Finalize != null;

    public Terms Terms => new() { Wage = Wage ?? 0, Years = Years ?? 0, Clauses = Clauses.ToList() };

    /// <summary>A manager (head coach) contract screen — never a player's contract.</summary>
    public static bool IsShowing(ScreenModel s, GameText t) =>
        s.Text(t.CurrentOffer) != null && s.Text(t.ContractLength) != null && s.Text(t.Salary) != null &&
        s.Text(t.JobRow) != null && (s.Text(t.SuggestTerms) != null || s.Text(t.FinalizeDeal) != null);

    /// <summary>The job-offer message with "Enter Contract Negotiations".</summary>
    public static UiNode OfferMessageButton(ScreenModel s, GameText t) =>
        s.Text(t.RejectOffer) != null ? s.Button(t.EnterNegotiations) : null;

    public static ContractScreen Read(ScreenModel s, GameText t, IEnumerable<string> knownClauses)
    {
        if (!IsShowing(s, t)) return null;
        var c = new ContractScreen();

        c.SalaryLabel = s.Text(t.Salary);
        c.LengthLabel = s.Text(t.ContractLength);
        float left = c.SalaryLabel.Right + 60;

        // --- salary row
        var (y0, y1) = Band(c.SalaryLabel);
        c.SalaryField = s.Nodes.Where(n => n.IsField && n.CY >= y0 && n.CY <= y1 && n.CX > left).OrderBy(n => n.X).FirstOrDefault();
        foreach (var n in s.TextsInBand(y0, y1, left).OrderBy(n => n.X))
        {
            var txt = Ui.Clean(n.Text);
            if (txt == "-" || txt == "−" || txt == "–") { c.Minus ??= s.ClickableOf(n) ?? n; continue; }
            if (txt == "+") { c.Plus ??= s.ClickableOf(n) ?? n; continue; }
            if (c.SalaryValue == null && Money.TryParse(txt, out var m) && txt.Any(char.IsDigit))
            {
                c.SalaryValue = s.ClickableOf(n) ?? (c.SalaryField != null && s.IsInside(n, c.SalaryField) ? c.SalaryField : n);
                c.Wage = m.Value;
                if (m.Prefix.Length > 0) c.WagePrefix = m.Prefix;
                if (m.Unit.Length > 0) c.WageUnit = m.Unit;
            }
        }
        if (c.SalaryField != null && Money.TryParse(c.SalaryField.FieldValue, out var fv)) c.Wage = fv.Value;
        if (c.SalaryValue == null && c.SalaryField != null) c.SalaryValue = c.SalaryField;

        // --- contract length row
        (y0, y1) = Band(c.LengthLabel);
        foreach (var n in s.TextsInBand(y0, y1, left).OrderBy(n => n.X))
        {
            var yrs = YearsText.Parse(n.Text);
            if (yrs == null) continue;
            c.LengthValue = s.ClickableOf(n) ?? n;
            c.Years = yrs;
            break;
        }

        // --- clauses section
        c.AddClause = s.Button(t.AddClause) ?? s.Texts.Where(n => Ui.Clean(n.Text).EndsWith(ScreenModel.Split(t.AddClause)[0], StringComparison.OrdinalIgnoreCase))
            .Select(s.ClickableOf).FirstOrDefault(n => n != null);
        c.ClausesHeader = s.Text(t.ClausesHeader);
        c.ResetButton = s.Button(t.ResetButton);
        c.Suggest = s.Button(t.SuggestTerms);
        c.Finalize = s.Button(t.FinalizeDeal);

        if (c.ClausesHeader != null)
        {
            float top = c.ClausesHeader.Bottom + 2;
            float bottom = c.ResetButton?.Y ?? s.Height;
            float right = (c.AddClause?.Right ?? c.SalaryLabel.X + 1100) + 40;
            float leftEdge = c.SalaryLabel.X - 40;
            var known = knownClauses?.ToList() ?? new List<string>();
            foreach (var n in s.Texts.Where(n => n.CY > top && n.CY < bottom && n.CX > leftEdge && n.CX < right).OrderBy(n => n.Y))
            {
                var name = known.FirstOrDefault(k => ClauseNames.Same(k, n.Text));
                if (name != null && !c.Clauses.Any(x => ClauseNames.Same(x, name))) c.Clauses.Add(name);
            }
        }

        // --- club name: first text after the "About Club" card title
        var about = s.Text(t.AboutClub);
        if (about != null)
            c.Club = s.Texts.Where(n => n.Order > about.Order && n.Text.Trim().Length > 1).OrderBy(n => n.Order).Select(n => n.Text.Trim()).FirstOrDefault() ?? "";
        if (c.Club.Length == 0)
        {
            // FM27: the club card left of the offer — a club badge with the club's name beside it.
            var offer = s.Text(t.CurrentOffer);
            var root = s.RootOf(offer);
            var badge = s.Nodes.Where(n => n.Name == "ClubButton" && n.W >= 40 && n.H >= 40 && n.Right < offer.X && (root == null || s.IsInside(n, root)))
                .OrderBy(n => n.Y).FirstOrDefault();
            if (badge != null)
                c.Club = s.Texts.Where(n => n.X > badge.Right && n.X < badge.Right + 60 && n.Y >= badge.Y - 8 && n.Y < badge.Y + badge.H / 2 && n.Text.Trim().Length > 1)
                    .OrderBy(n => n.Y).Select(n => n.Text.Trim()).FirstOrDefault() ?? "";
        }

        // --- latest chat line from the chairperson (right column, above the action buttons)
        var action = c.Suggest ?? c.Finalize;
        if (action != null)
        {
            var chat = s.Texts.Where(n => n.X >= action.X - 30 && n.Bottom < action.Y && n.Y > 120 && n.Text.Length > 8)
                .OrderByDescending(n => n.Y).FirstOrDefault();
            c.LastChat = chat?.Text ?? "";
        }
        return c;
    }

    private static (float, float) Band(UiNode label)
    {
        float half = Math.Max(label.H, 18f);
        return (label.CY - half, label.CY + half);
    }
}
