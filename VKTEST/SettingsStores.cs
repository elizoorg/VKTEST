using Microsoft.Win32;

namespace BmwBenchmarkAutomator;

/// Редактор Unreal Engine INI: обновляет существующие ключи в их секциях,
/// недостающие добавляет в целевую секцию. Порядок и остальные строки сохраняются.
// SettingsStores.cs — IniEditor заменить целиком
using System.Text;
using System.Text.RegularExpressions;

public static class IniEditor
{
    public static void Apply(string path, IDictionary<string, string> values, string appendSection)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
        var missing = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < lines.Count; i++)
        {
            int eq = lines[i].IndexOf('=');
            if (eq <= 0) continue;
            var key = lines[i][..eq].Trim();
            if (missing.TryGetValue(key, out var val))
            {
                lines[i] = $"{key}={val}";
                missing.Remove(key);
            }
        }

        if (missing.Count > 0)
        {
            Log.Warn($"Не найдены в файле (дописаны в {appendSection}): {string.Join(", ", missing.Keys)}");
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add(string.Empty);
            if (!lines.Any(l => l.Trim().Equals(appendSection.Trim(), StringComparison.OrdinalIgnoreCase)))
                lines.Add(appendSection);
            lines.AddRange(missing.Select(kv => $"{kv.Key}={kv.Value}"));
        }

        File.WriteAllLines(path, lines);
    }
}
public static class RegistryStore
{
    private static string ToSubKey(string path)
    {
        const string prefix = @"HKEY_CURRENT_USER\";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path[prefix.Length..] : path;
    }

    public static bool Exists(string path)
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(ToSubKey(path)); return k != null; }
        catch { return false; }
    }

    public static string? FindExisting(IEnumerable<string> candidates) => candidates.FirstOrDefault(Exists);

    public static void ApplyDwords(string path, IDictionary<string, uint> values)
    {
        using var key = Registry.CurrentUser.CreateSubKey(ToSubKey(path));
        foreach (var (name, value) in values)
        {
            var old = key.GetValue(name);
            key.SetValue(name, value, RegistryValueKind.DWord);
            Log.Info($"  реестр: {name} = {value} (было: {old ?? "<нет>"})");
        }
    }

    public static Dictionary<string, string> ReadValues(string path)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(ToSubKey(path));
            if (k == null) return dict;
            foreach (var name in k.GetValueNames())
            {
                var v = k.GetValue(name);
                var s = v is byte[] b
                    ? Convert.ToHexString(b)[..Math.Min(64, Convert.ToHexString(b).Length)] + "…"
                    : v?.ToString();
                dict[name] = s ?? "";
            }
        }
        catch { }
        return dict;
    }
}

/// Схема «бэкап + два профиля» (файл GameUserSettings.ini — единственная точка настройки):
///   templates/GameUserSettings.original.ini — бэкап оригинала, создаётся ОДИН раз при первом запуске;
///   templates/GameUserSettings.CPU.ini      — генерируется из бэкапа + значения профиля CPU из config.json;
///   templates/GameUserSettings.GPU.ini      — аналогично для GPU.
/// Перед прогоном файл профиля копируется поверх GameUserSettings.ini,
/// после всех прогонов оригинал возвращается из бэкапа.
/// Всё идемпотентно: существующие файлы templates/ не трогаются.
public static class SettingsApplier
{
    private static string TemplatesDir => Path.GetFullPath("templates");
    private static string BackupPath => Path.Combine(TemplatesDir, "GameUserSettings.original.ini");
    private static string ProfilePath(string p) => Path.Combine(TemplatesDir, $"GameUserSettings.{p}.ini");

    // 1) бэкап оригинала + генерация профилей (если ещё нет — иначе пропуск)
    public static void EnsureTemplates(ToolConfig cfg, EnvironmentContext ctx)
    {
        if (!File.Exists(ctx.IniPath))
            throw new InvalidOperationException($"GameUserSettings.ini не найден: {ctx.IniPath}");

        Directory.CreateDirectory(TemplatesDir);
        if (!File.Exists(BackupPath))
        {
            File.Copy(ctx.IniPath, BackupPath);
            Log.Success($"Бэкап: {BackupPath}");
        }

        foreach (var (name, profile) in cfg.Profiles)
        {
            var path = ProfilePath(name);
            if (File.Exists(path)) { Log.Info($"Профиль {name} уже есть — пропуск."); continue; }
            File.Copy(BackupPath, path);
            IniEditor.Apply(path, ResolveNativeValues(profile.Ini), cfg.Settings.TargetIniSection);
            Log.Success($"Профиль {name} создан: {path}");
        }
    }

    // 2) подмена файла профилем
    public static void ApplyProfileFile(EnvironmentContext ctx, string profileName) =>
        File.Copy(ProfilePath(profileName), ctx.IniPath, overwrite: true);

    // 3) возврат оригинала
    public static void RestoreOriginal(EnvironmentContext ctx) =>
        File.Copy(BackupPath, ctx.IniPath, overwrite: true);

    private static Dictionary<string, string> ResolveNativeValues(Dictionary<string, string> ini) => ini;
    // NATIVE-подстановку можно убрать совсем: разрешение не трогаем, рычаг — sg.ResolutionQuality
}

public static class ScreenHelper
{
    public static (int Width, int Height) GetNativeResolution()
    {
        try
        {
            var b = System.Windows.Forms.Screen.PrimaryScreen?.Bounds;
            if (b is { Width: > 0 }) return (b.Value.Width, b.Value.Height);
        }
        catch { }
        return (1920, 1080);
    }
}

public static class ScreenshotHelper
{
    public static string? SaveTo(string dir, string name)
    {
        try
        {
            Directory.CreateDirectory(dir);
            using var bmp = ScreenCapture.Capture();
            var path = Path.Combine(dir, name);
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return path;
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось сохранить скриншот: {ex.Message}");
            return null;
        }
    }
}