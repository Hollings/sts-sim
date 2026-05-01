using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
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
        return await BootstrapAndRun();
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
    private static async Task<int> BootstrapAndRun()
    {
        Console.WriteLine($"Resolving game DLLs from: {GameDir}");
        try
        {
            await Sim.RunSmokeTest();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FATAL: " + ex);
            return 1;
        }
    }
}
