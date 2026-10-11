using System.Diagnostics;
using System.Globalization;

namespace BmwBenchmarkAutomator;

public sealed class PresentMonStats
{
    public double AvgFps { get; set; }
    public double MaxFps { get; set; }
    public double Low1Fps { get; set; }
    public double Low01Fps { get; set; }
    public double AvgFpsRaw { get; set; }
    public int Frames { get; set; }
    public int FramesRaw { get; set; }
    public double TrimHeadSec { get; set; }
    public double TrimTailSec { get; set; }
    public string CsvPath { get; set; } = "";
}

/// Независимый захват кадров через PresentMon (ETW) — перекрёстная проверка цифр с экрана.
public sealed class PresentMonRunner
{
    private readonly PresentMonOptions _opt;
    private Process? _proc;
    private string _csvPath = "";

    public PresentMonRunner(PresentMonOptions opt) => _opt = opt;

    private static string? ResolveExe(string configured)
    {
        var p = Environment.ExpandEnvironmentVariables(configured);
        if (File.Exists(p)) return Path.GetFullPath(p);
        var near = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Path.GetFileName(p)));
        if (File.Exists(near)) return near;
        var cwd = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), Path.GetFileName(p)));
        if (File.Exists(cwd)) return cwd;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var cand = Path.Combine(dir.Trim(), Path.GetFileName(p));
            if (File.Exists(cand)) return cand;
        }
        return null;
    }

    public bool Start(string gameProcessName)
    {
        try
        {
            var exe = ResolveExe(_opt.Path);
            if (exe == null)
            {
                Log.Warn($"PresentMon не найден («{_opt.Path}») — внешняя статистика кадров недоступна. " +
                         "Скачайте PresentMon-1.9.x-x64.exe и положите в tools/PresentMon.exe.");
                return false;
            }

            var dir = Path.GetFullPath("pm-capture");
            Directory.CreateDirectory(dir);
            _csvPath = Path.Combine(dir, $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

            var pn = gameProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? gameProcessName : gameProcessName + ".exe";

            _proc = Process.Start(new ProcessStartInfo(exe)
            {
                // флаги актуальны для PresentMon 1.9.x
                Arguments = $"-process_name {pn} -stop_existing_session -output_file \"{_csvPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            Log.Info($"PresentMon запущен (ETW-захват кадров процесса {pn}) → {_csvPath}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"PresentMon не запущен: {ex.Message}");
            return false;
        }
    }

    public void EnsureStopped()
    {
        try
        {
            if (_proc != null && !_proc.HasExited)
            {
                _proc.Kill(entireProcessTree: true);
                _proc.WaitForExit(3000);
            }
        }
        catch { }
        foreach (var p in Process.GetProcessesByName("PresentMon"))
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            p.Dispose();
        }
    }

    public PresentMonStats? StopAndParse(double trimHeadSec, double trimTailSec)
    {
        EnsureStopped();

        if (_csvPath == "" || !File.Exists(_csvPath))
        {
            Log.Warn("PresentMon CSV не создан (ETW требует прав администратора — запустите терминал от админа).");
            return null;
        }

        Thread.Sleep(1500); // даём дописать буфер

        try
        {
            var lines = File.ReadAllLines(_csvPath);
            if (lines.Length < 2) { Log.Warn("PresentMon CSV пуст."); return null; }

            var header = lines[0].Split(',').Select(h => h.Trim('"', ' ')).ToArray();
            int iMs = Col(header, "msBetweenPresents", "msBetweenDisplayChange");
            int iT = Col(header, "TimeInSeconds");
            if (iMs < 0) { Log.Warn("В CSV нет msBetweenPresents — другая версия PresentMon CLI?"); return null; }

            var rows = new List<(double T, double Ms)>();
            for (int i = 1; i < lines.Length; i++)
            {
                var c = lines[i].Split(',');
                if (c.Length <= iMs) continue;
                if (!double.TryParse(c[iMs], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)
                    || ms <= 0 || ms >= 1000) continue;
                double t = iT >= 0 && double.TryParse(c[iT], NumberStyles.Float, CultureInfo.InvariantCulture, out var tt) ? tt : i;
                rows.Add((t, ms));
            }

            if (rows.Count < 10) { Log.Warn($"PresentMon: слишком мало кадров ({rows.Count})."); return null; }

            var s = new PresentMonStats { CsvPath = _csvPath, TrimHeadSec = trimHeadSec, TrimTailSec = trimTailSec };
            s.FramesRaw = rows.Count;
            s.AvgFpsRaw = 1000.0 / rows.Average(r => r.Ms);

            // Отрезаем меню до старта и экран результатов после — остаётся сам пробег
            double tMin = rows.Min(r => r.T), tMax = rows.Max(r => r.T);
            var data = rows.Where(r => r.T >= tMin + trimHeadSec && r.T <= tMax - trimTailSec).ToList();
            if (data.Count < 10) data = rows;

            s.Frames = data.Count;
            s.AvgFps = 1000.0 / data.Average(r => r.Ms);
            var fps = data.Select(r => 1000.0 / r.Ms).OrderBy(x => x).ToList();
            s.MaxFps = fps[^1];
            s.Low1Fps = fps.Take(Math.Max(1, fps.Count / 100)).Average();
            s.Low01Fps = fps.Take(Math.Max(1, fps.Count / 1000)).Average();
            return s;
        }
        catch (Exception ex)
        {
            Log.Warn($"Разбор PresentMon CSV: {ex.Message}");
            return null;
        }
    }

    private static int Col(string[] header, params string[] names)
    {
        for (int i = 0; i < header.Length; i++)
            foreach (var n in names)
                if (header[i].Equals(n, StringComparison.OrdinalIgnoreCase))
                    return i;
        return -1;
    }
}
