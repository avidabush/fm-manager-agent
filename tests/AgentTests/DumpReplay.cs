using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ManagerAgent;

/// <summary>
/// Loads a UI dump saved by the agent ("Dump UI") and runs the agent's own detection on it:
///   dotnet AgentTests.dll --replay ui-20261030-134639.txt
/// Lets a real game screen be checked without the game.
/// </summary>
internal static class DumpReplay
{
    private static readonly Regex LineRx = new(
        @"^(?<ind> *)(?<type>\S+)(?: #(?<name>\S+))?(?: \.(?<cls>\S+))?(?:  \[(?<x>-?[\d.]+),(?<y>-?[\d.]+) (?<w>[\d.]+)x(?<h>[\d.]+)\])?(?<click>  \(clickable\))?(?:  ""(?<text>.*)"")?\s*$");

    public static ScreenModel Load(string path)
    {
        var s = new ScreenModel();
        var stack = new List<(int depth, UiNode node, bool disabled)>();
        bool first = true;
        foreach (var raw in File.ReadLines(path))
        {
            if (raw.StartsWith("===")) { stack.Clear(); continue; }
            var m = LineRx.Match(raw);
            if (!m.Success || !m.Groups["x"].Success) continue;
            int depth = m.Groups["ind"].Value.Length / 2;
            while (stack.Count > 0 && stack[^1].depth >= depth) stack.RemoveAt(stack.Count - 1);
            var parent = stack.Count > 0 ? stack[^1] : default;
            var cls = m.Groups["cls"].Value;
            bool disabled = (parent.node != null && parent.disabled) || Regex.IsMatch(cls, @"(^|\.)(unity-disabled|disabled|is-disabled)(\.|$)");
            var n = new UiNode
            {
                Id = s.Nodes.Count,
                Order = s.Nodes.Count,
                Type = m.Groups["type"].Value,
                Name = m.Groups["name"].Value,
                Text = m.Groups["text"].Success ? m.Groups["text"].Value : null,
                X = F(m, "x"), Y = F(m, "y"), W = F(m, "w"), H = F(m, "h"),
                Clickable = m.Groups["click"].Success,
                Enabled = !disabled,
                Parent = parent.node?.Id ?? -1,
            };
            n.IsScroll = n.Type.Contains("ScrollView");
            n.Click = n.Clickable ? n.Id : parent.node == null ? -1 : parent.node.Click;
            if (first) { s.Width = n.W; s.Height = n.H; first = false; }
            s.Nodes.Add(n);
            stack.Add((depth, n, disabled));
        }
        return s;
    }

    private static float F(Match m, string g) => float.Parse(m.Groups[g].Value, System.Globalization.CultureInfo.InvariantCulture);

    public static int Run(string path)
    {
        var s = Load(path);
        var t = new GameText();
        Console.WriteLine($"{Path.GetFileName(path)}: {s.Nodes.Count} elements, UI {s.Width}x{s.Height}");
        Console.WriteLine($"  jobs page showing: {JobsPage.IsShowing(s, t)}");
        var all = s.Button(t.ApplyForAllJobs);
        Console.WriteLine($"  \"{t.ApplyForAllJobs}\" button: {(all == null ? "NOT FOUND" : all.ToString())}");
        var single = s.Button(t.ApplyForJob);
        Console.WriteLine($"  \"{t.ApplyForJob}\" button: {(single == null ? "NOT FOUND" : single.ToString())}");
        Console.WriteLine($"  confirm box: {(JobsPage.ConfirmShowing(s, t) ? JobsPage.ConfirmButton(s, t)?.ToString() ?? "title only" : "none")}");
        var rows = JobsPage.Read(s, t, out var scroll, out var tl, out var tr, out var hb);
        Console.WriteLine($"  rows read: {rows.Count}, open: {rows.Count(r => r.IsVacancy)}, \"Available\" rows on screen: {JobsPage.VacancyWordsOnScreen(s, t, tl, tr, hb, scroll)} (table x {tl:0}–{tr:0}), scroll: {(scroll == null ? "none" : scroll.ToString())}");
        foreach (var r in rows) Console.WriteLine("    " + r.Debug());
        return all != null && all.Enabled ? 0 : 1;
    }
}
