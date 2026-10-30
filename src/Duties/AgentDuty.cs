namespace ManagerAgent;

/// <summary>
/// One job the agent does for you (job hunting, contract negotiation…). A duty watches the screen,
/// runs a step-by-step script when it's working, and draws its own card in the agent panel.
/// </summary>
internal abstract class AgentDuty
{
    protected readonly ScriptRunner Runner = new();
    protected readonly IAgentHost Host;

    protected AgentDuty(IAgentHost host) { Host = host; }

    public abstract string Id { get; }
    public abstract string Title { get; }

    public bool IsRunning => Runner.Active && Runner.Choice == null;
    public bool IsWaitingForYou => Runner.Choice != null;
    public UserChoice Choice => Runner.Choice;

    /// <summary>Short status for the agent tab, or null when there's nothing to show.</summary>
    public abstract string TabStatus { get; }
    /// <summary>Changes whenever the panel card needs redrawing.</summary>
    public abstract string StateKey { get; }

    protected IGame Game => Host.Game;
    protected GameText Text => Host.Text;
    protected AgentData Data => Host.Data;

    public void Tick()
    {
        Watch();
        if (Runner.Active) Game.WantFast(true);
        var crash = Runner.Tick(Game);
        if (crash != null)
        {
            AgentLog.Error($"[{Id}] {crash}");
            AgentLog.Warn($"{Title}: something went wrong ({crash.Message}). I've stopped.");
            OnStopped();
        }
    }

    /// <summary>Called every frame (cheap checks; reads <see cref="IGame.Screen"/>).</summary>
    protected abstract void Watch();

    protected virtual void OnStopped() { }

    public virtual void Stop(string reason)
    {
        if (!Runner.Active) return;
        Runner.Stop();
        AgentLog.Info($"{Title}: stopped — {reason}.");
        OnStopped();
    }

    public void Choose(string key)
    {
        var c = Runner.Choice;
        if (c != null) c.Picked = key;
    }

    /// <summary>You clicked in the game (not on the agent's panel).</summary>
    public virtual void OnUserClick(UiNode clicked) { }

    // ------------------------------------------------------------------ script helpers

    /// <summary>
    /// Presses a node, trying each press method until <paramref name="done"/> becomes true.
    /// <paramref name="find"/> re-locates the node on each attempt (screens change).
    /// </summary>
    protected IEnumerable<Wait> PressUntil(Func<ScreenModel, UiNode> find, Func<ScreenModel, bool> done, string what, Result r, double timeout = 2.5)
    {
        r.Ok = false;
        for (int attempt = 0; attempt < Game.PressMethods; attempt++)
        {
            var node = Game.Screen == null ? null : find(Game.Screen);
            if (node == null) { r.Error = $"couldn't find {what}"; yield break; }
            if (!Game.Press(node, attempt, out var how)) { r.Error = how; continue; }
            var w = Wait.Until(Game, done, timeout);
            yield return w;
            if (!w.TimedOut) { r.Ok = true; r.How = how; yield break; }
            r.Error = $"{what} didn't react";
        }
    }

    protected Wait Fresh() => Wait.Fresh(Game);

    internal sealed class Result
    {
        public bool Ok;
        public string Error = "";
        public string How = "";
        public double Value;
    }
}

/// <summary>What duties need from the agent (lets tests run duties against a simulated game).</summary>
internal interface IAgentHost
{
    IGame Game { get; }
    GameText Text { get; }
    AgentData Data { get; }
    void Attention();
    /// <summary>Opens "Your terms" in the panel.</summary>
    void ShowTerms();
    /// <summary>Saves a UI dump for troubleshooting (once something looks wrong).</summary>
    void DumpUi(string reason);
    /// <summary>True while another duty is in the middle of something.</summary>
    bool OtherDutyBusy(AgentDuty me);
}
