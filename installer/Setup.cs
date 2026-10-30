using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ManagerAgentSetup
{
    internal static class Log
    {
        private static readonly object Gate = new object();
        public static event Action<string> Line;

        public static string Dir
        {
            get
            {
                var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ManagerAgent");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        public static string FilePath => Path.Combine(Dir, "setup.log");

        public static void Write(string msg)
        {
            var line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg;
            lock (Gate)
            {
                try { File.AppendAllText(FilePath, line + Environment.NewLine); } catch { }
            }
            var h = Line;
            if (h != null) h(line);
        }
    }

    /// <summary>Downloads with progress; TLS 1.2/1.3 for .NET Framework.</summary>
    internal static class Net
    {
        static Net()
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072 | (SecurityProtocolType)12288; }
            catch { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; }
        }

        private const string Agent = "ManagerAgentSetup/" + SetupInfo.Version + " (+FM mod installer)";

        public static string GetText(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = Agent;
            req.Timeout = 30000;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return r.ReadToEnd();
        }

        public static void Download(string url, string dest, Action<long, long> progress, CancellationToken ct)
        {
            Log.Write("Downloading " + url);
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = Agent;
            req.Timeout = 60000;
            req.ReadWriteTimeout = 60000;
            var tmp = dest + ".part";
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var src = resp.GetResponseStream())
            using (var dst = File.Create(tmp))
            {
                long total = resp.ContentLength, done = 0;
                var buf = new byte[81920];
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    dst.Write(buf, 0, n);
                    done += n;
                    if (progress != null) progress(done, total);
                }
            }
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(tmp, dest);
        }
    }

    internal static class SetupInfo
    {
        public const string Version = "0.3.4";
        public const string BepInExBuildsPage = "https://builds.bepinex.dev/projects/bepinex_be";
    }

    /// <summary>Everything the installer knows about one game folder.</summary>
    internal sealed class GameState
    {
        public readonly GameInstall Game;
        public GameState(GameInstall g) { Game = g; }

        public string BepInExDir => Path.Combine(Game.Folder, "BepInEx");
        public string PluginDir => Path.Combine(BepInExDir, "plugins", "ManagerAgent");
        public string PluginDll => Path.Combine(PluginDir, "ManagerAgent.dll");
        public string DataDir => Path.Combine(BepInExDir, "config", "ManagerAgent");
        public string SelfTestFile => Path.Combine(DataDir, "selftest.txt");
        public string LogFile => Path.Combine(BepInExDir, "LogOutput.log");
        public string MarkerFile => Path.Combine(BepInExDir, "config", "ManagerAgent.setup.txt");

        public bool BepInExInstalled => File.Exists(Path.Combine(BepInExDir, "core", "BepInEx.Unity.IL2CPP.dll"));
        public bool InteropReady => File.Exists(Path.Combine(BepInExDir, "interop", "UnityEngine.UIElementsModule.dll"));
        public bool PluginInstalled => File.Exists(PluginDll);

        /// <summary>"OK", "FAILED" or null (not run yet).</summary>
        public string SelfTestStatus(out string details, DateTime? notBefore = null)
        {
            details = null;
            try
            {
                if (!File.Exists(SelfTestFile)) return null;
                if (notBefore.HasValue && File.GetLastWriteTime(SelfTestFile) < notBefore.Value.AddSeconds(-2)) return null;
                var lines = ReadShared(SelfTestFile).Split('\n').Select(l => l.Trim()).ToList();
                var status = lines.FirstOrDefault(l => l.StartsWith("status="));
                details = string.Join(Environment.NewLine, lines.Where(l => l.StartsWith("problem=")).Select(l => l.Substring(8)).Take(10));
                return status == null ? null : status.Substring(7);
            }
            catch { return null; }
        }

        public bool IsRunning
        {
            get
            {
                try { return Process.GetProcessesByName(Game.ExeName).Length > 0; }
                catch { return false; }
            }
        }

        public static string ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(fs))
                return r.ReadToEnd();
        }

        public bool CanWrite()
        {
            try
            {
                var probe = Path.Combine(Game.Folder, ".manageragent-write-test");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }
    }

    internal static class BepInExInstaller
    {
        /// <summary>Downloads (or uses a zip placed next to the installer) and extracts BepInEx into the game folder.</summary>
        public static void Install(GameState st, Action<string, int> report, CancellationToken ct)
        {
            string zip = FindLocalZip();
            if (zip != null)
            {
                Log.Write("Using BepInEx zip found next to the installer: " + zip);
            }
            else
            {
                zip = Path.Combine(Log.Dir, "bepinex.zip");
                string url = null;
                foreach (var source in CandidateUrls(st.Game))
                {
                    try
                    {
                        url = source();
                        if (url != null) break;
                    }
                    catch (Exception ex) { Log.Write("BepInEx source failed: " + ex.Message); }
                }
                if (url == null)
                    throw new SetupException("Couldn't find a BepInEx download automatically.",
                        "Open the BepInEx page, download the file whose name starts with \"BepInEx-Unity.IL2CPP-win-x64\", " +
                        "put it in the same folder as this installer, then press Install again.", SetupInfo.BepInExBuildsPage);
                report("Downloading BepInEx…", 0);
                Net.Download(url, zip, (done, total) =>
                {
                    if (total > 0) report("Downloading BepInEx… " + (done * 100 / total) + "%", (int)(done * 100 / total));
                }, ct);
            }

            report("Unpacking BepInEx…", 100);
            var written = ExtractBepInEx(zip, st.Game.Folder);
            File.WriteAllLines(st.MarkerFile, new[] { "# Files added by Manager Agent setup (used by Uninstall)" }.Concat(written));
            Log.Write("BepInEx installed (" + written.Count + " top-level items).");
        }

        private static IEnumerable<Func<string>> CandidateUrls(GameInstall g)
        {
            // 1) The Thunderstore pack made for this game edition (FM26 has one; FM27 may later).
            var digits = Regex.Match(g.Title ?? "", @"\d{2}").Value;
            if (digits.Length == 2)
            {
                var pkg = "BepInExPack_FootballManager" + digits;
                yield return () => ParseThunderstore(Net.GetText("https://thunderstore.io/api/experimental/package/BepInEx/" + pkg + "/"));
            }
            // 2) Latest BepInEx 6 IL2CPP build for Windows.
            yield return () => ParseBuildsPage(Net.GetText(SetupInfo.BepInExBuildsPage));
        }

        public static string ParseThunderstore(string json)
        {
            var m = Regex.Match(json ?? "", "\"download_url\"\\s*:\\s*\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>First (newest) Windows x64 IL2CPP zip linked from builds.bepinex.dev.</summary>
        public static string ParseBuildsPage(string html)
        {
            var m = Regex.Match(html ?? "", "href=\"([^\"]*BepInEx-Unity\\.IL2CPP-win-x64[^\"]*\\.zip)\"", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            var href = m.Groups[1].Value.Replace("&amp;", "&");
            return href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? href
                : "https://builds.bepinex.dev" + (href.StartsWith("/") ? "" : "/") + href;
        }

        public static string FindLocalZip()
        {
            try
            {
                var here = AppDomain.CurrentDomain.BaseDirectory;
                return Directory.GetFiles(here, "*.zip")
                    .Where(f =>
                    {
                        var n = Path.GetFileName(f);
                        return n.IndexOf("BepInEx", StringComparison.OrdinalIgnoreCase) >= 0 &&
                               (n.IndexOf("IL2CPP", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("FootballManager", StringComparison.OrdinalIgnoreCase) >= 0);
                    })
                    .OrderByDescending(File.GetLastWriteTime)
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        /// <summary>
        /// Extracts the BepInEx payload (the folder that contains winhttp.dll) into the game folder.
        /// Works for both the plain BepInEx zip and Thunderstore packs (which nest it one level down).
        /// Returns the top-level files/folders written.
        /// </summary>
        public static List<string> ExtractBepInEx(string zipPath, string gameDir)
        {
            using (var zip = new Zip(zipPath))
            {
                var loader = zip.Entries.FirstOrDefault(e => e.Name.EndsWith("winhttp.dll", StringComparison.OrdinalIgnoreCase));
                if (loader == null) throw new SetupException("That download doesn't look like BepInEx for Windows (no winhttp.dll inside).", null, null);
                var prefix = loader.Name.Substring(0, loader.Name.Length - "winhttp.dll".Length);
                if (!zip.Entries.Any(e => e.Name.Equals(prefix + "BepInEx/core/BepInEx.Unity.IL2CPP.dll", StringComparison.OrdinalIgnoreCase)))
                    throw new SetupException("That download isn't the IL2CPP edition of BepInEx 6 that Football Manager needs.", null, null);

                var top = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                var root = Path.GetFullPath(gameDir);
                foreach (var e in zip.Entries)
                {
                    if (!e.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    var rel = e.Name.Substring(prefix.Length);
                    if (rel.Length == 0) continue;
                    var dest = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
                    if (!dest.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue; // zip-slip guard
                    top.Add(rel.Split('/')[0]);
                    if (e.IsDirectory) { Directory.CreateDirectory(dest); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    zip.ExtractTo(e, dest);
                }
                return top.ToList();
            }
        }
    }

    internal static class PluginInstaller
    {
        public static void Install(GameState st)
        {
            Directory.CreateDirectory(st.PluginDir);
            using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("ManagerAgent.dll"))
            {
                if (res == null) throw new SetupException("The installer is damaged (plugin missing). Download it again.", null, null);
                using (var f = File.Create(st.PluginDll)) res.CopyTo(f);
            }
            TryDelete(st.SelfTestFile);
            Log.Write("Manager Agent " + SetupInfo.Version + " copied to " + st.PluginDll);
        }

        public static void Uninstall(GameState st, bool removeData, bool removeBepInEx)
        {
            if (Directory.Exists(st.PluginDir)) Directory.Delete(st.PluginDir, true);
            if (removeData)
            {
                if (Directory.Exists(st.DataDir)) Directory.Delete(st.DataDir, true);
                TryDelete(Path.Combine(st.BepInExDir, "config", "com.manageragent.fm.cfg"));
            }
            if (removeBepInEx && File.Exists(st.MarkerFile))
            {
                var items = File.ReadAllLines(st.MarkerFile).Where(l => l.Length > 0 && !l.StartsWith("#")).ToList();
                foreach (var item in items)
                {
                    var p = Path.Combine(st.Game.Folder, item);
                    try
                    {
                        if (Directory.Exists(p)) Directory.Delete(p, true);
                        else if (File.Exists(p)) File.Delete(p);
                    }
                    catch (Exception ex) { Log.Write("Couldn't remove " + p + ": " + ex.Message); }
                }
            }
            Log.Write("Uninstalled (data removed: " + removeData + ", BepInEx removed: " + removeBepInEx + ").");
        }

        public static bool BepInExWasInstalledByUs(GameState st) => File.Exists(st.MarkerFile);

        public static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    /// <summary>An error with a plain-language explanation for the user.</summary>
    internal sealed class SetupException : Exception
    {
        public readonly string Advice;
        public readonly string Link;
        public SetupException(string message, string advice, string link) : base(message) { Advice = advice; Link = link; }
    }
}
