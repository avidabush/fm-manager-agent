using System.IO;
using System.Text;
using UnityEngine;

namespace ManagerAgent;

/// <summary>Writes the visible UI tree to a text file — the first thing to look at if detection misbehaves.</summary>
internal static class UiDump
{
    private sealed class DumpVisitor : IScanVisitor
    {
        public readonly StringBuilder Sb = new();

        public bool Visit(VisualElement ve, int depth, int order, string type, string name, PathItem[] path)
        {
            Sb.Append(' ', Math.Min(depth, 60) * 2);
            Sb.Append(type);
            if (name.Length > 0) Sb.Append(" #").Append(name);
            var classes = Ui.Classes(ve);
            if (classes.Count > 0) Sb.Append(" .").Append(string.Join(".", classes));
            if (Ui.TryBounds(ve, out var x, out var y, out var w, out var h))
                Sb.Append($"  [{x:0},{y:0} {w:0}x{h:0}]");
            if (Ui.LooksClickable(ve)) Sb.Append("  (clickable)");
            var t = Ui.OwnText(ve);
            if (t.Length > 0) Sb.Append("  \"").Append(t.Length > 140 ? t.Substring(0, 140) + "…" : t).Append('"');
            Sb.Append('\n');
            return true;
        }
    }

    public static string Write(HashSet<long> skip)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Manager Agent UI dump — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Screen {Screen.width}x{Screen.height}; window client " +
                      (GameWindow.TryClientSize(out var cw, out var ch) ? $"{cw}x{ch}" : "unknown"));
        sb.AppendLine("Only shown elements are listed. Format: NativeType #name .classes [x,y WxH] \"own text\"");
        sb.AppendLine();
        foreach (var r in Ui.Roots)
        {
            sb.AppendLine($"=== Panel root (UIDocument '{r.Name}', sorting {r.Sorting}) ===");
            var v = new DumpVisitor();
            new TreeScan(new[] { r.Root }, v, skip).RunToEnd();
            sb.Append(v.Sb);
            sb.AppendLine();
        }
        var path = Path.Combine(AgentPaths.DumpDir, $"ui-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(path, sb.ToString());
        return path;
    }
}
