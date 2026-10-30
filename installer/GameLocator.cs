using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ManagerAgentSetup
{
    internal enum Store { Steam, Epic, Xbox, Other }

    /// <summary>One Football Manager install found on this PC.</summary>
    internal sealed class GameInstall
    {
        public string Folder;
        public string ExeName;        // e.g. "fm" (from fm_Data)
        public Store Store;
        public string SteamAppId;
        public string EpicLaunchUri;
        public string Title;          // "Football Manager 26"

        public string ExePath => Path.Combine(Folder, ExeName + ".exe");

        public string Display
        {
            get
            {
                string store = Store == Store.Steam ? "Steam" : Store == Store.Epic ? "Epic Games" : Store == Store.Xbox ? "Xbox app" : "folder";
                return Title + "  (" + store + ")";
            }
        }
    }

    /// <summary>Finds Football Manager installs (Unity/IL2CPP editions, i.e. FM26 and later).</summary>
    internal static class GameLocator
    {
        public static List<GameInstall> FindAll()
        {
            var found = new List<GameInstall>();
            try { found.AddRange(FindSteam()); } catch (Exception ex) { Log.Write("Steam search failed: " + ex.Message); }
            try { found.AddRange(FindEpic()); } catch (Exception ex) { Log.Write("Epic search failed: " + ex.Message); }
            try { found.AddRange(FindXbox()); } catch (Exception ex) { Log.Write("Xbox search failed: " + ex.Message); }

            // De-duplicate by folder, newest edition first.
            return found
                .GroupBy(g => Normalize(g.Folder))
                .Select(g => g.First())
                .OrderByDescending(g => g.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Checks a folder chosen by hand (or one level below it).</summary>
        public static GameInstall FromFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
            var g = Inspect(folder, Store.Other);
            if (g != null) return g;
            foreach (var sub in SafeDirs(folder))
            {
                g = Inspect(sub, Store.Other);
                if (g != null) return g;
            }
            return null;
        }

        /// <summary>A Unity IL2CPP game folder: GameAssembly.dll plus an X.exe with an X_Data folder.</summary>
        public static GameInstall Inspect(string folder, Store store)
        {
            try
            {
                if (!File.Exists(Path.Combine(folder, "GameAssembly.dll"))) return null;
                foreach (var data in Directory.GetDirectories(folder, "*_Data"))
                {
                    var exe = Path.GetFileName(data);
                    exe = exe.Substring(0, exe.Length - "_Data".Length);
                    if (!File.Exists(Path.Combine(folder, exe + ".exe"))) continue;
                    return new GameInstall { Folder = folder, ExeName = exe, Store = store, Title = TitleFor(folder) };
                }
            }
            catch (Exception ex) { Log.Write("Inspect " + folder + ": " + ex.Message); }
            return null;
        }

        private static string TitleFor(string folder)
        {
            var name = Path.GetFileName(folder.TrimEnd('\\', '/'));
            if (string.Equals(name, "Content", StringComparison.OrdinalIgnoreCase))
                name = Path.GetFileName(Path.GetDirectoryName(folder.TrimEnd('\\', '/')));
            return name.IndexOf("Football", StringComparison.OrdinalIgnoreCase) >= 0 ? name : "Football Manager (" + name + ")";
        }

        // ------------------------------------------------------------- Steam

        public static IEnumerable<GameInstall> FindSteam()
        {
            var steamRoots = new List<string>();
            AddIf(steamRoots, ReadReg(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"));
            AddIf(steamRoots, ReadReg(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"));
            AddIf(steamRoots, ReadReg(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"));
            AddIf(steamRoots, @"C:\Program Files (x86)\Steam");

            var libraries = new List<string>();
            foreach (var root in steamRoots)
            {
                AddIf(libraries, root);
                var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf)) foreach (var lib in ParseLibraryFolders(File.ReadAllText(vdf))) AddIf(libraries, lib);
            }
            foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var common = Path.Combine(lib, "steamapps", "common");
                if (!Directory.Exists(common)) continue;
                var manifests = ReadAppManifests(Path.Combine(lib, "steamapps"));
                foreach (var dir in SafeDirs(common).Where(d => Path.GetFileName(d).StartsWith("Football Manager", StringComparison.OrdinalIgnoreCase)))
                {
                    var g = Inspect(dir, Store.Steam);
                    if (g == null) continue;
                    string appId;
                    if (manifests.TryGetValue(Path.GetFileName(dir), out appId)) g.SteamAppId = appId;
                    yield return g;
                }
            }
        }

        public static IEnumerable<string> ParseLibraryFolders(string vdf)
        {
            foreach (Match m in Regex.Matches(vdf, "\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase))
                yield return m.Groups[1].Value.Replace("\\\\", "\\");
        }

        /// <summary>installdir → appid from appmanifest_*.acf files.</summary>
        public static Dictionary<string, string> ReadAppManifests(string steamapps)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(steamapps)) return map;
            foreach (var acf in Directory.GetFiles(steamapps, "appmanifest_*.acf"))
            {
                try
                {
                    var text = File.ReadAllText(acf);
                    var id = Regex.Match(text, "\"appid\"\\s+\"(\\d+)\"", RegexOptions.IgnoreCase);
                    var dir = Regex.Match(text, "\"installdir\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
                    if (id.Success && dir.Success) map[dir.Groups[1].Value] = id.Groups[1].Value;
                }
                catch { }
            }
            return map;
        }

        // ------------------------------------------------------------- Epic

        public static IEnumerable<GameInstall> FindEpic()
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var dir = Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
            if (!Directory.Exists(dir)) yield break;
            foreach (var item in Directory.GetFiles(dir, "*.item"))
            {
                GameInstall g = null;
                try
                {
                    var json = File.ReadAllText(item);
                    var name = JsonField(json, "DisplayName");
                    if (name == null || name.IndexOf("Football Manager", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var loc = JsonField(json, "InstallLocation");
                    g = loc == null ? null : Inspect(loc, Store.Epic);
                    if (g == null) continue;
                    g.Title = name;
                    var ns = JsonField(json, "CatalogNamespace");
                    var cat = JsonField(json, "CatalogItemId");
                    var app = JsonField(json, "AppName");
                    if (ns != null && cat != null && app != null)
                        g.EpicLaunchUri = "com.epicgames.launcher://apps/" + ns + "%3A" + cat + "%3A" + app + "?action=launch&silent=true";
                }
                catch (Exception ex) { Log.Write("Epic manifest " + item + ": " + ex.Message); }
                if (g != null) yield return g;
            }
        }

        public static string JsonField(string json, string field)
        {
            var m = Regex.Match(json, "\"" + Regex.Escape(field) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? Regex.Unescape(m.Groups[1].Value) : null;
        }

        // ------------------------------------------------------------- Xbox / Game Pass

        public static IEnumerable<GameInstall> FindXbox()
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                string xbox;
                try
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                    xbox = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
                    if (!Directory.Exists(xbox)) continue;
                }
                catch { continue; }
                foreach (var dir in SafeDirs(xbox).Where(d => Path.GetFileName(d).StartsWith("Football Manager", StringComparison.OrdinalIgnoreCase)))
                {
                    var g = Inspect(Path.Combine(dir, "Content"), Store.Xbox) ?? Inspect(dir, Store.Xbox);
                    if (g != null) yield return g;
                }
            }
        }

        // ------------------------------------------------------------- helpers

        private static string ReadReg(RegistryKey hive, string key, string value)
        {
            try
            {
                using (var k = hive.OpenSubKey(key))
                    return k?.GetValue(value) as string;
            }
            catch { return null; }
        }

        private static void AddIf(List<string> list, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            path = path.Replace('/', Path.DirectorySeparatorChar);
            if (Directory.Exists(path) && !list.Any(p => string.Equals(Normalize(p), Normalize(path), StringComparison.OrdinalIgnoreCase)))
                list.Add(path);
        }

        private static IEnumerable<string> SafeDirs(string path)
        {
            try { return Directory.GetDirectories(path); }
            catch { return new string[0]; }
        }

        private static string Normalize(string p)
        {
            try { return Path.GetFullPath(p).TrimEnd('\\', '/').ToLowerInvariant(); }
            catch { return p.ToLowerInvariant(); }
        }
    }
}
