using System.Text.Json;

namespace BmwBenchmarkAutomator;

public static class ReportWriter
{
    public static void Write(ToolConfig cfg, EnvironmentContext ctx, HardwareReport hw,
        Dictionary<string, TestResult> results)
    {
        Log.Section("ИТОГОВЫЙ ОТЧЁТ");
        var lines = BuildLines(cfg, ctx, hw, results);
        foreach (var l in lines) Log.Plain(l);

        var txtPath = Path.GetFullPath("benchmark-report.txt");
        File.WriteAllLines(txtPath, lines);

        var jsonPath = Path.GetFullPath("benchmark-report.json");
        File.WriteAllText(jsonPath,
            JsonSerializer.Serialize(BuildJson(cfg, ctx, hw, results), ToolConfig.JsonOpts));

        Log.Plain("");
        Log.Success($"Текстовый отчёт: {txtPath}");
        Log.Success($"JSON-отчёт: {jsonPath}");
    }

    private static List<string> BuildLines(ToolConfig cfg, EnvironmentContext ctx, HardwareReport hw,
        Dictionary<string, TestResult> results)
    {
        var lines = new List<string>
        {
            $"════════ ИТОГОВЫЙ ОТЧЁТ — {cfg.GameName} ════════",
            $"Сформирован: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            "",
            "── Характеристики компьютера ──",
            $"  CPU      : {hw.Cpu}",
            $"  RAM      : {hw.Ram}",
        };
        for (var i = 0; i < hw.Gpus.Count; i++)
        {
            var g = hw.Gpus[i];
            lines.Add($"  GPU {i}    : {g.Name} (драйвер {g.Driver}{(g.VramGb > 0 ? $", VRAM {g.VramGb:0.#} ГБ" : "")})");
        }
        lines.Add($"  ОС       : {hw.Os}");
        lines.Add($"  Экран    : {hw.Screen}");
        lines.Add($"  Бенчмарк : {ctx.InstallDir ?? "<не найден>"}");
        lines.Add("");

        foreach (var r in results.Values)
        {
            lines.Add($"════════ {r.Profile}-тест ════════");
            lines.Add($"  Профиль      : {r.Description}");
            lines.Add($"  Длительность : {r.DurationSec:0} с ({r.ExitInfo})");
            if (r.ScreenshotPath != null) lines.Add($"  Скриншот     : {r.ScreenshotPath}");
            lines.Add("");

            if (r.Screen != null)
            {
                lines.Add("  ── Результаты бенчмарка (распознаны с финального экрана, OCR) ──");
                foreach (var m in r.Screen.Metrics)
                    lines.Add($"    {m.Key,-30} {m.Value:0.##}");
                lines.Add("    ── сырой текст с экрана ──");
                foreach (var l in r.Screen.Lines)
                    lines.Add($"    | {l}");
                lines.Add("");
            }

            if (r.PmStats != null)
            {
                var p = r.PmStats;
                lines.Add("  ── Независимый замер кадров (PresentMon, ETW) ──");
                lines.Add($"    Average FPS                  {p.AvgFps:0.##}");
                lines.Add($"    Max FPS                      {p.MaxFps:0.##}");
                lines.Add($"    1% Low FPS                   {p.Low1Fps:0.##}");
                lines.Add($"    0.1% Low FPS                 {p.Low01Fps:0.##}");
                lines.Add($"    Кадров в анализе             {p.Frames} (всего {p.FramesRaw})");
                lines.Add($"    Average FPS без обрезки      {p.AvgFpsRaw:0.##}");
                lines.Add($"    CSV                          {p.CsvPath}");
                lines.Add("");
            }

            if (r.ResultFiles.Count > 0)
            {
                lines.Add("  ── Файлы, созданные/обновлённые бенчмарком ──");
                foreach (var f in r.ResultFiles)
                {
                    lines.Add($"  Источник: {f.Path}");
                    foreach (var m in f.Metrics)
                        lines.Add($"    {m.Key,-30} {m.Value:0.##}");
                    foreach (var line in f.RelevantLines.Take(15))
                        lines.Add($"    | {line}");
                }
                lines.Add("");
            }
            else if (r.Screen == null && r.PmStats == null)
            {
                lines.Add("  [!] Результаты не получены ни одним из способов (OCR/PresentMon/файлы).");
                lines.Add("      Проверьте: терминал от администратора (ETW), языковой пакет с OCR,");
                lines.Add("      окно бенчмарка на основном мониторе и не перекрыто.");
                lines.Add("");
            }

            lines.Add($"  Настройки профиля {r.Profile}:");
            foreach (var kv in r.Settings.Ini) lines.Add($"    INI {kv.Key} = {kv.Value}");
            foreach (var kv in r.Settings.RegistryDwords) lines.Add($"    REG {kv.Key} = {kv.Value} (DWORD)");
            lines.Add("");
        }
        return lines;
    }

    private static object BuildJson(ToolConfig cfg, EnvironmentContext ctx, HardwareReport hw,
        Dictionary<string, TestResult> results) => new
        {
            GeneratedUtc = DateTime.UtcNow,
            cfg.GameName,
            cfg.SteamAppId,
            ctx.InstallDir,
            Hardware = new { hw.Cpu, hw.Ram, hw.Os, hw.Screen, Gpus = hw.Gpus },
            Tests = results.Values.Select(r => new
            {
                r.Profile,
                r.Description,
                r.StartedUtc,
                r.DurationSec,
                r.ExitInfo,
                r.ScreenshotPath,
                Screen = r.Screen == null
                    ? null
                    : new { r.Screen.Text, Lines = r.Screen.Lines, Metrics = r.Screen.Metrics },
                PresentMon = r.PmStats,
                ResultFiles = r.ResultFiles.Select(f => new { f.Path, f.Metrics, f.RelevantLines }),
                Settings = new { Ini = r.Settings.Ini, RegistryDwords = r.Settings.RegistryDwords },
            }),
        };
}
