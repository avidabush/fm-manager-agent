namespace ManagerAgent;

/// <summary>The manager's agent: owns the duties and the panel, and runs once per frame.</summary>
internal sealed class Agent : IAgentHost
{
    public static Agent Current { get; private set; }

    public readonly AgentSettings Settings;
    public readonly Clicker Clicker;
    public readonly InputState Input = new();
    public readonly AgentPanel Panel;
    public readonly UnityGame UGame;
    public readonly JobHunter Jobs;
    public readonly Negotiator Contract;
    public readonly List<AgentDuty> Duties = new();

    public IGame Game => UGame;
    public GameText Text { get; }
    public AgentData Data { get; }

    private readonly KeyCombo _kToggle, _kStart, _kStop, _kDump;

    public HashSet<long> OwnElementIds => Panel.OwnIds;

    public Agent(AgentSettings settings)
    {
        Current = this;
        Settings = settings;
        Text = settings.BuildGameText();
        Data = new AgentData(AgentData.DataFile);
        Data.Load();
        Clicker = new Clicker(settings);
        UGame = new UnityGame(this);
        Panel = new AgentPanel(this);

        Jobs = new JobHunter(this);
        Contract = new Negotiator(this);
        Duties.Add(Jobs);
        Duties.Add(Contract);

        _kToggle = Track(settings.KeyTogglePanel.Value);
        _kStart = Track(settings.KeyHandOver.Value);
        _kStop = Track(settings.KeyStop.Value);
        _kDump = Track(settings.KeyDumpUi.Value);
    }

    private KeyCombo Track(string text)
    {
        var k = KeyCombo.Parse(text);
        if (!k.IsValid) AgentLog.Warn($"Hotkey \"{text}\" not understood; it won't work.", panel: false);
        Input.Track(k);
        return k;
    }

    public void Tick()
    {
        Guard.Run("input", Input.Update);
        Guard.Run("clicker", Clicker.Tick);
        Guard.Run("game", UGame.Tick);
        Guard.Run("hotkeys", HandleHotkeys);
        Guard.Run("mouse", HandleMouse);
        foreach (var d in Duties) Guard.Run("duty:" + d.Id, d.Tick);
        Guard.Run("panel", Panel.Tick);
    }

    private void HandleHotkeys()
    {
        if (!Input.Focused) return;
        if (Input.Pressed(_kToggle)) Panel.Toggle();
        if (Input.Pressed(_kStop)) StopAll("you took over");
        if (Input.Pressed(_kStart) && Contract.CanStart) Contract.Start();
        if (Input.Pressed(_kDump)) DumpUi();
    }

    public void StopAll(string reason)
    {
        foreach (var d in Duties) d.Stop(reason);
        UGame.Cancel();
    }

    private void HandleMouse()
    {
        if (!Input.LeftClicked || Clicker.SyntheticMouseActive) return;
        if (Panel.HandleClick()) return;
        var s = UGame.Screen;
        var roots = Ui.Roots;
        if (s == null || roots.Count == 0 || !Coords.MouseToPanel(roots[^1].Root, out var px, out var py)) return;
        var node = s.At(px, py, n => n.Text != null || n.Clickable);
        foreach (var d in Duties) Guard.Run("click:" + d.Id, () => d.OnUserClick(node));
    }

    // IAgentHost
    public void Attention() => Panel.Attention();
    public void ShowTerms() => Panel.ShowTerms();
    public bool OtherDutyBusy(AgentDuty me) => Duties.Any(d => d != me && d.IsRunning);

    public void DumpUi()
    {
        try
        {
            var path = UiDump.Write(OwnElementIds);
            AgentLog.Info($"UI dump written: {System.IO.Path.GetFileName(path)} (in BepInEx\\config\\ManagerAgent\\dumps)");
        }
        catch (Exception ex)
        {
            AgentLog.Warn("UI dump failed: " + ex.Message);
        }
    }
}
