using BmwBenchmarkAutomator;
using System.Diagnostics;
try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
try { Interop.SetProcessDPIAware(); } catch { } // физические пиксели для захвата/кликов

try
{
    return await App.MainAsync(args);
}
catch (Exception ex)
{
    Log.Error("Критическая ошибка: " + ex.Message);
    Log.Error(ex.ToString());
    return 1;
}

public static class App
{
    public static async Task<int> MainAsync(string[] args)
    {
        var cmd = args.Length > 0 && !args[0].StartsWith('-') ? args[0].ToLowerInvariant() : "run";
        var configPath = "config.json";
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] is "--config" or "-c") configPath = args[i + 1];

        Log.Plain("═══════════════════════════════════════════════════");
        Log.Plain("   Black Myth: Wukong Benchmark Tool — Automator   ");
        Log.Plain("═══════════════════════════════════════════════════");

        var cfg = ToolConfig.Load(configPath);

        switch (cmd)
        {
            case "run": return await RunAsync(cfg);
            case "settings": return SettingsCmd(cfg, args);   
            case "restore": return RestoreCmd(cfg);         
            case "hw": return HwCmd();
            case "inspect": return await InspectAsync(cfg);
            default: return Usage();
        }
    }

    private static async Task<int> RunAsync(ToolConfig cfg)
    {
        var ctx = EnvironmentContext.Discover(cfg);
        var hw = HardwareInfo.Collect();

        var ocr = new OcrHelper();
        if (cfg.Ocr.Enabled) ocr.Init();

        // Шаги 2–3: бэкап оригинала + генерация профилей.
        // При повторных запусках, если templates/ уже заполнен, всё пропускается.
        SettingsApplier.EnsureTemplates(cfg, ctx);

        var results = new Dictionary<string, TestResult>();
        var runner = new BenchmarkRunner(cfg, ctx, ocr);

        try
        {
            foreach (var name in new[] { "CPU", "GPU" })
            {
                results[name] = await runner.RunProfileAsync(name);
                if (name == "CPU")
                {
                    Log.Info("Пауза 15 с между прогонами (сброс состояний)…");
                    await Task.Delay(15000);
                }
            }
        }
        finally
        {
            // Шаг 5: возврат оригинала — гарантированно, даже если второй прогон упал
            SettingsApplier.RestoreOriginal(ctx);
        }

        ReportWriter.Write(cfg, ctx, hw, results);
        return 0;
    }

    private static int SettingsCmd(ToolConfig cfg, string[] args)
    {
        var which = args.Length > 1 ? args[1].ToLowerInvariant() : "";
        if (which is not ("cpu" or "gpu"))
        {
            Log.Error("Укажите профиль: settings cpu | settings gpu");
            return 2;
        }
        var ctx = EnvironmentContext.Discover(cfg);
        SettingsApplier.EnsureTemplates(cfg, ctx);
        SettingsApplier.ApplyProfileFile(ctx, which.ToUpperInvariant());
        return 0;
    }

    private static int RestoreCmd(ToolConfig cfg)
    {
        var ctx = EnvironmentContext.Discover(cfg);
        SettingsApplier.RestoreOriginal(ctx);
        return 0;
    }

    private static int HwCmd()
    {
        var hw = HardwareInfo.Collect();
        Log.Section("ХАРАКТЕРИСТИКИ КОМПЬЮТЕРА");
        Log.Plain($"  CPU   : {hw.Cpu}");
        Log.Plain($"  RAM   : {hw.Ram}");
        foreach (var g in hw.Gpus)
            Log.Plain($"  GPU   : {g.Name} (драйвер {g.Driver}{(g.VramGb > 0 ? $", {g.VramGb:0.#} ГБ" : "")})");
        Log.Plain($"  ОС    : {hw.Os}");
        Log.Plain($"  Экран : {hw.Screen}");
        return 0;
    }

    /// Калибровка: запускаем бенчмарк, пользователь вручную меняет настройки и проходит тест,
    /// инструмент показывает, какие файлы и ключи реестра изменились.
    private static async Task<int> InspectAsync(ToolConfig cfg)
    {
        var ctx = EnvironmentContext.Discover(cfg);
        var sources = ctx.GetWatchDirs(cfg).ToList();
        var before = FileSnapshot.Capture(sources);
        var beforeReg = RegistryStore.ReadValues(ctx.RegistryKeyPath);

        Log.Section("РЕЖИМ КАЛИБРОВКИ (inspect)");
        Log.Info("Запускаю бенчмарк. Вручную выставьте интересующие настройки, пройдите тест и закройте инструмент.");
        Log.Info("Затем будет показано, какие файлы и ключи реестра изменились.");
        Process.Start(new ProcessStartInfo($"steam://rungameid/{cfg.SteamAppId}") { UseShellExecute = true });

        var names = cfg.Launch.ProcessNames.ToList();
        if (ctx.ExecutablePath != null)
            names.Insert(0, Path.GetFileNameWithoutExtension(ctx.ExecutablePath));

        var proc = ProcessHelper.WaitForAny(names, TimeSpan.FromSeconds(cfg.Launch.StartupTimeoutSec));
        if (proc == null) { Log.Error("Процесс бенчмарка не найден."); return 1; }
        Log.Info($"Процесс: {proc.ProcessName} (PID {proc.Id}). Ожидаю завершения…");

        while (true)
        {
            bool exited; try { exited = proc.HasExited; } catch { break; }
            if (exited) break;
            await Task.Delay(1000);
        }
        await Task.Delay(TimeSpan.FromSeconds(cfg.Launch.GracePeriodAfterExitSec));

        Log.Section("ИЗМЕНЁННЫЕ / НОВЫЕ ФАЙЛЫ");
        foreach (var f in FileSnapshot.Capture(sources).ChangedFiles(before).Take(60))
            Log.Info(f);

        Log.Section("ИЗМЕНЁННЫЕ КЛЮЧИ РЕЕСТРА");
        foreach (var (name, val) in RegistryStore.ReadValues(ctx.RegistryKeyPath))
        {
            beforeReg.TryGetValue(name, out var old);
            if (!Equals(old, val)) Log.Info($"{name}: {old ?? "<нет>"} → {val}");
        }
        Log.Info("Перенесите нужные значения в config.json → Profiles → CPU/GPU (Ini / RegistryDwords).");
        return 0;
    }

    private static int Usage()
    {
        Log.Plain("""
            Использование:
              bmw-bench run                  полный автопрогон (CPU + GPU) и отчёт
              bmw-bench settings [cpu|gpu|all]
                                             только применить настройки профиля
              bmw-bench inspect              калибровка: какие файлы/ключи реестра меняет бенчмарк
              bmw-bench hw                   характеристики компьютера
              --config <path>                путь к config.json
            """);
        return 2;
    }
}