using System.Text.Json;

namespace BmwBenchmarkAutomator;

public sealed class ToolConfig
{
    public int SteamAppId { get; set; } = 2435770;
    public string GameName { get; set; } = "Black Myth: Wukong Benchmark Tool";

    public LaunchOptions Launch { get; set; } = new();
    public SettingsOptions Settings { get; set; } = new();
    public ResultsOptions Results { get; set; } = new();
    public OcrOptions Ocr { get; set; } = new();
    public PresentMonOptions PresentMon { get; set; } = new();
    public Dictionary<string, Profile> Profiles { get; set; } = new();

    public static JsonSerializerOptions JsonOpts { get; } = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static ToolConfig Load(string path)
    {
        ToolConfig cfg;
        if (File.Exists(path))
        {
            cfg = JsonSerializer.Deserialize<ToolConfig>(File.ReadAllText(path), JsonOpts) ?? new ToolConfig();
            Log.Info($"Конфигурация загружена: {Path.GetFullPath(path)}");
        }
        else
        {
            cfg = new ToolConfig();
            cfg.Save(path);
            Log.Warn($"config.json не найден — создан со значениями по умолчанию: {Path.GetFullPath(path)}");
        }
        cfg.Profiles.TryAdd("CPU", Profile.CpuDefault());
        cfg.Profiles.TryAdd("GPU", Profile.GpuDefault());
        return cfg;
    }

    public void Save(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
}

public sealed class LaunchOptions
{
    /// "Steam" — через steam://rungameid; "Executable" — прямой запуск exe.
    public string Mode { get; set; } = "Steam";
    public string Args { get; set; } = "";
    public List<string> ProcessNames { get; set; } = new()
        { "b1"};
    public int StartupTimeoutSec { get; set; } = 180;
    public int BenchmarkTimeoutSec { get; set; } = 1800;

    /// Шаг 0 стартовой последовательности: клик ЛКМ по вступительному экрану
    /// («после запуска приложения нужно нажать ЛКМ»).
    public IntroClickOptions IntroClick { get; set; } = new();

    /// Fallback, если OCR недоступен: отправить Enter в окно бенчмарка.
    public bool AutoPressEnter { get; set; } = false;
    public int AutoPressEnterDelaySec { get; set; } = 20;
    /// Закрыть окно бенчмарка, как только результаты распознаны с экрана.
    public bool CloseWindowOnResults { get; set; } = true;
    public int GracePeriodAfterExitSec { get; set; } = 5;
    public bool ScreenshotOnFinish { get; set; } = true;
}

public sealed class IntroClickOptions
{
    public bool Enabled { get; set; } = true;
    /// Задержка после появления процесса (сек) — даём окну загрузиться.
    public int DelaySec { get; set; } = 20;
    /// Позиция клика в процентах от экрана (по умолчанию — центр).
    public int XPercent { get; set; } = 50;
    public int YPercent { get; set; } = 50;
}

public sealed class SettingsOptions
{
    /// Секция INI, в которую добавляются отсутствующие ключи.
    public string TargetIniSection { get; set; } = "[/Script/b1.GameUserSettings]";
    public List<string> IniCandidates { get; set; } = new()
    {
        @"%LOCALAPPDATA%\b1\Saved\Config\Windows\GameUserSettings.ini",
        @"%LOCALAPPDATA%\B1Benchmark\Saved\Config\Windows\GameUserSettings.ini",
        @"%LOCALAPPDATA%\b1Benchmark\Saved\Config\Windows\GameUserSettings.ini",
    };
    public List<string> RegistryKeys { get; set; } = new()
    {
        @"HKEY_CURRENT_USER\Software\Game Science\BlackMythWukongBenchmarkTool",
        @"HKEY_CURRENT_USER\Software\Game Science\BlackMythWukongBenchmark",
        @"HKEY_CURRENT_USER\Software\Game Science\BlackMythWukong",
    };
}

public sealed class ResultsOptions
{
    public List<string> WatchDirs { get; set; } = new()
    {
        @"%LOCALAPPDATA%\b1\Saved",
        @"%LOCALAPPDATA%\B1Benchmark\Saved",
        @"%LOCALAPPDATA%\b1Benchmark\Saved",
    };
    public bool IncludeInstallDir { get; set; } = true;
    public int WatchDirDepth { get; set; } = 8;
    public int InstallDirDepth { get; set; } = 2;
}

public sealed class OcrOptions
{
    public bool Enabled { get; set; } = true;
    public double PollIntervalSec { get; set; } = 2.0;

    /// Сколько секунд ждать КАЖДЫЙ из шагов стартовой последовательности.
    public double StartScreenWaitSec { get; set; } = 120;

    /// Раньше этого момента (отсчёт от клика «Подтвердить») экран результатов не ищем.
    /// Пробег длится 2:30 (150 с) → 145 с даёт запас, детект статики страхует ложно-ранние срабатывания.
    public double ResultsStartAfterSec { get; set; } = 145;

    /// Сколько секунд после завершения процесса ещё пытаться распознать экран.
    public double ResultsGiveUpAfterExitSec { get; set; } = 90;

    /// Шаг 1: пункт меню «Тест быстродействия». Матч — по подстроке в нижнем регистре.
    public List<string> MenuWords { get; set; } = new()
        { "быстродейств", "тест быстродействия", "benchmark", "开始测试" };

    /// Шаг 2: кнопка в диалоге подтверждения.
    public List<string> ConfirmWords { get; set; } = new() { "подтверд", "confirm", "确认" };

    /// Исключения для шага меню: не кликать строки, содержащие эти подстроки
    /// (заголовок «Black Myth: Wukong Benchmark Tool» тоже содержит "benchmark").
    public List<string> ExcludeWords { get; set; } = new() { "tool", "wukong", "myth", "black" };
}

public sealed class PresentMonOptions
{
    public bool Enabled { get; set; } = true;
    /// Путь к PresentMon.exe (v1.9.x: github.com/GameTechDev/PresentMon/releases).
    public string Path { get; set; } = "tools/PresentMon.exe";
    public double TrimHeadSec { get; set; } = 10;   // используется как запасной, если старт теста не зафиксирован
    public double TrimTailSec { get; set; } = 20;
}

public sealed class Profile
{
    public string Description { get; set; } = "";
    /// Пары ключ=значение для GameUserSettings.ini. Значение "NATIVE" = родное разрешение монитора.
    public Dictionary<string, string> Ini { get; set; } = new();
    /// DWORD-значения для реестра (расширенные опции; имена выясняются через 'inspect').
    public Dictionary<string, uint> RegistryDwords { get; set; } = new();

    public static Profile CpuDefault() => new()
    {
        Description = "CPU-тест: 1280x720, все настройки качества минимальные, апскейл и динамическое " +
                      "разрешение выключены, лимит FPS снят. Стоимость кадра для GPU минимальна, " +
                      "узкое место — процессор.",
        Ini = new Dictionary<string, string>
        {
            ["ResolutionSizeX"] = "1280",
            ["ResolutionSizeY"] = "720",
            ["LastUserConfirmedResolutionSizeX"] = "1280",
            ["LastUserConfirmedResolutionSizeY"] = "720",
            ["FullscreenMode"] = "1",
            ["LastConfirmedFullscreenMode"] = "1",
            ["PreferredFullscreenMode"] = "1",
            ["FrameRateLimit"] = "0.000000",
            ["bUseDynamicResolution"] = "False",
            ["ViewDistanceQuality"] = "0",
            ["GlobalIlluminationQuality"] = "0",
            ["ShadowsQuality"] = "0",
            ["PostProcessQuality"] = "0",
            ["TextureQuality"] = "0",
            ["EffectsQuality"] = "0",
            ["FoliageQuality"] = "0",
            ["ShadingQuality"] = "0",
            ["AntiAliasingQuality"] = "0",
            ["ReflectionQuality"] = "0",
            ["AudioQualityLevel"] = "0",
        },
        RegistryDwords = new Dictionary<string, uint>()
    };

    public static Profile GpuDefault() => new()
    {
        Description = "GPU-тест: родное разрешение монитора, все группы качества на максимуме (Cinematic), " +
                      "нативный рендер без апскейла и динамического разрешения, лимит FPS снят. " +
                      "Узкое место — GPU. Full Ray Tracing добавляется в RegistryDwords после калибровки.",
        Ini = new Dictionary<string, string>
        {
            ["ResolutionSizeX"] = "NATIVE",
            ["ResolutionSizeY"] = "NATIVE",
            ["LastUserConfirmedResolutionSizeX"] = "NATIVE",
            ["LastUserConfirmedResolutionSizeY"] = "NATIVE",
            ["FullscreenMode"] = "1",
            ["LastConfirmedFullscreenMode"] = "1",
            ["PreferredFullscreenMode"] = "1",
            ["FrameRateLimit"] = "0.000000",
            ["bUseDynamicResolution"] = "False",
            ["ViewDistanceQuality"] = "4",
            ["GlobalIlluminationQuality"] = "4",
            ["ShadowsQuality"] = "4",
            ["PostProcessQuality"] = "4",
            ["TextureQuality"] = "4",
            ["EffectsQuality"] = "4",
            ["FoliageQuality"] = "4",
            ["ShadingQuality"] = "4",
            ["AntiAliasingQuality"] = "4",
            ["ReflectionQuality"] = "4",
            ["AudioQualityLevel"] = "4",
        },
        RegistryDwords = new Dictionary<string, uint>()
    };
}