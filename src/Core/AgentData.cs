using System.IO;

namespace ManagerAgent;

public sealed class ClausePref
{
    public string Name { get; set; } = "";
    /// <summary>"want" = ask for it, "skip" = don't care, "never" = remove it if the club includes it.</summary>
    public string Want { get; set; } = "skip";
}

public sealed class AppliedJob
{
    public string Team { get; set; } = "";
    public string League { get; set; } = "";
    public string Date { get; set; } = "";
}

public sealed class JobPrefs
{
    public bool Enabled { get; set; }
    /// <summary>"all" = press the game's "Apply for all jobs"; "filtered" = only jobs matching the filters, one by one.</summary>
    public string ApplyMode { get; set; } = "all";
    public bool AutoVisit { get; set; } = true;
    public int CheckEveryDays { get; set; } = 7;
    public bool ReturnAfterCheck { get; set; } = true;
    public string MinRating { get; set; } = "Local";
    public bool IncludeNationalTeams { get; set; }
    public List<string> Nations { get; set; } = new();
    public List<string> ExcludeTeams { get; set; } = new();
    public List<string> KnownNations { get; set; } = new();
    public string LastCheckDate { get; set; } = "";
    /// <summary>Learned from you: the menu you click to reach the jobs page.</summary>
    public string MenuParentText { get; set; } = "";
    public string MenuItemText { get; set; } = "";
    public List<AppliedJob> Applied { get; set; } = new();
}

public sealed class ContractPrefs
{
    public double WageMin { get; set; }
    public double WageTarget { get; set; }
    public string WagePrefix { get; set; } = "$";
    public string WageUnit { get; set; } = "p/w";
    public double YearsWant { get; set; } = 3;
    public double YearsMin { get; set; } = 1;
    public List<ClausePref> Clauses { get; set; } = new();
    public bool AutoNegotiate { get; set; }
    public bool AutoSign { get; set; }
    public int MaxSuggestions { get; set; } = 8;
    public double ClauseTradeMaxPct { get; set; } = 5;
}

public sealed class AgentDataFile
{
    public int Version { get; set; } = 1;
    public JobPrefs Jobs { get; set; } = new();
    public ContractPrefs Contract { get; set; } = new();
}

/// <summary>Your agent preferences, saved to BepInEx\config\ManagerAgent\agent.json.</summary>
internal sealed class AgentData
{
    public static readonly string[] DefaultClauses =
    {
        "Yearly Salary Raise",
        "Promotion Salary Raise",
        "Relegation Salary Drop",
        "Contract Extension After Promotion",
        "Percentage Of Club Compensation Required For Coaching Role",
    };

    private readonly string _path;
    public AgentDataFile File { get; private set; } = new();
    public int Revision { get; private set; }

    public AgentData(string path) { _path = path; }

    public JobPrefs Jobs => File.Jobs;
    public ContractPrefs Contract => File.Contract;

    public void Load()
    {
        try { File = Json.Load<AgentDataFile>(_path); }
        catch (Exception ex)
        {
            AgentLog.Warn($"agent.json couldn't be read ({ex.Message}); using defaults.");
            File = new AgentDataFile();
        }
        File.Jobs ??= new JobPrefs();
        File.Contract ??= new ContractPrefs();
        File.Contract.Clauses ??= new List<ClausePref>();
        foreach (var name in DefaultClauses)
            if (!File.Contract.Clauses.Any(c => ClauseNames.Same(c.Name, name)))
                File.Contract.Clauses.Add(new ClausePref { Name = name, Want = DefaultWant(name) });
        Revision++;
    }

    private static string DefaultWant(string name)
    {
        var b = TextMatch.Basic(name);
        if (b.Contains("drop") || b.Contains("relegation salary")) return "never";
        if (b.Contains("raise") || b.Contains("extension")) return "want";
        return "skip";
    }

    public void Save()
    {
        Revision++;
        Guard.Run("save-agent", () => Json.Save(_path, File));
    }

    /// <summary>Adds clause names seen in the game's list so you can set a preference for them.</summary>
    public void LearnClauses(IEnumerable<string> names)
    {
        bool changed = false;
        foreach (var n in names)
        {
            var name = n?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length < 4) continue;
            if (Contract.Clauses.Any(c => ClauseNames.Same(c.Name, name))) continue;
            Contract.Clauses.Add(new ClausePref { Name = name, Want = DefaultWant(name) });
            changed = true;
        }
        if (changed) Save();
    }

    public NegotiationPrefs ToPrefs() => new()
    {
        WageMin = Contract.WageMin,
        WageTarget = Math.Max(Contract.WageTarget, Contract.WageMin),
        YearsWant = Math.Max(Contract.YearsWant, Contract.YearsMin),
        YearsMin = Contract.YearsMin,
        WantClauses = Contract.Clauses.Where(c => c.Want == "want").Select(c => c.Name).ToList(),
        NeverClauses = Contract.Clauses.Where(c => c.Want == "never").Select(c => c.Name).ToList(),
        MaxSuggestions = Math.Max(2, Contract.MaxSuggestions),
        ClauseTradeMaxPct = Contract.ClauseTradeMaxPct,
    };

    public JobFilter ToFilter() => new()
    {
        MinRating = Enum.TryParse<ClubRating>(Jobs.MinRating, true, out var r) && r != ClubRating.Unknown ? r : ClubRating.Local,
        IncludeNationalTeams = Jobs.IncludeNationalTeams,
        Nations = Jobs.Nations.ToList(),
        ExcludeTeams = Jobs.ExcludeTeams.ToList(),
    };

    public IEnumerable<string> KnownClauseNames => Contract.Clauses.Select(c => c.Name);

    public static string DataFile => Path.Combine(AgentPaths.DataDir, "agent.json");
}
