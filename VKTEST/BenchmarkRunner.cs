using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace BmwBenchmarkAutomator;

public sealed class TestResult
{
    public string Profile { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public double DurationSec { get; set; }
    public string ExitInfo { get; set; } = "";
    public string? ScreenshotPath { get; set; }
    public ScreenResult? Screen { get; set; }
    public PresentMonStats? PmStats { get; set; }
    public List<ParsedResultFile> ResultFiles { get; set; } = new();
    public Profile Settings { get; set; } = new();
}

public sealed class ScreenResult
{
    public string Text { get; set; } = "";
    public List<string> Lines { get; set; } = new();
    public List<Metric> Metrics { get; set; } = new();
}

public sealed class BenchmarkRunner
{
    private readonly ToolConfig _cfg;
    private readonly EnvironmentContext _ctx;
    private readonly OcrHelper _ocr;

    // состояние детектора «статичного экрана»
    private double[]? _prevGray;
    private int _staticCount;

    // момент старта PresentMon — для точной обрезки головы захвата
    private DateTime _pmStartUtc;

    public BenchmarkRunner(ToolConfig cfg, EnvironmentContext ctx, OcrHelper ocr)
    {
        _cfg = cfg;
        _ctx = ctx;
        _ocr = ocr;
    }

    public async Task<TestResult> RunProfileAsync(string profileName)
    {
        var profile = _cfg.Profiles[profileName];
        var launch = _cfg.Launch;
        _prevGray = null;
        _staticCount = 0;

        Log.Section($"ПРОГОН: {profileName}-ТЕСТ");
        Log.Info(profile.Description);

        // 1. Чистим «зависшие» процессы от прошлых запусков
        ProcessHelper.KillAll(launch.ProcessNames);

        // 2. Шаг 4 схемы: подменяем GameUserSettings.ini файлом нужного профиля (templates/)
        SettingsApplier.ApplyProfileFile(_ctx, profileName);
        if (profile.RegistryDwords.Count > 0) // опционально, по умолчанию пусто
        {
            RegistryStore.ApplyDwords(_ctx.RegistryKeyPath, profile.RegistryDwords);
        }

        // 3. Снапшот файловой системы «до» — строго ПОСЛЕ подмены INI,
        //    иначе сама подмена попадёт в дифф как «изменённый файл»
        var sources = _ctx.GetWatchDirs(_cfg).ToList();
        var before = FileSnapshot.Capture(sources);

        // 4. PresentMon: независимый ETW-захват кадров
        var pm = new PresentMonRunner(_cfg.PresentMon);
        _pmStartUtc = DateTime.UtcNow;
        var pmStarted = _cfg.PresentMon.Enabled && pm.Start(launch.ProcessNames[0]);


        PresentMonStats? pmStats = null;
        try
        {
            // 5. Запуск
            if (launch.Mode.Equals("Executable", StringComparison.OrdinalIgnoreCase) && _ctx.ExecutablePath != null)
            {
                Log.Info($"Запуск напрямую: {_ctx.ExecutablePath} {launch.Args}");
                Process.Start(new ProcessStartInfo(_ctx.ExecutablePath)
                { Arguments = launch.Args, UseShellExecute = false });
            }
            else
            {
                if (launch.Mode.Equals("Executable", StringComparison.OrdinalIgnoreCase))
                    Log.Warn("EXE не найден — откат к запуску через Steam.");
                Log.Info($"Запуск через Steam: steam://rungameid/{_cfg.SteamAppId}");
                Process.Start(new ProcessStartInfo($"steam://rungameid/{_cfg.SteamAppId}")
                { UseShellExecute = true });
            }

            var names = launch.ProcessNames.ToList();
            if (_ctx.ExecutablePath != null)
                names.Insert(0, Path.GetFileNameWithoutExtension(_ctx.ExecutablePath));
            await Task.Delay(TimeSpan.FromSeconds(3));
            var proc = ProcessHelper.WaitForAny(names, TimeSpan.FromSeconds(launch.StartupTimeoutSec))
                ?? throw new TimeoutException($"Процесс бенчмарка не найден за {launch.StartupTimeoutSec} с. " +
                                              "Проверьте Launch.ProcessNames в config.json (ProcMon/Диспетчер задач).");
            proc.Refresh();
            Log.Success($"Процесс найден: {proc.ProcessName} (PID {proc.Id})");

            // 6. Стартовая последовательность: ЛКМ (вступительный экран) →
            //    «Тест быстродействия» → «Подтвердить»
            await TryStartBenchmarkFromMenuAsync(proc);

            // Отсчёт времени прогона — от клика «Подтвердить»
            var startedUtc = DateTime.UtcNow;
            var sw = Stopwatch.StartNew();

            // PresentMon: обрезаем всё, что было до момента подтверждения (меню, загрузки)
            var pmHeadTrim = Math.Max(_cfg.PresentMon.TrimHeadSec,
                (DateTime.UtcNow - _pmStartUtc).TotalSeconds + 3);

            // 7. Основной цикл: ждём завершения / таймаут / экран результатов
            ScreenResult? screen = null;
            string? screenshot = null;
            string exitInfo = "завершено";
            var pollMs = (int)(_cfg.Ocr.PollIntervalSec * 1000);
            var screenshotsDir = Path.GetFullPath("screenshots");

            while (true)
            {
                bool exited;
                try { exited = proc.HasExited; } catch { exited = true; }

                if (!exited && sw.Elapsed.TotalSeconds > launch.BenchmarkTimeoutSec)
                {
                    Log.Warn($"Таймаут {launch.BenchmarkTimeoutSec} с — принудительно завершаем.");
                    screenshot = ScreenshotHelper.SaveTo(screenshotsDir,
                        $"{profileName}_timeout_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png");
                    ProcessHelper.KillAll(names);
                    exitInfo = "остановлено по таймауту";
                    break;
                }

                // Экран результатов ищем только после ResultsStartAfterSec (пробег = 2:30)
                // и только на СТАТИЧНОЙ картинке (детект статики дешёвый, OCR — редко).
                if (screen == null && !exited && _cfg.Ocr.Enabled && _ocr.Available
                    && sw.Elapsed.TotalSeconds >= _cfg.Ocr.ResultsStartAfterSec && IsScreenStatic())
                {
                    var candidate = await TryReadResultsScreenAsync();
                    if (candidate != null)
                    {
                        screen = candidate;
                        screenshot = ScreenshotHelper.SaveTo(screenshotsDir,
                            $"{profileName}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png");
                        Log.Success("Экран результатов распознан: " +
                                    string.Join(", ", screen.Metrics.Select(m => $"{m.Key} = {m.Value:0.##}")));
                        exitInfo = "результаты распознаны с финального экрана";
                        if (launch.CloseWindowOnResults) TryGracefulClose(proc);
                        break;
                    }
                }

                if (exited)
                {
                    if (_cfg.Ocr.Enabled && _ocr.Available)
                    {
                        Log.Info("Процесс завершился — пытаюсь распознать экран результатов…");
                        var until = DateTime.UtcNow.AddSeconds(_cfg.Ocr.ResultsGiveUpAfterExitSec);
                        while (DateTime.UtcNow < until)
                        {
                            screen = await TryReadResultsScreenAsync();
                            if (screen != null)
                            {
                                screenshot = ScreenshotHelper.SaveTo(screenshotsDir,
                                    $"{profileName}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png");
                                Log.Success("Экран результатов распознан после завершения процесса.");
                                exitInfo = "результаты распознаны с финального экрана";
                                break;
                            }
                            await Task.Delay(pollMs);
                        }
                    }
                    if (screen == null)
                    {
                        try { exitInfo = $"процесс завершился (код {proc.ExitCode})"; }
                        catch { exitInfo = "процесс завершился"; }
                    }
                    break;
                }

                await Task.Delay(pollMs);
            }
            Log.Info(exitInfo);

            // 8. Останавливаем PresentMon и считаем метрики кадров
            pmStats = pmStarted ? pm.StopAndParse(pmHeadTrim, _cfg.PresentMon.TrimTailSec) : null;
            if (pmStats != null)
                Log.Info($"PresentMon: avg {pmStats.AvgFps:0.##} FPS, 1% low {pmStats.Low1Fps:0.##}, кадров {pmStats.Frames}");

            if (screen == null && launch.ScreenshotOnFinish && screenshot == null)
                screenshot = ScreenshotHelper.SaveTo(screenshotsDir,
                    $"{profileName}_final_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png");

            // 9. Файловый diff (UE-логи и всё, что тулза всё же пишет)
            await Task.Delay(TimeSpan.FromSeconds(launch.GracePeriodAfterExitSec));
            var changed = FileSnapshot.Capture(sources).ChangedFiles(before);
            Log.Info($"Изменённых/новых файлов за прогон: {changed.Count}");
            var parsed = ResultsScanner.ParseResults(changed);

            return new TestResult
            {
                Profile = profileName,
                Description = profile.Description,
                StartedUtc = startedUtc,
                DurationSec = sw.Elapsed.TotalSeconds,
                ExitInfo = exitInfo,
                ScreenshotPath = screenshot,
                ResultFiles = parsed,
                Settings = profile,
                Screen = screen,
                PmStats = pmStats,
            };
        }
        finally
        {
            // страховка: даже при исключении PresentMon не остаётся висеть
            if (pmStarted) pm.EnsureStopped();
        }
    }

    /// Дешёвая проверка «экран не меняется» (кинематик кончился, рисуется таблица результатов).
    private bool IsScreenStatic()
    {
        try
        {
            using var full = ScreenCapture.Capture();
            using var small = new Bitmap(full, 96, 54);
            var gray = new double[96 * 54];
            for (int y = 0; y < 54; y++)
                for (int x = 0; x < 96; x++)
                {
                    var c = small.GetPixel(x, y);
                    gray[y * 96 + x] = c.R * 0.299 + c.G * 0.587 + c.B * 0.114;
                }

            if (_prevGray != null)
            {
                double diff = 0;
                for (int i = 0; i < gray.Length; i++) diff += Math.Abs(gray[i] - _prevGray[i]);
                _staticCount = diff / gray.Length < 1.5 ? _staticCount + 1 : 0;
            }
            _prevGray = gray;
            return _staticCount >= 2;
        }
        catch { return false; }
    }

    private async Task<ScreenResult?> TryReadResultsScreenAsync()
    {
        if (!_cfg.Ocr.Enabled || !_ocr.Available) return null;
        using var bmp = ScreenCapture.Capture();
        var res = await _ocr.RecognizeAsync(bmp);
        if (res == null) return null;

        var (text, lines) = res.Value;
        var low = text.ToLowerInvariant();
        bool looksLikeResults = (low.Contains("fps") || text.Contains("帧率") || low.Contains("frame"))
                                && text.Any(char.IsDigit);
        if (!looksLikeResults) return null;

        var metrics = BenchmarkResultParser.ParseOcrLines(lines.Select(l => l.Text));
        if (metrics.Count == 0) return null;

        return new ScreenResult { Text = text, Lines = lines.Select(l => l.Text).ToList(), Metrics = metrics };
    }

    /// Стартовая последовательность (по фактическому UI тулзы):
    /// шаг 0: клик ЛКМ по вступительному экрану;
    /// шаг 1: клик по пункту «Тест быстродействия»;
    /// шаг 2: клик по «Подтвердить» в диалоге.
    private async Task TryStartBenchmarkFromMenuAsync(Process proc)
    {
        var launch = _cfg.Launch;
        var o = _cfg.Ocr;

        BringToFront(proc);

        // ── Шаг 0. Вступительный экран: клик ЛКМ ──
        if (launch.IntroClick.Enabled)
        {
            Log.Info($"Вступительный экран: клик ЛКМ через {launch.IntroClick.DelaySec} с " +
                     $"({launch.IntroClick.XPercent}%×{launch.IntroClick.YPercent}% экрана)…");
            await Task.Delay(TimeSpan.FromSeconds(launch.IntroClick.DelaySec));
            BringToFront(proc);

            var scr0 = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            MouseHelper.LeftClick(
                scr0.X + scr0.Width * launch.IntroClick.XPercent / 100,
                scr0.Y + scr0.Height * launch.IntroClick.YPercent / 100);
            Log.Success("Клик по вступительному экрану выполнен.");
            await Task.Delay(7000);
        }

        if (!o.Enabled || !_ocr.Available)
        {
            if (launch.AutoPressEnter)
            {
                Log.Info("OCR выключен: fallback — Enter в окно бенчмарка.");
                await Task.Delay(TimeSpan.FromSeconds(launch.AutoPressEnterDelaySec));
                SendEnterTo(proc);
            }
            else
            {
                Log.Warn("OCR выключен и AutoPressEnter выключен — запустите тест вручную, " +
                         "инструмент дождётся результатов и снимет экран.");
            }
            return;
        }

        // ── Шаг 1. Пункт меню «Тест быстродействия» ──
        Log.Info("Шаг 1: ищу пункт «Тест быстродействия»…");
        var menuClicked = await ClickFirstMatchingLineAsync(proc, o.MenuWords, o.ExcludeWords,
            "пункт меню", saveDebugShot: true);
        if (!menuClicked)
        {
            Log.Warn("Пункт меню не найден (см. screenshots/menu_debug_*.png).");
            if (launch.AutoPressEnter) { Log.Info("Fallback: Enter."); SendEnterTo(proc); }
            return;
        }

        // ── Шаг 2. Кнопка «Подтвердить» в диалоге ──
        Log.Info("Шаг 2: ищу кнопку «Подтвердить»…");
        var confirmed = await ClickFirstMatchingLineAsync(proc, o.ConfirmWords, Array.Empty<string>(),
            "кнопка подтверждения", saveDebugShot: false);
        if (!confirmed)
        {
            Log.Warn("Кнопка подтверждения не найдена.");
            if (launch.AutoPressEnter) { Log.Info("Fallback: Enter."); SendEnterTo(proc); }
        }
        else
        {
            Log.Success("Тест запущен. Пробег длится ~2:30.");
        }
    }

    /// Опрос экрана: OCR → поиск строки, совпадающей с одним из words (и не содержащей exclude) → клик.
    private async Task<bool> ClickFirstMatchingLineAsync(Process proc, IEnumerable<string> words,
        IEnumerable<string> exclude, string what, bool saveDebugShot)
    {
        var pollMs = (int)(_cfg.Ocr.PollIntervalSec * 1000);
        var deadline = DateTime.UtcNow.AddSeconds(_cfg.Ocr.StartScreenWaitSec);
        bool shotSaved = false;

        while (DateTime.UtcNow < deadline)
        {
            bool exited;
            try { exited = proc.HasExited; } catch { exited = true; }
            if (exited) return false;

            try
            {
                using var bmp = ScreenCapture.Capture();
                var res = await _ocr.RecognizeAsync(bmp);
                if (res != null)
                {
                  
                    if (saveDebugShot && !shotSaved)
                    {
                        shotSaved = true;
                        ScreenshotHelper.SaveTo(Path.GetFullPath("screenshots"),
                            $"menu_debug_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png");
                    }

                    var line = res.Value.Lines.FirstOrDefault(l =>
                    {
                        var t = l.Text.ToLowerInvariant();
                        return words.Any(w => t.Contains(w)) && !exclude.Any(x => t.Contains(x));
                    });

                  

                    if (line != null)
                    {
                        var scr = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
                        int sx = scr.X + (int)((line.X + line.W / 2) * scr.Width / Math.Max(1, bmp.Width));
                        int sy = scr.Y + (int)((line.Y + line.H / 2) * scr.Height / Math.Max(1, bmp.Height));
                        Log.Success($"Найдено: «{line.Text}» → клик ({sx},{sy})");
                        MouseHelper.LeftClick(sx, sy);
                        await Task.Delay(1500); // даём UI отреагировать
                        return true;
                    }
                }
             
            }
            catch (Exception ex) { Log.Warn($"OCR-поиск ({what}): {ex.Message}"); }

            await Task.Delay(pollMs);
        }
        return false;
    }

    private static void BringToFront(Process proc)
    {
        try
        {
            proc.Refresh();
            if (proc.MainWindowHandle != IntPtr.Zero)
                InputHelper.BringToFront(proc.MainWindowHandle);
        }
        catch { }
    }

    private static void SendEnterTo(Process proc)
    {
        try
        {
            proc.Refresh();
            if (!proc.HasExited && proc.MainWindowHandle != IntPtr.Zero)
                InputHelper.SendEnter(proc.MainWindowHandle);
        }
        catch { }
    }

    private static void TryGracefulClose(Process proc)
    {
        try
        {
            proc.Refresh();
            if (proc.HasExited) return;
            Log.Info("Закрываю окно бенчмарка (CloseMainWindow)…");
            if (proc.CloseMainWindow() && proc.WaitForExit(15000)) return;
            Log.Warn("Окно не закрылось — Kill.");
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(5000);
        }
        catch (Exception ex) { Log.Warn($"Закрытие процесса: {ex.Message}"); }
    }
}

public static class ProcessHelper
{
    public static void KillAll(IEnumerable<string> names)
    {
        foreach (var name in names)
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                    Log.Info($"Остановлен leftover-процесс: {p.ProcessName} (PID {p.Id})");
                }
                catch (Exception ex) { Log.Warn($"Не удалось остановить {name}: {ex.Message}"); }
                finally { p.Dispose(); }
            }
    }

    public static Process? WaitForAny(IEnumerable<string> names, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            foreach (var name in names)
                foreach (var p in Process.GetProcessesByName(name))
                    return p;
            Thread.Sleep(500);
        }
        return null;
    }
}

public static class InputHelper
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern uint keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const byte VK_RETURN = 0x0D;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    public static void BringToFront(IntPtr hwnd)
    {
        BmwBenchmarkAutomator.InputHelper.ShowWindow(hwnd, 9);              // SW_RESTORE
        BmwBenchmarkAutomator.InputHelper.SetForegroundWindow(hwnd);
        Thread.Sleep(200);
    }

    public static void SendEnter(IntPtr hwnd)
    {
        BringToFront(hwnd);
        keybd_event(VK_RETURN, 0, 0, UIntPtr.Zero);
        Thread.Sleep(60);
        keybd_event(VK_RETURN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }
}