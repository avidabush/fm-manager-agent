using System.Diagnostics;
using System.IO;
using BepInEx.Logging;

namespace ManagerAgent;

/// <summary>Monotonic clock in seconds; independent of Unity's Time class.</summary>
internal static class Clock
{
    private static readonly Stopwatch Sw = Stopwatch.StartNew();
    public static double Now => Sw.Elapsed.TotalSeconds;
}

internal static class AgentPaths
{
    public static string DataDir
    {
        get
        {
            var dir = Path.Combine(BepInEx.Paths.ConfigPath, "ManagerAgent");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string AnswersFile => Path.Combine(DataDir, "answers.json");
    public static string ScreensFile => Path.Combine(DataDir, "screens.json");

    public static string DumpDir
    {
        get
        {
            var dir = Path.Combine(DataDir, "dumps");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}

/// <summary>
/// Logging to the BepInEx log plus a short ring buffer shown in the agent panel.
/// </summary>
internal static class AgentLog
{
    public static ManualLogSource Source;
    public static readonly List<string> Recent = new();
    public static int Version { get; private set; }

    public static void Info(string msg, bool panel = true)
    {
        Source?.LogInfo(msg);
        if (panel) Push(msg);
    }

    public static void Warn(string msg, bool panel = true)
    {
        Source?.LogWarning(msg);
        if (panel) Push("⚠ " + msg);
    }

    public static void Error(string msg) => Source?.LogError(msg);

    public static void Debug(string msg)
    {
        if (Agent.Current?.Settings.VerboseLog.Value == true) Source?.LogInfo("[debug] " + msg);
    }

    private static void Push(string msg)
    {
        Recent.Add($"{DateTime.Now:HH:mm} {msg}");
        while (Recent.Count > 30) Recent.RemoveAt(0);
        Version++;
    }
}

/// <summary>
/// Runs a subsystem step and swallows exceptions, logging each distinct failure at most
/// once every 30 seconds so a broken frame can't flood the log or kill the agent.
/// </summary>
internal static class Guard
{
    private static readonly Dictionary<string, double> LastLogged = new();

    public static void Run(string area, Action action)
    {
        try { action(); }
        catch (Exception ex) { Report(area, ex); }
    }

    public static T Run<T>(string area, Func<T> func, T fallback)
    {
        try { return func(); }
        catch (Exception ex) { Report(area, ex); return fallback; }
    }

    public static void Report(string area, Exception ex)
    {
        var key = area + ":" + ex.GetType().Name + ":" + ex.Message;
        var now = Clock.Now;
        if (LastLogged.TryGetValue(key, out var t) && now - t < 30) return;
        LastLogged[key] = now;
        AgentLog.Error($"[{area}] {ex}");
    }
}

internal static class Json
{
    public static T Load<T>(string path) where T : class, new()
    {
        if (!File.Exists(path)) return new T();
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return new T();
        return MiniJson.Deserialize<T>(text);
    }

    /// <summary>Writes atomically (temp file + replace) and keeps one .bak copy.</summary>
    public static void Save<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, MiniJson.Serialize(value), new System.Text.UTF8Encoding(false));
        if (File.Exists(path))
        {
            File.Copy(path, path + ".bak", true);
            File.Delete(path);
        }
        File.Move(tmp, path);
    }
}
