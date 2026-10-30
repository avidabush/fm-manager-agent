using BepInEx.Configuration;

namespace ManagerAgent;

/// <summary>All BepInEx config entries (BepInEx\config\com.manageragent.fm.cfg). Your wage range,
/// clauses and job filters live in ManagerAgent\agent.json and are set from the agent panel.</summary>
internal sealed class AgentSettings
{
    public readonly ConfigEntry<string> KeyTogglePanel;
    public readonly ConfigEntry<string> KeyHandOver;
    public readonly ConfigEntry<string> KeyStop;
    public readonly ConfigEntry<string> KeyDumpUi;

    public readonly ConfigEntry<string> ClickStrategies;
    public readonly ConfigEntry<bool> RestoreCursorAfterClick;

    public readonly ConfigEntry<bool> PanelStartsVisible;
    public readonly ConfigEntry<bool> PanelStartsCollapsed;
    public readonly ConfigEntry<float> PanelOffsetRight;
    public readonly ConfigEntry<float> PanelOffsetTop;
    public readonly ConfigEntry<float> PanelWidth;
    public readonly ConfigEntry<float> PanelFontSize;
    public readonly ConfigEntry<float> TabX;
    public readonly ConfigEntry<float> TabY;
    public readonly ConfigEntry<bool> LockTabPosition;

    public readonly ConfigEntry<bool> VerboseLog;

    private readonly ConfigFile _cfg;

    public AgentSettings(ConfigFile cfg)
    {
        _cfg = cfg;
        KeyTogglePanel = cfg.Bind("Hotkeys", "TogglePanel", "Ctrl+Shift+A", "Show/hide the Agent tab and panel.");
        KeyHandOver = cfg.Bind("Hotkeys", "NegotiateNow", "Ctrl+Shift+H", "Let the agent negotiate the contract offer on screen.");
        KeyStop = cfg.Bind("Hotkeys", "Stop", "Ctrl+Shift+X", "Take back control immediately.");
        KeyDumpUi = cfg.Bind("Hotkeys", "DumpUi", "Ctrl+Shift+U", "Write the current UI tree to a text file (for troubleshooting).");

        ClickStrategies = cfg.Bind("Autopilot", "ClickStrategies", "submit,invoke,mouse",
            "How the agent presses buttons, tried in order until the game reacts. " +
            "submit = the game's own 'confirm' input (like Enter); invoke = FM's click handler; " +
            "mouse = really move the cursor and click (game window must be focused).");
        RestoreCursorAfterClick = cfg.Bind("Autopilot", "RestoreCursorAfterClick", true, "With the mouse method, put your cursor back where it was after each click.");

        PanelStartsVisible = cfg.Bind("Panel", "StartsVisible", true, "Show the Agent tab when the game starts.");
        PanelStartsCollapsed = cfg.Bind("Panel", "StartsCollapsed", true, "Start with only the Agent tab showing (the panel opens by itself when it needs you).");
        PanelOffsetRight = cfg.Bind("Panel", "OffsetRight", 24f, "Default Agent tab position: distance from the right edge (UI units). Used until you drag the tab.");
        PanelOffsetTop = cfg.Bind("Panel", "OffsetTop", 120f, "Default Agent tab position: distance from the top (UI units).");
        PanelWidth = cfg.Bind("Panel", "Width", 420f, "Panel width (UI units).");
        PanelFontSize = cfg.Bind("Panel", "FontSize", 14f, "Base font size of the panel.");
        TabX = cfg.Bind("Panel", "TabX", -1f, "Where you dragged the Agent tab (fraction of screen width). -1 = default corner.");
        TabY = cfg.Bind("Panel", "TabY", -1f, "Where you dragged the Agent tab (fraction of screen height). -1 = default corner.");
        LockTabPosition = cfg.Bind("Panel", "LockTabPosition", false, "Stop the Agent tab from being dragged by accident.");

        VerboseLog = cfg.Bind("Debug", "VerboseLog", false, "Extra detail in BepInEx\\LogOutput.log.");
    }

    /// <summary>The on-screen words the agent looks for; editable under [Game text] for non-English games.</summary>
    public GameText BuildGameText()
    {
        var t = new GameText();
        string B(string key, string def, string what) =>
            _cfg.Bind("Game text", key, def, what + " (\"|\" separates alternatives; change only if your game isn't in English).").Value;
        t.AvailableJobs = B("AvailableJobs", t.AvailableJobs, "Jobs page title, after the number");
        t.ApplyForJob = B("ApplyForJob", t.ApplyForJob, "Apply button on the jobs page");
        t.ApplyForAllJobs = B("ApplyForAllJobs", t.ApplyForAllJobs, "Apply-to-everything button");
        t.ConfirmTitle = B("ConfirmTitle", t.ConfirmTitle, "Title of confirmation boxes");
        t.Yes = B("Yes", t.Yes, "Yes button in confirmation boxes");
        t.Vacancy = B("Vacancy", t.Vacancy, "Job Security value meaning the job is open");
        t.ColCoach = B("ColumnCoach", t.ColCoach, "Jobs table column");
        t.ColTeam = B("ColumnTeam", t.ColTeam, "Jobs table column");
        t.ColRating = B("ColumnRating", t.ColRating, "Jobs table column");
        t.ColNation = B("ColumnNation", t.ColNation, "Jobs table column");
        t.ColPosition = B("ColumnPosition", t.ColPosition, "Jobs table column");
        t.ColSecurity = B("ColumnSecurity", t.ColSecurity, "Jobs table column");
        t.ColFinances = B("ColumnFinances", t.ColFinances, "Jobs table column");
        t.Ratings = B("ClubRatings", t.Ratings, "Club ratings from lowest to highest");
        t.CareerMenu = B("CareerMenu", t.CareerMenu, "Top menu that contains the jobs page");
        t.JobsMenuItems = B("JobsMenuItems", t.JobsMenuItems, "Menu entry that opens the jobs page");
        t.EnterNegotiations = B("EnterNegotiations", t.EnterNegotiations, "Button on a job offer message");
        t.RejectOffer = B("RejectOffer", t.RejectOffer, "Button on a job offer message");
        t.CurrentOffer = B("CurrentOffer", t.CurrentOffer, "Contract screen title");
        t.JobRow = B("JobRow", t.JobRow, "First row label of a head coach contract");
        t.Salary = B("Salary", t.Salary, "Contract row label");
        t.ContractLength = B("ContractLength", t.ContractLength, "Contract row label");
        t.AddClause = B("AddClause", t.AddClause, "Contract button");
        t.ClausesHeader = B("ClausesHeader", t.ClausesHeader, "Contract section title");
        t.SuggestTerms = B("SuggestTerms", t.SuggestTerms, "Contract button");
        t.FinalizeDeal = B("FinalizeDeal", t.FinalizeDeal, "Contract button");
        t.AboutClub = B("AboutClub", t.AboutClub, "Contract screen club card title");
        t.ResetButton = B("Reset", t.ResetButton, "Contract screen reset button");
        return t;
    }
}
