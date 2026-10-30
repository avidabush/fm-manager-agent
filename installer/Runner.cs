using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace ManagerAgentSetup
{
    internal enum WatchResult { Ok, Failed, NotLoaded, GameClosed, TimedOut }

    /// <summary>Starts the game and waits for the plugin's self-test verdict.</summary>
    internal static class GameRunner
    {
        /// <summary>How long after "Chainloader startup complete" to wait for the plugin's verdict.</summary>
        internal static int NotLoadedGraceSeconds = 20;

        /// <summary>Returns false when the user must start the game themselves (Xbox app).</summary>
        public static bool Launch(GameState st)
        {
            var g = st.Game;
            if (g.Store == Store.Steam && g.SteamAppId != null) { ShellOpen("steam://rungameid/" + g.SteamAppId); return true; }
            if (g.Store == Store.Epic && g.EpicLaunchUri != null) { ShellOpen(g.EpicLaunchUri); return true; }
            if (g.Store == Store.Xbox) return false;
            StartUnelevated(g.ExePath, g.Folder);
            return true;
        }

        private static void ShellOpen(string uri)
        {
            Log.Write("Launching " + uri);
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }

        private static void StartUnelevated(string exe, string workDir)
        {
            Log.Write("Launching " + exe);
            if (IsElevated())
            {
                // Let Explorer start it so the game doesn't inherit administrator rights.
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + exe + "\"") { UseShellExecute = true });
                return;
            }
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = workDir });
        }

        public static bool IsElevated()
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }

        public static WatchResult Watch(GameState st, DateTime since, Action<string> status, CancellationToken ct, out string details)
        {
            details = null;
            var deadline = DateTime.Now.AddMinutes(30);
            DateTime? chainloaderDoneAt = null, lastSeenRunning = null;
            bool everRunning = false;
            string lastStatus = null;

            while (DateTime.Now < deadline)
            {
                ct.ThrowIfCancellationRequested();

                string d;
                var verdict = st.SelfTestStatus(out d, since);
                if (verdict == "OK") return WatchResult.Ok;
                if (verdict == "FAILED") { details = d; return WatchResult.Failed; }

                bool running = st.IsRunning;
                if (running) { everRunning = true; lastSeenRunning = DateTime.Now; }

                var log = ReadLog(st, since);
                string phase;
                if (log == null)
                    phase = everRunning ? "Football Manager is starting…" : "Waiting for Football Manager to start…";
                else if (log.IndexOf("Chainloader startup complete", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    phase = "Checking Manager Agent…";
                    if (chainloaderDoneAt == null) chainloaderDoneAt = DateTime.Now;
                    if (DateTime.Now - chainloaderDoneAt.Value > TimeSpan.FromSeconds(NotLoadedGraceSeconds))
                    {
                        details = string.Join(Environment.NewLine, log.Split('\n')
                            .Where(l => l.IndexOf("Manager Agent", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        l.IndexOf("ManagerAgent", StringComparison.OrdinalIgnoreCase) >= 0)
                            .Select(l => l.Trim()).Take(12));
                        return WatchResult.NotLoaded;
                    }
                }
                else if (log.IndexOf("Loading [Manager Agent", StringComparison.OrdinalIgnoreCase) >= 0)
                    phase = "Checking Manager Agent…";
                else if (log.IndexOf("Chainloader initialized", StringComparison.OrdinalIgnoreCase) >= 0)
                    phase = "Loading mods…";
                else if (log.IndexOf("Cpp2IL", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         log.IndexOf("interop assemblies", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         log.IndexOf("unity base libraries", StringComparison.OrdinalIgnoreCase) >= 0)
                    phase = "Preparing Football Manager for mods (first time only)…";
                else
                    phase = "Football Manager is starting…";

                if (phase != lastStatus) { status(phase); lastStatus = phase; }

                if (everRunning && !running && lastSeenRunning.HasValue && DateTime.Now - lastSeenRunning.Value > TimeSpan.FromSeconds(8))
                {
                    // One last look — the verdict may have landed just before it closed.
                    verdict = st.SelfTestStatus(out d, since);
                    if (verdict == "OK") return WatchResult.Ok;
                    if (verdict == "FAILED") { details = d; return WatchResult.Failed; }
                    return WatchResult.GameClosed;
                }
                Thread.Sleep(2000);
            }
            return WatchResult.TimedOut;
        }

        /// <summary>Newest BepInEx log written since <paramref name="since"/>, or null.</summary>
        private static string ReadLog(GameState st, DateTime since)
        {
            try
            {
                if (!Directory.Exists(st.BepInExDir)) return null;
                var f = new DirectoryInfo(st.BepInExDir).GetFiles("LogOutput*.log")
                    .Where(x => x.LastWriteTime >= since.AddSeconds(-2))
                    .OrderByDescending(x => x.LastWriteTime).FirstOrDefault();
                return f == null ? null : GameState.ReadShared(f.FullName);
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// "Repair": builds the plugin from source against this PC's game files. Installs a private
    /// copy of the .NET SDK if none is present (no admin rights needed, ~200 MB, one time).
    /// </summary>
    internal static class LocalBuilder
    {
        private static string ToolsDir => Path.Combine(Log.Dir, "dotnet");
        private static string SrcDir => Path.Combine(Log.Dir, "source");

        public static void Build(GameState st, Action<string, int> report, CancellationToken ct)
        {
            if (!st.InteropReady)
                throw new SetupException("Football Manager hasn't been prepared for mods yet.",
                    "Start Football Manager once (with this installer open) and wait for the main menu, then try Repair again.", null);

            var dotnet = FindDotnet();
            if (dotnet == null)
            {
                report("Getting the .NET build tools (one time, about 200 MB)…", 5);
                var script = Path.Combine(Log.Dir, "dotnet-install.ps1");
                Net.Download("https://dot.net/v1/dotnet-install.ps1", script, null, ct);
                var args = "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" -Channel 8.0 -InstallDir \"" + ToolsDir + "\" -NoPath";
                var code = Run("powershell.exe", args, Log.Dir, report, "Getting the .NET build tools…", 10, ct);
                dotnet = FindDotnet();
                if (code != 0 || dotnet == null)
                    throw new SetupException("Couldn't install the .NET build tools.",
                        "Check your internet connection and try Repair again. You can also install the .NET 8 SDK yourself from Microsoft and retry.",
                        "https://dotnet.microsoft.com/download/dotnet/8.0");
            }

            report("Unpacking Manager Agent source…", 40);
            if (Directory.Exists(SrcDir)) Directory.Delete(SrcDir, true);
            Directory.CreateDirectory(SrcDir);
            using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("source.zip"))
            {
                if (res == null) throw new SetupException("The installer is damaged (source missing). Download it again.", null, null);
                var zipPath = Path.Combine(Log.Dir, "source.zip");
                using (var f = File.Create(zipPath)) res.CopyTo(f);
                Zip.ExtractAll(zipPath, SrcDir);
            }

            report("Building Manager Agent for your game version (a minute or two)…", 50);
            var csproj = Directory.GetFiles(SrcDir, "ManagerAgent.csproj", SearchOption.AllDirectories).First();
            PluginInstaller.TryDelete(st.SelfTestFile);
            var buildArgs = "build \"" + csproj + "\" -c Release -nologo -p:GameDir=\"" + st.Game.Folder.TrimEnd('\\') + "\"";
            var rc = Run(dotnet, buildArgs, Path.GetDirectoryName(csproj), report, "Building Manager Agent…", 60, ct);
            if (rc != 0 || !File.Exists(st.PluginDll))
                throw new SetupException("Building Manager Agent for your game didn't work.",
                    "Send the setup log (button below) to whoever gave you this mod so it can be fixed.", null);
            report("Built and installed.", 100);
        }

        private static string FindDotnet()
        {
            var local = Path.Combine(ToolsDir, "dotnet.exe");
            if (File.Exists(local) && HasSdk(local)) return local;
            foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramW6432") })
            {
                if (string.IsNullOrEmpty(root)) continue;
                var exe = Path.Combine(root, "dotnet", "dotnet.exe");
                if (File.Exists(exe) && HasSdk(exe)) return exe;
            }
            return null;
        }

        private static bool HasSdk(string dotnet)
        {
            try
            {
                var psi = new ProcessStartInfo(dotnet, "--list-sdks") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                using (var p = Process.Start(psi))
                {
                    var outp = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(20000);
                    return outp.Split('\n').Any(l =>
                    {
                        int major;
                        var dot = l.IndexOf('.');
                        return dot > 0 && int.TryParse(l.Substring(0, dot).Trim(), out major) && major >= 6;
                    });
                }
            }
            catch { return false; }
        }

        private static int Run(string exe, string args, string workDir, Action<string, int> report, string label, int pct, CancellationToken ct)
        {
            Log.Write("> " + exe + " " + args);
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workDir,
            };
            psi.EnvironmentVariables["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            psi.EnvironmentVariables["DOTNET_NOLOGO"] = "1";
            psi.EnvironmentVariables["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
            using (var p = new Process { StartInfo = psi })
            {
                DataReceivedEventHandler onLine = (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Write("  " + e.Data.Trim()); };
                p.OutputDataReceived += onLine;
                p.ErrorDataReceived += onLine;
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                report(label, pct);
                while (!p.WaitForExit(500))
                {
                    if (ct.IsCancellationRequested) { try { p.Kill(); } catch { } ct.ThrowIfCancellationRequested(); }
                }
                p.WaitForExit();
                Log.Write("exit code " + p.ExitCode);
                return p.ExitCode;
            }
        }
    }
}
