using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace ManagerAgent;

/// <summary>
/// Startup check that this build of the plugin matches the game's interop assemblies.
/// It JIT-compiles every method in the plugin, which resolves every game/BepInEx type and
/// member the plugin references; anything missing shows up here instead of mid-interview.
/// The result goes to the log and to ManagerAgent\selftest.txt, which the installer reads.
/// </summary>
internal static class SelfTest
{
    public static string StatusFile => Path.Combine(AgentPaths.DataDir, "selftest.txt");

    public static bool Run(out string summary)
    {
        var failures = new List<string>();
        int prepared = 0;

        Type[] types;
        try { types = typeof(SelfTest).Assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex)
        {
            foreach (var le in ex.LoaderExceptions.Where(e => e != null).Take(20)) failures.Add("type load: " + le.Message);
            types = ex.Types.Where(t => t != null).ToArray();
        }

        const BindingFlags all = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic |
                                 BindingFlags.Instance | BindingFlags.Static;
        foreach (var t in types)
        {
            if (t.ContainsGenericParameters) continue;
            MethodBase[] methods;
            try { methods = t.GetMethods(all).Cast<MethodBase>().Concat(t.GetConstructors(all)).ToArray(); }
            catch (Exception ex) { failures.Add($"{t.FullName}: {ex.Message}"); continue; }

            foreach (var m in methods)
            {
                if (m.IsAbstract || m.ContainsGenericParameters) continue;
                if ((m.Attributes & MethodAttributes.PinvokeImpl) != 0) continue;
                var impl = m.GetMethodImplementationFlags();
                if ((impl & (MethodImplAttributes.InternalCall | MethodImplAttributes.Runtime)) != 0) continue;
                try
                {
                    RuntimeHelpers.PrepareMethod(m.MethodHandle);
                    prepared++;
                }
                catch (Exception ex)
                {
                    var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                    failures.Add($"{t.Name}.{m.Name}: {inner.GetType().Name}: {inner.Message}");
                }
            }
        }

        bool ok = failures.Count == 0;
        summary = ok ? $"OK ({prepared} methods checked)" : $"FAILED ({failures.Count} problems)";
        Write(ok, prepared, failures);
        return ok;
    }

    private static void Write(bool ok, int prepared, List<string> failures)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("status=" + (ok ? "OK" : "FAILED"));
            sb.AppendLine("version=" + Plugin.Version);
            sb.AppendLine("time=" + DateTime.Now.ToString("o"));
            sb.AppendLine("methods=" + prepared);
            foreach (var f in failures.Take(50)) sb.AppendLine("problem=" + f.Replace('\n', ' '));
            File.WriteAllText(StatusFile, sb.ToString());
        }
        catch (Exception ex)
        {
            AgentLog.Error("Couldn't write selftest.txt: " + ex.Message);
        }
    }
}
