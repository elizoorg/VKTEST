

namespace BmwBenchmarkAutomator;

public sealed class EnvironmentContext
{
    public string? SteamPath { get; set; }
    public string? InstallDir { get; set; }
    public string? ExecutablePath { get; set; }
    public string IniPath { get; set; } = "";
    public string RegistryKeyPath { get; set; } = "";

    // EnvironmentContext: новое свойство
    public string? GameDir { get; set; }   // ...\Black Myth Wukong Benchmark Tool\b1

    public List<(string Dir, int Depth)> GetWatchDirs(ToolConfig cfg)
    {
        var list = new List<(string, int)>();
        foreach (var d in cfg.Results.WatchDirs)
        {
            var p = Environment.ExpandEnvironmentVariables(d);
            if (Directory.Exists(p)) list.Add((p, cfg.Results.WatchDirDepth));
        }
        if (cfg.Results.IncludeInstallDir && InstallDir != null && Directory.Exists(InstallDir)
            && !list.Any(x => x.Item1.Equals(InstallDir, StringComparison.OrdinalIgnoreCase)))
            list.Add((InstallDir, cfg.Results.InstallDirDepth));
        return list;
    }

    public static EnvironmentContext Discover(ToolConfig cfg)
    {
        var ctx = new EnvironmentContext();

        Log.Section("ОБНАРУЖЕНИЕ ОКРУЖЕНИЯ");
        ctx.SteamPath = SteamLocator.FindSteamPath();
        if (ctx.SteamPath == null)
            Log.Warn("Steam не найден — steam:// запуск может не сработать.");
        else
            Log.Info($"Steam: {ctx.SteamPath}");

        if (ctx.SteamPath != null)
            ctx.InstallDir = SteamLocator.FindAppInstallDir(ctx.SteamPath, cfg.SteamAppId);

        if (ctx.InstallDir != null)
        {
            Log.Info($"Папка бенчмарка: {ctx.InstallDir}");
            ctx.ExecutablePath = SteamLocator.GuessBenchmarkExe(ctx.InstallDir);
            Log.Info($"EXE бенчмарка: {ctx.ExecutablePath ?? "<не определён — используется steam:// запуск>"}");
        }
        else
        {
            Log.Warn($"Установка AppID {cfg.SteamAppId} не найдена в библиотеках Steam. " +
                     "Запустите Benchmark Tool один раз вручную и проверьте config.json.");
        }

        // в Discover, ПОСЛЕ того как ctx.InstallDir уже найден:
        if (ctx.InstallDir != null)
        {
            var dir = new DirectoryInfo(ctx.InstallDir);
            ctx.GameDir = dir.Parent?.Parent?.FullName;   // Win64 → Binaries → b1
            Log.Info($"GAME_DIR: {ctx.GameDir ?? "<не определён>"}");
        }

        // ваша строка превращается в конвейер из двух замен:
        var iniCandidates = cfg.Settings.IniCandidates
            .Select(c => c.Replace("{GAME_DIR}", ctx.GameDir ?? ""))
            .Select(Environment.ExpandEnvironmentVariables)
            .ToList();
        ctx.IniPath = iniCandidates.FirstOrDefault(File.Exists) ?? iniCandidates[0];
        Log.Info($"INI настроек: {ctx.IniPath} ({(File.Exists(ctx.IniPath) ? "найден" : "будет создан при применении")})");

        ctx.RegistryKeyPath = RegistryStore.FindExisting(cfg.Settings.RegistryKeys) ?? cfg.Settings.RegistryKeys[0];
        Log.Info($"Ветка реестра: {ctx.RegistryKeyPath} ({(RegistryStore.Exists(ctx.RegistryKeyPath) ? "найдена" : "будет создана")})");

        return ctx;
    }
}