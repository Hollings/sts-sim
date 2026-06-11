using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace StS2Sim;

internal static class Program
{
    private static readonly string GameDir = ResolveGameDir();

    private static async Task<int> Main(string[] args)
    {
        if (!File.Exists(Path.Combine(GameDir, "sts2.dll")))
        {
            Console.Error.WriteLine("ERROR: Couldn't find Slay the Spire 2.");
            Console.Error.WriteLine($"       Looked for sts2.dll in: {GameDir}");
            Console.Error.WriteLine();
            Console.Error.WriteLine("       Install the game via Steam, or point the sim at it manually by");
            Console.Error.WriteLine("       setting the STS2_GAME_DIR environment variable to the game's");
            Console.Error.WriteLine(@"       data_sts2_windows_x86_64 folder, e.g.:");
            Console.Error.WriteLine(@"       D:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64");
            HoldWindowOpenIfDoubleClicked();
            return 1;
        }

        InstallAssemblyResolver();
        GodotShims.Apply();
        return await BootstrapAndRun(args);
    }

    /// <summary>
    /// Find the game's managed-assembly folder. Order: explicit STS2_GAME_DIR
    /// override → default Steam paths → every Steam library folder from the
    /// registry + libraryfolders.vdf (covers games installed on other drives).
    /// </summary>
    private static string ResolveGameDir()
    {
        var env = Environment.GetEnvironmentVariable("STS2_GAME_DIR");
        if (!string.IsNullOrEmpty(env)) return env;

        const string gameSubPath = @"steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64";
        var candidates = new List<string>
        {
            @"C:\Program Files (x86)\Steam\" + gameSubPath,
            @"C:\Program Files\Steam\" + gameSubPath,
        };
        foreach (var library in EnumerateSteamLibraries())
            candidates.Add(Path.Combine(library, gameSubPath));

        foreach (var candidate in candidates)
        {
            if (File.Exists(Path.Combine(candidate, "sts2.dll"))) return candidate;
        }
        return candidates[0]; // not found; Main prints the friendly error
    }

    private static IEnumerable<string> EnumerateSteamLibraries()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        string? steamPath = null;
        try
        {
            steamPath = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        }
        catch { /* no registry access / no Steam */ }
        if (string.IsNullOrEmpty(steamPath)) yield break;

        steamPath = steamPath.Replace('/', '\\');
        yield return steamPath;

        // libraryfolders.vdf lists every additional library: "path"  "D:\\SteamLibrary"
        var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;
        string text;
        try { text = File.ReadAllText(vdf); }
        catch { yield break; }
        foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
            yield return m.Groups[1].Value.Replace(@"\\", @"\");
    }

    /// <summary>
    /// When launched by double-click (own console window), an immediate fatal
    /// error would close the window before anyone can read it.
    /// </summary>
    private static void HoldWindowOpenIfDoubleClicked()
    {
        try
        {
            if (!Console.IsInputRedirected)
            {
                Console.Error.WriteLine("\nPress any key to exit...");
                Console.ReadKey(intercept: true);
            }
        }
        catch { /* no console at all */ }
    }

    private static void InstallAssemblyResolver()
    {
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            var candidate = Path.Combine(GameDir, name.Name + ".dll");
            return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
        };
    }

    // Game-types-touching code lives in a separate frame so the JIT only resolves
    // sts2 metadata after our resolver is installed.
    private static async Task<int> BootstrapAndRun(string[] args)
    {
        Console.WriteLine($"Resolving game DLLs from: {GameDir}");
        try
        {
            // First arg "experiment" runs the legacy console comparison instead of the server.
            if (args.Length > 0 && args[0] == "experiment")
            {
                await ExperimentMode.Run();
                return 0;
            }

            // "silent-tests" runs the per-card Silent battery (88+ tests) and
            // returns non-zero if any test crashed the harness.
            if (args.Length > 0 && args[0] == "silent-tests")
                return await SilentTests.SilentTestsRunner.RunAll();

            // "smoke" runs just the fast Ironclad assertion suite (the same one
            // experiment mode runs first) without the long benchmark afterwards.
            if (args.Length > 0 && args[0] == "smoke")
            {
                Harness.Bootstrap();
                await SmokeTests.RunAll();
                return 0;
            }

            // "encounter-sweep" runs one short trial against EVERY encounter in
            // the catalog and reports pass/crash per encounter — the empirical
            // coverage map for encounter mode. Exit 2 if anything crashed.
            if (args.Length > 0 && args[0] == "encounter-sweep")
            {
                Harness.Bootstrap();
                return await EncounterSweep.RunAll();
            }

            // "policy-bench" measures play-policy uplift on a pinned deck ×
            // encounter suite (win rate is a lower bound: higher = better).
            if (args.Length > 0 && args[0] == "policy-bench")
            {
                Harness.Bootstrap();
                return await PolicyBench.RunAll();
            }

            // "character-sweep" runs one starter-deck trial per character
            // (dummy + real fight). Exit 2 if any character crashes.
            if (args.Length > 0 && args[0] == "character-sweep")
            {
                Harness.Bootstrap();
                return await CharacterSweep.RunAll();
            }

            // "char-tests" runs the Regent/Necrobinder/Defect card batteries
            // (stars, Osty, orbs — the fragile character mechanics). Exit 2 on
            // harness crashes.
            if (args.Length > 0 && args[0] == "char-tests")
                return await CharTests.CharTestsRunner.RunAll();

            // "card-sweep" plays EVERY playable card once (base + upgraded)
            // and reports crashes/hangs — the per-card coverage map. Exit 2
            // on any failure.
            if (args.Length > 0 && args[0] == "card-sweep")
            {
                Harness.Bootstrap();
                return await CardSweep.RunAll();
            }

            Harness.Bootstrap();
            var webRoot = ResolveWebRoot();
            var port = int.TryParse(Environment.GetEnvironmentVariable("STS2SIM_PORT"), out var p) ? p : 52324;
            var server = new SimServer(port, webRoot);
            server.Start();

            var url = $"http://localhost:{port}/";
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch { /* couldn't open browser; user can copy from console */ }

            Console.WriteLine($"\n  Open {url} in your browser. Press Ctrl+C to quit.");
            await Task.Delay(Timeout.Infinite);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FATAL: " + ex);
            HoldWindowOpenIfDoubleClicked();
            return 1;
        }
    }

    private static string ResolveWebRoot()
    {
        // www/ next to the exe is what `dotnet build` places. In dev (dotnet run),
        // walk up to find the project's www/ directory.
        var exeDir = AppContext.BaseDirectory;
        var local = Path.Combine(exeDir, "www");
        if (Directory.Exists(local)) return local;

        var probe = exeDir;
        for (int i = 0; i < 6 && probe != null; i++, probe = Path.GetDirectoryName(probe))
        {
            var candidate = Path.Combine(probe, "www");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "index.html")))
                return candidate;
        }
        throw new DirectoryNotFoundException("Could not find www/ folder for static assets.");
    }
}
