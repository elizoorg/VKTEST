using System.Text.RegularExpressions;
using System.Diagnostics;
using Microsoft.Win32;

namespace BmwBenchmarkAutomator;

public static class SteamLocator
{
    private static readonly Regex PathRegex =
        new(@"""path""\s*""([^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex InstallDirRegex =
        new(@"""installdir""\s*""([^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string? FindSteamPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string p && Directory.Exists(p)) return p;
        }
        catch { /* ignore */ }

        var def = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        return Directory.Exists(def) ? def : null;
    }

    public static string? FindAppInstallDir(string steamPath, int appId)
    {
        return "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Black Myth Wukong Benchmark Tool\\b1\\Binaries\\Win64";
        Log.Info($"FindAppInstallDir: steamPath='{steamPath}', appId={appId}");

        var steamapps = new List<string> { Path.Combine(steamPath, "steamapps") };
        var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        Log.Info($"Looking for libraryfolders.vdf at: {vdf}");
        if (File.Exists(vdf))
        {
            var vdfText = File.ReadAllText(vdf);
            foreach (Match m in PathRegex.Matches(vdfText))
            {
                var libraryPath = m.Groups[1].Value.Replace(@"\\", @"\");
                var saPath = Path.Combine(libraryPath, "steamapps");
                Log.Info($"Found library folder in vdf: {libraryPath} -> adding {saPath}");
                steamapps.Add(saPath);
            }
        }

        foreach (var sa in steamapps.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Log.Info($"Checking steamapps folder: {sa}");
            var manifest = Path.Combine(sa, $"appmanifest_{appId}.acf");
            Log.Info($"Looking for manifest: {manifest}");
            if (!File.Exists(manifest))
            {
                Log.Info($"Manifest not found: {manifest}");
                continue;
            }

            var manifestText = File.ReadAllText(manifest);
            var m2 = InstallDirRegex.Match(manifestText);
            if (!m2.Success)
            {
                Log.Info($"installdir not found in manifest: {manifest}");
                continue;
            }

            var dir = Path.Combine(sa, "common", m2.Groups[1].Value);
            Log.Info($"Resolved install dir candidate: {dir}");
            if (Directory.Exists(dir))
            {
                Log.Info($"Install dir exists, returning: {dir}");
                return dir;
            }
            Log.Info($"Directory does not exist: {dir}");
        }

        Log.Info($"FindAppInstallDir: install dir not found for appId={appId}");
        return null;
    }

    /// Эвристика: exe с "bench" в имени → b1.exe → самый крупный exe в корне установки.
    public static string? GuessBenchmarkExe(string installDir)
    {
        try
        {
            var exes = Directory.EnumerateFiles(installDir, "*.exe", SearchOption.TopDirectoryOnly).ToList();
            return exes.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f)
                                            .Contains("bench", StringComparison.OrdinalIgnoreCase))
                ?? exes.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f)
                                            .Equals("b1", StringComparison.OrdinalIgnoreCase))
                ?? exes.OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
        }
        catch { return null; }
    }
}