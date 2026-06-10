using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;

namespace StS2Sim;

internal static class Program
{
    private static readonly string GameDir =
        Environment.GetEnvironmentVariable("STS2_GAME_DIR")
        ?? @"C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64";

    private static async Task<int> Main(string[] args)
    {
        InstallAssemblyResolver();
        GodotShims.Apply();
        return await BootstrapAndRun(args);
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
