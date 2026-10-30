using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using ManagerAgentSetup;

internal static class SetupTests
{
    private static int pass, fail;
    private static string Tmp;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) pass++; else fail++;
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (ok || detail == "" ? "" : "   → " + detail));
    }

    private static string FakeGame(string name, bool bepinex = false)
    {
        var dir = Path.Combine(Tmp, name);
        Directory.CreateDirectory(Path.Combine(dir, "fm_Data"));
        File.WriteAllText(Path.Combine(dir, "fm.exe"), "x");
        File.WriteAllText(Path.Combine(dir, "GameAssembly.dll"), "x");
        if (bepinex)
        {
            Directory.CreateDirectory(Path.Combine(dir, "BepInEx", "core"));
            File.WriteAllText(Path.Combine(dir, "BepInEx", "core", "BepInEx.Unity.IL2CPP.dll"), "x");
        }
        return dir;
    }

    private static string MakeZip(string name, Dictionary<string, string> files, CompressionLevel level = CompressionLevel.Optimal)
    {
        var path = Path.Combine(Tmp, name);
        if (File.Exists(path)) File.Delete(path);
        using (var fs = File.Create(path))
        using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (var kv in files)
            {
                var e = z.CreateEntry(kv.Key, kv.Key.EndsWith(".txt") ? CompressionLevel.NoCompression : level);
                if (kv.Value == null) continue;
                using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false))) w.Write(kv.Value);
            }
        }
        return path;
    }

    private static Dictionary<string, string> BepInExLayout(string prefix)
    {
        return new Dictionary<string, string>
        {
            [prefix + "winhttp.dll"] = "loader",
            [prefix + "doorstop_config.ini"] = "[General]\nenabled=true",
            [prefix + ".doorstop_version"] = "4.3.0",
            [prefix + "BepInEx/core/BepInEx.Unity.IL2CPP.dll"] = new string('B', 5000),
            [prefix + "BepInEx/core/BepInEx.Core.dll"] = "core",
            [prefix + "dotnet/coreclr.dll"] = new string('c', 20000),
            [prefix + "changelog.txt"] = "stored entry",
        };
    }

    private static int Main()
    {
        Tmp = Path.Combine(Path.GetTempPath(), "ma-setup-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(Tmp);
        try { Run(); }
        catch (Exception ex) { fail++; Console.WriteLine("CRASH " + ex); }
        finally { try { Directory.Delete(Tmp, true); } catch { } }
        Console.WriteLine();
        Console.WriteLine(pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }

    private static void Run()
    {
        // ------------------------------------------------ game detection
        var fm26 = FakeGame("Football Manager 26");
        var g = GameLocator.Inspect(fm26, Store.Steam);
        Check("Detects a Unity/IL2CPP FM folder", g != null && g.ExeName == "fm" && g.Title == "Football Manager 26", g == null ? "null" : g.ExeName + "/" + g.Title);
        Directory.CreateDirectory(Path.Combine(Tmp, "Football Manager 2024"));
        File.WriteAllText(Path.Combine(Tmp, "Football Manager 2024", "fm.exe"), "x");
        Check("Ignores pre-Unity FM (no GameAssembly.dll)", GameLocator.Inspect(Path.Combine(Tmp, "Football Manager 2024"), Store.Steam) == null);
        var parent = Path.Combine(Tmp, "Games");
        Directory.CreateDirectory(parent);
        Directory.Move(fm26, Path.Combine(parent, "Football Manager 26"));
        fm26 = Path.Combine(parent, "Football Manager 26");
        var picked = GameLocator.FromFolder(parent);
        Check("Browse: picking the parent folder still finds the game", picked != null && picked.Folder == fm26, picked?.Folder);

        var vdf = "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"D:\\\\SteamLibrary\"\n\t\t\"label\"\t\t\"\"\n\t}\n}";
        var libs = GameLocator.ParseLibraryFolders(vdf).ToList();
        Check("Steam libraryfolders.vdf parsed", libs.Count == 2 && libs[1] == @"D:\SteamLibrary", string.Join(" | ", libs));
        var steamapps = Path.Combine(Tmp, "steamapps");
        Directory.CreateDirectory(steamapps);
        File.WriteAllText(Path.Combine(steamapps, "appmanifest_3551340.acf"), "\"AppState\"\n{\n\t\"appid\"\t\t\"3551340\"\n\t\"name\"\t\t\"Football Manager 26\"\n\t\"installdir\"\t\t\"Football Manager 26\"\n}");
        var map = GameLocator.ReadAppManifests(steamapps);
        Check("Steam app id from appmanifest", map.ContainsKey("Football Manager 26") && map["Football Manager 26"] == "3551340");
        var epic = "{\n\t\"DisplayName\": \"Football Manager 26\",\n\t\"InstallLocation\": \"C:\\\\Program Files\\\\Epic Games\\\\FootballManager26\",\n\t\"AppName\": \"Sage\"\n}";
        Check("Epic manifest field (with escaped backslashes)", GameLocator.JsonField(epic, "InstallLocation") == @"C:\Program Files\Epic Games\FootballManager26" && GameLocator.JsonField(epic, "AppName") == "Sage");

        // ------------------------------------------------ download-page parsing
        var ts = "{\"namespace\":\"BepInEx\",\"name\":\"BepInExPack_FootballManager26\",\"latest\":{\"version_number\":\"6.0.738\",\"download_url\":\"https://thunderstore.io/package/download/BepInEx/BepInExPack_FootballManager26/6.0.738/\"}}";
        Check("Thunderstore API → download url", BepInExInstaller.ParseThunderstore(ts) == "https://thunderstore.io/package/download/BepInEx/BepInExPack_FootballManager26/6.0.738/");
        var html = "<div><a href=\"/projects/bepinex_be/752/BepInEx-Unity.Mono-win-x64-6.0.0-be.752%2Bdd0655f.zip\">mono</a>" +
                   "<a href=\"/projects/bepinex_be/752/BepInEx-Unity.IL2CPP-win-x86-6.0.0-be.752%2Bdd0655f.zip\">x86</a>" +
                   "<a href=\"/projects/bepinex_be/752/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.752%2Bdd0655f.zip\">il2cpp x64</a>" +
                   "<a href=\"/projects/bepinex_be/751/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.751%2Babc.zip\">older</a></div>";
        Check("BepInEx builds page → newest IL2CPP win-x64 zip", BepInExInstaller.ParseBuildsPage(html) == "https://builds.bepinex.dev/projects/bepinex_be/752/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.752%2Bdd0655f.zip", BepInExInstaller.ParseBuildsPage(html));
        Check("Unrelated page → no url", BepInExInstaller.ParseBuildsPage("<html>maintenance</html>") == null && BepInExInstaller.ParseThunderstore("{}") == null);

        // ------------------------------------------------ zip reader (vs. a different zip writer)
        var zf = new Dictionary<string, string> { ["a/b/ünïcode.cfg"] = string.Concat(Enumerable.Repeat("line of text\n", 3000)), ["a/stored.txt"] = "plain", ["empty/"] = null };
        var zpath = MakeZip("reader.zip", zf);
        var outDir = Path.Combine(Tmp, "reader-out");
        Zip.ExtractAll(zpath, outDir);
        Check("Zip reader: deflated file + unicode name", File.ReadAllText(Path.Combine(outDir, "a", "b", "ünïcode.cfg")) == zf["a/b/ünïcode.cfg"]);
        Check("Zip reader: stored file + directory entry", File.ReadAllText(Path.Combine(outDir, "a", "stored.txt")) == "plain" && Directory.Exists(Path.Combine(outDir, "empty")));
        var evil = MakeZip("evil.zip", new Dictionary<string, string> { ["../escaped.txt"] = "nope", ["ok.txt"] = "fine" });
        Zip.ExtractAll(evil, Path.Combine(Tmp, "evil-out"));
        Check("Zip reader blocks path traversal", !File.Exists(Path.Combine(Tmp, "escaped.txt")) && File.Exists(Path.Combine(Tmp, "evil-out", "ok.txt")));
        try { new Zip(Path.Combine(outDir, "a", "stored.txt")).Dispose(); Check("Zip reader rejects non-zip", false); }
        catch (InvalidDataException) { Check("Zip reader rejects non-zip", true); }
        var realSource = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", ".build", "source.zip");
        if (File.Exists(realSource))
        {
            var so = Path.Combine(Tmp, "source-out");
            Zip.ExtractAll(realSource, so);
            Check("Zip reader extracts the real source.zip (made by Info-ZIP)", File.Exists(Path.Combine(so, "ManagerAgent.csproj")) && File.Exists(Path.Combine(so, "src", "Plugin.cs")));
        }
        else Console.WriteLine("      (source.zip not found at " + realSource + " — skipped)");

        // ------------------------------------------------ BepInEx extraction
        var gameA = FakeGame("GameA");
        var plain = MakeZip("BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.752.zip", BepInExLayout(""));
        var top = BepInExInstaller.ExtractBepInEx(plain, gameA);
        Check("Plain BepInEx zip → game folder", File.Exists(Path.Combine(gameA, "winhttp.dll")) && File.ReadAllText(Path.Combine(gameA, "BepInEx", "core", "BepInEx.Unity.IL2CPP.dll")).Length == 5000);
        Check("Reports top-level items for uninstall", top.SequenceEqual(new[] { ".doorstop_version", "BepInEx", "changelog.txt", "doorstop_config.ini", "dotnet", "winhttp.dll" }, StringComparer.OrdinalIgnoreCase), string.Join(",", top));

        var gameB = FakeGame("GameB");
        var tsLayout = BepInExLayout("BepInExPack/");
        tsLayout["manifest.json"] = "{}";
        tsLayout["icon.png"] = "png";
        var pack = MakeZip("BepInEx-BepInExPack_FootballManager26-6.0.738.zip", tsLayout);
        BepInExInstaller.ExtractBepInEx(pack, gameB);
        Check("Thunderstore pack (nested folder) → game folder, extras skipped",
            File.Exists(Path.Combine(gameB, "winhttp.dll")) && File.Exists(Path.Combine(gameB, "BepInEx", "core", "BepInEx.Core.dll")) &&
            !File.Exists(Path.Combine(gameB, "manifest.json")) && !Directory.Exists(Path.Combine(gameB, "BepInExPack")));

        var noLoader = MakeZip("bad1.zip", new Dictionary<string, string> { ["readme.md"] = "hi" });
        try { BepInExInstaller.ExtractBepInEx(noLoader, gameA); Check("Rejects zip without winhttp.dll", false); }
        catch (SetupException) { Check("Rejects zip without winhttp.dll", true); }
        var monoEdition = MakeZip("bad2.zip", new Dictionary<string, string> { ["winhttp.dll"] = "x", ["BepInEx/core/BepInEx.Unity.Mono.dll"] = "x" });
        try { BepInExInstaller.ExtractBepInEx(monoEdition, gameA); Check("Rejects the Mono edition of BepInEx", false); }
        catch (SetupException) { Check("Rejects the Mono edition of BepInEx", true); }

        // ------------------------------------------------ local zip pickup
        var here = AppDomain.CurrentDomain.BaseDirectory;
        var local = Path.Combine(here, "BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.752+dd0655f.zip");
        File.Copy(plain, local, true);
        try { Check("Uses a BepInEx zip placed next to Setup", BepInExInstaller.FindLocalZip() == local, BepInExInstaller.FindLocalZip()); }
        finally { File.Delete(local); }

        // ------------------------------------------------ plugin install / self-test parsing / uninstall
        var st = new GameState(new GameInstall { Folder = gameA, ExeName = "fm-not-running", Store = Store.Other, Title = "Football Manager 26" });
        Directory.CreateDirectory(st.DataDir);
        File.WriteAllText(st.SelfTestFile, "status=OK");
        PluginInstaller.Install(st);
        Check("Installs embedded plugin and clears old check result", File.Exists(st.PluginDll) && new FileInfo(st.PluginDll).Length > 50000 && !File.Exists(st.SelfTestFile));
        Check("Installed plugin version readable", System.Reflection.AssemblyName.GetAssemblyName(st.PluginDll).Version.ToString(3) == SetupInfo.Version);

        string details;
        Check("No check result yet → null", st.SelfTestStatus(out details) == null);
        File.WriteAllText(st.SelfTestFile, "status=FAILED\nversion=0.2.0\nproblem=Ui.HasClass: MissingMethodException: x\nproblem=Ui.OwnText: y\n");
        Check("FAILED result + problems parsed", st.SelfTestStatus(out details) == "FAILED" && details.Contains("Ui.HasClass") && details.Contains("Ui.OwnText"), details);
        File.SetLastWriteTime(st.SelfTestFile, DateTime.Now.AddMinutes(-10));
        Check("Ignores a result from before this launch", st.SelfTestStatus(out details, DateTime.Now) == null);

        File.WriteAllLines(st.MarkerFile, new[] { "# added", ".doorstop_version", "BepInEx", "doorstop_config.ini", "dotnet", "winhttp.dll", "changelog.txt" });
        File.WriteAllText(Path.Combine(st.DataDir, "answers.json"), "{}");
        PluginInstaller.Uninstall(st, removeData: false, removeBepInEx: false);
        Check("Uninstall (keep answers, keep BepInEx)", !Directory.Exists(st.PluginDir) && File.Exists(Path.Combine(st.DataDir, "answers.json")) && File.Exists(Path.Combine(gameA, "winhttp.dll")));
        PluginInstaller.Install(st);
        PluginInstaller.Uninstall(st, removeData: true, removeBepInEx: true);
        Check("Uninstall everything Setup added, nothing else",
            !Directory.Exists(Path.Combine(gameA, "BepInEx")) && !File.Exists(Path.Combine(gameA, "winhttp.dll")) && !Directory.Exists(Path.Combine(gameA, "dotnet")) &&
            File.Exists(Path.Combine(gameA, "fm.exe")) && File.Exists(Path.Combine(gameA, "GameAssembly.dll")));

        // ------------------------------------------------ start & check watcher
        var gameC = FakeGame("GameC", bepinex: true);
        var stC = new GameState(new GameInstall { Folder = gameC, ExeName = "fm-not-running", Store = Store.Other, Title = "FM" });
        Directory.CreateDirectory(stC.DataDir);
        var statuses = new List<string>();
        var since = DateTime.Now;
        var writer = new Thread(() =>
        {
            Thread.Sleep(300);
            File.WriteAllText(stC.LogFile, "[Message:   BepInEx] Running Cpp2IL to generate dummy assemblies\n");
            Thread.Sleep(2500);
            File.AppendAllText(stC.LogFile, "[Message:   BepInEx] Chainloader initialized\n[Info   :   BepInEx] Loading [Manager Agent 0.2.0]\n");
            Thread.Sleep(2500);
            File.WriteAllText(stC.SelfTestFile, "status=OK\nversion=0.2.0\n");
        });
        writer.Start();
        string d2;
        var r = GameRunner.Watch(stC, since, s => statuses.Add(s), CancellationToken.None, out d2);
        writer.Join();
        Check("Watch → OK when the plugin reports success", r == WatchResult.Ok, r.ToString());
        Check("Watch shows friendly progress", statuses.Any(s => s.Contains("first time only")) && statuses.Any(s => s.Contains("Manager Agent")), string.Join(" / ", statuses));

        File.Delete(stC.SelfTestFile);
        since = DateTime.Now;
        File.WriteAllText(stC.LogFile, "[Error  :   BepInEx] Could not load [Manager Agent 0.2.0] because it has missing dependencies\n[Message:   BepInEx] Chainloader startup complete\n");
        GameRunner.NotLoadedGraceSeconds = 1;
        r = GameRunner.Watch(stC, since, s => { }, CancellationToken.None, out d2);
        Check("Watch → NotLoaded when the plugin never reports (and quotes the log)", r == WatchResult.NotLoaded && d2.Contains("Could not load [Manager Agent"), r + " / " + d2);

        since = DateTime.Now;
        File.WriteAllText(stC.SelfTestFile, "status=FAILED\nproblem=VisualElement.Add missing\n");
        r = GameRunner.Watch(stC, since, s => { }, CancellationToken.None, out d2);
        Check("Watch → Failed with the reason", r == WatchResult.Failed && d2.Contains("VisualElement.Add"), r + " / " + d2);

        var cts = new CancellationTokenSource(1500);
        File.Delete(stC.SelfTestFile);
        File.Delete(stC.LogFile);
        try { GameRunner.Watch(stC, DateTime.Now, s => { }, cts.Token, out d2); Check("Watch can be cancelled", false); }
        catch (OperationCanceledException) { Check("Watch can be cancelled", true); }
    }
}
